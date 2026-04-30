# VRSL URP Fixture Configuration Guide

Setup and authoring for VRSL realtime lights on Unity 6 URP. For pipeline architecture, struct layouts, and tuning internals, see `URP-Realtime-Volumetric-Lights.md`.

This guide covers two paths:

- **DMX** — fixtures driven by an Artnet/OSC signal through the existing CRT chain.
- **AudioLink** — fixtures driven by AudioLink's audio analysis texture, no DMX needed.

The two paths share most authoring concepts; differences are called out per section.

---

## Quickstart

Three menu utilities cover most setup work. All are idempotent — safe to re-run.

| Menu | Effect |
|---|---|
| **VRSL → Configure URP Renderer for VRSL Realtime Lights (DMX)** | Sets the active URP renderer to Forward+, disables Depth Priming, enables the URP asset's Depth Texture, appends `VRSLRealtimeLightFeature`. |
| **VRSL → Add VRSL URP Light Manager to Active Scene** | Creates a `VRSL URP Light Manager` GameObject in the active scene with compute / lighting / volumetric shader references assigned. |
| **VRSL → Setup AudioLink URP Realtime Lights in Scene** | Adds `VRStageLighting_AudioLink_RealtimeLight` to every AudioLink mover spotlight in the active scene and wires up pan/tilt transforms. |

For AudioLink scenes, also add `VRSLAudioLinkRealtimeLightFeature` to the URP Renderer (manual step — there is no menu utility for this) or place the supplied `VRSL-AudioLink-URP-LightManager` prefab.

The remainder of this document describes manual setup and the per-fixture fields exposed in the inspectors.

---

## URP Renderer Setup

1. Set **Rendering Path** to **Forward+** on the URP asset.
2. Open the URP Renderer asset used by your camera.
3. Set **Depth Priming Mode** → `Disabled` (required for Depth Normals Prepass to populate correctly under Forward+).
4. Enable **Depth Normals Prepass**.
5. Under **Renderer Features**, add **VRSLRealtimeLightFeature** (DMX) and/or **VRSLAudioLinkRealtimeLightFeature** (AudioLink).

---

## Manager Setup

### DMX — `VRSL_URPLightManager`

| Field | Asset |
|---|---|
| `dmxMainTexture` | CRT producing `_Udon_DMXGridRenderTexture` |
| `dmxMovementTexture` | CRT producing `_Udon_DMXGridRenderTextureMovement` |
| `dmxStrobeTexture` | CRT producing `_Udon_DMXGridStrobeOutput` |
| `dmxSpinTimerTexture` | CRT producing `_Udon_DMXGridSpinTimer` |
| `computeShader` | `VRSLDMXLightUpdate` |
| `lightingShader` | `Hidden/VRSL/DeferredLighting` |
| `volumetricShader` | `Hidden/VRSL/VolumetricLighting` |
| `goboTextures` | Optional `Texture2D[]` packed into a shared `Texture2DArray`; DMX channel +11 selects the slot. |

### AudioLink — `VRSL_AudioLinkURPLightManager`

| Field | Asset |
|---|---|
| `computeShader` | `VRSLAudioLinkLightUpdate` |
| `lightingShader` | `Hidden/VRSL/DeferredLighting` |
| `volumetricShader` | `Hidden/VRSL/VolumetricLighting` |
| `goboTextures` | Optional `Texture2D[]` for the shared gobo wheel. |

The AudioLink manager auto-discovers `_AudioTexture` from the global shader property; no texture references are needed.

### Volumetric tuning (both managers)

The volumetric pass runs whenever `volumetricShader` is assigned. Inspector fields:

| Field | Effect |
|---|---|
| `volumetricStepCount` | Integration steps per ray (default 32). Cost scales linearly with steps × active fixture count. |
| `volumetricDensity` | Base scattering density. |
| `volumetricAnisotropy` | Henyey–Greenstein g (default 0.2; 0 = isotropic; positive forward-scatters). |
| `volumetricTint` / `volumetricIntensity` | Colour tint and global multiplier. |
| `volumetricUseNoise` + scale / scroll / strength | Modulated 3D-noise density. Off compiles the noise out (zero cost). |
| `coupleToSceneFog` | Multiply density by `unity_FogParams.x` and tint by `unity_FogColor` so a URP VolumeProfile drives shaft brightness globally. |
| `volumetricResolution` | `Half` (default; live VR) or `Full` (cinematic capture; ~4× per-pixel cost). |

To disable the volumetric cones at runtime without touching the shader assignment, drop `volumetricIntensity` to 0.

---

## Fixture Authoring — Shared Fields

These fields appear on both `VRStageLighting_DMX_RealtimeLight` and `VRStageLighting_AudioLink_RealtimeLight`:

| Field | Notes |
|---|---|
| `fixtureType` | `MoverSpotlight`, `MoverWashlight`, `StaticBlinder`, `StaticParLight`, `Custom`. Drives inspector field visibility and sets the wash-vs-spot inner-cone ratio (wash 0.65 = flat-bright with long feather; spot/static 0.5 = falloff over the outer half). |
| `maxIntensity` | Peak lux at full output. Tune relative to scene scale. |
| `range` | Attenuation range in metres. |
| `spotAngle` (AudioLink) / `minSpotAngle` & `maxSpotAngle` (DMX) | Outer cone angle in degrees. DMX channel +4 lerps between min and max. |
| `isPointLight` | Emit as a point light instead of a spot. |
| `emitterDepth` | (m) Pushes the cone apex back along the light direction so the cone arrives at the lens with finite radius `emitterDepth × tan(halfAngle)`. Default 0 reproduces a point source. As a starting point: an LED-bar fixture with a 30° outer half-angle reads well at `emitterDepth ≈ 0.3–0.5 m`. |
| `fixtureShellRenderers` | Optional `MeshRenderer[]` on the fixture body. The Realtime light pushes a `MaterialPropertyBlock` so each renderer's lit-lens emissive picks up the same DMX or AudioLink data. |
| `goboIndex` | Selects a slot in the manager's shared gobo wheel. AudioLink uses 1-based indexing (1 = open beam, 2–8 = shaped gobos). |
| `goboSpinSpeed` | Bipolar rotation speed for the projected gobo (negative = CCW, positive = CW). |

### Pan/tilt (movers only)

| Field | Notes |
|---|---|
| `enablePanTilt` | Enable for moving-head fixtures. |
| `panTransform` | Transform rotated on Y for pan. Its world position becomes the light origin. |
| `tiltTransform` | Transform rotated on X for tilt. Its world forward becomes the light direction. |
| `maxMinPan` / `maxMinTilt` | Total travel in degrees (±half from centre). |
| `panOffset` / `tiltOffset` | Per-fixture aim offset. |
| `invertPan` / `invertTilt` | Reverse direction. |

DMX movers get pan/tilt from the DMX channels and apply Rodrigues rotation on the GPU; the `panTransform` / `tiltTransform` references are unused on the DMX path. AudioLink movers read pan/tilt from the animated transforms each frame, so you must wire your animator to those transforms.

---

## DMX-Specific Authoring

`VRStageLighting_DMX_RealtimeLight`:

| Field | Notes |
|---|---|
| `enableDMXChannels` | Enable DMX control. |
| `dmxChannel` / `dmxUniverse` | Industry-standard channel and Artnet universe (1-based). |
| `useLegacySectorMode` / `sector` | Legacy sector addressing for older patches. Sector 0 = channels 1–13, sector 1 = 14–26, etc. |
| `enableFineChannels` | 16-bit pan/tilt via the +1 / +3 channels. |
| `enableStrobe` | Allow the DMX strobe channel to gate the light on/off. |
| `enableConeWidth` | Allow ch+4 (motor speed / zoom) to modulate the cone between `minSpotAngle` and `maxSpotAngle`. Disable on par cans and blinders so unrelated traffic on ch+4 doesn't flicker their cone width. |
| `enableGobo` | Allow ch+11 to select gobos. |
| `enableGoboSpin` | Allow ch+10 to drive gobo spin speed. |
| `finalIntensity` / `globalIntensity` | User-side intensity caps (0–1). |

The full per-fixture channel layout (offsets relative to `dmxChannel`):

| Offset | Channel |
|---|---|
| +0 | Pan coarse |
| +1 | Pan fine |
| +2 | Tilt coarse |
| +3 | Tilt fine |
| +4 | Motor speed / cone width |
| +5 | Dimmer / intensity |
| +6 | Strobe gate |
| +7 / +8 / +9 | Red / Green / Blue |
| +10 | Gobo spin speed |
| +11 | Gobo selection |

---

## AudioLink-Specific Authoring

`VRStageLighting_AudioLink_RealtimeLight`:

| Field | Notes |
|---|---|
| `enableAudioLink` | When enabled, intensity is driven by AudioLink amplitude. When disabled, the light runs at full `maxIntensity × finalIntensity` regardless — useful for static fixtures in the same scene. |
| `band` | `Bass` / `LowMids` / `HighMids` / `Treble`. |
| `delay` | History delay (0 = most recent, 127 = most delayed). Useful for chasing effects across a row of fixtures. |
| `bandMultiplier` | Sensitivity multiplier — increase if amplitude reads too low for your audio levels. |
| `colorMode` | `Emission` (fixed `emissionColor`), `ThemeColor0–3`, `ColorChord`, `ColorTexture` (modern, HSV-normalised), `ColorTextureTraditional` (raw sample). |
| `emissionColor` | Active when `colorMode == Emission`. Author in HDR for bright fixtures. |
| `textureSamplingCoordinates` | Active in `ColorTexture` modes — UV into `_AudioTexture` to read the colour from. |
| `targetToFollow` | Optional aim target. When set, the fixture's tilt transform tracks this object. |
| `finalIntensity` / `globalIntensity` | User-side intensity caps. |

### Tuning maxIntensity

AudioLink amplitude is normalised (0–1), so `maxIntensity` directly controls peak lux output. Calibration:

1. Play a loud section so the target band is near full amplitude.
2. Adjust `maxIntensity` until illumination on a lit surface matches your artistic intent.
3. Use `bandMultiplier` to control sensitivity (how quickly the light reaches `maxIntensity` from quiet audio) independently of peak brightness.

---

## Sibling-Static Inheritance

For projects that retain the legacy `VRStageLighting_DMX_Static` / `VRStageLighting_AudioLink_Static` components on the same GameObject (typically VRChat scenes targeting both the volumetric mesh shaders and the URP path), the Realtime light components automatically inherit configuration from a sibling Static, so authors edit one component and both pipelines stay in sync.

| Realtime field | Sibling source (DMX) | Sibling source (AudioLink) |
|---|---|---|
| Addressing | sector / channel / universe / fine-channel mode | — |
| Pan/tilt modifiers | invertPan, invertTilt, maxMinPan, maxMinTilt, panOffset, tiltOffset | — |
| AudioLink reaction | — | enableAudioLink, band, delay, bandMultiplier |
| Final intensity | — | finalIntensity |
| Emission | — | emissionColor (`LightColorTint` on Static) |
| Gobo | — | goboIndex (`SelectGOBO`), goboSpinSpeed (`SpinSpeed`) |

Inherited fields render as read-only "(inherited)" widgets in the Realtime light inspector so the effective values stay visible at a glance.

The supplied URP prefab variants ship without a Static sibling — the Realtime light is the sole authoring surface and drives the fixture body emissive directly via `fixtureShellRenderers`. Sibling-Static inheritance only applies when one is explicitly added.

---

## Runtime API

```csharp
// DMX: after changing a fixture's config field
VRSL_URPLightManager.Instance.MarkConfigDirty();

// DMX or AudioLink: after adding or removing fixtures at runtime
VRSL_URPLightManager.Instance.RefreshFixtures();
VRSL_AudioLinkURPLightManager.Instance.RefreshFixtures();
```

AudioLink's per-frame fields (`band`, `delay`, `bandMultiplier`, `colorMode`, `emissionColor`, `maxIntensity`, `finalIntensity`, `enableAudioLink`, `goboIndex`, `goboSpinSpeed`) are read every `LateUpdate` and don't require a refresh call.

---

## Prefabs

URP prefab variants ship under `Runtime/Prefabs/`:

| Path | Contents |
|---|---|
| `Runtime/Prefabs/AudioLink/AudioLink-URP-Fixtures/` | AudioLink mover spotlight, mover washlight, static blinder, static parlight, plus the manager prefab |
| `Runtime/Prefabs/DMX/Horizontal Mode/DMX-13CH-URP-Fixtures/` | DMX 13-channel fixtures + manager (horizontal patch layout) |
| `Runtime/Prefabs/DMX/Vertical Mode/DMX-13CH-URP-Fixtures/` | Same fixtures, vertical patch layout |
| `Runtime/Prefabs/DMX/Legacy Mode/DMX-13CH-URP-Fixtures/` | Manager prefab for legacy sector-mode patches |
| `Runtime/Prefabs/DMX/*/5-Channel Statics/DMX-5CH-URP-Fixtures/` | DMX 5-channel static fixtures (par, blinder) |

URP prefabs are standalone — they ship without the legacy `*_Static` sibling component and without the volumetric mesh GameObject. The Realtime light component is the sole authoring surface, and the volumetric pass renders the cone.

---

## Example Scenes

| Scene | Path |
|---|---|
| `VRSL-ExampleScene-AudioLink-URPRealtimeLights` | `Runtime/Example Scenes/AudioLink-Scenes/` |
| `VRSL-ExampleScene-EditorViaOSC-Horizontal-URPRealtimeLights` | `Runtime/Example Scenes/DMX-EditorViaOSCScenes/` |
| `VRSL-ExampleScene-EditorViaOSC-Vertical-URPRealtimeLights` | `Runtime/Example Scenes/DMX-EditorViaOSCScenes/` |

Each ships with the renderer feature configured and the manager pre-populated.
