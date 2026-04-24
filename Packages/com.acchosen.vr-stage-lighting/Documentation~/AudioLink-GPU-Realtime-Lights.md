# VRSL AudioLink GPU Realtime Lights — Implementation Guide

This document describes the design and implementation of extending VRSL's GPU realtime light pipeline to support AudioLink as a data source, complementing the existing DMX path documented in `URP-Realtime-Lights.md`. It is intended as a reference for contributors and for anyone integrating AudioLink-driven scene illumination into a Unity 6 URP project.

---

## Requirements

| | Minimum |
|---|---|
| Unity | **6.0 LTS** or later |
| Universal Render Pipeline | **17.0** or later (ships with Unity 6) |
| URP Rendering Path | **Forward+** |
| URP Renderer feature | **Depth Normals Prepass** enabled, **Depth Priming Mode** set to `Disabled` |
| AudioLink | Installed and active in the scene (sets `_AudioTexture` as a global RenderTexture each frame) |

Same Unity/URP version floor as the DMX GPU light path — both features depend on the Unity 6 Render Graph API, and the package's `VRSL.GPU` assembly is skipped automatically when URP isn't present. The AudioLink path does **not** require the DMX CRT chain; scenes can run AudioLink-only without GridReader or an Artnet source.

---

## Background

The DMX GPU realtime light path (see `URP-Realtime-Lights.md`) solved the problem of driving genuine scene illumination from VRSL fixture data without touching the CPU per-frame. Its data source is the DMX CRT texture chain — a pipeline that only exists when an Artnet/OSC signal is present and GridReader is active.

AudioLink fixtures are the other major fixture family in VRSL. They share prefab structure and shader conventions with the DMX family but are driven by an entirely different data source: the AudioLink `_AudioTexture` global RenderTexture, which encodes real-time audio frequency analysis, beat detection, theme colors, and color chord data. AudioLink fixtures are self-contained — they require no external DMX controller, no GridReader, and no CRT chain — making them the default choice for scenes where stage lighting should react to music rather than external control.

Like the DMX fixtures, the original AudioLink fixture is built from three meshes drawn alongside the body: a **volumetric cone** (the audio-reactive beam shaft in haze), a **projection disc** (a stylised gobo footprint quad), and the **fixture emissive** (the lit lamp body). All three react to AudioLink in real time — but none of them illuminate scene geometry. A wash light pulsing on bass beats would visibly pulse its volumetric cone but the floor beneath it would not change.

The goal of this work is to drive the same `VRSLDeferredLighting.shader` fullscreen additive pass used by the DMX path from AudioLink data instead, preserving the GPU-resident, zero-CPU-per-frame-per-fixture architecture (with one small CPU bookkeeping cost per frame for animated transforms; see *Why the DMX GPU Path Cannot Be Directly Reused for AudioLink* below). The new path complements rather than replaces the existing volumetric stack:

- **Volumetric cone meshes are kept** and run alongside the GPU pass — they remain the right tool for the visible audio-reactive beam-in-haze effect and the fixture's emissive lamp glow.
- **The projection-disc mesh is superseded by the GPU pass.** The deferred shader's `SampleGobo` projects the gobo per-pixel onto every surface inside the light cone using the proper attenuation model. The projection-disc GameObject is removed from every AudioLink GPU fixture prefab.

---

## The Original AudioLink Architecture (Volumetric Shader Path)

Understanding how the existing AudioLink shader path works is essential context before reading about the GPU extension.

```
AudioLink (external Unity package)
    │
    ▼
AudioLink RenderTexture (_AudioTexture — set as global via Shader.SetGlobalTexture)
  128×64 px, updated every frame by the AudioLink system
  Contains: amplitude bands, beat data, theme colors, color chord, waveform, DFT
    │
    ▼
Fixture Shaders (HLSL, per-fragment, every pixel of every fixture mesh)
  VRStageLighting_AudioLink_Static.cs pushes static config via MaterialPropertyBlock:
    _Band, _Delay, _BandMultiplier, _EnableColorChord, _Emission, etc.
  Shaders call AudioLinkLerp() / AudioLinkData() from AudioLink.cginc each fragment
    to decode amplitude for intensity, theme/chord color for emission
  Volumetric cone, projection disc, and fixture emissive are drawn as
    instanced meshes — pan/tilt driven by _FixtureRotationX / _FixtureBaseRotationY
    instance properties, which are set by animations or scripted movers
```

### The AudioLink Texture Layout

The AudioLink texture is a 128×64 RenderTexture updated every frame by the AudioLink system. Key regions (pixel coordinates match `AudioLink.cginc` defines):

| Region | Pixel coords | Contents |
|--------|-------------|----------|
| Amplitude bands | `x=0..127, y=0..3` | Per-band amplitude history. `y=0` bass, `y=1` low-mids, `y=2` high-mids, `y=3` treble. `x=0` is most recent; `x=127` is most delayed. |
| Theme Color 0–3 | `x=0..3, y=23` | Four user-assignable palette colors |
| Color Chord strip | `x=0..127, y=25` | Real-time chord color sequence |
| Filtered AudioLink | `x=0..15, y=28..31` | Smoothed/filtered band amplitudes |
| Chronotensity | `x=16..23, y=28..31` | Monotonically increasing timers per band |

The key difference from the DMX texture is that the AudioLink texture does **not** contain any directional data. There is no pan or tilt channel. In the volumetric shader path, fixture direction is entirely controlled by the GameObject transforms — the `panTransform` is rotated on its Y axis and the `tiltTransform` on its X axis, either by an animator or by scripts reacting to music. This design choice has a significant consequence for the GPU path, discussed below.

### What the Volumetric Path Gives You (and What It Doesn't)

The AudioLink volumetric path produces the same category of results as the DMX volumetric path: convincing beam shafts, gobo projections, strobe effects, and fixture emissive glow that reacts in real time to music. The fundamental limitation is identical — none of this illuminates anything. The floor beneath a pulsing bass wash light receives no additional light contribution.

---

## Why the DMX GPU Path Cannot Be Directly Reused for AudioLink

The DMX GPU path (`VRSLDMXLightUpdate.compute`) reads fixture direction from the DMX movement texture — pan and tilt channel values that are decoded and converted to a world-space direction using Rodrigues' rotation formula, entirely on the GPU. No CPU involvement is required for direction per frame.

AudioLink provides no directional data. Pan and tilt for AudioLink movers are driven by GameObject transforms that are animated by the Unity animation system or scripted. The key constraint is therefore:

**The world-space forward direction for each AudioLink fixture is only known on the CPU, not in any GPU texture.**

This changes one architectural assumption of the DMX path while leaving everything else intact:

| Aspect | DMX GPU path | AudioLink GPU path |
|--------|-------------|-------------------|
| Intensity source | DMX texture, dimmer channel | AudioLink texture, amplitude band |
| Color source | DMX texture, RGB channels | AudioLink texture, emission/theme/chord |
| Direction source | Computed in compute shader from DMX pan/tilt channels + Rodrigues rotation | Supplied from CPU each frame (tiltTransform.forward) |
| Config upload frequency | Once at setup, re-upload only when config changes | **Every frame** — animated transforms update direction continuously |
| Strobe | Dedicated DMX strobe channel | Not applicable — AudioLink has no strobe channel |
| Fine channels | Optional 16-bit pan/tilt via +1/+3 channels | Not applicable |

The consequence of per-frame config uploads is a small but real CPU cost: `N × 112 bytes` uploaded to the GPU each frame as a `GraphicsBuffer.SetData` call. For 100 fixtures that is 10.9 KB per frame, well within acceptable `SetData` overhead. The compute shader work itself (sampling `_AudioTexture`, writing `VRSLLightData`) is unchanged in cost model from the DMX path.

---

## The GPU-Driven AudioLink Pipeline

### Design Goals

- All per-frame amplitude and color sampling runs in a compute shader, directly reading `_AudioTexture`
- Direction is supplied CPU-side from animated transforms, uploaded to the config buffer every frame
- Light data lives in a GPU-resident `GraphicsBuffer` — never read back to CPU
- The existing fullscreen `VRSLDeferredLighting.shader` is reused without modification
- The volumetric cone meshes and emissive fixture body shaders are untouched — both run alongside the GPU pass, providing the visible beam shaft and lamp glow
- The projection-disc mesh is replaced by the GPU pass's per-pixel gobo projection on illuminated surfaces and is dropped from the AudioLink GPU fixture prefabs
- `VRSL.Core` remains unchanged; no URP dependency introduced into the base assembly
- VRChat builds are unaffected (`VRSL.GPU.asmdef` only compiles when URP ≥17.0 is present)

### Full Pipeline Architecture

```
AudioLink RenderTexture (_AudioTexture)
  Set globally each frame by the AudioLink system
         │
         │  VRSL_AudioLinkGPULightManager reads Shader.GetGlobalTexture("_AudioTexture"),
         │  wraps in RTHandle, imports into render graph
         │
         │  VRSL_AudioLinkGPULightManager reads animated fixture transforms each frame,
         │  writes VRSLALFixtureConfig StructuredBuffer (112 bytes × N fixtures)
         │
         ▼
[COMPUTE PASS — VRSLAudioLinkLightUpdate.compute]
[RenderPassEvent: BeforeRenderingOpaques]
  Dispatch: ceil(fixtureCount / 64) groups × 64 threads
  Per thread = per fixture:
    • Read VRSLALFixtureConfig from StructuredBuffer (CPU-written, GPU-read)
    • Read amplitude: _AudioTexture.Load(int3(delay, band, 0)).r × bandMultiplier
    • Read color based on colorMode:
        – Emission: read cfg.emissionColor.rgb
        – ThemeColor0–3: _AudioTexture.Load(int3(colorIndex, 23, 0)).rgb
        – ColorChord: _AudioTexture.Load(int3(0, 25, 0)).rgb
    • Combine intensity: amplitude × maxIntensity × finalIntensity
    • Normalise direction: normalize(cfg.forwardAndType.xyz) [pre-supplied from CPU]
    • Precompute spot cosines from config angles
    • Write VRSLLightData to RWStructuredBuffer (GPU-written, GPU-read)
         │
         ▼
  GraphicsBuffer<VRSLLightData>  (persistent, GPU-resident, 80 bytes × N fixtures)
         │
         │  imported into render graph as BufferHandle
         ▼
[RASTER PASS — VRSLDeferredLighting.shader]
[RenderPassEvent: AfterRenderingOpaques]
  (Identical to DMX path — see URP-Realtime-Lights.md for detail)
  Full-screen triangle, Blend One One
  Reads _VRSLLights / _VRSLLightCount globals set by the lighting pass
  Reconstructs world position from depth, reads normals from _CameraNormalsTexture
  Evaluates all N lights per pixel, accumulates, writes additively to colour target
```

### Direction: Why CPU-Side Transform Reads Are Correct Here

The DMX GPU path avoids CPU reads by computing direction entirely in the compute shader. The AudioLink path cannot do this — but that does not mean it is wrong to read transforms on the CPU.

The critical observation is that pan and tilt for AudioLink movers are already being driven by the CPU. The Unity animation system evaluates the animator each frame and writes the resulting bone transforms to main-thread memory before `LateUpdate`. `VRSL_AudioLinkGPULightManager.UploadFixtureConfigs()` runs in `LateUpdate`, after animation, so it always reads the final animated world position and forward direction for that frame. There is no additional CPU work being introduced — the transform data exists and is current; the manager simply reads it and packages it into a `GraphicsBuffer.SetData` call.

This is meaningfully different from the CPU bottleneck described in `URP-Realtime-Lights.md` for the naive Unity `Light` component approach. That approach required a GPU→CPU readback (AudioLink texture readback) followed by CPU Light property writes. Here there is no GPU→CPU readback at all: the AudioLink texture stays on the GPU and is sampled directly in the compute shader. The only CPU work is reading N transforms that are already in main-thread memory — a tight loop of `tiltTransform.forward` calls.

### AudioLink Texture Sampling in Compute

AudioLink.cginc defines helper functions (`AudioLinkData`, `AudioLinkLerp`) for use in fragment shaders. These use `UNITY_ACCESS_INSTANCED_PROP` and URP/BIRP-specific macros that are not available in compute shaders. The compute shader accesses `_AudioTexture` directly via integer-coordinate `Load()` calls:

```hlsl
// Amplitude — x=delay (0..127), y=band (0..3)
float amplitude = _AudioTexture.Load(int3(delay, band, 0)).r;

// Theme color — x=colorIndex (0..3), y=23
float3 themeColor = _AudioTexture.Load(int3(colorIndex, 23, 0)).rgb;

// Color Chord representative pixel — y=25, x=0
float3 chordColor = _AudioTexture.Load(int3(0, 25, 0)).rgb;
```

Point sampling (`.Load()`) is used rather than `SampleLevel` with a bilinear sampler. Unlike the DMX texture where adjacent texels encode adjacent channels and bilinear sampling would corrupt values, the AudioLink texture also stores discrete data (one value per texel in most regions) — point sampling is correct and consistent with `AudioLinkData(coords)` in fragment shaders.

The `.r` channel is read for amplitude because `AudioLinkLerp` returns the `.r` component of the amplitude band texel. The luminance conversion used in the DMX path (`c.r * 0.2126 + ...`) is not needed — band amplitude is a scalar already stored in the red channel.

### GPU Data Structs

**VRSLALFixtureConfig** (112 bytes, 7 × `float4`) — written from CPU every frame:

| Field | Contents |
|-------|----------|
| `positionAndRange` | xyz = world position (from panTransform or light), w = attenuation range |
| `forwardAndType` | xyz = world forward direction (from tiltTransform or light, animated), w = light type (0=spot, 1=point) |
| `intensityParams` | x = maxIntensity, y = finalIntensity, z = AudioLink active flag (1 = sample AL amplitude, 0 = static full intensity), w = unused |
| `spotAngles` | x = inner half-angle (deg), y = outer half-angle (deg), zw = unused |
| `alParams` | x = band (0–3), y = delay (0–127), z = bandMultiplier, w = colorMode (0–5) |
| `emissionColor` | xyz = linear RGB (used when colorMode == 0), w = unused |
| `reserved` | x = gobo array index (−1 = no gobo, 0+ = slice in `_VRSLGobos`), y = gobo spin speed (matches `_SpinSpeed` range −10..10), zw = unused |

The 7×float4 stride matches `VRSLFixtureConfig` intentionally. Future work unifying the two paths into a single buffer can rely on consistent struct sizes.

**VRSLLightData** (80 bytes, 5 × `float4`) — written by the compute shader each frame, read by the fragment shader. This struct is identical to the DMX path and is defined once in `VRSLLightingLibrary.hlsl`.

**colorMode values:**

| Value | Meaning |
|-------|---------|
| 0 | Fixed emission color (`cfg.emissionColor.rgb`) |
| 1 | AudioLink Theme Color 0 (`x=0, y=23`) |
| 2 | AudioLink Theme Color 1 (`x=1, y=23`) |
| 3 | AudioLink Theme Color 2 (`x=2, y=23`) |
| 4 | AudioLink Theme Color 3 (`x=3, y=23`) |
| 5 | AudioLink Color Chord (`x=0, y=25`) |

### VRSL_AudioLinkGPULightManager — Key Differences from VRSL_GPULightManager

The AudioLink manager follows the same singleton pattern and renderer feature handshake as `VRSL_GPULightManager` but with three structural differences:

**1. Config upload frequency.**
`VRSL_GPULightManager` uploads the fixture config buffer once in `RefreshFixtures()` and re-uploads only when `MarkConfigDirty()` is called. `VRSL_AudioLinkGPULightManager` calls `UploadFixtureConfigs()` unconditionally in `LateUpdate()`. Animated fixture transforms change every frame; a dirty flag would need to be set every frame anyway and adds complexity for no savings.

**2. No DMX RenderTexture references.**
The DMX manager holds explicit references to the three CRT outputs (`dmxMainTexture`, `dmxMovementTexture`, `dmxStrobeTexture`) assigned in the Inspector. The AudioLink manager has no texture fields — it discovers `_AudioTexture` from the global shader property via `Shader.GetGlobalTexture("_AudioTexture")`. The reference is polled each `LateUpdate` and an `RTHandle` is allocated or reallocated only when the underlying `RenderTexture` instance changes (e.g. AudioLink restarts or changes resolution).

**3. Direction source.**
`BuildConfig` in the DMX manager reads `realtimeLight.transform.forward` or `f.transform.up` for the base forward direction — a one-time value for the config. `BuildConfig` in the AudioLink manager calls `f.GetWorldForward()` which returns `tiltTransform.forward` when pan/tilt is enabled. Because this changes every frame, it is valid only at the moment `BuildConfig` is called during `LateUpdate` (after animation).

### VRSLAudioLinkRealtimeLightFeature — Render Graph Integration

The renderer feature uses the same Unity 6 Render Graph API (`RecordRenderGraph`) as `VRSLRealtimeLightFeature`. The lighting pass (`LightingPass`) is a verbatim duplicate — it reads `_VRSLLights` and `_VRSLLightCount`, which are written by whichever compute pass ran before it. The compute pass (`ComputePass`) differs only in its buffer and texture names:

| Parameter | DMX compute pass | AudioLink compute pass |
|-----------|-----------------|----------------------|
| Config buffer binding | `_FixtureConfigs` | `_ALFixtureConfigs` |
| Texture binding | `_DMXMainTex`, `_DMXMovementTex`, `_DMXStrobeTex` | `_AudioTexture` |
| Texel size uniform | `_VRSLDMXTexelSize` (float4) | not needed — `.Load()` uses integer coords |
| Direction computation | Rodrigues pan/tilt in shader | None — direction in config |

The `PassData` class per-pass pattern, `rg.ImportBuffer`, `rg.ImportTexture`, and `AccessFlags` declarations are identical in structure to the DMX feature. The AudioLink texture RTHandle is imported as `AccessFlags.Read`; the light data buffer as `AccessFlags.Write` in the compute pass and `AccessFlags.Read` in the lighting pass.

The lighting pass calls `cmd.SetGlobalBuffer` and `cmd.SetGlobalInteger` to publish `_VRSLLights` and `_VRSLLightCount` for the fragment shader. In Unity 6 URP the native render pass compiler prohibits global state mutation from raster passes by default. The builder must declare `builder.AllowGlobalStateModification(true)` before `SetRenderFunc`, or Unity throws `InvalidOperationException: Modifying global state from this command buffer is not allowed`. `ConfigureInput` must be called in `AddRenderPasses`, not `SetupRenderPasses` — the latter override does not exist on `ScriptableRendererFeature` in this Unity 6 URP version.

### Component Inheritance — Sibling AudioLink_Static

Every AudioLink GPU fixture prefab carries two components on the same
GameObject: the volumetric-side `VRStageLighting_AudioLink_Static` and the
GPU-path `VRStageLighting_AudioLink_RealtimeLight`. To spare scene authors
having to override parallel fields on both, the RealtimeLight now defers to a
sibling Static for every setting the two components can both express. The
Static's public property accessors (`EnableAudioLink`, `Band`, `Delay`,
`BandMultiplier`, `LightColorTint`, `SpinSpeed`, `SelectGOBO`) plus the public
`finalIntensity` field feed the `GetEffective*()` accessors on the
RealtimeLight:

| RealtimeLight field | Sibling Static source |
|---|---|
| `enableAudioLink` | `EnableAudioLink` |
| `band` | `Band` |
| `delay` | `Delay` |
| `bandMultiplier` | `BandMultiplier` |
| `finalIntensity` | `finalIntensity` |
| `emissionColor` | `LightColorTint` |
| `goboSpinSpeed` | `SpinSpeed` |
| `goboIndex` | `SelectGOBO` |

`VRSL_AudioLinkGPULightManager.BuildConfig()` calls those accessors instead of
reading fields directly, so overriding AudioLink reaction / colour / gobo
settings on the Static component at the scene level propagates to the GPU path
automatically. No sibling present → fallback to the RealtimeLight's own
fields, same as the DMX path.

### Gobo Spin Integration on the GPU

The DMX path integrates gobo spin phase via the `_Udon_DMXGridSpinTimer` CRT —
a texture that accumulates `t += dt · rate` across frames, which the compute
shader samples directly. AudioLink has no equivalent CRT chain, so phase is
integrated in the compute kernel itself.

`VRSLAudioLinkRealtimeLightFeature` passes `Time.timeSinceLevelLoad` into the
compute as the `_VRSLTime` uniform each frame. The kernel computes:

```hlsl
float spinPhase = fmod(spinSpeed * _VRSLTime * -0.34906585, 6.28318530718);
```

- The negative sign matches the volumetric fragment's
  `spinSpeed *= -goboSpinSpeed` convention so the GPU-projected gobo rotates
  the same direction as the visible stripe pattern in the volumetric cone.
- `0.34906585` is `π/9` — 2× the projection shader's `10 · spinSpeed` degrees
  rate, picked empirically to match the volumetric's visible rotation speed in
  typical scenes.
- `fmod` to `[-2π, 2π]` keeps the phase bounded so `sin`/`cos` in the deferred
  pass don't lose precision over long runs.

The phase is written to `goboAndSpin.y` and consumed directly as radians by
`SampleGobo` in `VRSLDeferredLighting.shader` — the deferred pass applies the
rotation without any further `_Time` multiplication, so rate changes never
retroactively re-interpret past rotation.

### Empty Renderer-Slot Pitfall

The `objRenderers` array on `VRStageLighting_AudioLink_Static` drives the
volumetric property-block updates. If a trailing slot is left unassigned
(`fileID: 0`), the `case 2` branch of `_UpdateInstancedProperties` blindly
dereferences it and throws `UnassignedReferenceException` when the editor's
`PlayModeStateChanged.LoadFixtureSettings` hook runs on entering play mode.

The shipped AudioLink GPU fixture prefabs have their arrays trimmed; if you
build a new fixture from scratch, make sure the array contains only populated
entries.

### Wash Cone Angle Defaults

The volumetric mover shader's `CalculateConeWidth` applies a wash-specific
`scalar *= 2.0; scalar -= 2.5` multiplication so wash cones are visibly wider
than spots at the same DMX cone width. The GPU path does a flat
`lerp(minOuter, maxOuter, coneRaw)` and previously used the same `5°/50°` as
spots on wash prefabs, making the projected wash footprint narrower than the
volumetric. The current wash GPU prefabs (`VRSL-DMX-Mover-WashLight-*-13CH-GPU`
and the Legacy variant) ship with `minSpotAngle: 10`, `maxSpotAngle: 100` to
roughly match the 2× factor. Set your own values larger on hand-authored wash
fixtures if the projected cone reads too tight.

### Custom Inspector

`Editor/VRStageLighting_AudioLink_RealtimeLightEditor.cs` provides the
AudioLink-side counterpart to the DMX custom inspector. It uses the same
shared `VRSL_EditorHeader` helper for the logo and version bar, and groups
fields into sections that mirror the conceptual grouping of the AudioLink
Static editor:

- **AudioLink Settings** — reaction toggle, band, delay, band multiplier
- **General Settings** — max intensity, final intensity, colour mode, emission
  colour, point-light flag, spot angle, spot range
- **Movement Settings** — pan/tilt enable and transform references
- **Fixture Settings** — gobo index and spin speed

When a sibling `AudioLink_Static` is attached, the inheriting fields
(AudioLink reaction params, final intensity, emission colour, gobo) render as
disabled "(inherited)" widgets backed by the `GetEffective*()` accessors
rather than the sibling's `SerializedProperty`. This bypasses the sibling's
`[Header]` attributes (`[Header("Audio Link Settings")]`,
`[Header("Fixture Settings")]`) which would otherwise duplicate the custom
section titles.

### Emission Color Gamma Handling

Emission colors in Unity are authored in gamma space via the color picker but must be in linear space for physically correct lighting math in URP. `BuildConfig` calls `f.emissionColor.linear` before packing into the `emissionColor` Vector4 field. This matches the convention used by URP's own light color pipeline. AudioLink theme and color chord colors are already linear — they are set by the AudioLink system in linear space and should not be converted again.

---

## Performance Model

The AudioLink GPU path shares the same scalability profile as the DMX GPU path documented in `URP-Realtime-Lights.md`. Re-stating it with AudioLink-specific numbers:

**Per-frame CPU cost is bounded and tiny.** `UploadFixtureConfigs()` runs once per frame in `LateUpdate` and issues a single `GraphicsBuffer.SetData` of `N × 112 bytes`. For 100 fixtures that's ~10.9 KB/frame, well within typical `SetData` latency. No per-fixture decode, no per-fixture property-block update, no `Light` component write — all per-fixture work lives on the GPU.

**No GPU→CPU readback.** The AudioLink texture never leaves the GPU. The compute shader samples `_AudioTexture` via integer `Load()` calls at whatever resolution AudioLink is running (typically 128×64). There is no async readback, no frame latency, no main-thread pressure from audio data.

**No shadow pass penalty.** Because the pipeline bypasses Unity's `Light` component entirely, URP does not generate a shadow map per fixture. For a 100-fixture wash rig this is the difference between sub-millisecond lighting cost and a shadow atlas that would eat the entire frame — see `URP-Realtime-Lights.md` → *The Shadow Map Rendering Wall* for the full analysis.

**Per-fixture GPU cost is amortised.** The compute dispatch is `ceil(fixtureCount / 64)` groups × 64 threads — a single dispatch handles up to 64 fixtures in parallel. The fullscreen additive pass evaluates every light per illuminated pixel, but the loop body is small (distance attenuation, spot attenuation, optional gobo sample, NdotL). In practice both paths together (DMX + AudioLink simultaneously, if you patch the merged-buffer limitation under Known Limitations) contribute a few milliseconds at 1080p with 100+ fixtures on mid-range hardware.

The cost model is dominated entirely by the fullscreen pass's per-pixel light loop, not by fixture count on the CPU — so scaling to a large rig is a GPU-bandwidth discussion, not a CPU one.

---

## Setup Guide (Unity 6 URP)

### Prerequisites

All URP renderer requirements match the DMX GPU path — see the **Requirements** section at the top of this document. If `VRSLRealtimeLightFeature` is already installed in the URP Renderer, the renderer is configured correctly for AudioLink too; skip ahead to *Adding the AudioLink Renderer Feature*.

### Adding the AudioLink Renderer Feature

Open the URP Renderer asset. Under **Renderer Features**, add **`VRSLAudioLinkRealtimeLightFeature`**.

### Scene Setup

**Option A — Use the included prefabs (recommended for new scenes)**

Drag **`Packages/VR Stage Lighting/Runtime/Prefabs/GPU/VRSL-AudioLink-GPU-Manager`** into the scene. Both the Compute Shader and Lighting Shader fields are pre-assigned.

For fixtures, use one of the included AudioLink GPU prefabs from `Packages/VR Stage Lighting/Runtime/Prefabs/GPU/`:

| Prefab | Fixture type |
|--------|-------------|
| `VRSL-AudioLink-Mover-Spotlight-GPU` | Moving-head spotlight |
| `VRSL-AudioLink-Mover-Washlight-GPU` | Moving-head wash light |
| `VRSL-AudioLink-Static-Blinder-GPU` | Static blinder / strobe bar |
| `VRSL-AudioLink-Static-ParLight-GPU` | Static par can |

Each prefab contains the full mesh hierarchy with a pre-configured `Light` and `VRStageLighting_AudioLink_RealtimeLight` component.

**Option B — Migrate existing mover instances with the Editor utility**

If the scene already has `VRSL-AudioLink-Mover-Spotlight` instances placed and animated, run **VRSL → Setup AudioLink GPU Realtime Lights in Scene**. The utility scans for the child chain `MoverLightMesh-LampFixture-Base → MoverLightMesh-LampFixture-Head`, adds the required components to each root, and registers all changes under a single Undo group.

**Option C — Manual setup**

1. Add **`VRSL_AudioLinkGPULightManager`** to any persistent scene GameObject. Assign:

   | Field | Asset |
   |-------|-------|
   | `computeShader` | `VRSLAudioLinkLightUpdate` |
   | `lightingShader` | `Hidden/VRSL/DeferredLighting` |

   No texture fields — the AudioLink texture is discovered automatically from the global `_AudioTexture` property.

2. For each fixture, add **`VRStageLighting_AudioLink_RealtimeLight`** and configure:

   | Field | Notes |
   |-------|-------|
   | `enableAudioLink` | When enabled, light intensity is driven by the AudioLink amplitude band. When disabled, the light runs at full `maxIntensity × finalIntensity` regardless of audio — useful for static or manually animated lights in the same scene. |
   | `band` | Frequency band this fixture reacts to (Bass / LowMids / HighMids / Treble) |
   | `delay` | History delay (0 = most recent, 127 = most delayed). Useful for chasing effects. |
   | `bandMultiplier` | Sensitivity multiplier. Increase if the band amplitude is too low at your audio levels. |
   | `colorMode` | Emission (fixed), ThemeColor0–3, or ColorChord |
   | `emissionColor` | Active when colorMode is Emission. Author in HDR for bright fixtures. Inherited from a sibling `AudioLink_Static`'s `lightColorTint` when present. |
   | `maxIntensity` | Peak lux at full amplitude. Tune per scene scale — start around 10–30 for indoor scenes. |
   | `finalIntensity` | User-side intensity cap (0–1). Equivalent to Final Intensity on shader fixtures. Inherited from a sibling Static when present. |
   | `spotAngle` / `range` | Cone outer half-angle (degrees) and attenuation range (metres). Read on every `UploadFixtureConfigs()` call, so changes take effect the next `LateUpdate` automatically. |
   | `isPointLight` | Emit as a point light instead of a spot. |
   | `goboIndex` | Gobo slot (1–8) into the manager's shared gobo wheel. 1 = Default (open beam), 2–8 = shaped gobos. Matches the **Select GOBO** slider on the AudioLink Static shader. Inherited from a sibling Static's `SelectGOBO`. The wheel is packed into a shared `Texture2DArray` built once by the manager — per-fixture texture overrides are not supported on this path. |
   | `goboSpinSpeed` | Rotation speed for the projected gobo (−10..10, default 0). Matches the **Auto Spin Speed** (`_SpinSpeed`) property on the volumetric shader meshes. Phase is integrated on the GPU each frame; negative values spin in reverse. Inherited from a sibling Static's `SpinSpeed`. |
   | `enablePanTilt` | Enable for moving-head fixtures. |
   | `panTransform` | The transform rotated on Y for pan (its world position becomes the light origin). |
   | `tiltTransform` | The transform rotated on X for tilt (its world-space forward becomes the light direction). |

   The GPU path does **not** use Unity's `Light` component — the per-fixture `VRStageLighting_AudioLink_RealtimeLight` carries every setting the deferred pass needs.

3. **Matching spot angles to the volumetric cone width:** The `_ConeWidth` property on the VRSL volumetric shader meshes and the GPU cone angles are independent systems — `_ConeWidth` is a mesh vertex displacement scalar, not an angle, so there is no formula to convert between them. Tune the two visually so the illumination footprint on surfaces aligns with the outer edge of the volumetric cone.

   As a starting point for the standard AudioLink mover spotlight mesh at common `_ConeWidth` values:

   | `_ConeWidth` (inspector) | Suggested outer angle (`spotAngle`) | Suggested inner angle (`innerSpotAngle`) |
   |---|---|---|
   | 1.0 (narrow) | 15–20° | 8–10° |
   | 2.0 | 25–30° | 12–15° |
   | **2.5 (default)** | **30–35°** | **15–18°** |
   | 3.5 | 40–50° | 20–25° |
   | 5.5 (max) | 60–70° | 30–35° |

   The **Wash mover** mesh applies an additional `scalar *= 2.5` internally, making it considerably wider than the standard mover at the same `_ConeWidth` — increase the outer angle proportionally.

4. Press Play. `RefreshFixtures()` runs on `OnEnable`, discovers all `VRStageLighting_AudioLink_RealtimeLight` components, creates the GPU buffers, and starts uploading per-frame transform data on `LateUpdate`.

### Tuning maxIntensity

Because AudioLink amplitude is normalized (0–1), `maxIntensity` directly controls peak lux output. A useful calibration approach:

1. Play a loud section of music so the target band is near full amplitude.
2. Look at a lit surface in the scene and adjust `maxIntensity` until the illumination matches your artistic intent.
3. Use `bandMultiplier` to control sensitivity (how quickly the light reaches `maxIntensity` from quiet audio) independently of peak brightness.

### Runtime Changes

```csharp
// After adding or removing fixture GameObjects at runtime:
VRSL_AudioLinkGPULightManager.Instance.RefreshFixtures();

// After changing a fixture's Light component (range, spot angle) at runtime:
// RefreshFixtures() re-reads all Light properties.
VRSL_AudioLinkGPULightManager.Instance.RefreshFixtures();

// After changing the manager's shared gobo wheel (goboTextures[]) at runtime:
// RefreshFixtures() rebuilds the shared Texture2DArray.
VRSL_AudioLinkGPULightManager.Instance.RefreshFixtures();

// The following fields are read per-frame in BuildConfig() — changes take effect
// the next LateUpdate automatically. No manual refresh call is needed:
//   band, delay, bandMultiplier, colorMode, emissionColor,
//   maxIntensity, finalIntensity, enableAudioLink, goboIndex, goboSpinSpeed
```

---

## Known Limitations

**No strobe channel.** The DMX path supports a dedicated strobe gate (a pre-computed binary on/off from `_Udon_DMXGridStrobeOutput`). AudioLink has no equivalent. Strobe effects must be implemented in the volumetric shaders (where they already exist) or by driving `finalIntensity` from a separate C# animator that manipulates the `VRStageLighting_AudioLink_RealtimeLight` component.

**Color Chord uses a single representative pixel.** The color chord strip (`y=25`) is 128 pixels wide, encoding different chord colors at different x positions. The compute shader reads only `x=0` as a single representative. A future extension could sample multiple pixels across the strip and distribute them across fixture groups, or expose an `x` offset per fixture in `alParams`.

**Transparent geometry is not illuminated.** Identical to the DMX path — the additive lighting pass runs after opaques. AudioLink volumetric shaders remain the correct tool for haze and transparent materials.

**No shadow casting.** Identical to the DMX path — bypassing Unity's `Light` component means no shadow maps are generated. See `URP-Realtime-Lights.md` Known Limitations for the full shadow cost discussion.

**Simultaneous DMX + AudioLink GPU paths are not supported.** Both `VRSLRealtimeLightFeature` and `VRSLAudioLinkRealtimeLightFeature` write to the same `_VRSLLights` / `_VRSLLightCount` globals, and the last lighting pass to execute overwrites the other. A future merged-buffer path would require extending `VRSLDeferredLighting.shader` to accept two separate buffers and light counts, or a dedicated merge pass that concatenates both into a single buffer before the lighting pass.

**Filtered / smoothed amplitude not exposed.** AudioLink provides a filtered band response at `ALPASS_FILTEREDAUDIOLINK` (`x=0..15, y=28..31`) that is smoother than the raw band data. The current compute shader reads the raw amplitude from `y=0..3`. The `alParams` struct fields have room (`z` and `w` are partially used) but exposing a `useFiltered` flag is future work.

---

## File Reference

| File | Assembly | Description |
|------|----------|-------------|
| `Runtime/Scripts/VRStageLighting_AudioLink_RealtimeLight.cs` | VRSL.Core | Per-fixture config component. No URP dependency. Dual-compiles as UdonSharpBehaviour / MonoBehaviour. Exposes `GetWorldPosition()` and `GetWorldForward()` for the manager. |
| `Runtime/Scripts/GPU/VRSL_AudioLinkGPULightManager.cs` | VRSL.GPU | Singleton manager. Discovers fixtures, builds GPU buffers, uploads `VRSLALFixtureConfig` every frame from animated transforms, tracks AudioLink RTHandle. |
| `Runtime/Scripts/GPU/VRSLAudioLinkRealtimeLightFeature.cs` | VRSL.GPU | URP `ScriptableRendererFeature`. Schedules compute pass (AudioLink → light buffer) and lighting pass (fullscreen additive) via Unity 6 Render Graph API. |
| `Runtime/Shaders/Compute/VRSLAudioLinkLightUpdate.compute` | — | Compute kernel `UpdateLights`. Samples `_AudioTexture` for amplitude and color using integer `Load()` calls, reads world forward from config, passes gobo index and spin speed from `reserved` through to `VRSLLightData.goboAndSpin`. 64 threads/group. |
| `Runtime/Shaders/Shared/VRSLLightingLibrary.hlsl` | — | Extended with `VRSLALFixtureConfig` struct (7×float4, 112 bytes). `VRSLLightData` (5×float4, 80 bytes) and all lighting evaluation functions are shared between DMX and AudioLink paths. |
| `Runtime/Shaders/VRSLDeferredLighting.shader` | — | Fullscreen additive pass shared by both GPU paths. Contains `SampleGobo()` — a perspective-projection function that derives light-space right/up from `lightDir`, reprojects the world surface point to UV using `tanHalf` from the stored `cosOuter`, and applies the pre-integrated rotation from `goboAndSpin.y` (radians) directly to the UV before sampling `_VRSLGobos`. No `_Time.w` multiplication happens in the shader — both the DMX path (sampling the SpinnerTimer CRT) and the AudioLink path (integrating `spinSpeed · _VRSLTime` on the GPU) write a fully integrated phase. The `Texture2DArray _VRSLGobos` is bound via `Shader.SetGlobalTexture` in `AddRenderPasses` (not inside the render graph, where only `TextureHandle` is accepted). |
| `Editor/VRStageLighting_AudioLink_RealtimeLightEditor.cs` | VRSL.Editor | Custom inspector; section layout, sibling-inherited read-only field display, shared logo/version header via `VRSL_EditorHeader`. |
| `Editor/VRSL_EditorHeader.cs` | VRSL.Editor | Shared VRSL logo + version bar drawing helper; used by both the AudioLink Static editor and the realtime light editors (DMX and AudioLink) — one source of truth for the header look. |
