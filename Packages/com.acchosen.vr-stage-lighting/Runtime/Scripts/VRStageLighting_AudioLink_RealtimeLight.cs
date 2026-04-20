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
        [Tooltip("Frequency band to react to.")]
        public AudioLinkBandState band = AudioLinkBandState.Bass;

        [Range(0, 127)]
        [Tooltip("Delay offset into the AudioLink history buffer (0 = most recent).")]
        public int delay = 0;

        [Range(1f, 15f)]
        [Tooltip("Multiplier applied to the raw band amplitude before driving intensity.")]
        public float bandMultiplier = 1f;

        // ── Color ─────────────────────────────────────────────────────────────
        [Header("Color")]
        [Tooltip("Source for the light color.\n"
               + "Emission: fixed color below.\n"
               + "ThemeColor0–3: AudioLink theme palette.\n"
               + "ColorChord: AudioLink color chord representative pixel.")]
        public ALRealtimeColorMode colorMode = ALRealtimeColorMode.Emission;

        [ColorUsage(false, true)]
        [Tooltip("Fixed emission color used when Color Mode is set to Emission.")]
        public Color emissionColor = Color.white;

        // ── Light ─────────────────────────────────────────────────────────────
        [Header("Light Settings")]
        [Tooltip("Unity Light for this fixture. Range and spot angles are read at config upload time. "
               + "Leave empty to auto-detect from this GameObject.")]
        public Light realtimeLight;

        [Tooltip("Peak light intensity (lux) at AudioLink full amplitude (1.0). Tune per scene scale.")]
        public float maxIntensity = 10f;

        [Range(0f, 1f)]
        [Tooltip("User-side intensity cap, equivalent to Final Intensity on shader fixtures.")]
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
        void Awake()
        {
            if (realtimeLight == null)
                realtimeLight = GetComponent<Light>();
        }

        /// <summary>World-space position to use for this light (called per-frame by the manager).</summary>
        public Vector3 GetWorldPosition()
        {
            if (enablePanTilt && panTransform != null)
                return panTransform.position;
            if (realtimeLight != null)
                return realtimeLight.transform.position;
            return transform.position;
        }

        /// <summary>World-space forward direction for this light (called per-frame by the manager).</summary>
        public Vector3 GetWorldForward()
        {
            if (enablePanTilt && tiltTransform != null)
                return tiltTransform.forward;
            if (realtimeLight != null)
                return realtimeLight.transform.forward;
            return transform.forward;
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
