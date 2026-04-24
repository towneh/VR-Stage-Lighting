using UnityEngine;

namespace VRSL
{
    /// <summary>
    /// Per-fixture config component for the VRSL DMX GPU realtime light path.
    ///
    /// Attach to each fixture that should contribute scene illumination driven by DMX.
    /// VRSL_GPULightManager collects these components, uploads their config to a GPU
    /// StructuredBuffer once (and again on change), then a compute shader decodes DMX
    /// channel data each frame and a fullscreen additive pass renders the lighting.
    ///
    /// Uses the same DMX addressing scheme as VRStageLighting_DMX_Static.
    ///
    /// 13-channel layout (relative to the fixture's base DMX channel):
    ///   +0  Pan coarse       +1  Pan fine        +2  Tilt coarse     +3  Tilt fine
    ///   +4  Motor speed      +5  Dimmer           +6  Strobe          +7  Red
    ///   +8  Green            +9  Blue             +10 Gobo spin       +11 Gobo select
    /// </summary>
    [AddComponentMenu("VRSL/DMX Realtime Light")]
    public class VRStageLighting_DMX_RealtimeLight : MonoBehaviour
    {
        // ──────────────────────────────────────────────────────────────────────────
        // DMX Addressing — mirrors VRStageLighting_DMX_Static field names/defaults
        // ──────────────────────────────────────────────────────────────────────────
        [Header("DMX Settings")]
        [Tooltip("Enables DMX control for this fixture.")]
        public bool enableDMXChannels = true;

        [Tooltip("Enable 16-bit fine-channel resolution for pan and tilt.")]
        public bool enableFineChannels = false;

        [Tooltip("Industry-standard DMX start channel (1-512 per universe).")]
        public int dmxChannel = 1;

        [Tooltip("Artnet universe (1-based).")]
        public int dmxUniverse = 1;

        [Tooltip("Use legacy sector-based addressing instead of industry-standard channels.")]
        public bool useLegacySectorMode = false;

        [Tooltip("Sector number when using legacy mode. Sector 0 = channels 1-13, Sector 1 = 14-26, etc.")]
        public int sector = 0;

        // ──────────────────────────────────────────────────────────────────────────
        // Light settings
        // ──────────────────────────────────────────────────────────────────────────
        [Header("Light Settings")]
        [Tooltip("Light intensity emitted at DMX full-on (255). Scale to taste for your scene.")]
        public float maxIntensity = 10f;

        [Range(0f, 1f)]
        [Tooltip("User-side intensity cap, equivalent to Final Intensity on shader fixtures.")]
        public float finalIntensity = 1f;

        [Tooltip("Allow DMX strobe channel to gate this light on/off.")]
        public bool enableStrobe = true;

        [Tooltip("Spot angle in degrees when the cone-width DMX channel (ch+4) is at minimum (0).")]
        public float minSpotAngle = 5f;

        [Tooltip("Spot angle in degrees when the cone-width DMX channel (ch+4) is at maximum (255).")]
        public float maxSpotAngle = 60f;

        [Tooltip("Light attenuation range. Increase for larger spaces.")]
        public float range = 20f;

        [Tooltip("Emit as a point light instead of a spot.")]
        public bool isPointLight = false;

        [Tooltip("Enable gobo selection via DMX channel +11. When disabled the gobo defaults to open beam (slot 1).")]
        public bool enableGobo = true;

        [Tooltip("Allow DMX channel +10 to drive gobo spin speed. Disable to lock the gobo in place.")]
        public bool enableGoboSpin = true;

        // ──────────────────────────────────────────────────────────────────────────
        // Pan / Tilt (for moving-head fixtures)
        // ──────────────────────────────────────────────────────────────────────────
        [Header("Pan / Tilt (Moving Head)")]
        [Tooltip("Read pan and tilt channels and apply Rodrigues rotation on the GPU.")]
        public bool enablePanTilt = false;

        [Tooltip("Total pan travel in degrees (±half this value from centre).")]
        public float maxMinPan = 180f;

        [Tooltip("Total tilt travel in degrees (±half this value from centre).")]
        public float maxMinTilt = 180f;

        [Tooltip("Invert the pan direction.")]
        public bool invertPan = false;

        [Tooltip("Invert the tilt direction.")]
        public bool invertTilt = false;

        [Range(0f, 360f)]
        [Tooltip("Pan position offset in degrees applied after DMX decoding.")]
        public float panOffset = 0f;

        [Range(0f, 360f)]
        [Tooltip("Tilt position offset in degrees applied after DMX decoding.")]
        public float tiltOffset = 90f;

        // ──────────────────────────────────────────────────────────────────────────
        int _absChannel;

        void Awake()
        {
            _absChannel = ComputeAbsoluteChannel();
        }

        void OnValidate()
        {
            _absChannel = ComputeAbsoluteChannel();
        }

        // Matches RawDMXConversion() / SectorConversion() in VRStageLighting_DMX_Static
        public int ComputeAbsoluteChannel()
        {
            if (useLegacySectorMode)
                return Mathf.Abs(sector * 13 + 1);

            return Mathf.Abs(dmxChannel + (dmxUniverse - 1) * 512 + (dmxUniverse - 1) * 8);
        }
    }
}
