# VRSL GPU Realtime Lights — Basis Integration Guide

This guide documents the steps required to enable the VRSL GPU realtime light pipeline (both DMX and AudioLink paths) in a Unity 6 project using the [Basis](https://github.com/BasisVR/Basis) Social VR framework. Several steps differ from the generic Unity 6 URP setup described in the other documentation files due to specifics of how Basis packages URP and how Unity 6 handles assembly version defines for embedded packages.

---

## Prerequisites

- Unity 6 (6000.x)
- Basis project with URP configured
- `com.acchosen.vr-stage-lighting` referenced as a local package in `Packages/manifest.json`:
  ```json
  "com.acchosen.vr-stage-lighting": "file:/path/to/VR-Stage-Lighting/Packages/com.acchosen.vr-stage-lighting"
  ```

---

## Step 1 — Add the VRSL_URP Scripting Define

The `VRSL.GPU` assembly (which contains `VRSL_GPULightManager`, `VRSL_AudioLinkGPULightManager`, and both renderer features) uses a `versionDefines` entry in its `.asmdef` to emit `VRSL_URP` when `com.unity.render-pipelines.universal >= 14.0` is detected. This guard exists to keep VRChat builds clean.

**In Basis, URP is embedded as a local package** (`"source": "embedded"` in `packages-lock.json`) with no semver version string. Unity cannot evaluate version conditions against embedded packages, so `VRSL_URP` is never automatically defined and the entire `VRSL.GPU` assembly is silently excluded from compilation.

**Fix:** add the define manually.

1. Open **Edit → Project Settings → Player**
2. Under **Other Settings → Script Compilation → Scripting Define Symbols**
3. Add `VRSL_URP`
4. Click **Apply**

Unity will recompile. `VRSL_AudioLinkGPULightManager`, `VRSL_GPULightManager`, `VRSLAudioLinkRealtimeLightFeature`, and `VRSLRealtimeLightFeature` will all become available.

---

## Step 2 — Configure the URP Renderer Asset

Basis ships several URP renderer assets under `Assets/Basis/Settings/Unity Rendering Defaults/`. For a desktop scene, the relevant one is **`DesktopRenderer.asset`**.

Open it in the Inspector and verify or change the following:

| Setting | Required value | Notes |
|---------|---------------|-------|
| Rendering Path | **Forward+** | Already set to Forward+ in Basis by default |
| Depth Priming Mode | **Disabled** | Default is Auto; with MSAA enabled the warning "Depth priming is not supported because MSAA is enabled" appears — set to Disabled to resolve it. This also correctly enables baked Bakery lightmap materials that were previously suppressed by the spurious depth prepass. |
| Depth Normals Prepass | *no toggle* | **Does not exist as an Inspector toggle in Unity 6 URP.** The prepass is activated automatically when a renderer feature declares the requirement via `ConfigureInput`. Both VRSL renderer features do this — no manual action needed. |

### Add the Renderer Feature

Under **Renderer Features** at the bottom of the renderer asset, click **Add Renderer Feature** and select:

- **`VRSLAudioLinkRealtimeLightFeature`** — for AudioLink-driven GPU lights
- **`VRSLRealtimeLightFeature`** — for DMX/ArtNet-driven GPU lights (if applicable)

> **Note:** Running both features simultaneously is not currently supported. Both write to the same `_VRSLLights` / `_VRSLLightCount` globals and the last pass to execute overwrites the other.

---

## Step 3 — Add the Manager to the Scene

### AudioLink path

1. Create an empty GameObject in the scene (e.g. `VRSL_Manager`)
2. Add component **`AudioLink GPU Light Manager`** (listed under VRSL in the component picker, or search "AudioLink GPU")
3. Assign in the Inspector:

   | Field | Value |
   |-------|-------|
   | Compute Shader | `VRSLAudioLinkLightUpdate` |
   | Lighting Shader | `Hidden/VRSL/DeferredLighting` |

   No texture assignments needed — the AudioLink texture is discovered automatically from the global `_AudioTexture` property set by the AudioLink system.

### DMX path

1. Add component **`GPU Light Manager`** (search "GPU Light Manager")
2. Assign in the Inspector:

   | Field | Value |
   |-------|-------|
   | DMX Main Texture | CRT producing `_Udon_DMXGridRenderTexture` |
   | DMX Movement Texture | CRT producing `_Udon_DMXGridRenderTextureMovement` |
   | DMX Strobe Texture | CRT producing `_Udon_DMXGridStrobeOutput` |
   | Compute Shader | `VRSLDMXLightUpdate` |
   | Lighting Shader | `Hidden/VRSL/DeferredLighting` |

---

## Step 4 — Per-Fixture Setup

For each fixture that should contribute genuine scene illumination, add two components:

1. A Unity **`Light`** component (Spot or Point). Configure Range and Spot Angle to match the fixture. This component is read once at config time — it is not driven at runtime.

2. **`VRStageLighting_AudioLink_RealtimeLight`** (AudioLink path) or **`VRStageLighting_DMX_RealtimeLight`** (DMX path).

For moving-head fixtures, assign `panTransform` (the transform that rotates on Y for pan) and `tiltTransform` (the transform that rotates on X for tilt, and whose world-space forward becomes the GPU light direction). Enable `enablePanTilt`.

See `AudioLink-GPU-Realtime-Lights.md` or `URP-Realtime-Lights.md` for full field reference and tuning guidance.

---

## Troubleshooting

**Component not visible in Add Component menu**
The `VRSL.GPU` assembly is not compiling. Verify `VRSL_URP` is in the project's Scripting Define Symbols (Step 1). If already present, try **Assets → Reimport All** to force a full recompile.

**`[VRSL] GPU lighting requires 'Depth Normals Prepass'` warning in console**
The renderer feature is active but `_CameraNormalsTexture` is not being populated. Confirm the renderer feature was added to the same URP Renderer asset that the scene camera is using. The `ConfigureInput` call in `AddRenderPasses` should activate the prepass automatically — if not, verify there are no other compile errors in the VRSL.GPU assembly.

**Baked lighting looks different after Step 2**
Expected. Setting Depth Priming Mode to Disabled removes a spurious depth prepass that was incorrectly suppressing Bakery lightmap material contributions. The scene is now rendering baked lighting correctly.

**Scene camera uses a different renderer asset**
Basis uses multiple renderer assets for different contexts (Desktop, Quest, Headless). The renderer feature must be added to whichever asset the active camera references. For a desktop scene this is `DesktopRenderer.asset`; confirm via the Camera component's **Renderer** field or the URP asset's renderer list.
