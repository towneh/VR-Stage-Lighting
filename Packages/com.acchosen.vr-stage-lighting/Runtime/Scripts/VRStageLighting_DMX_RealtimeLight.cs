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
    /// This component is the sole authoring surface on GPU-only fixture prefabs —
    /// no VRStageLighting_DMX_Static sibling participates. The legacy DMX Static
    /// component remains the authoring surface for VRChat / mobile / Built-in-RP
    /// fixture prefabs, which use a different rendering path entirely.
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
        // Fixture type — drives inspector field visibility
        // ──────────────────────────────────────────────────────────────────────────
        [Tooltip("Selects the fixture archetype this component represents. Drives "
               + "which inspector sections are shown — movers expose pan/tilt, "
               + "spotlights expose gobo selection, statics hide both. Pick Custom "
               + "to keep every section visible regardless of preset.")]
        public DMXFixtureType fixtureType = DMXFixtureType.MoverSpotlight;

        // ──────────────────────────────────────────────────────────────────────────
        // DMX Addressing — mirrors VRStageLighting_DMX_Static field names/defaults
        // ──────────────────────────────────────────────────────────────────────────
        [Tooltip("Enables DMX control for this fixture.")]
        public bool enableDMXChannels = true;

        [Tooltip("Enable 16-bit fine-channel resolution for pan and tilt.")]
        public bool enableFineChannels = false;

        [Tooltip("Use legacy sector-based addressing instead of industry-standard channels.")]
        public bool useLegacySectorMode = false;

        [Tooltip("Sector number when using legacy mode. Sector 0 = channels 1-13, Sector 1 = 14-26, etc.")]
        public int sector = 0;

        [Tooltip("Industry-standard DMX start channel (1-512 per universe).")]
        public int dmxChannel = 1;

        [Tooltip("Artnet universe (1-based).")]
        public int dmxUniverse = 1;

        // ──────────────────────────────────────────────────────────────────────────
        // Light settings
        // ──────────────────────────────────────────────────────────────────────────
        [Tooltip("Light intensity emitted at DMX full-on (255). Scale to taste for your scene.")]
        public float maxIntensity = 10f;

        [Range(0f, 1f)]
        [Tooltip("User-side intensity cap, equivalent to Final Intensity on shader fixtures.")]
        public float finalIntensity = 1f;

        [Range(0f, 1f)]
        [Tooltip("Global intensity scalar applied on top of Final Intensity. Useful for "
               + "scene-wide dimming without adjusting every fixture individually.")]
        public float globalIntensity = 1f;

        [Tooltip("Allow DMX strobe channel to gate this light on/off.")]
        public bool enableStrobe = true;

        [Tooltip("Allow DMX channel +4 (motor speed / zoom) to modulate the spot cone between "
               + "minSpotAngle and maxSpotAngle. Disable for fixtures without a zoom motor "
               + "(par cans, blinders) so their cone stays locked at maxSpotAngle.")]
        public bool enableConeWidth = true;

        [Tooltip("Spot angle in degrees when the cone-width DMX channel (ch+4) is at minimum (0). "
               + "Ignored when enableConeWidth is false.")]
        public float minSpotAngle = 5f;

        [Tooltip("Spot angle in degrees when the cone-width DMX channel (ch+4) is at maximum (255). "
               + "Also used as the fixed cone angle when enableConeWidth is false.")]
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
        // Light output axis
        // ──────────────────────────────────────────────────────────────────────────
        [Tooltip("Local-space direction the light shines from this fixture. "
               + "Defaults to forward (+Z) which matches moving-head fixtures. "
               + "Set to up (+Y) for par cans and other fixtures whose lens faces "
               + "local +Y (e.g. when the fixture body is mounted upside-down).")]
        public Vector3 localLightDirection = Vector3.forward;

        // ──────────────────────────────────────────────────────────────────────────
        // Fixture shell driving
        // ──────────────────────────────────────────────────────────────────────────
        [Tooltip("MeshRenderers on the fixture body that should react to this fixture's "
               + "DMX state (lit lamp lens, status indicators, etc.). When a sibling "
               + "VRStageLighting_DMX_Static is present it owns these renderers — leave "
               + "this list empty in that case. On GPU-only prefabs without a Static "
               + "sibling, populate with the fixture body's MeshRenderer(s) so the lens "
               + "still lights up under DMX without the legacy Static component.")]
        public MeshRenderer[] fixtureShellRenderers;

        [ColorUsage(showAlpha: false)]
        [Tooltip("Color tint applied to the fixture body's emissive output. Multiplies "
               + "with the DMX-decoded color. White = no tint.")]
        public Color shellEmissionTint = Color.white;

        // ──────────────────────────────────────────────────────────────────────────
        int _absChannel;
        MaterialPropertyBlock _shellProps;

        void Awake()
        {
            _absChannel = ComputeAbsoluteChannel();
            DriveFixtureShells();
        }

        void OnValidate()
        {
            _absChannel = ComputeAbsoluteChannel();
            DriveFixtureShells();
        }

        // ──────────────────────────────────────────────────────────────────────────
        // Fixture shell driving — pushes a MaterialPropertyBlock to the body
        // MeshRenderers so the legacy fixture-mesh shader (which samples DMX
        // textures globally) sees the right per-instance channel offset.
        //
        // Skipped when a sibling VRStageLighting_DMX_Static is present — Static
        // is the authoritative driver in that case and pushes its own (richer)
        // set of properties. This component takes over only on GPU-only prefab
        // variants that have intentionally dropped the Static sibling.
        // ──────────────────────────────────────────────────────────────────────────
        public void DriveFixtureShells()
        {
            if (fixtureShellRenderers == null || fixtureShellRenderers.Length == 0) return;

            if (_shellProps == null) _shellProps = new MaterialPropertyBlock();

            // Property names and value semantics mirror VRStageLighting_DMX_Static's
            // _UpdateInstancedProperties() so the same body shader works under either
            // driver. Fields with no equivalent on this component use sensible
            // defaults (no NineUniverse, no legacy gobo range, no global brightness
            // override, no cone-mesh tuning since GPU prefabs drop the cone mesh).
            _shellProps.SetInt(  "_DMXChannel",          _absChannel);
            _shellProps.SetInt(  "_NineUniverseMode",    0);
            _shellProps.SetInt(  "_PanInvert",           invertPan          ? 1 : 0);
            _shellProps.SetInt(  "_TiltInvert",          invertTilt         ? 1 : 0);
            _shellProps.SetInt(  "_LegacyGoboRange",     0);
            _shellProps.SetInt(  "_EnableStrobe",        enableStrobe       ? 1 : 0);
            _shellProps.SetInt(  "_EnableSpin",          enableGoboSpin     ? 1 : 0);
            _shellProps.SetInt(  "_EnableDMX",           enableDMXChannels  ? 1 : 0);
            _shellProps.SetInt(  "_EnableFineChannels",  enableFineChannels ? 1 : 0);
            _shellProps.SetInt(  "_ProjectionSelection", 1);
            _shellProps.SetFloat("_FixtureRotationX",    tiltOffset);
            _shellProps.SetFloat("_FixtureBaseRotationY",panOffset);
            _shellProps.SetColor("_Emission",            shellEmissionTint);
            _shellProps.SetColor("_EmissionDMX",         shellEmissionTint);
            _shellProps.SetFloat("_GlobalIntensity",     globalIntensity);
            _shellProps.SetFloat("_FinalIntensity",      finalIntensity);
            _shellProps.SetFloat("_MaxMinPanAngle",      maxMinPan  / 2f);
            _shellProps.SetFloat("_MaxMinTiltAngle",     maxMinTilt / 2f);

            for (int i = 0; i < fixtureShellRenderers.Length; i++)
            {
                var r = fixtureShellRenderers[i];
                if (r != null) r.SetPropertyBlock(_shellProps);
            }
        }

        // Matches RawDMXConversion() / SectorConversion() in VRStageLighting_DMX_Static
        // so the GPU path resolves the same absolute channel index that the legacy
        // shader path would for the same addressing inputs.
        public int ComputeAbsoluteChannel()
        {
            if (useLegacySectorMode)
                return Mathf.Abs(sector * 13 + 1);
            return Mathf.Abs(dmxChannel + (dmxUniverse - 1) * 512 + (dmxUniverse - 1) * 8);
        }
    }

    /// <summary>Fixture archetype for <see cref="VRStageLighting_DMX_RealtimeLight"/>.
    /// Drives inspector field visibility — movers expose pan/tilt, spotlights expose
    /// gobo selection, static fixtures hide both. Custom keeps every section visible
    /// for authoring novel fixtures that don't fit the four built-in shapes.</summary>
    public enum DMXFixtureType
    {
        MoverSpotlight = 0,
        MoverWashlight = 1,
        StaticBlinder  = 2,
        StaticParLight = 3,
        Custom         = 4,
    }
}
