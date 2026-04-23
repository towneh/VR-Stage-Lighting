using UnityEngine;

namespace VRSL
{
    /// <summary>
    /// Drives a Unity realtime Light component from VRSL DMX data.
    /// Requires a VRSLDMXReader instance in the scene to supply the decoded channel values.
    ///
    /// Designed for Unity 6.2+ URP projects where realtime lights are not cost-prohibitive.
    /// Uses the same DMX addressing scheme as VRStageLighting_DMX_Static so existing
    /// fixture configurations transfer directly.
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
        [Tooltip("The Unity Light to drive. If left empty, falls back to a Light on this GameObject.")]
        public Light realtimeLight;

        [Tooltip("Light intensity emitted at DMX full-on (255). Scale to taste for your scene.")]
        public float maxIntensity = 10f;

        [Range(0f, 1f)]
        [Tooltip("User-side intensity cap, equivalent to Final Intensity on shader fixtures.")]
        public float finalIntensity = 1f;

        [Tooltip("Multiplicative tint on top of the incoming DMX RGB color. Leave white to pass DMX color through unchanged.")]
        [ColorUsage(false, true)]
        public Color lightColorTint = Color.white;

        [Tooltip("Allow DMX strobe channel to gate this light on/off.")]
        public bool enableStrobe = true;

        [Tooltip("Read the motor-speed/zoom channel (ch+4) and apply it to the spotlight cone angle.")]
        public bool enableConeWidth = true;

        [Tooltip("Spot angle in degrees when the cone-width DMX channel is at minimum (0).")]
        public float minSpotAngle = 5f;

        [Tooltip("Spot angle in degrees when the cone-width DMX channel is at maximum (255).")]
        public float maxSpotAngle = 60f;

        // ──────────────────────────────────────────────────────────────────────────
        // Pan / Tilt (for moving-head fixtures)
        // ──────────────────────────────────────────────────────────────────────────
        [Header("Pan / Tilt (Moving Head)")]
        [Tooltip("Read pan and tilt channels and apply them to the transforms below.")]
        public bool enablePanTilt = false;

        [Tooltip("Transform rotated on its Y axis for pan (left/right swing).")]
        public Transform panTransform;

        [Tooltip("Transform rotated on its X axis for tilt (up/down swing).")]
        public Transform tiltTransform;

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
            if (realtimeLight == null)
                realtimeLight = GetComponent<Light>();

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

        void LateUpdate()
        {
            if (!enableDMXChannels || realtimeLight == null) return;

            var reader = VRSLDMXReader.Instance;
            if (reader == null) return;

            // Dimmer — ch+5
            float intensity = reader.GetMainValue(_absChannel + 5);

            // RGB color — ch+7, ch+8, ch+9
            float r = reader.GetMainValue(_absChannel + 7);
            float g = reader.GetMainValue(_absChannel + 8);
            float b = reader.GetMainValue(_absChannel + 9);

            // Strobe gate — ch+6 (1.0 when no strobe texture available)
            float strobe = enableStrobe ? reader.GetStrobeValue(_absChannel + 6) : 1f;

            realtimeLight.color = new Color(r, g, b) * lightColorTint;
            realtimeLight.intensity = intensity * maxIntensity * finalIntensity * strobe;

            // Cone width — ch+4 (motor speed / zoom channel), matches getDMXConeWidth() in VRSL-DMXFunctions.cginc
            if (enableConeWidth && realtimeLight.type == LightType.Spot)
            {
                float coneRaw = reader.GetMainValue(_absChannel + 4);
                realtimeLight.spotAngle = Mathf.Lerp(minSpotAngle, maxSpotAngle, coneRaw);
                realtimeLight.innerSpotAngle = realtimeLight.spotAngle * 0.5f;
            }

            if (enablePanTilt)
                ApplyPanTilt(reader);
        }

        void ApplyPanTilt(VRSLDMXReader reader)
        {
            // Pan — ch+0 coarse, ch+1 fine
            float panRaw = reader.GetMovementValue(_absChannel);
            if (enableFineChannels)
                panRaw += reader.GetMovementValue(_absChannel + 1) / 256f;

            // Matches GetPanValue() in VRSL-DMXFunctions.cginc.
            // Static passes maxMinPan/2 as _MaxMinPanAngle, so the shader computes
            // (maxMinPan/2 * 2 * inputValue) - maxMinPan/2 = maxMinPan * inputValue - maxMinPan/2.
            float panDeg = (maxMinPan * panRaw) - (maxMinPan / 2f);
            if (invertPan) panDeg = -panDeg;
            panDeg += panOffset;

            // Tilt — ch+2 coarse, ch+3 fine
            float tiltRaw = reader.GetMovementValue(_absChannel + 2);
            if (enableFineChannels)
                tiltRaw += reader.GetMovementValue(_absChannel + 3) / 256f;

            // Matches GetTiltValue() in VRSL-DMXFunctions.cginc (same derivation as pan).
            float tiltDeg = (maxMinTilt * tiltRaw) - (maxMinTilt / 2f);
            if (invertTilt) tiltDeg = -tiltDeg;
            tiltDeg += tiltOffset;

            if (panTransform != null)
            {
                // The shader's rotateYMatrix rotates around object-space Z (keeps Z fixed,
                // rotates XY). With the standard 90° X root rotation, object Z = world -Y,
                // so pan must rotate around local Z — not local Y.
                panTransform.localRotation = Quaternion.AngleAxis(panDeg, Vector3.forward);
            }
            if (tiltTransform != null)
            {
                // The shader's tiltOffset=90 rotates the beam from object +Y to object +Z
                // (world -Y, pointing straight down). The fixture root's 90° X rotation
                // already provides that same 90° shift in the Transform path, so subtract
                // 90° here to avoid applying it twice.
                tiltTransform.localRotation = Quaternion.Euler(tiltDeg - 90f, 0f, 0f);
            }
        }
    }
}
