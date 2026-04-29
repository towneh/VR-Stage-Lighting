# VRSL URP Volumetric Lights — Design Document

> **Status:** Active implementation on the `urp-volumetric-lights` branch. The DMX raymarched volumetric pass has landed (clean uniform density, multi-light, screen-space depth occlusion, half-res with bilateral upsample) and is gated by a manager-side toggle. Modulated noise density, AudioLink port, wash mover tuning, fixture-shell driving, and new GPU prefab variants are pending — see *Phasing* for the ordered list. The companion document `URP-Realtime-Lights.md` describes the surface-illumination pipeline this work builds on.

---

## Goal

Add genuine volumetric scattering to the URP GPU realtime light path so the visible beam-in-haze effect is rendered by the same data that drives surface illumination. The volumetric mesh shader (`VRSL-StandardMover-VolumetricMesh.shader` and the AudioLink/Wash variants) is **kept** as the VRChat / Quest / mobile / Built-in-RP path; this document covers a **second, additive pipeline** specific to Unity 6 URP that runs alongside surface lighting and is selected by using the GPU prefab variants.

The two paths coexist permanently. There is no deprecation of the volumetric mesh shader. Users target whichever path matches their platform:

| Target | Volumetric path |
|---|---|
| VRChat worlds, Quest, mobile, Built-in RP, pre-Unity-6 | Volumetric cone meshes (existing `*VolumetricMesh.shader`) |
| Unity 6 URP, desktop / strong-perf standalone | This document — raymarched scattering in the GPU light feature |

---

## Requirements

| | Minimum |
|---|---|
| Unity | **6.0 LTS** or later |
| Universal Render Pipeline | **17.0** or later |
| URP Rendering Path | **Forward+** |
| URP Renderer feature | **Depth Normals Prepass** enabled, **Depth Priming Mode** = `Disabled` |
| Prior pipeline | The surface-illumination pipeline described in `URP-Realtime-Lights.md` must be present — this design extends it |

The work compiles only when `VRSL_URP` is defined (URP ≥17 is installed). Same gating as the existing `VRSL.GPU` assembly; nothing additional is required to keep VRChat builds clean.

---

## Background

### What the existing volumetric mesh shader does

A textured cone mesh is drawn additively per fixture, parented to the fixture's tilt transform. The fragment shader samples 3D magic-noise and 2D scrolling noise to fake atmospheric particulates inside the cone, fades with distance, and uses a stencil ref of 142 to avoid drawing inside interior geometry. DMX channel +4 modulates cone width via material properties pushed by `VRStageLighting_DMX_Static`; the AudioLink variant reads the band amplitude directly.

This is a **vertex cone, not participating media.** Beautiful in haze, near-zero per-fixture cost, but it has no concept of the rest of the scene — it doesn't know about fog, doesn't darken behind occluders, and the cone exists whether or not any actual light is present at that pixel. It also occupies its own GameObject and material slot per fixture and is driven by a parallel set of MaterialPropertyBlock pushes that the `VRStageLighting_*_Static` component exists primarily to maintain.

### What "real volumetric" means in URP

URP has no built-in volumetric-light-shaft system. HDRP has volumetric fog + per-light volumetric scattering; URP 17 has volumetric clouds (atmospheric, not local), and `VolumeProfile` fog (surfaces only). A genuine beam shaft in URP is therefore a **custom raymarched in-scattering pass**: for each pixel, integrate scattering contribution along the view ray from camera to the visible surface, accumulating from each VRSL light whose cone the ray passes through.

The existing GPU light feature is already structured to host this. `_VRSLLights` is GPU-resident, depth and normals are already imported via `UniversalResourceData`, and Render Graph is available for explicit pass scheduling.

---

## Expected Gains vs the Volumetric Mesh Shader

For projects that can target the Unity 6 URP path, the new pipeline is expected to deliver:

**Architectural / authoring:**
- A single source of truth (`_VRSLLights`) drives both surface illumination and the visible cone. The cone is guaranteed to match the actual lighting contribution — a dimmed light produces a dimmed cone with no extra wiring.
- A GPU-prefab fixture is a single GameObject with one Realtime component and a fixture-body mesh. The cone mesh, projection disc, and Static sibling component disappear from the GPU prefab variants. Fewer GameObjects per fixture, smaller scenes, less inspector surface.
- One shader (`VRSLVolumetricLighting.shader`) services DMX, AudioLink, spot, and wash — replacing the four separate mesh-shader variants currently maintained per data source × cone shape.

**Visual:**
- Scene fog density (URP `VolumeProfile`) directly modulates the cone. Author once at scene level; all fixtures respond. No per-fixture haze tuning.
- Screen-space depth occlusion. Cones do not paint through walls, out the back of the room, or onto avatars from behind. On-screen occluders silhouette out of the cone correctly. The mesh-shader path uses a stencil ref to suppress interior draws but cannot react to dynamic occluders.
- Gobo pattern in the volume matches the gobo pattern on illuminated surfaces, because both call the same `SampleGobo()` in `VRSLLightingLibrary.hlsl`.

**Performance — to be measured:**
This is the part that **needs prototype data before claims can be made**. The two approaches scale differently:

| | Volumetric mesh shader | GPU raymarched volumetric |
|---|---|---|
| Cost shape | Per-cone screen coverage × noise samples per fragment | Half-res fullscreen × step count × active fixture count |
| Scales with | Visible cone area on screen × number of fixtures | Screen resolution + total fixture count |
| Per-frame CPU | `MaterialPropertyBlock` push per cone, projection, emissive each frame (Static component) | One config upload at startup; nothing per-frame |
| Crossover point | Likely cheaper at low fixture count | Likely cheaper at high fixture count when many cones overlap on screen |

The CPU saving is the only number we can claim today: removing the per-frame property-block pushes for the cone and projection meshes recovers a small but consistent amount of main-thread work. The GPU side is genuinely unknown — the prototype's first measurement task is to characterise the crossover on a representative scene (16 fixtures, 32 fixtures, 64 fixtures) so the docs can carry real numbers when the work merges.

Even if GPU performance turns out roughly equal at typical fixture counts, the architectural and visual gains alone justify the GPU path for projects that can target Unity 6 URP.

---

## Design Goals

- The volumetric pass runs **after** `VRSLDeferredLighting` in the same `ScriptableRendererFeature` and reads the same `_VRSLLights` buffer — no new CPU data on the hot path.
- Density couples to URP's standard fog parameters (`unity_FogParams`) so changing scene fog density in the volume profile changes the apparent shaft brightness.
- Two visual modes ship: **clean** (uniform density) and **modulated** (3D noise-driven density that approximates the dusty look of the existing mesh shader). Modulated is the default because it preserves the established VRSL aesthetic.
- The pass is half-resolution with jittered ray origins and bilateral upsample, paying for the cost in temporal stability rather than samples-per-pixel.
- Occlusion uses the camera depth buffer only. No per-fixture shadowmap rendering.
- The same shader services both DMX and AudioLink fixtures, both spot and wash. Cone half-angle is the only knob distinguishing wash from spot.
- The Realtime light component gains the optional ability to drive the fixture-body emissive `MeshRenderer`s itself, so a GPU prefab can be authored without a sibling `*_Static` component for projects that don't need the volumetric mesh path.

---

## The Volumetric Pipeline

```
DMX or AudioLink data → VRSLLightData buffer (already produced by surface pipeline)
         │
         ▼ [AfterRenderingOpaques, immediately after VRSLDeferredLighting]
[RASTER PASS — VRSLVolumetricLighting.shader]
  Render target: half-resolution _VRSLVolumetricRT (R11G11B10_FLOAT)
  Full-screen triangle, no blending — output replaces RT contents
  Per pixel:
    • Read camera depth at this pixel (downsampled with min-depth filter)
    • Reconstruct view ray; ray length = camera-to-surface distance
    • Apply blue-noise jitter to ray origin (temporal blue-noise texture)
    • For each step in N (default 32):
        – Compute world-space sample position
        – Density = unity_FogParams-derived base * (modulated mode: 3D noise term)
        – For each light L in _VRSLLights:
            * Compute toLight, distance attenuation, cone attenuation
            * Phase function (Henyey–Greenstein, g configurable)
            * Gobo sample at this point via SampleGobo() — already in
              VRSLLightingLibrary for surface use, reused unchanged
            * Accumulate L.color * L.intensity * cone * distAtten * phase * density
    • Output accumulated radiance
         │
         ▼ [Same pass, second sub-pass — bilateral upsample]
  Reads half-res RT + full-res depth + full-res normals
  Edge-aware reconstruction to full resolution, additive blend onto camera color
```

### Density model

Two `multi_compile` variants gated by a manager-side quality setting:

```
multi_compile _ _VRSL_VOLUMETRIC_NOISE
```

Clean variant: density is a constant scalar derived from `unity_FogParams`. The shaft fades naturally as fog density increases — turn fog off, no shaft; turn fog up, beam fills the room.

Modulated variant: density also multiplies a 3D noise sampled in world space, slowly scrolling on time. Approximates the dust/haze look. Cost: one 3D texture sample per ray step. At 32 steps × N lights this adds up; modulated is recommended for stages of moderate fixture count, clean is recommended at high fixture density.

Both variants ship and the manager exposes the toggle in the inspector so authors can compare directly on their own scenes.

### Resolution strategy

Half-res + jittered + bilateral upsample is the chosen default. Quarter-res with temporal accumulation was considered and rejected for v1 because temporal accumulation interacts poorly with rapid mover panning, which is the common case in a stage rig — ghosting on fast pan was judged a worse artefact than the cost saved. Half-res keeps the pass below ~1.5 ms per eye at typical fixture counts on midrange desktop hardware (rough number, to be verified during prototype).

Camera depth is downsampled with a min-depth filter before the volumetric pass — taking the minimum of the 2×2 source pixels keeps the half-res depth tight to silhouettes so the bilateral upsample doesn't fringe near object edges.

### Occlusion: screen-space depth

The raymarch terminates at the surface depth read from `_CameraDepthTexture`. This means:

- Walls, floors, props, and avatars visible to the camera correctly stop the integration at their surface — the cone never paints "through" a wall.
- An avatar standing between the camera and the back of the cone correctly cuts a silhouette out of the cone, because the raymarch for those pixels terminates at the avatar's depth.
- Each observer's view is internally self-consistent. There is no case where the cone visibly extends through a visible occluder.

What screen-space depth occlusion does **not** produce: a true light-perspective shadow inside the volume — i.e., an avatar standing in the beam casting a darkened wedge onto the rest of the volume past them, viewed from the side. That requires sampling occlusion from the light's perspective (a shadowmap), not the camera's. The artefact is rare in stage usage because moving heads sweep through occluders rather than dwelling on them; for static-mounted spots aimed at a performer, a noticeable "no-shadow-in-beam" result is possible. This is the trade-off accepted for the cost saving.

### Per-fixture shadowmaps — considered, deferred

A full shadowmap-per-fixture path was considered and is documented here for performance comparison and future revisit.

| Approach | Visual benefit | Approximate cost (16 fixtures) | Status |
|---|---|---|---|
| Screen-space depth (chosen) | Cone respects on-screen geometry | ~0 ms beyond the volumetric pass itself | Default in v1 |
| Per-fixture shadowmaps, 1024² each | Light-perspective shadows; avatars in beam darken volume past them | +3–5 ms VR per frame for shadow rasterisation alone | Deferred |
| Main-light shadowmap sampling only | Sun-shadows in beam | ~0 ms | Rejected — sun is rarely the occluder in a stage scene; produces wrong-looking results for indoor rigs |

Per-fixture shadowmaps are deferred rather than abandoned. The cost estimate above is for shadow rasterisation only and does not include the additional per-step shadow sampling cost in the volumetric raymarch itself, which would scale with both step count and fixture count. For VR — the dominant target for VRSL stage scenes — this cost is the reason we are not adopting it now. If the screen-space approach proves visually insufficient on real scenes, a follow-up branch can layer this in as an opt-in per-fixture flag (`castVolumetricShadows`) capped at a small pool of fixtures, similar to how Unity-Light shadow casting is discussed in `URP-Realtime-Lights.md`.

---

## GPU Data — Reuse, Not Extension

The volumetric pass reads the existing `VRSLLightData` struct unchanged. No new fields are needed for the v1 pass — position, direction, range, color, intensity, spot cosines, gobo index, and spin angle are all the inputs the raymarch needs.

A small new `cbuffer` carries pass-global parameters:

```hlsl
cbuffer VRSLVolumetricParams
{
    float4 _VRSLVolStepCount;        // x = step count (int as float), y = max distance, z = jitter scale, w = scattering coefficient
    float4 _VRSLVolDensity;          // x = base density, y = noise scale, z = noise scroll speed, w = HG anisotropy g
    float4 _VRSLVolFogTint;          // xyz = fog colour tint, w = global intensity multiplier
};
```

CPU upload happens once per parameter change in the manager, not per-frame.

---

## Coexistence with the Static Component

`VRStageLighting_DMX_Static` and `VRStageLighting_AudioLink_Static` continue to exist and continue to drive volumetric mesh `MeshRenderer`s, fixture-body emissives, and projection meshes for projects targeting the legacy / VRChat / mobile path. Nothing about that pipeline changes.

For the GPU path, the goal is that a fixture authored with **only** the Realtime light component (no Static sibling) is fully functional — including the lit lamp lens and any fixture-body emissive that should react to DMX or AudioLink. To support this:

- `VRStageLighting_DMX_RealtimeLight` and `VRStageLighting_AudioLink_RealtimeLight` gain a `MeshRenderer[] fixtureShellRenderers` field and a `Color shellEmissive` / `float shellIntensity` (or AudioLink equivalent) configuration. A single `MaterialPropertyBlock` is pushed to those renderers — once at startup for DMX (the value is sampled from the same DMX buffer in shader, so the CPU only has to push once per config change), and per-frame in `LateUpdate` for AudioLink (animated transforms and per-frame band amplitude).
- The existing sibling-inheritance behaviour described in `URP-Realtime-Lights.md` is unchanged. When a Static sibling is present, the Realtime light still inherits DMX addressing and movement settings from it, and the Static component continues to drive its own renderers as today. The two are not mutually exclusive — they layer.
- New GPU prefab variants will be authored with no Static component and no volumetric mesh GameObject, so the cone is rendered solely by this pipeline.

This is a **clearer split between platforms**, not a consolidation. The Static component is not deprecated. The GPU prefab variants simply don't need it.

---

## Phasing

The work is sequenced so each step produces something visually evaluable before the next is committed.

1. ✅ **Compute infrastructure + multi-light raymarch (DMX)** — volumetric pass added to `VRSLRealtimeLightFeature`, half-res RT and half-res min-depth allocated as transient render-graph resources, three sub-passes (depth downsample → raymarch → bilateral upsample) record from one `VolumetricPass.RecordRenderGraph`. Reads `_VRSLLights` directly; gobo sampling reuses `SampleGobo()` (lifted into `VRSLLightingLibrary.hlsl` as a shared definition for both surface and volumetric). Density is uniform, manager-driven. *Decision gate:* visual evaluation on the DMX example scene.
2. **Modulated density variant** — second `multi_compile` keyword, 3D noise sampling at each ray step, manager toggle exposed.
3. **DMX wash mover** — same shader, density/falloff tuning for the wider cone.
4. **AudioLink port** — `VRSLAudioLinkRealtimeLightFeature` gets the same volumetric pass; `VRSLAudioLinkLightUpdate.compute` already produces compatible `VRSLLightData`, so the shader-side change is zero. AudioLink wash follows.
5. **Fixture-shell driving on Realtime components** — adds `fixtureShellRenderers` field and the property-block push, so a GPU prefab works without a Static sibling.
6. **GPU prefab variants** — new prefabs with no volumetric mesh, no Static, no projection disc. Old prefabs unchanged. Update the Editor setup utility to optionally generate the new variant.
7. **CHANGELOG + setup-guide section** in this document, describing the final-state pipeline.

Each step is a separate commit on the `urp-volumetric-lights` branch. The branch merges to `main` in one PR after step 6 but the doc grows in place as the implementation lands.

---

## Open Questions

These are still open and will be resolved during implementation:

1. **Step count default.** 32 is a starting estimate; profiling on the reference scene determines whether 24 or 48 is the better trade-off for VR.
2. **Henyey–Greenstein anisotropy.** Forward-scattering (g > 0) makes the beam read as bright when looking down it, faint from the side. Stage cones in haze read closer to isotropic (g ≈ 0). Default likely g = 0.2 with author override.
3. **Fog coupling strength.** Whether `_VRSLVolFogTint` always uses `unity_FogColor` or whether authors get an override per-manager. Lean toward override to allow "warm haze under cool light" looks that don't match scene fog.
4. **Half-res vs full-res toggle.** A manager-side quality preset of `Half / Full` may be useful — full-res for cinematic capture, half-res for live VR. Decision after measuring quality at half-res in motion.
5. **Wash mover near-field artefact.** Wider cones with screen-space depth occlusion may show edge artefacts near the cone-floor intersection where the upsample kernel is asked to span large depth discontinuities. May need a wider bilateral kernel for wash-only or a different upsample scheme.
6. **GPU performance crossover vs the mesh-shader path.** Per the *Expected Gains* section, the prototype must measure the cost of the raymarched pass at 16 / 32 / 64 fixtures against the equivalent mesh-shader scene on the same hardware (desktop and VR), and on a small fixture count (4–8) where the mesh shader is expected to win. Numbers belong in the final-state doc — not in commit messages or the CHANGELOG.

---

## Known Limitations (planned)

These will be documented in the final state:

- **No light-perspective shadows in volume.** See *Per-fixture shadowmaps — considered, deferred*. Avatars in a beam do not cast a darkened wedge through the rest of the volume past them; on-screen occluders correctly silhouette out of the cone but off-screen occluders do not.
- **Transparent geometry is not occluded.** The pass terminates the raymarch at opaque depth only. A transparent prop will be visually overpainted by the volumetric. Same caveat as the surface-illumination pipeline.
- **Modulated noise is decoupled from the existing volumetric mesh shader's noise.** The two paths use different noise to compute density, so a fixture rendered through both paths simultaneously (Static sibling + GPU pass on the same prefab — not the recommended setup) will show two slightly different dust patterns layered. This is one of the reasons the new GPU prefab variants drop the volumetric mesh entirely.

---

## File Plan

| File | Status | Purpose |
|---|---|---|
| `Runtime/Shaders/VRSLVolumetricLighting.shader` | new | Half-res raymarch pass + bilateral upsample sub-pass |
| `Runtime/Shaders/Shared/VRSLLightingLibrary.hlsl` | extended | Adds `VRSL_EvaluateLightVolumetric()`; existing `SampleGobo` reused unchanged |
| `Runtime/Scripts/GPU/VRSLRealtimeLightFeature.cs` | extended | Adds the volumetric raster pass after `VRSLDeferredLighting`, conditional on manager flag |
| `Runtime/Scripts/GPU/VRSLAudioLinkRealtimeLightFeature.cs` | extended | Same volumetric pass, shared shader |
| `Runtime/Scripts/GPU/VRSL_GPULightManager.cs` | extended | Manager-side volumetric quality / density / mode settings; uploads `VRSLVolumetricParams` cbuffer |
| `Runtime/Scripts/GPU/VRSL_AudioLinkGPULightManager.cs` | extended | Same parameter ownership for AudioLink path |
| `Runtime/Scripts/VRStageLighting_DMX_RealtimeLight.cs` | extended | `fixtureShellRenderers[]` + shell emissive driving |
| `Runtime/Scripts/VRStageLighting_AudioLink_RealtimeLight.cs` | extended | Same |
| `Runtime/Prefabs/GPU/VRSL-DMX-*-GPU.prefab` (new variants) | new | DMX GPU prefab variants without volumetric mesh / Static sibling |
| `Runtime/Prefabs/AudioLink/AudioLink-URP-Fixtures/VRSL-AudioLink-*-GPU.prefab` | revised | AudioLink GPU prefabs adjusted to drop the volumetric mesh GameObject |
| `Editor/VRStageLighting_DMX_RealtimeLightEditor.cs` | extended | Inspector section for fixture-shell renderers + volumetric quality preview |
| `Editor/VRSL_AudioLinkGPUSetup.cs` | extended | Optional flag to generate the no-volumetric-mesh variant |
