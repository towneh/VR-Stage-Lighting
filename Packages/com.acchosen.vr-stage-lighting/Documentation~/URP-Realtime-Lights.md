# VRSL URP Realtime Lights — Implementation Guide

This document describes the implementation journey of extending VRSL's GPU-driven DMX pipeline to drive genuine scene lighting in Unity 6 URP, replacing the original shader-based volumetric simulation with a fully GPU-resident realtime light path. It is intended as a reference for contributors and for anyone integrating the system into a new Unity 6 URP project.

---

## Background

VRSL (VR Stage Lighting) was designed from the ground up for VRChat, where realtime Unity lights are prohibitively expensive and GPU-accelerated shader tricks are necessary to simulate stage lighting at scale. The core of the system encodes DMX512 data inside a video stream, decodes it through a chain of Custom Render Textures (CRTs) on the GPU, and drives HLSL shaders directly — colour, intensity, pan, tilt, strobe, and gobo selection are all resolved on the GPU, per-fragment, with no CPU involvement in the per-fixture calculation.

This approach produces convincing volumetric beam and projection effects but does not illuminate scene geometry. Surfaces near a stage light look the same whether the light is on or off.

In Unity 6.2+ with URP's **Forward+** rendering path, the cost model for realtime lights changed significantly. Forward+ moves per-tile light assignment to a GPU compute pass, removing the traditional per-object light limit and allowing hundreds of per-pixel lights with negligible CPU overhead. This makes it practical to replace VRSL's volumetric simulation with genuine scene-illuminating lights driven by the same DMX infrastructure — and, critically, to do so without ever involving the CPU in the per-frame lighting calculation.

---

## The Original Architecture (Volumetric Shader Path)

Understanding what already exists on the GPU is essential context for the migration.

```
Artnet/OSC
    │
    ▼
GridReader.cs
  Receives OSC packets, decodes 0–255 → 0.0–1.0 floats,
  writes to CPU buffer then uploads to _DataBuffer RenderTexture
    │
    ▼
CRT Shader Chain (GPU, every frame)
  DMXInterpolation.shader    — smooths incoming pixel values over time
  StrobeTimings.shader       — computes strobe phase from frequency channel
  StrobeOutput.shader        — converts phase to binary on/off gate
  SpinnerTimer.shader        — computes gobo spin phase
    │
    ▼
Four Global Shader Textures (set via VRCShader.SetGlobalTexture)
  _Udon_DMXGridRenderTexture          — main channels (intensity, colour, gobo, motor)
  _Udon_DMXGridRenderTextureMovement  — movement channels (pan, tilt)
  _Udon_DMXGridStrobeOutput           — pre-computed strobe gate
  _Udon_DMXGridSpinTimer              — pre-computed gobo spin phase
    │
    ▼
Fixture Shaders (HLSL, per-fragment, every pixel of every fixture mesh)
  VRStageLighting_DMX_Static.cs pushes only static config via MaterialPropertyBlock:
    _DMXChannel, _EnableDMX, _MaxMinPanAngle, _ConeWidth, etc.
  Shaders call getValueAtCoords() from VRSL-DMXFunctions.cginc each fragment
    to decode colour, intensity, pan, tilt, strobe, gobo directly from the textures
  Volumetric cone, projection disc, and fixture emissive are all drawn as
    instanced meshes with the decoded DMX values driving per-instance properties
```

The channel layout within each 13-channel fixture block (relative to the fixture's base absolute channel):

| Offset | Channel                   | Source Texture              |
|--------|---------------------------|-----------------------------|
| +0     | Pan coarse                | Movement texture            |
| +1     | Pan fine                  | Movement texture            |
| +2     | Tilt coarse               | Movement texture            |
| +3     | Tilt fine                 | Movement texture            |
| +4     | Motor speed / cone width  | Main texture                |
| +5     | Dimmer / intensity        | Main texture                |
| +6     | Strobe gate               | Strobe output texture       |
| +7     | Red                       | Main texture                |
| +8     | Green                     | Main texture                |
| +9     | Blue                      | Main texture                |
| +10    | Gobo spin speed           | Main texture + Spin timer   |
| +11    | Gobo selection            | Main texture                |
| +12    | (spare / tilt fine alias) | —                           |

The absolute channel number passed to the shader is computed from `dmxChannel` and `dmxUniverse` by `RawDMXConversion()`:

```csharp
int absChannel = dmxChannel + (dmxUniverse - 1) * 512 + (dmxUniverse - 1) * 8;
```

The `+8` per universe accounts for inter-universe spacing in the DMX grid texture layout.

### The UV Sampling Function

`getValueAtCoords()` in `VRSL-DMXFunctions.cginc` is the single most important function to understand before touching this system. It converts an absolute channel number to UV coordinates on the DMX grid texture using the `IndustryRead` path:

```glsl
// x = channel % 13 (1–13; wraps 0 → 13)
// y = channel / 13.0, corrected when channel is an exact multiple of 13
float resMultiplierX = textureWidth / 13.0;
float u = (x  * resMultiplierX / textureWidth)  - 0.015;
float v = ((y + 1.0) * resMultiplierX / textureHeight) - 0.001915;
half4 c = tex2Dlod(_Tex, float4(u, v, 0, 0));
return LinearRgbToLuminance(c.rgb);
```

The small constant offsets (`-0.015`, `-0.001915`) are empirical alignment corrections baked into the original shader. Any port of this function — to a compute shader or anywhere else — must replicate these offsets exactly to read the correct texels.

### What the Volumetric Path Gives You (and What It Doesn't)

The volumetric path excels at visual effects: beam shafts, gobo projections, fixture emissive glow, and strobe effects all look convincing and run with near-zero per-fixture CPU cost. The fundamental limitation is that none of this illuminates anything. A spotlight beam drawn as a cone mesh does not cast light on the floor. A wash light does not change the colour of the wall behind it. For installations where the lighting rig must genuinely illuminate performers or set pieces, the volumetric path alone is insufficient.

---

## Why Unity Lights Don't Scale: The CPU Architecture and Shadow Cost Problem

### The Per-Frame CPU Bottleneck

The most important constraint going into this work is stated directly: **the existing architecture never leaves the GPU for per-fixture work, and the new path must not either.**

Unity's `Light` component — the standard answer for scene illumination — is a CPU-only construct. Its `color`, `intensity`, and `range` properties must be written from the main thread each frame. Doing so naively would mean reading the DMX textures back to CPU (introducing GPU pipeline stalls or async latency), decoding the values in C#, and writing to each `Light` in a sequential loop. With 100+ fixtures this becomes a sequential bottleneck that completely abandons VRSL's GPU-first architecture.

A softer version of this using `AsyncGPUReadback` avoids GPU stalls and keeps the readback off the critical path, but at the cost of 1–2 frames of latency. Crucially, even with the latency accepted, this only solves half the problem.

### The Shadow Map Rendering Wall

The deeper cost of using Unity `Light` components is not the CPU update loop — it is shadow map generation. URP renders the entire scene geometry **once per shadow-casting light** into a shadow atlas every frame. At scale this becomes the dominant cost, dwarfing everything else in the pipeline:

| Shadow-casting spot lights | Estimated shadow pass cost | 60 fps viability |
|---|---|---|
| 0 | baseline | ✅ no overhead |
| 4 | +2–5 ms | ✅ comfortable |
| 8–12 | +6–15 ms | ⚠️ borderline on mid-range GPU |
| 100 | ~100 full scene redraws | ❌ not feasible |

Point lights are six times worse than spot lights, requiring one shadow map pass per cubemap face. Even with frustum culling helping on a sparse scene, 100 shadow-casting lights would exhaust a 60 fps frame budget before a single opaque pixel of the actual scene renders.

For reference, the rest of the GPU pipeline at 100 lights is fast: the compute dispatch decoding all 100 fixtures from the DMX textures takes well under 1 ms, and the fullscreen additive lighting pass — which evaluates all 100 lights per pixel — runs at approximately 1–3 ms at 1080p. The shadow maps are the wall.

### What This Means for the Design

The forward requirement is therefore not simply "make realtime lights work" but specifically: **decode DMX on the GPU, produce light data on the GPU, and apply that light data to scene geometry on the GPU, without routing any per-frame data through the CPU** — and without incurring per-light shadow map passes for the majority of fixtures.

Shadow casting remains an open problem. The practical path, if shadows are needed, is a `castShadows` flag per fixture driving a small pool of real Unity `Light` components (via async readback) capped at around 4–8 shadow-casting spots. Wash lights, strips, and blinders — which make up the bulk of a large rig — produce no visible shadow benefit and should never be in that pool. The fullscreen additive pass handles their illumination contribution with zero shadow overhead.

The GPU-driven pipeline described below addresses the illumination problem completely. Shadow casting at scale is a separate, harder problem and is noted under Known Limitations.

---

## The GPU-Driven Pipeline

### Design Goals

- All per-frame DMX decoding runs in a compute shader, directly sampling the existing CRT output textures
- Light data lives in a GPU-resident `GraphicsBuffer` never read back to CPU
- Scene geometry is illuminated by a fullscreen additive pass that reads the GPU light buffer in HLSL
- The existing CRT shader chain and volumetric fixture shaders are untouched — they continue to run alongside the new path
- The VRSL.Core assembly remains unchanged and VRChat builds are unaffected

### How the Original DMX Sampling Was Ported to Compute

The first challenge was reproducing `getValueAtCoords()` inside a compute shader. The function uses GLSL-style texture access (`tex2Dlod`) and instanced property accessors (`UNITY_ACCESS_INSTANCED_PROP`) that have no direct equivalent in a compute context.

The translation required three changes:

1. `tex2Dlod` → `Texture2D.SampleLevel` with a `sampler_point_clamp` inline sampler. Point sampling is critical — each texel encodes exactly one DMX channel and interpolating between adjacent channels would produce incorrect values.

2. `LinearRgbToLuminance` → inline: `c.r * 0.2126 + c.g * 0.7152 + c.b * 0.0722`. This is the non-NineUniverse, non-Legacy mode path, which is what all standard VRSL fixtures use.

3. The `_TexelSize` vector (available implicitly in shader code from the `_Tex_TexelSize` auto-property) must be passed explicitly as a `float4` uniform `_VRSLDMXTexelSize`.

The resulting compute-side function:

```hlsl
float GetDMXValue(int ch, Texture2D<float4> tex)
{
    int   x = ch % 13;
    if (x == 0) x = 13;
    float y = (ch % 13 == 0) ? (ch / 13.0) - 1.0 : (ch / 13.0);

    // Correction table for channel 13 in specific ranges — matches CGINC exactly
    if (x == 13)
    {
        if (ch >= 90  && ch <= 101)  y -= 1.0;
        if (ch >= 160 && ch <= 205)  y -= 1.0;
        if (ch >= 326 && ch <= 404)  y -= 1.0;
        if (ch >= 676 && ch <= 819)  y -= 1.0;
        if (ch >= 1339)              y -= 1.0;
    }

    float resMultX = _VRSLDMXTexelSize.z / 13.0;
    float u = (x       * resMultX * _VRSLDMXTexelSize.x) - 0.015;
    float v = ((y+1.0) * resMultX * _VRSLDMXTexelSize.y) - 0.001915;

    float4 c = tex.SampleLevel(sampler_point_clamp, float2(saturate(u), saturate(v)), 0);
    return c.r * 0.2126 + c.g * 0.7152 + c.b * 0.0722;
}
```

All three DMX textures share the same resolution in all VRSL CRT configurations, so a single `_VRSLDMXTexelSize` covers all three.

### Pan/Tilt in a Compute Shader

The original VRSL pan/tilt system works by rotating GameObjects: `VRStageLighting_DMX_Static.cs` writes pan and tilt angles into shader properties, and the fixture meshes (volumetric cone, projection, emissive body) are physically rotated by the pan and tilt transforms in the scene hierarchy.

In the compute shader there are no GameObjects or transforms. The direction a light is pointing must be computed entirely from data. The solution is **Rodrigues' rotation formula**, which rotates a vector around an arbitrary axis by an angle without constructing a full rotation matrix:

```hlsl
float3 RotateAroundAxis(float3 v, float3 axis, float angleDeg)
{
    float rad = radians(angleDeg);
    float c = cos(rad); float s = sin(rad);
    return v * c + cross(axis, v) * s + axis * dot(axis, v) * (1.0 - c);
}
```

Tilt is applied first, rotating the base forward direction around world X (object X under the standard 90° X root rotation on every VRSL fixture). Pan then rotates around the base forward direction itself (object Z = world -Y for a hanging fixture, matching `rotateYMatrix` in `VRSL-StandardMover-Vertex.cginc`, which despite its name rotates around object Z). Doing pan around the base forward rather than world +Y is required because `baseForward` is anti-parallel to world +Y for a hanging fixture, so rotating it around world +Y is a no-op.

### GPU Data Structs

Two structs bridge the CPU and GPU sides of the pipeline, defined in `VRSLLightingLibrary.hlsl` and matched exactly by C# `[StructLayout(LayoutKind.Sequential)]` structs in `VRSL_GPULightManager.cs`.

All fields use `float4`/`Vector4` rather than `float3`/`Vector3`. HLSL `StructuredBuffer` members are not subject to the 16-byte alignment rules of constant buffers, so `float3` is technically safe — but the C# `Vector3` layout (12 bytes) and HLSL `float3` layout (also 12 bytes) can diverge subtly across compilers and platforms. Using `float4` everywhere guarantees identical layout with no ambiguity.

**VRSLFixtureConfig** (112 bytes, 7 × `float4`) — written from CPU once at setup and on config changes:

| Field | Contents |
|---|---|
| `positionAndRange` | xyz = world position, w = attenuation range |
| `forwardAndType` | xyz = base forward direction, w = light type (0=spot, 1=point) |
| `rightAndMaxIntensity` | xyz = local +X in world space (tilt rotation axis), w = max intensity scalar |
| `spotAngles` | x = inner half-angle (deg), y = max outer half-angle (deg), z = finalIntensity cap, w = min outer half-angle (deg) |
| `dmxChannel` | x = absolute channel, y = enableStrobe, z = enablePanTilt, w = enableFineChannels |
| `panSettings` | x = maxMinPan (deg), y = panOffset (deg), z = invertPan (0/1), w = enableGoboSpin (0/1) |
| `tiltSettings` | x = maxMinTilt (deg), y = tiltOffset (deg), z = invertTilt (0/1), w = enableGobo (0/1) |

**VRSLLightData** (80 bytes, 5 × `float4`) — written by the compute shader each frame, read by the fragment shader:

| Field | Contents |
|---|---|
| `positionAndRange` | xyz = world position, w = range |
| `directionAndType` | xyz = normalised direction, w = type (0=spot, 1=point) |
| `colorAndIntensity` | xyz = linear RGB, w = combined intensity (dimmer × max × final × strobe) |
| `spotCosines` | x = cos(innerHalfAngle), y = cos(outerHalfAngle), z = active flag (0 = skip light) |
| `goboAndSpin` | x = gobo array index (-1 = none, 0+ = slice in `_VRSLGobos`), y = gobo spin speed (±10 bipolar) |

### Full Pipeline Architecture

```
DMX RenderTextures — already GPU-resident, produced by the CRT chain
  _Udon_DMXGridRenderTexture
  _Udon_DMXGridRenderTextureMovement
  _Udon_DMXGridStrobeOutput
         │
         │  imported into render graph as TextureHandles (no CPU readback)
         ▼
[COMPUTE PASS — VRSLDMXLightUpdate.compute]
[RenderPassEvent: BeforeRenderingOpaques]
  Dispatch: ceil(fixtureCount / 64) groups × 64 threads
  Per thread = per fixture:
    • Read VRSLFixtureConfig from StructuredBuffer (CPU-written, GPU-read)
    • Call GetDMXValue() for dimmer, R, G, B, strobe, pan, tilt channels
    • Apply Rodrigues pan/tilt rotation to produce final world-space direction
    • Precompute spot cosines from config angles
    • Write VRSLLightData to RWStructuredBuffer (GPU-written, GPU-read)
         │
         ▼
  GraphicsBuffer<VRSLLightData>  (persistent, GPU-resident)
         │
         │  imported into render graph as BufferHandle
         ▼
[RASTER PASS — VRSLDeferredLighting.shader]
[RenderPassEvent: AfterRenderingOpaques]
  Full-screen triangle (3 vertices, SV_VertexID, no vertex buffer)
  Blend One One — additive contribution on top of the opaque scene
  Per pixel:
    • Early-out if depth == sky/far plane
    • Reconstruct world position via ComputeWorldSpacePosition + depth buffer
    • Read surface normal from _CameraNormalsTexture (Depth Normals Prepass)
    • Loop over all N VRSLLightData entries:
        – Skip if active flag == 0 (light intensity negligible)
        – Evaluate distance attenuation (smoothed inverse-square)
        – Evaluate cone attenuation (spot lights only)
        – Multiply NdotL × radiance and accumulate
    • Output float4(accumulatedLighting, 0) — added to frame by blend state
```

### VRSLDeferredLighting.shader — Technical Notes

**Full-screen triangle** avoids allocating a vertex buffer. Three vertices are generated entirely from `SV_VertexID` in the vertex shader:

```hlsl
Varyings vert(uint vertexID : SV_VertexID)
{
    Varyings o;
    o.uv         = float2((vertexID << 1) & 2, vertexID & 2);
    o.positionCS = float4(o.uv * 2.0 - 1.0, 0.0, 1.0);
    return o;
}
```

**World position reconstruction** uses URP's built-in helper rather than manual inverse projection, which handles reversed-Z, platform NDC conventions, and stereo correctly:

```hlsl
float rawDepth = SampleSceneDepth(uv);
float3 posWS   = ComputeWorldSpacePosition(uv, rawDepth, UNITY_MATRIX_I_VP);
```

**Normals** are read from `_CameraNormalsTexture` via URP's `DeclareNormalsTexture.hlsl` include. This texture is only populated when **Depth Normals Prepass** is enabled in the URP Renderer asset. Without it the pass will not run and a `Debug.LogWarning` is emitted.

**Attenuation model** matches URP's own `GetDistanceAttenuation` and `GetAngleAttenuation` to ensure consistent falloff with any URP lights present in the scene:

```hlsl
float VRSL_DistanceAttenuation(float distSq, float range)
{
    float d2 = distSq / (range * range);
    float f  = saturate(1.0 - d2 * d2);      // smooth cutoff at range boundary
    return (f * f) / max(distSq, 0.0001);     // inverse-square core falloff
}

float VRSL_SpotAttenuation(float3 lightDir, float3 toLight, float cosInner, float cosOuter)
{
    float cosAngle = dot(-lightDir, normalize(toLight));
    float t = saturate((cosAngle - cosOuter) / max(cosInner - cosOuter, 0.0001));
    return t * t;                              // squared for smooth penumbra
}
```

### VRSL_GPULightManager.cs

The manager's only CPU-per-frame work is uploading the `VRSLFixtureConfig` buffer when `_configDirty` is true — once at startup and again only when a fixture's settings change. All per-frame DMX decode work happens in the compute shader.

Buffer sizing uses `Marshal.SizeOf<T>()` rather than hand-computed constants to guarantee the C# and HLSL struct strides match, regardless of future struct changes.

The three `RenderTexture` references (the CRT outputs) are wrapped in `RTHandle`s via `RTHandles.Alloc()`. This is required by the Unity 6 render graph's `ImportTexture` API, which takes `RTHandle` rather than raw `RenderTexture`. RTHandles are allocated in `OnEnable` and released in `OnDisable` to avoid leaks.

`RefreshFixtures()` uses `FindObjectsByType` to auto-discover all `VRStageLighting_DMX_RealtimeLight` components. This runs once on `OnEnable`; call it again if fixtures are added or removed dynamically at runtime.

### VRSLRealtimeLightFeature.cs — Render Graph Integration

The renderer feature uses the **Unity 6 Render Graph API** exclusively (`RecordRenderGraph`). The legacy `Execute` path, which was the standard in Unity 2022.x, is not used. The render graph enables explicit resource dependency tracking, automatic barrier insertion, and pass culling.

**ComputePass** (`BeforeRenderingOpaques`): All three DMX texture RTHandles and both `GraphicsBuffer`s are imported into the render graph. Access flags (`AccessFlags.Read` on configs and textures, `AccessFlags.Write` on the light data buffer) let the render graph insert the correct GPU memory barriers. The dispatch count is `ceil(fixtureCount / 64)` groups in the X dimension.

**LightingPass** (`AfterRenderingOpaques`): The light data buffer, `cameraDepthTexture`, and `cameraNormalsTexture` are imported from `UniversalResourceData`. The active colour target is declared as a read-write render attachment so the additive blend operates in-place. `DrawProcedural` with `MeshTopology.Triangles, 3` triggers the full-screen triangle without any mesh asset.

The `PassData` class per-pass pattern is the idiomatic Unity 6 render graph approach: data needed inside the `SetRenderFunc` lambda is stored in a typed struct so the render graph can defer execution and validate resource usage at compile time.

### Assembly Isolation

The GPU scripts live in `Runtime/Scripts/GPU/` under `VRSL.GPU.asmdef`, separate from `VRSL.Core`. This separation exists because `VRSL.Core` is used by VRChat projects that have the VRChat SDK but not URP. Adding `Unity.RenderPipelines.Universal.Runtime` to `VRSL.Core`'s references would break every VRChat build.

`VRSL.GPU.asmdef` uses `versionDefines` to emit the `VRSL_URP` scripting define when `com.unity.render-pipelines.universal >= 14.0` is installed, and `defineConstraints` to skip compilation of the entire assembly when URP is absent:

```json
"versionDefines": [{
    "name": "com.unity.render-pipelines.universal",
    "expression": "14.0",
    "define": "VRSL_URP"
}],
"defineConstraints": ["VRSL_URP"]
```

`VRSL.Core` retains `VRStageLighting_DMX_RealtimeLight.cs` (the per-fixture config component) because it carries no URP dependency — it is a plain `MonoBehaviour` with DMX addressing fields that `VRSL_GPULightManager` reads at config time.

---

## Setup Guide (Unity 6 URP)

### URP Renderer Asset

1. Set **Rendering Path** to **Forward+** on the URP asset.
2. Open the URP Renderer asset used by your camera.
3. Set **Depth Priming Mode** → `Disabled` (required for the depth normals prepass to populate correctly with Forward+).
4. Enable **Depth Normals Prepass**.
5. Under **Renderer Features**, add **VRSLRealtimeLightFeature**.

### Scene Setup

1. Add **`VRSL_GPULightManager`** to any persistent scene GameObject. Assign:

   | Field | Asset |
   |---|---|
   | `dmxMainTexture` | CRT producing `_Udon_DMXGridRenderTexture` |
   | `dmxMovementTexture` | CRT producing `_Udon_DMXGridRenderTextureMovement` |
   | `dmxStrobeTexture` | CRT producing `_Udon_DMXGridStrobeOutput` |
   | `computeShader` | `VRSLDMXLightUpdate` |
   | `lightingShader` | `Hidden/VRSL/DeferredLighting` |

2. For each fixture, add **`VRStageLighting_DMX_RealtimeLight`** and configure:
   - `dmxChannel` / `dmxUniverse` — matching your Artnet patch
   - `maxIntensity` — lux value at DMX full-on (tune per scene scale)
   - `realtimeLight` — the `Light` component for this fixture (spot or point). Range, spot angle, and inner spot angle are read once for the config buffer; the `Light` is not written at runtime
   - For moving heads: enable `enablePanTilt`, assign `panTransform` and `tiltTransform`

3. Press Play. `RefreshFixtures()` runs on `OnEnable`, discovers all fixture components, creates the GPU buffers, and uploads the initial config.

### Runtime Changes

```csharp
// After changing a fixture's dmxChannel, maxIntensity, or any other config field:
VRSL_GPULightManager.Instance.MarkConfigDirty();

// After adding or removing fixture GameObjects at runtime:
VRSL_GPULightManager.Instance.RefreshFixtures();
```

---

## Known Limitations

**Transparent geometry is not illuminated.** The additive lighting pass runs after opaques. Haze, glass, and other transparent materials will not receive light from this system. The original VRSL volumetric shaders remain the correct tool for those visual effects and can run in parallel.

**No shadow casting.** Shadows require shadow maps, which are generated per-light by Unity's shadow pass. Since this system bypasses Unity's `Light` component entirely, no shadow maps are generated. Adding shadow support would require integrating the GPU light buffer into URP's shadow casting path, which is not currently implemented.

**NineUniverse mode not supported.** The compute shader's `GetDMXValue` implements the standard `IndustryRead` path only. NineUniverse mode alters which colour channel of the texture encodes each channel value and requires additional branching in the sampling function.

---

## File Reference

| File | Assembly | Description |
|---|---|---|
| `Runtime/Scripts/VRStageLighting_DMX_RealtimeLight.cs` | VRSL.Core | Per-fixture DMX config component; shared between GPU manager and scene setup |
| `Runtime/Scripts/GPU/VRSL_GPULightManager.cs` | VRSL.GPU | Singleton; owns both GraphicsBuffers and DMX texture RTHandles; uploads fixture config |
| `Runtime/Scripts/GPU/VRSLRealtimeLightFeature.cs` | VRSL.GPU | URP ScriptableRendererFeature; schedules compute pass + fullscreen lighting pass via Render Graph |
| `Runtime/Scripts/GPU/VRSL.GPU.asmdef` | — | Assembly definition; isolates URP dependency; only compiles when URP 14.0+ present |
| `Runtime/Shaders/Shared/VRSLLightingLibrary.hlsl` | — | HLSL struct definitions and light evaluation functions shared by compute and fragment shaders |
| `Runtime/Shaders/Compute/VRSLDMXLightUpdate.compute` | — | Compute shader; DMX texture sampling, pan/tilt rotation, light buffer write |
| `Runtime/Shaders/VRSLDeferredLighting.shader` | — | Fullscreen additive pass; depth/normal read, world position reconstruction, light loop |
