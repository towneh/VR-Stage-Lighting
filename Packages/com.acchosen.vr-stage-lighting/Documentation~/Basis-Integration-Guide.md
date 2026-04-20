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

### Option A — Use the prefab (recommended)

Drag **`Packages/VR Stage Lighting/Runtime/Prefabs/GPU/VRSL-AudioLink-GPU-Manager`** into the scene hierarchy. Both the **Compute Shader** (`VRSLAudioLinkLightUpdate`) and **Lighting Shader** (`Hidden/VRSL/DeferredLighting`) are pre-assigned — no further Inspector work needed.

### Option B — Manual setup

1. Create an empty GameObject in the scene (e.g. `VRSL_Manager`)
2. Add component **`AudioLink GPU Light Manager`** (search "AudioLink GPU" in the component picker)
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

Each fixture that should contribute genuine scene illumination needs a Unity `Light` component and a `VRStageLighting_AudioLink_RealtimeLight` component on its root GameObject, with `tiltTransform` pointing to the mesh part whose world-space forward represents the beam direction.

There are three paths depending on your starting point.

---

### Path A — New scene, placing fixtures fresh

Place `VRSL-AudioLink-Mover-Spotlight` instances as normal, then immediately run **VRSL → Setup AudioLink GPU Realtime Lights in Scene** (see Path B). The utility adds the required GPU components in one pass regardless of whether the fixtures were just placed or have been in the scene for some time.

> **Note:** The AudioLink mover prefabs reference UdonSharp scripts that only resolve in a VRChat SDK project. In a Basis project these will show a "missing scripts" warning — this is expected and does not affect the mesh, constraints, animation, or the GPU realtime light components added by the utility.

Adjust the following per fixture or in bulk via multi-select after running the utility:

| Field | Where | Notes |
|-------|-------|-------|
| Spot Angle | Light component | Match to the fixture's physical beam angle |
| Range | Light component | Match to scene scale |
| Band | AudioLink Realtime Light | Frequency band to react to (Bass/LowMid/HighMid/Treble) |
| Max Intensity | AudioLink Realtime Light | Peak illumination in lux at full amplitude — tune per scene |
| Color Mode | AudioLink Realtime Light | Emission (fixed), ThemeColor0–3, or ColorChord |

---

### Path B — Migrating existing `VRSL-AudioLink-Mover-Spotlight` instances

If your scene already has the standard AudioLink mover prefab instances placed and animated, use the one-shot Editor utility rather than replacing them.

1. Open the scene in Unity
2. Run **VRSL → Setup AudioLink GPU Realtime Lights in Scene** from the menu bar

The utility scans every GameObject in the scene for the child chain `MoverLightMesh-LampFixture-Base → MoverLightMesh-LampFixture-Head`, then for each fixture found:

- Adds a disabled **Spot Light** (range 20, spotAngle 45°) to the root
- Adds **`AudioLink Realtime Light`** with `enablePanTilt = true`, `panTransform → Base`, `tiltTransform → Head`
- Skips any fixture already configured
- Registers everything under a single Undo group (Ctrl+Z reverts all changes)

After the utility runs, adjust Spot Angle, Range, Band, Max Intensity, and Color Mode in bulk via multi-select as described in Path A above.

> **Why the Light is disabled:** The GPU deferred lighting pass handles scene illumination. Leaving the Unity Light enabled would cause the fixture to contribute twice — once via the GPU pass and once via Unity's standard light rendering.

---

### Path C — Manual per-fixture setup

For fixtures not based on the standard VRSL mover hierarchy, or for fine-grained control:

1. Add a Unity **`Light`** component (Spot or Point) to the fixture root. Configure Range and Spot Angle to match the fixture. Set the Light to **disabled**.

2. Add **`AudioLink Realtime Light`** (listed under VRSL in the component picker, or search "AudioLink Realtime").

3. Assign in the Inspector:

   | Field | Value |
   |-------|-------|
   | Realtime Light | The Light component on this GameObject |
   | Enable Pan Tilt | Checked for moving-head fixtures |
   | Pan Transform | The transform that provides world position (typically the yoke/base) |
   | Tilt Transform | The transform whose `forward` is the beam direction — updated each frame by animation, constraints, or script |

   For static fixtures, leave Pan/Tilt unchecked — the manager reads position and forward from the Light component's transform directly.

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

**Fixtures not found by the setup utility**
The utility identifies movers by the child chain `MoverLightMesh-LampFixture-Base → MoverLightMesh-LampFixture-Head`. If your fixtures use a different hierarchy (custom prefabs, renamed GameObjects), use Path C above to set up components manually.

**No illumination visible at runtime**
Confirm AudioLink is active in the scene and `_AudioTexture` is being set as a global. The manager logs no warning if AudioLink is absent — it simply finds no texture and the compute pass skips. Check that the `VRSL_Manager` GameObject is active and the Compute Shader and Lighting Shader fields are assigned.
