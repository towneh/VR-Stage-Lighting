# VRSL URP Realtime Lights — Implementation Guide

This document describes the full implementation journey of adding Unity realtime `Light` support to VRSL, covering both the initial CPU-bridged path and the final fully GPU-driven pipeline. It is intended as a reference for contributors and for anyone integrating the system into a new Unity 6 URP project.

---

## Background

VRSL (VR Stage Lighting) was designed from the ground up for VRChat, where realtime Unity lights are prohibitively expensive and where GPU-accelerated shader tricks are necessary to simulate stage lighting at scale. The core of the system encodes DMX512 data inside a video stream, decodes it through a chain of Custom Render Textures (CRTs) on the GPU, and drives HLSL shaders directly from the resulting render textures. No CPU is ever involved in the per-fixture lighting calculation — colour, intensity, pan, tilt, strobe, and gobo selection are all shader-side.

In Unity 6.2+ with URP's **Forward+** rendering path, realtime lights are no longer a bottleneck in the same way. Forward+ moves per-tile light assignment to a GPU compute pass, allowing hundreds of per-pixel lights with minimal CPU overhead. This creates an opportunity to use VRSL's mature DMX control infrastructure to drive genuine Unity `Light` components — and eventually, to bypass `Light` components entirely and inject lighting contributions directly into the render pipeline from the GPU.

---

## The Original Architecture (Shader Path)

```
Artnet/OSC
    │
    ▼
GridReader.cs
  Receives OSC packets, decodes 0-255 values → 0.0-1.0 floats,
  writes to a CPU-side buffer then uploads to _DataBuffer RenderTexture
    │
    ▼
CRT Shader Chain (GPU, per-frame)
  DMXInterpolation.shader    — smooths incoming pixel values
  StrobeTimings.shader       — computes strobe phase
  StrobeOutput.shader        — converts phase to binary gate
  SpinnerTimer.shader        — computes gobo spin phase
    │
    ▼
Global Shader Textures (set via VRCShader.SetGlobalTexture)
  _Udon_DMXGridRenderTexture          — main channels (intensity, colour, gobo, motor)
  _Udon_DMXGridRenderTextureMovement  — movement channels (pan, tilt)
  _Udon_DMXGridStrobeOutput           — strobe gate
  _Udon_DMXGridSpinTimer              — gobo spin phase
    │
    ▼
Fixture Shaders (HLSL, per-fragment)
  VRStageLighting_DMX_Static.cs pushes only config via MaterialPropertyBlock:
    _DMXChannel, _EnableDMX, _MaxMinPanAngle, etc.
  Shaders sample the global textures at runtime using
    getValueAtCoords() from VRSL-DMXFunctions.cginc
    to decode colour, intensity, pan, tilt, strobe per fragment
```

The channel layout within each 13-channel fixture block (relative to the fixture's base absolute channel):

| Offset | Channel         | Source Texture          |
|--------|-----------------|-------------------------|
| +0     | Pan coarse      | Movement texture        |
| +1     | Pan fine        | Movement texture        |
| +2     | Tilt coarse     | Movement texture        |
| +3     | Tilt fine       | Movement texture        |
| +4     | Motor speed / cone width | Main texture  |
| +5     | Dimmer / intensity | Main texture         |
| +6     | Strobe gate     | Strobe output texture   |
| +7     | Red             | Main texture            |
| +8     | Green           | Main texture            |
| +9     | Blue            | Main texture            |
| +10    | Gobo spin speed | Main texture + Spin timer |
| +11    | Gobo selection  | Main texture            |
| +12    | (spare / tilt fine duplicate) | — |

The absolute channel number passed to the shader is computed by `RawDMXConversion()`:

```csharp
int absChannel = dmxChannel + (dmxUniverse - 1) * 512 + (dmxUniverse - 1) * 8;
```

The `+8` per universe accounts for inter-universe spacing in the grid texture layout.

### The UV Sampling Problem

`getValueAtCoords()` in `VRSL-DMXFunctions.cginc` converts an absolute channel number to UV coordinates on the DMX grid texture using the `IndustryRead` path:

```glsl
float resMultiplierX = textureWidth / 13.0;        // pixels per 13-channel block
float u = (x  * resMultiplierX / textureWidth)  - 0.015;
float v = ((y + 1.0) * resMultiplierX / textureHeight) - 0.001915;
```

Where:
- `x = channel % 13` (1–13; the 13th channel wraps to 13 instead of 0)
- `y = floor(channel / 13)` with a correction when `channel` is an exact multiple of 13

The small constant offsets (`-0.015`, `-0.001915`) are empirical alignment corrections baked into the original shader. Porting to CPU or a compute shader requires replicating these exactly.

---

## Phase 1 — CPU Bridge Path

### Motivation

The simplest way to drive Unity `Light` components from VRSL's DMX data is to read the GPU textures back to CPU each frame and apply the decoded values to `light.color` and `light.intensity`. This avoids writing any rendering pipeline code.

### Components

#### `VRSLDMXReader.cs` (VRSL.Core assembly)

A singleton `MonoBehaviour` that issues one `AsyncGPUReadback.Request` per DMX texture per frame. When each request completes (1–2 frames later), the `Color[]` pixel cache is updated.

`AsyncGPUReadback` was chosen over the synchronous `ReadPixels` approach because:
- It does not stall the GPU pipeline
- The latency (1–2 frames) is imperceptible for stage lighting
- A single readback feeds any number of fixture scripts with no additional GPU cost

The `SampleChannel` static method is a direct C# port of `getValueAtCoords()`:

```csharp
static float SampleChannel(int dmxChannel, Color[] pixels, int w, int h)
{
    int x = dmxChannel % 13;
    if (x == 0) x = 13;

    float y = dmxChannel % 13 == 0
        ? dmxChannel / 13f - 1f
        : dmxChannel / 13f;

    // Correction table for channel 13 in specific ranges (matches CGINC exactly)
    if (x == 13)
    {
        if (dmxChannel >= 90   && dmxChannel <= 101) y -= 1f;
        // ... (four more range corrections)
    }

    float resMultX = w / 13f;
    float u = (x * resMultX / w) - 0.015f;
    float v = ((y + 1f) * resMultX / h) - 0.001915f;

    int px = Mathf.Clamp(Mathf.FloorToInt(u * w), 0, w - 1);
    int py = Mathf.Clamp(Mathf.FloorToInt(v * h), 0, h - 1);

    Color c = pixels[py * w + px];
    return c.r * 0.2126f + c.g * 0.7152f + c.b * 0.0722f; // LinearRgbToLuminance
}
```

Point sampling (floor rather than bilinear) is used because each texel encodes exactly one DMX channel value; interpolating between adjacent channels would be incorrect.

#### `VRStageLighting_DMX_RealtimeLight.cs` (VRSL.Core assembly)

A per-fixture `MonoBehaviour` that mirrors the DMX addressing fields of `VRStageLighting_DMX_Static` (same field names and defaults for easy migration) but targets a Unity `Light` component instead of shader properties.

Each `LateUpdate`, it samples `VRSLDMXReader.Instance` for:
- Dimmer (`absChannel + 5`) → `light.intensity`
- R/G/B (`absChannel + 7/8/9`) → `light.color`
- Strobe gate (`absChannel + 6`) → multiplied into intensity (0 or 1)
- Pan/Tilt (`absChannel + 0/2`) → applied to `panTransform.localRotation` / `tiltTransform.localRotation` via `Quaternion.Euler`

### Limitations of the CPU Path

The CPU bridge path works for small to medium fixture counts but has two fundamental bottlenecks:

1. **GPU→CPU readback**: While async, it still transfers texture data across the PCIe bus every frame. At high fixture counts the transfer bandwidth compounds.
2. **`Light` component writes are main-thread only**: Each fixture's `light.color` and `light.intensity` write must happen on the main thread in `LateUpdate`, sequentially. There is no Unity API to drive `Light` properties from a job or from GPU data directly.

These limits make the CPU path unsuitable for large fixture counts (>64) or for projects targeting the highest possible frame rates.

---

## Phase 2 — GPU-Driven Pipeline

### Design Goals

- Zero `AsyncGPUReadback` latency (all DMX decoding runs on GPU)
- No `Light` component writes from CPU
- Scale to 256+ fixtures without growing CPU cost
- Integrate cleanly with the Unity 6 URP Render Graph API
- Leave the VRSL.Core assembly and VRChat builds unaffected

### Architecture

```
DMX RenderTextures (already on GPU from CRT chain)
  _Udon_DMXGridRenderTexture
  _Udon_DMXGridRenderTextureMovement
  _Udon_DMXGridStrobeOutput
         │
         │  (no CPU readback)
         ▼
[COMPUTE PASS — VRSLDMXLightUpdate.compute]
  64 threads/group, 1 group per 64 fixtures
  Per thread (= per fixture):
    • Reads fixture config from StructuredBuffer<VRSLFixtureConfig>
    • Samples DMX channels via ported getValueAtCoords() using SampleLevel()
    • Decodes colour, intensity, strobe gate
    • Applies Rodrigues pan/tilt rotation (moving heads only)
    • Writes VRSLLightData to RWStructuredBuffer<VRSLLightData>
         │
         ▼
  GraphicsBuffer<VRSLLightData>  (GPU-resident, persistent)
         │
         │  (no CPU involvement)
         ▼
[RASTER PASS — VRSLDeferredLighting.shader, AFTER OPAQUES]
  Full-screen triangle (3 vertices, no VBO)
  Blend One One  (additive)
  Per pixel:
    • Reconstruct world position from _CameraDepthTexture
    • Sample surface normal from _CameraNormalsTexture
    • Loop over all N lights in the StructuredBuffer
    • Accumulate: distance attenuation × cone attenuation × NdotL × colour × intensity
    • Output is added to the existing frame colour
```

### GPU Data Structs

Two GPU structs are defined in `VRSLLightingLibrary.hlsl` and matched by C# structs (using `[StructLayout(LayoutKind.Sequential)]`) in `VRSL_GPULightManager.cs`. They must remain bit-for-bit identical.

**VRSLFixtureConfig** (112 bytes, 7 × `float4`) — written from CPU once per config change:

| Field | Contents |
|---|---|
| `positionAndRange` | xyz = world position, w = attenuation range |
| `forwardAndType` | xyz = base forward direction, w = light type (0=spot, 1=point) |
| `upAndMaxIntensity` | xyz = pan rotation axis (world Y by default), w = max intensity scalar |
| `spotAngles` | x = inner half-angle (deg), y = outer half-angle (deg), z = finalIntensity cap |
| `dmxChannel` | x = absolute DMX channel, y = enableStrobe, z = enablePanTilt, w = enableFineChannels |
| `panSettings` | x = maxMinPan, y = panOffset, z = invertPan (0/1) |
| `tiltSettings` | x = maxMinTilt, y = tiltOffset, z = invertTilt (0/1) |

**VRSLLightData** (64 bytes, 4 × `float4`) — written by compute, read by fragment:

| Field | Contents |
|---|---|
| `positionAndRange` | xyz = world position, w = range |
| `directionAndType` | xyz = normalised direction (spot), w = type (0=spot, 1=point) |
| `colorAndIntensity` | xyz = linear RGB, w = combined intensity (dimmer × max × final × strobe) |
| `spotCosines` | x = cos(innerHalfAngle), y = cos(outerHalfAngle), z = active flag (0 = skip) |

Packing all data into `float4` fields avoids HLSL StructuredBuffer alignment ambiguities with `float3` and matches C#'s `Vector4` layout exactly.

### VRSLDMXLightUpdate.compute

The compute shader is the heart of the GPU path. Key design decisions:

**Thread group size of 64** maps well to GPU wavefront/warp widths (32 on NVIDIA, 64 on AMD). The C# dispatch is:
```csharp
cmd.DispatchCompute(cs, kernel, Mathf.CeilToInt(fixtureCount / 64f), 1, 1);
```

**DMX sampling** uses `Texture2D.SampleLevel` with a `sampler_point_clamp` inline sampler, which matches `tex2Dlod` with point filtering in the original fixture shaders. The UV calculation is identical to the CGINC:
```hlsl
float resMultX = _VRSLDMXTexelSize.z / 13.0;
float u = (x  * resMultX * _VRSLDMXTexelSize.x) - 0.015;
float v = ((y + 1.0) * resMultX * _VRSLDMXTexelSize.y) - 0.001915;
float4 c = tex.SampleLevel(sampler_point_clamp, float2(saturate(u), saturate(v)), 0);
return c.r * 0.2126 + c.g * 0.7152 + c.b * 0.0722;
```

All three DMX textures are assumed to share the same resolution (true of all VRSL CRT configurations). A single `_VRSLDMXTexelSize` `float4` covers all three.

**Pan/Tilt rotation** uses Rodrigues' rotation formula, which rotates a vector around an arbitrary axis by an angle without constructing a full rotation matrix:
```hlsl
float3 RotateAroundAxis(float3 v, float3 axis, float angleDeg)
{
    float rad = radians(angleDeg);
    float c = cos(rad); float s = sin(rad);
    return v * c + cross(axis, v) * s + axis * dot(axis, v) * (1.0 - c);
}
```

Pan is applied first (rotating around the world-up pan axis), then tilt is applied around the resulting right vector. This matches how a real moving head fixture mechanically works.

**The active flag** (`spotCosines.z`) is set to 0 when the combined intensity is below 0.001. The lighting shader checks this flag and skips the light early, avoiding unnecessary math for dark fixtures.

### VRSLDeferredLighting.shader

This is a standard URP fullscreen additive pass using the full-screen triangle technique: the vertex shader generates a triangle from `SV_VertexID` that covers the entire screen without a vertex buffer.

```hlsl
Varyings vert(uint vertexID : SV_VertexID)
{
    Varyings o;
    o.uv         = float2((vertexID << 1) & 2, vertexID & 2);
    o.positionCS = float4(o.uv * 2.0 - 1.0, 0.0, 1.0);
    return o;
}
```

**World position reconstruction** uses URP's built-in helper, which handles reversed-Z and platform differences automatically:
```hlsl
float  rawDepth = SampleSceneDepth(uv);
float3 posWS    = ComputeWorldSpacePosition(uv, rawDepth, UNITY_MATRIX_I_VP);
```

Sky pixels (depth at the far plane) are discarded early to avoid wasting ALU on background fragments.

**Normals** come from `_CameraNormalsTexture` via URP's `DeclareNormalsTexture.hlsl` include. This requires **Depth Normals Prepass** enabled in the URP Renderer asset.

**Light evaluation** follows URP's own attenuation model for compatibility:
```hlsl
// Distance attenuation — matches URP GetDistanceAttenuation
float VRSL_DistanceAttenuation(float distSq, float range)
{
    float d2 = distSq / (range * range);
    float f  = saturate(1.0 - d2 * d2);          // smooth range cutoff
    return (f * f) / max(distSq, 0.0001);          // inverse-square falloff
}

// Spot cone attenuation — matches URP GetAngleAttenuation
float VRSL_SpotAttenuation(float3 lightDir, float3 toLight, float cosInner, float cosOuter)
{
    float cosAngle = dot(-lightDir, normalize(toLight));
    float t = saturate((cosAngle - cosOuter) / max(cosInner - cosOuter, 0.0001));
    return t * t;                                   // squared for smooth falloff
}
```

**Blending** is `Blend One One` (additive). No depth write or depth test occurs — this pass adds light energy on top of the fully-rendered opaque scene.

### VRSL_GPULightManager.cs

The manager is responsible for the CPU-side of the GPU pipeline:

1. **Buffer creation**: Two `GraphicsBuffer`s with `GraphicsBuffer.Target.Structured` are created on `OnEnable`/`RefreshFixtures`. Stride is computed via `Marshal.SizeOf<T>()` to guarantee it matches the C# struct layout.

2. **RTHandle management**: The three `RenderTexture` references are wrapped in `RTHandle`s via `RTHandles.Alloc()` so the render graph can import them as `TextureHandle`s. RTHandles are released in `OnDisable`.

3. **Config upload**: `FixtureConfigBuffer.SetData(configs)` is called in `LateUpdate` only when `_configDirty` is true. Fixture positions are sampled from `realtimeLight.transform.forward` and `realtimeLight.spotAngle` so they stay in sync with any `Light` component placed in the scene.

4. **`FindObjectsByType`**: Used in `RefreshFixtures()` to auto-discover all `VRStageLighting_DMX_RealtimeLight` components. Call this again at runtime if fixtures are added or removed dynamically.

### VRSLRealtimeLightFeature.cs

Implements `ScriptableRendererFeature` using the **Unity 6 Render Graph API** (`RecordRenderGraph`). The legacy `Execute` path is not used.

**Two passes** are enqueued:

| Pass | Event | Description |
|---|---|---|
| `ComputePass` | `BeforeRenderingOpaques` | Dispatches the compute shader. Running before opaques means shadow-casting code can theoretically query the light buffer, though that requires additional shadow map integration not included here. |
| `LightingPass` | `AfterRenderingOpaques` | Runs the fullscreen additive shader. Running after opaques means the depth and normals textures are fully populated. Transparents are not lit by this pass. |

**Render graph resource handling**:

External `GraphicsBuffer`s are imported with `renderGraph.ImportBuffer()`. External `RenderTexture`s (the DMX CRT outputs) are imported via their `RTHandle` wrappers with `renderGraph.ImportTexture()`. All resources declare their access mode (`AccessFlags.Read` / `AccessFlags.Write`) so the render graph can insert correct barriers.

The `PassData` struct pattern (a class allocated per pass, captured by the `SetRenderFunc` lambda) is how the Unity 6 render graph passes data into the execution function while remaining compatible with render graph compilation and pass culling.

**Null guards**: Both passes check `VRSL_GPULightManager.Instance` and early-return if it is missing. The `LightingPass` additionally checks `resources.cameraNormalsTexture.IsValid()` and emits a `Debug.LogWarning` if the depth normals prepass is not configured.

### Assembly Isolation (VRSL.GPU.asmdef)

The GPU scripts (`VRSL_GPULightManager` and `VRSLRealtimeLightFeature`) live in `Runtime/Scripts/GPU/` under their own assembly definition, separate from `VRSL.Core`. This is necessary because:

- `VRSL.Core` is used by VRChat projects that do not have URP installed
- Adding `Unity.RenderPipelines.Universal.Runtime` to `VRSL.Core` would break those builds
- A separate asmdef with `defineConstraints: ["VRSL_URP"]` and a matching `versionDefines` entry means the entire assembly is only compiled when URP 14.0+ is present:

```json
"versionDefines": [{
    "name": "com.unity.render-pipelines.universal",
    "expression": "14.0",
    "define": "VRSL_URP"
}],
"defineConstraints": ["VRSL_URP"]
```

`VRSL.Core` declares no URP dependency and continues to compile in all environments.

---

## Setup Guide (Unity 6.2+ URP)

### URP Renderer Asset

1. Open the URP Renderer asset used by your camera.
2. Enable **Depth Priming Mode** → `Disabled` (required for depth normals prepass to work in Forward+).
3. Enable **Depth Normals Prepass** (the `VRSLDeferredLighting` shader requires `_CameraNormalsTexture`).
4. Under **Renderer Features**, click **Add Renderer Feature** and select **VRSLRealtimeLightFeature**.
5. Set the **Rendering Path** on the URP asset to **Forward+** for GPU-driven light culling.

### Scene Setup

1. Add a **VRSL_GPULightManager** component to any persistent scene GameObject (e.g. the lighting rig root).

2. Assign the three VRSL `CustomRenderTexture` assets:

   | Field | Asset to assign |
   |---|---|
   | `dmxMainTexture` | The CRT that outputs `_Udon_DMXGridRenderTexture` |
   | `dmxMovementTexture` | The CRT that outputs `_Udon_DMXGridRenderTextureMovement` |
   | `dmxStrobeTexture` | The CRT that outputs `_Udon_DMXGridStrobeOutput` |

3. Assign `VRSLDMXLightUpdate` (the compute shader asset) and `VRSLDeferredLighting` (the shader asset) to the manager.

4. For each physical light fixture in the scene, add a **VRStageLighting_DMX_RealtimeLight** component. Configure:
   - `dmxChannel` / `dmxUniverse` — matching your Artnet/DMX patch
   - `maxIntensity` — the lux value at DMX full-on (tune to taste for your scene scale)
   - `realtimeLight` — the Unity `Light` component on this fixture (spot or point). The manager reads `range`, `spotAngle`, and `innerSpotAngle` from it once for configuration; the `Light` component is not updated at runtime in the GPU path.
   - For moving heads: enable `enablePanTilt`, assign `panTransform` and `tiltTransform`.

5. Press Play. `VRSL_GPULightManager.RefreshFixtures()` runs on `OnEnable`, discovers all fixture components, and creates the GPU buffers automatically.

### Runtime Fixture Changes

If you change a fixture's `dmxChannel`, `maxIntensity`, or any other config field at runtime, call:
```csharp
VRSL_GPULightManager.Instance.MarkConfigDirty();
```

If you add or remove fixture GameObjects at runtime, call:
```csharp
VRSL_GPULightManager.Instance.RefreshFixtures();
```

---

## Choosing Between the CPU and GPU Paths

Both paths are included and can coexist. The `VRStageLighting_DMX_RealtimeLight` component is shared between them.

| | CPU Path | GPU Path |
|---|---|---|
| **Requires** | `VRSLDMXReader` in scene | `VRSL_GPULightManager` + renderer feature |
| **Unity `Light` component** | Driven from CPU each frame | Read once for config; not updated at runtime |
| **Fixture count limit** | ~64 before CPU cost is noticeable | 256+ with negligible CPU cost |
| **Latency** | 1–2 frames (AsyncGPUReadback) | 0 frames (all GPU) |
| **Depth Normals Prepass** | Not required | Required |
| **URP version** | Any (AsyncGPUReadback is core Unity) | URP 14.0+ (Unity 2022.2 / Unity 6) |
| **VRChat compatible** | Yes (VRSL.Core assembly) | No (VRSL.GPU assembly, URP only) |
| **Shadow casting** | Yes (Unity handles it) | Not supported without additional shadow integration |

Note that neither path illuminates **transparent geometry** — the additive pass in the GPU path runs after opaques. Transparent fixtures (e.g. haze, glass panels) would need a separate pass or continued use of the original VRSL volumetric shader path for visual representation.

---

## File Reference

| File | Assembly | Description |
|---|---|---|
| `Runtime/Scripts/VRSLDMXReader.cs` | VRSL.Core | AsyncGPUReadback manager; CPU-side DMX texture cache |
| `Runtime/Scripts/VRStageLighting_DMX_RealtimeLight.cs` | VRSL.Core | Per-fixture config + CPU light driver |
| `Runtime/Scripts/GPU/VRSL_GPULightManager.cs` | VRSL.GPU | Singleton; buffer creation, fixture config upload, RTHandle management |
| `Runtime/Scripts/GPU/VRSLRealtimeLightFeature.cs` | VRSL.GPU | URP ScriptableRendererFeature; schedules compute + lighting passes |
| `Runtime/Scripts/GPU/VRSL.GPU.asmdef` | — | Assembly definition; isolates URP dependency from VRSL.Core |
| `Runtime/Shaders/Shared/VRSLLightingLibrary.hlsl` | — | HLSL structs (`VRSLFixtureConfig`, `VRSLLightData`) + light evaluation functions |
| `Runtime/Shaders/Compute/VRSLDMXLightUpdate.compute` | — | Compute shader; DMX decode + pan/tilt + buffer write |
| `Runtime/Shaders/VRSLDeferredLighting.shader` | — | Fullscreen additive lighting pass shader |
