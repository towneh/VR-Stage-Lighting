using UnityEngine;
using UnityEngine.Animations;
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
    /// This component is the sole authoring surface on GPU-only fixture prefabs —
    /// no VRStageLighting_AudioLink_Static sibling participates. The legacy
    /// AudioLink Static component remains the authoring surface for VRChat / mobile /
    /// Built-in-RP fixture prefabs, which use a different rendering path entirely.
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
        // ── Fixture type ──────────────────────────────────────────────────────
        [Tooltip("Selects the fixture archetype this component represents. Drives "
               + "which inspector sections are shown — movers expose pan/tilt and "
               + "targetToFollow, spotlights expose gobo selection, statics hide "
               + "movement and gobo entirely. Pick Custom to keep every section "
               + "visible regardless of preset.")]
        public AudioLinkFixtureType fixtureType = AudioLinkFixtureType.MoverSpotlight;

        // ── AudioLink ─────────────────────────────────────────────────────────
        [Tooltip("Enable or disable AudioLink reaction for this fixture. When disabled the light contributes zero intensity to the scene.")]
        public bool enableAudioLink = true;

        [Tooltip("Frequency band to react to.")]
        public AudioLinkBandState band = AudioLinkBandState.Bass;

        [Range(0, 127)]
        [Tooltip("Delay offset into the AudioLink history buffer (0 = most recent).")]
        public int delay = 0;

        [Range(1f, 15f)]
        [Tooltip("Multiplier applied to the raw band amplitude before driving intensity.")]
        public float bandMultiplier = 1f;

        // ── Color ─────────────────────────────────────────────────────────────
        [Tooltip("Source for the light color.\n"
               + "Emission: fixed color below.\n"
               + "ThemeColor0–3: AudioLink theme palette.\n"
               + "ColorChord: AudioLink color chord representative pixel.\n"
               + "ColorTexture: sample _AudioTexture at the UV coordinates below, "
               + "HSV-normalised to full brightness so dim atlas regions still "
               + "contribute meaningful color.\n"
               + "ColorTextureTraditional: same UV sample but returns the raw "
               + "texel without brightness normalisation.")]
        public ALRealtimeColorMode colorMode = ALRealtimeColorMode.Emission;

        [ColorUsage(false, true)]
        [Tooltip("Fixed emission color used when Color Mode is set to Emission.")]
        public Color emissionColor = Color.white;

        [Tooltip("UV coordinates within _AudioTexture to sample when Color Mode is set to "
               + "ColorTexture. Useful for picking specific audio reactivity regions of "
               + "AudioLink's atlas (e.g., a particular theme color region or a custom "
               + "AudioLink Shader Range). Range [0,1] in both axes.")]
        public Vector2 textureSamplingCoordinates = new Vector2(0f, 0.5f);

        // ── Light ─────────────────────────────────────────────────────────────
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
        [Tooltip("Gobo rotation speed. 0 = no spin, negative = anti-clockwise, positive = clockwise. Matches the volumetric shader's spin range.")]
        public float goboSpinSpeed = 0f;

        [Range(0f, 1f)]
        [Tooltip("User-side intensity cap, equivalent to Final Intensity on shader fixtures.")]
        public float finalIntensity = 1f;

        [Range(0f, 1f)]
        [Tooltip("Global intensity scalar applied on top of Final Intensity. Useful for "
               + "scene-wide dimming without adjusting every fixture individually.")]
        public float globalIntensity = 1f;

        // ── Pan / Tilt ────────────────────────────────────────────────────────
        [Tooltip("Enable per-frame world transform read for moving-head direction. "
               + "The animation system rotates the transforms; this component reads the result.")]
        public bool enablePanTilt = false;

        [Tooltip("Transform rotated on Y for pan. Its world position is used as the light origin.")]
        public Transform panTransform;

        [Tooltip("Transform rotated on X for tilt. Its world-space forward is sent to the GPU as the light direction.")]
        public Transform tiltTransform;

        [Tooltip("Optional follow target. When assigned, the AimConstraint on tiltTransform "
               + "is rebound to this Transform at Start, so the fixture aims at it instead "
               + "of the prefab's per-instance PanTiltTarget child. Useful when many movers "
               + "should track a shared target (e.g., a stage mark, a performer rig, or a "
               + "DoublePanTiltTarget driven by an animation). Leave empty to keep the "
               + "prefab's default aim source (the per-instance PanTiltTarget).")]
        public Transform targetToFollow;

        // ── Fixture shell driving ─────────────────────────────────────────────
        [Tooltip("MeshRenderers on the fixture body that should react to AudioLink (lit "
               + "lamp lens, status indicators, etc.). When a sibling "
               + "VRStageLighting_AudioLink_Static is present it owns these renderers — "
               + "leave this list empty in that case. On GPU-only prefabs without a "
               + "Static sibling, populate with the fixture body's MeshRenderer(s) so "
               + "the lens still reacts under AudioLink without the legacy Static.")]
        public MeshRenderer[] fixtureShellRenderers;

        MaterialPropertyBlock _shellProps;

        void Start()
        {
            DriveFixtureShells();
            ApplyTargetToFollow();
        }

        // When targetToFollow is set, rebind the AimConstraint on tiltTransform so the
        // fixture aims at the supplied Transform rather than the prefab's per-instance
        // PanTiltTarget. No-op when targetToFollow is null — the prefab's existing
        // AimConstraint source (PanTiltTarget) is kept.
        public void ApplyTargetToFollow()
        {
            if (targetToFollow == null || tiltTransform == null) return;
            var aim = tiltTransform.GetComponent<AimConstraint>();
            if (aim == null) return;
            var src = new ConstraintSource
            {
                sourceTransform = targetToFollow,
                weight = 1f,
            };
            if (aim.sourceCount > 0) aim.SetSource(0, src);
            else                     aim.AddSource(src);
        }

        // Pushes a MaterialPropertyBlock to the body MeshRenderers so the legacy
        // fixture-mesh shader (which samples AudioLink data via material properties)
        // sees the right per-instance configuration. Used on GPU-only prefab
        // variants where this component is the sole authoring surface.
        public void DriveFixtureShells()
        {
            if (fixtureShellRenderers == null || fixtureShellRenderers.Length == 0) return;

            if (_shellProps == null) _shellProps = new MaterialPropertyBlock();

            // Map colorMode to the AudioLink Static body shader's sampling flags
            // (ThemeColorSampling / ColorChord / ColorTexture / Emission are
            // mutually exclusive). The two ColorTexture variants share the
            // sampling toggle but differ on the traditional flag.
            int themeColorTarget    = 0;
            int enableThemeColor    = 0;
            int enableColorChord    = 0;
            int enableTextureSample = 0;
            int useTraditional      = 0;
            if (colorMode >= ALRealtimeColorMode.ThemeColor0
                && colorMode <= ALRealtimeColorMode.ThemeColor3)
            {
                enableThemeColor = 1;
                themeColorTarget = (int)colorMode - (int)ALRealtimeColorMode.ThemeColor0;
            }
            else if (colorMode == ALRealtimeColorMode.ColorChord)
            {
                enableColorChord = 1;
            }
            else if (colorMode == ALRealtimeColorMode.ColorTexture)
            {
                enableTextureSample = 1;
            }
            else if (colorMode == ALRealtimeColorMode.ColorTextureTraditional)
            {
                enableTextureSample = 1;
                useTraditional      = 1;
            }

            _shellProps.SetFloat("_EnableAudioLink",          enableAudioLink ? 1f : 0f);
            _shellProps.SetFloat("_Band",                     (float)(int)band);
            _shellProps.SetFloat("_Delay",                    delay);
            _shellProps.SetFloat("_BandMultiplier",           bandMultiplier);
            _shellProps.SetColor("_Emission",                 emissionColor);
            _shellProps.SetFloat("_GlobalIntensity",          globalIntensity);
            _shellProps.SetFloat("_FinalIntensity",           finalIntensity);
            _shellProps.SetFloat("_SpinSpeed",                goboSpinSpeed);
            _shellProps.SetInt(  "_EnableSpin",               goboSpinSpeed != 0f ? 1 : 0);
            _shellProps.SetInt(  "_ProjectionSelection",      goboIndex);
            _shellProps.SetInt(  "_EnableColorTextureSample", enableTextureSample);
            _shellProps.SetInt(  "_UseTraditionalSampling",   useTraditional);
            _shellProps.SetInt(  "_EnableThemeColorSampling", enableThemeColor);
            _shellProps.SetInt(  "_ThemeColorTarget",         themeColorTarget);
            _shellProps.SetInt(  "_EnableColorChord",         enableColorChord);
            _shellProps.SetFloat("_TextureColorSampleX",      textureSamplingCoordinates.x);
            _shellProps.SetFloat("_TextureColorSampleY",      textureSamplingCoordinates.y);

            for (int i = 0; i < fixtureShellRenderers.Length; i++)
            {
                if (fixtureShellRenderers[i] != null)
                    fixtureShellRenderers[i].SetPropertyBlock(_shellProps);
            }
        }

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

    }

    /// <summary>Fixture archetype for <see cref="VRStageLighting_AudioLink_RealtimeLight"/>.
    /// Drives inspector field visibility — movers expose pan/tilt, spotlights expose gobo
    /// selection, static fixtures hide both. Custom keeps every section visible for
    /// authoring novel fixtures that don't fit the four built-in shapes.</summary>
    public enum AudioLinkFixtureType
    {
        MoverSpotlight = 0,
        MoverWashlight = 1,
        StaticBlinder  = 2,
        StaticParLight = 3,
        Custom         = 4,
    }

    /// <summary>Color source for <see cref="VRStageLighting_AudioLink_RealtimeLight"/>.</summary>
    public enum ALRealtimeColorMode
    {
        Emission                = 0,
        ThemeColor0             = 1,
        ThemeColor1             = 2,
        ThemeColor2             = 3,
        ThemeColor3             = 4,
        ColorChord              = 5,
        ColorTexture            = 6,
        ColorTextureTraditional = 7,
    }
}
