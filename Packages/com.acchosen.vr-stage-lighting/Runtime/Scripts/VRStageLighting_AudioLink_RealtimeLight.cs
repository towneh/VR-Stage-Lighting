using UnityEngine;
#if UDONSHARP
using UdonSharp;
using VRC.SDKBase;
using VRC.Udon;
#endif

namespace VRSL
{
    /// <summary>
    /// Per-fixture config component for the AudioLink GPU realtime light path.
    ///
    /// Attach this alongside a Unity Light component on each fixture that should
    /// contribute genuine scene illumination driven by AudioLink amplitude and color data.
    /// The volumetric shader meshes on the same fixture continue to run independently —
    /// this component only feeds the GPU light buffer.
    ///
    /// For moving-head fixtures: assign panTransform and tiltTransform.
    /// VRSL_AudioLinkGPULightManager reads their world-space transforms every frame,
    /// so animations driving the pan/tilt hierarchy automatically drive the GPU light direction.
    /// </summary>
#if UDONSHARP
    [UdonBehaviourSyncMode(BehaviourSyncMode.None)]
    public class VRStageLighting_AudioLink_RealtimeLight : UdonSharpBehaviour
#else
    [AddComponentMenu("VRSL/AudioLink Realtime Light")]
    public class VRStageLighting_AudioLink_RealtimeLight : MonoBehaviour
#endif
    {
        // ── AudioLink ─────────────────────────────────────────────────────────
        [Header("AudioLink Settings")]
        [Tooltip("Enable or disable AudioLink reaction for this fixture. When disabled the light contributes zero intensity to the scene. "
               + "Ignored when a sibling VRStageLighting_AudioLink_Static is present — inherited from it.")]
        public bool enableAudioLink = true;

        [Tooltip("Frequency band to react to. "
               + "Ignored when a sibling AudioLink_Static is present — inherited from it.")]
        public AudioLinkBandState band = AudioLinkBandState.Bass;

        [Range(0, 127)]
        [Tooltip("Delay offset into the AudioLink history buffer (0 = most recent). "
               + "Ignored when a sibling AudioLink_Static is present — inherited from it.")]
        public int delay = 0;

        [Range(1f, 15f)]
        [Tooltip("Multiplier applied to the raw band amplitude before driving intensity. "
               + "Ignored when a sibling AudioLink_Static is present — inherited from it.")]
        public float bandMultiplier = 1f;

        // ── Color ─────────────────────────────────────────────────────────────
        [Header("Color")]
        [Tooltip("Source for the light color.\n"
               + "Emission: fixed color below.\n"
               + "ThemeColor0–3: AudioLink theme palette.\n"
               + "ColorChord: AudioLink color chord representative pixel.")]
        public ALRealtimeColorMode colorMode = ALRealtimeColorMode.Emission;

        [ColorUsage(false, true)]
        [Tooltip("Fixed emission color used when Color Mode is set to Emission. "
               + "Ignored when a sibling AudioLink_Static is present — inherited from its lightColorTint.")]
        public Color emissionColor = Color.white;

        // ── Light ─────────────────────────────────────────────────────────────
        [Header("Light Settings")]
        [Tooltip("Peak light intensity (lux) at AudioLink full amplitude (1.0). Tune per scene scale.")]
        public float maxIntensity = 10f;

        [Tooltip("Spot angle in degrees for the light cone.")]
        public float spotAngle = 60f;

        [Tooltip("Light attenuation range. Increase for larger spaces.")]
        public float range = 20f;

        [Tooltip("Emit as a point light instead of a spot.")]
        public bool isPointLight = false;

        [Range(1, 8)]
        [Tooltip("Gobo selection (matches the AudioLink Static Select GOBO slider). 1 = Default (open beam), 2–8 = shaped gobos.")]
        public int goboIndex = 1;

        [Range(-10f, 10f)]
        [Tooltip("Gobo rotation speed. 0 = no spin, negative = anti-clockwise, positive = clockwise. Matches the volumetric shader's spin range. "
               + "Ignored when a sibling AudioLink_Static is present — inherited from its spinSpeed.")]
        public float goboSpinSpeed = 0f;

        [Range(0f, 1f)]
        [Tooltip("User-side intensity cap, equivalent to Final Intensity on shader fixtures. "
               + "Ignored when a sibling AudioLink_Static is present — inherited from it.")]
        public float finalIntensity = 1f;

        // ── Pan / Tilt ────────────────────────────────────────────────────────
        [Header("Pan / Tilt (Moving Head)")]
        [Tooltip("Enable per-frame world transform read for moving-head direction. "
               + "The animation system rotates the transforms; this component reads the result.")]
        public bool enablePanTilt = false;

        [Tooltip("Transform rotated on Y for pan. Its world position is used as the light origin.")]
        public Transform panTransform;

        [Tooltip("Transform rotated on X for tilt. Its world-space forward is sent to the GPU as the light direction.")]
        public Transform tiltTransform;

        // ─────────────────────────────────────────────────────────────────────

        /// <summary>World-space position to use for this light (called per-frame by the manager).</summary>
        public Vector3 GetWorldPosition()
        {
            if (enablePanTilt && panTransform != null)
                return panTransform.position;
            return transform.position;
        }

        /// <summary>World-space forward direction for this light (called per-frame by the manager).</summary>
        public Vector3 GetWorldForward()
        {
            if (enablePanTilt && tiltTransform != null)
                return tiltTransform.forward;
            return transform.forward;
        }

        // ── Sibling inheritance ───────────────────────────────────────────────
        // Same pattern as VRStageLighting_DMX_RealtimeLight: if a sibling
        // VRStageLighting_AudioLink_Static is present, its fields win so scene
        // authors who override band/delay/intensity/color on the Static
        // component get the same values flowing through to the GPU path without
        // needing duplicate overrides here. Some sibling field names differ from
        // the GPU component's (lightColorTint → emissionColor, spinSpeed →
        // goboSpinSpeed) — the mapping is intentional per concept.

        public bool GetEffectiveEnableAudioLink()
        {
            var s = GetComponent<VRStageLighting_AudioLink_Static>();
            return s != null ? s.EnableAudioLink : enableAudioLink;
        }

        public AudioLinkBandState GetEffectiveBand()
        {
            var s = GetComponent<VRStageLighting_AudioLink_Static>();
            return s != null ? s.Band : band;
        }

        public int GetEffectiveDelay()
        {
            var s = GetComponent<VRStageLighting_AudioLink_Static>();
            return s != null ? s.Delay : delay;
        }

        public float GetEffectiveBandMultiplier()
        {
            var s = GetComponent<VRStageLighting_AudioLink_Static>();
            return s != null ? s.BandMultiplier : bandMultiplier;
        }

        public float GetEffectiveFinalIntensity()
        {
            var s = GetComponent<VRStageLighting_AudioLink_Static>();
            return s != null ? s.finalIntensity : finalIntensity;
        }

        public Color GetEffectiveEmissionColor()
        {
            var s = GetComponent<VRStageLighting_AudioLink_Static>();
            return s != null ? s.LightColorTint : emissionColor;
        }

        public float GetEffectiveGoboSpinSpeed()
        {
            var s = GetComponent<VRStageLighting_AudioLink_Static>();
            return s != null ? s.SpinSpeed : goboSpinSpeed;
        }
    }

    /// <summary>Color source for <see cref="VRStageLighting_AudioLink_RealtimeLight"/>.</summary>
    public enum ALRealtimeColorMode
    {
        Emission     = 0,
        ThemeColor0  = 1,
        ThemeColor1  = 2,
        ThemeColor2  = 3,
        ThemeColor3  = 4,
        ColorChord   = 5,
    }
}
