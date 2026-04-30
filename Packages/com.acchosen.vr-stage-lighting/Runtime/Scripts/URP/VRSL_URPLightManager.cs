using System.Runtime.InteropServices;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Rendering;

namespace VRSL
{
    /// <summary>Render resolution for the VRSL volumetric raymarch pass.
    /// Half is the default — half-resolution raymarch with bilateral upsample,
    /// suited to live VR. Full runs the raymarch at the camera target resolution
    /// and additively blends the result; ~4× per-pixel cost but no upsample
    /// artefacts, suited to cinematic capture and high-perf desktop targets.
    /// </summary>
    public enum VolumetricResolution
    {
        Half = 0,
        Full = 1,
    }

    /// <summary>
    /// Singleton manager for the URP realtime light path (DMX data source).
    ///
    /// Collects every VRStageLighting_DMX_RealtimeLight in the scene, uploads
    /// their static configuration to a GPU StructuredBuffer once (and again
    /// whenever a fixture's settings change), and exposes the persistent
    /// GraphicsBuffers and DMX texture RTHandles that VRSLRealtimeLightFeature
    /// drives through the render graph.
    ///
    /// Setup: add this component to any scene object, assign the three VRSL
    /// CustomRenderTextures, and assign the VRSLDMXLightUpdate compute shader.
    /// </summary>
    [AddComponentMenu("VRSL/URP Light Manager")]
    public class VRSL_URPLightManager : MonoBehaviour
    {
        public static VRSL_URPLightManager Instance { get; private set; }

        // ── Inspector ─────────────────────────────────────────────────────────
        [Header("DMX Render Textures")]
        public RenderTexture dmxMainTexture;
        public RenderTexture dmxMovementTexture;
        public RenderTexture dmxStrobeTexture;
        [Tooltip("SpinnerTimer CRT (the CRT fed by DMXRTShader-SpinnerTimer). The URP path "
               + "samples its accumulated phase to drive gobo spin, matching the volumetric "
               + "shader's getGoboSpinSpeed() so rate changes don't cause visible jumps.")]
        public RenderTexture dmxSpinTimerTexture;

        [Header("Compute")]
        public ComputeShader computeShader;

        [Header("Lighting")]
        [Tooltip("Assign Hidden/VRSL/DeferredLighting (the VRSLDeferredLighting shader asset).")]
        public Shader lightingShader;

        [Header("Volumetric")]
        [Tooltip("Assign Hidden/VRSL/VolumetricLighting (the VRSLVolumetricLighting shader asset). "
               + "The volumetric raymarch pass runs whenever this is assigned and the renderer "
               + "feature is active — there is no separate enable toggle since the URP prefab "
               + "path has no legacy mesh-cone shader to fall back to.")]
        public Shader volumetricShader;

        [Tooltip("Render resolution for the raymarch. Half is half-res with bilateral upsample "
               + "(default; right for live VR). Full runs the raymarch at the camera target "
               + "resolution and additively blends — ~4× per-pixel cost, no upsample artefacts, "
               + "suited to cinematic capture or high-perf desktop targets.")]
        public VolumetricResolution volumetricResolution = VolumetricResolution.Half;

        [Range(8, 64)]
        [Tooltip("Number of integration steps along each view ray. Higher = smoother, more cost. "
               + "Cost scales linearly with step count and active fixture count.")]
        public int volumetricStepCount = 32;

        [Range(0f, 2f)]
        [Tooltip("Base scattering density. Lower = subtler shafts; higher = denser haze. "
               + "Tune relative to scene scale.")]
        public float volumetricDensity = 0.1f;

        [Range(-0.95f, 0.95f)]
        [Tooltip("Henyey–Greenstein anisotropy. 0 = isotropic (cones look the same from any "
               + "angle); positive values brighten when looking down the beam; negative values "
               + "back-scatter.")]
        public float volumetricAnisotropy = 0.2f;

        [Tooltip("Colour tint applied to the accumulated in-scattering. White = no tint.")]
        [ColorUsage(showAlpha: false, hdr: false)]
        public Color volumetricTint = Color.white;

        [Range(0f, 8f)]
        [Tooltip("Global intensity multiplier for the volumetric contribution. Multiplies on "
               + "top of the per-light intensity already encoded in _VRSLLights.")]
        public float volumetricIntensity = 1f;

        [Tooltip("Couple density and tint to URP scene fog. When on, density is multiplied by "
               + "unity_FogParams.x (the scene fog coefficient) and tint by unity_FogColor — so "
               + "raising scene fog density brightens the shafts and turning fog off hides them. "
               + "When off, the manager's density and tint values are used directly. Most useful "
               + "when the project drives haze level globally from a URP VolumeProfile.")]
        public bool coupleToSceneFog = false;

        [Header("Volumetric — Modulated Density")]
        [Tooltip("Multiply density by 3D world-space noise to approximate dusty stage haze. "
               + "When off, the noise code is compiled out of the shader and there is no cost. "
               + "Adds roughly 5–10% to the raymarch pass on desktop VR when on.")]
        public bool volumetricUseNoise = true;

        [Range(0.05f, 2f)]
        [Tooltip("Spatial frequency of the dust noise in world units. Higher = finer patches; "
               + "lower = larger blobs.")]
        public float volumetricNoiseScale = 0.3f;

        [Range(0f, 2f)]
        [Tooltip("Vertical drift speed of the noise in world units per second. 0 = static.")]
        public float volumetricNoiseScrollSpeed = 0.1f;

        [Range(0f, 1f)]
        [Tooltip("How strongly the noise modulates density. 0 = clean uniform; "
               + "1 = density drops to zero in the darkest patches.")]
        public float volumetricNoiseStrength = 0.7f;

        [Header("Gobo Wheel")]
        [Tooltip("Gobo textures available to all DMX fixtures. Packed into a shared Texture2DArray. "
               + "DMX channel +11 selects the slot (0 = open/no gobo). Order matches DMX value range.")]
        public Texture2D[] goboTextures;

        // ── Public API for the renderer feature ───────────────────────────────
        public GraphicsBuffer  FixtureConfigBuffer { get; private set; }
        public GraphicsBuffer  LightDataBuffer     { get; private set; }
        public RTHandle        DMXMainHandle       { get; private set; }
        public RTHandle        DMXMovementHandle   { get; private set; }
        public RTHandle        DMXStrobeHandle     { get; private set; }
        public RTHandle        DMXSpinTimerHandle  { get; private set; }
        public Texture2DArray  GoboArray           { get; private set; }
        public int  FixtureCount   { get; private set; }
        public int  GoboCount      { get; private set; }
        public int  ComputeKernel  { get; private set; }
        public Material LightingMaterial   { get; private set; }
        public Material VolumetricMaterial { get; private set; }

        // Volumetric shader parameter packing — read by VRSLRealtimeLightFeature each frame
        // and uploaded as global vectors before the raymarch pass.
        public Vector4 VolumetricStepParams =>
            new Vector4(volumetricStepCount, coupleToSceneFog ? 1f : 0f, 0f, volumetricAnisotropy);
        public Vector4 VolumetricDensityParams =>
            new Vector4(volumetricDensity, volumetricNoiseScale,
                        volumetricNoiseScrollSpeed, volumetricNoiseStrength);
        public Vector4 VolumetricFogTintParams =>
            new Vector4(volumetricTint.r, volumetricTint.g, volumetricTint.b, volumetricIntensity);
        public bool VolumetricUseNoise => volumetricUseNoise;
        public bool VolumetricUseFullRes => volumetricResolution == VolumetricResolution.Full;

        const int GoboResolution = 256;

        // ── Structs — must match VRSLLightingLibrary.hlsl exactly ─────────────
        // 8 × float4 = 128 bytes
        [StructLayout(LayoutKind.Sequential)]
        internal struct VRSLFixtureConfig
        {
            public Vector4 positionAndRange;    // xyz=pos,     w=range
            public Vector4 forwardAndType;      // xyz=forward, w=lightType(0=spot,1=point)
            public Vector4 rightAndMaxIntensity;// xyz=local +X in world space (tilt axis), w=maxIntensity
            public Vector4 spotAngles;          // x=innerRatio(0..1), y=maxOuterHalf(deg),
                                               //   z=finalIntensity,    w=minOuterHalf(deg)
            public Vector4 dmxChannel;          // x=absChannel, y=enableStrobe,
                                               //   z=enablePanTilt, w=enableFineChannels
            public Vector4 panSettings;         // x=maxMinPan, y=panOffset, z=invertPan, w=enableGoboSpin
            public Vector4 tiltSettings;        // x=maxMinTilt,y=tiltOffset,z=invertTilt,w=enableGobo
            public Vector4 extras;              // x=emitterDepth(m), yzw=reserved
        }

        // 5 × float4 = 80 bytes
        [StructLayout(LayoutKind.Sequential)]
        internal struct VRSLLightData
        {
            public Vector4 positionAndRange;
            public Vector4 directionAndType;
            public Vector4 colorAndIntensity;
            public Vector4 spotCosines;
            public Vector4 goboAndSpin;
        }

        List<VRStageLighting_DMX_RealtimeLight> _fixtures = new();
        bool _configDirty = true;

#if UNITY_EDITOR
        // Called by Unity when the component is first added or the context-menu Reset is chosen.
        void Reset() => LoadDefaultGoboWheel();

        [ContextMenu("Load Default Gobo Wheel")]
        void LoadDefaultGoboWheel()
        {
            const string folder =
                "Packages/com.acchosen.vr-stage-lighting/Runtime/Textures/MoverLightTextures/GOBO/IndividualGobos";

            var guids = UnityEditor.AssetDatabase.FindAssets("t:Texture2D", new[] { folder });
            var list  = new List<Texture2D>();
            foreach (var guid in guids)
            {
                var tex = UnityEditor.AssetDatabase.LoadAssetAtPath<Texture2D>(
                    UnityEditor.AssetDatabase.GUIDToAssetPath(guid));
                if (tex != null) list.Add(tex);
            }

            // Default gobo first (matches shader slot 1 = lowest DMX values), then alphabetically
            list.Sort((a, b) =>
            {
                bool aD = a.name.Contains("Default");
                bool bD = b.name.Contains("Default");
                if (aD != bD) return aD ? -1 : 1;
                return string.Compare(a.name, b.name, System.StringComparison.Ordinal);
            });

            goboTextures = list.ToArray();
            UnityEditor.EditorUtility.SetDirty(this);
        }
#endif

        // ── Lifecycle ─────────────────────────────────────────────────────────
        void Awake()
        {
            if (Instance != null && Instance != this) { Destroy(this); return; }
            Instance = this;
        }

        void OnEnable()
        {
            CreateTextureHandles();
            RefreshFixtures();
        }

        void OnDisable()
        {
            ReleaseBuffers();
            ReleaseTextureHandles();
        }

        void OnDestroy()
        {
            if (Instance == this) Instance = null;
        }

        void LateUpdate()
        {
            if (_configDirty)
            {
                UploadFixtureConfigs();
                _configDirty = false;
            }
        }

        // ── Public ────────────────────────────────────────────────────────────
        /// <summary>Re-scan the scene for VRStageLighting_DMX_RealtimeLight components
        /// and rebuild the GPU buffers. Call after adding/removing fixtures at runtime.</summary>
        public void RefreshFixtures()
        {
            _fixtures.Clear();
            _fixtures.AddRange(FindObjectsByType<VRStageLighting_DMX_RealtimeLight>(
                FindObjectsSortMode.None));

            FixtureCount = _fixtures.Count;
            if (FixtureCount == 0) return;

            ReleaseBuffers();
            FixtureConfigBuffer = new GraphicsBuffer(
                GraphicsBuffer.Target.Structured,
                FixtureCount,
                Marshal.SizeOf<VRSLFixtureConfig>());   // 112 bytes

            LightDataBuffer = new GraphicsBuffer(
                GraphicsBuffer.Target.Structured,
                FixtureCount,
                Marshal.SizeOf<VRSLLightData>());       // 80 bytes

            if (computeShader != null)
                ComputeKernel = computeShader.FindKernel("UpdateLights");

            if (lightingShader != null && LightingMaterial == null)
                LightingMaterial = new Material(lightingShader) { hideFlags = HideFlags.HideAndDontSave };

            if (volumetricShader != null && VolumetricMaterial == null)
                VolumetricMaterial = new Material(volumetricShader) { hideFlags = HideFlags.HideAndDontSave };

            BuildGoboArray();
            _configDirty = true;
        }

        /// <summary>Mark config dirty so it is re-uploaded next LateUpdate.</summary>
        public void MarkConfigDirty() => _configDirty = true;

        // ── Internal ──────────────────────────────────────────────────────────
        void UploadFixtureConfigs()
        {
            if (FixtureConfigBuffer == null || FixtureCount == 0) return;

            var configs = new VRSLFixtureConfig[FixtureCount];
            for (int i = 0; i < FixtureCount; i++)
                configs[i] = BuildConfig(_fixtures[i]);

            FixtureConfigBuffer.SetData(configs);
        }

        VRSLFixtureConfig BuildConfig(VRStageLighting_DMX_RealtimeLight f)
        {
            Vector3 pos = f.transform.position;

            // Use the fixture's declared local light axis (defaults to forward for moving heads).
            // Par cans and similar fixtures whose lens faces local +Y use Vector3.up here.
            Vector3 localDir    = f.localLightDirection.sqrMagnitude > 0f
                                      ? f.localLightDirection.normalized
                                      : Vector3.forward;
            Vector3 baseForward = f.transform.TransformDirection(localDir);
            // Local +X in world space — used by the compute shader as the tilt rotation axis.
            // The volumetric mover shader rotates object-space X by the tilt matrix; we need the
            // same axis in world space since the compute shader has no ObjectToWorld matrix.
            Vector3 baseRight   = f.transform.right;

            int   lightType    = f.isPointLight ? 1 : 0;
            float outerHalf    = f.maxSpotAngle * 0.5f;
            // When enableConeWidth is false, collapse minOuter == outerHalf so the compute
            // shader's lerp over DMX ch+4 is a no-op and the cone stays fixed at maxSpotAngle.
            float minOuterHalf = f.enableConeWidth ? f.minSpotAngle * 0.5f : outerHalf;
            // Inner-to-outer ratio (0..1). Wash movers keep most of the cone bright with
            // a longer soft feather at the outer edge — broad diffuse beam without reading
            // as a flat disc. Spotlights and statics use 0.5 so the falloff occupies the
            // outer half of the cone. The compute shader applies this ratio against the
            // dynamic outer half-angle (which DMX ch+4 lerps between min and max), so the
            // inner cone tracks any cone-width changes the fixture makes at runtime.
            float innerRatio   = f.fixtureType == DMXFixtureType.MoverWashlight ? 0.65f : 0.5f;

            return new VRSLFixtureConfig
            {
                positionAndRange     = new Vector4(pos.x, pos.y, pos.z, f.range),
                forwardAndType       = new Vector4(baseForward.x, baseForward.y, baseForward.z, lightType),
                rightAndMaxIntensity = new Vector4(baseRight.x, baseRight.y, baseRight.z, f.maxIntensity),
                // spotAngles.x = inner-to-outer ratio (0..1) — applied to the dynamic
                // outer half-angle in the compute shader so it tracks ch+4 cone width.
                // spotAngles.y = max outer half-angle, spotAngles.w = min outer half-angle.
                // spotAngles.z carries the combined finalIntensity × globalIntensity scalar
                // (folded CPU-side so the compute shader stays oblivious to the split).
                spotAngles        = new Vector4(innerRatio, outerHalf,
                                                f.finalIntensity * f.globalIntensity, minOuterHalf),
                dmxChannel        = new Vector4(
                    f.ComputeAbsoluteChannel(),
                    f.enableStrobe       ? 1f : 0f,
                    f.enablePanTilt      ? 1f : 0f,
                    f.enableFineChannels ? 1f : 0f),
                panSettings  = new Vector4(
                    f.maxMinPan,
                    f.panOffset,
                    f.invertPan      ? 1f : 0f,
                    f.enableGoboSpin ? 1f : 0f),
                // Subtract 90° from tiltOffset: baseForward for moving heads already points
                // world -Y (via the 90° X root rotation), so the Rodrigues default is not re-applied.
                tiltSettings = new Vector4(
                    f.maxMinTilt,
                    f.tiltOffset - 90f,
                    f.invertTilt ? 1f : 0f,
                    f.enableGobo ? 1f : 0f),
                extras       = new Vector4(f.emitterDepth, 0f, 0f, 0f),
            };
        }

        void CreateTextureHandles()
        {
            ReleaseTextureHandles();
            if (dmxMainTexture      != null) DMXMainHandle      = RTHandles.Alloc(dmxMainTexture);
            if (dmxMovementTexture  != null) DMXMovementHandle  = RTHandles.Alloc(dmxMovementTexture);
            if (dmxStrobeTexture    != null) DMXStrobeHandle    = RTHandles.Alloc(dmxStrobeTexture);
            if (dmxSpinTimerTexture != null) DMXSpinTimerHandle = RTHandles.Alloc(dmxSpinTimerTexture);
        }

        void ReleaseTextureHandles()
        {
            RTHandles.Release(DMXMainHandle);      DMXMainHandle      = null;
            RTHandles.Release(DMXMovementHandle);  DMXMovementHandle  = null;
            RTHandles.Release(DMXStrobeHandle);    DMXStrobeHandle    = null;
            RTHandles.Release(DMXSpinTimerHandle); DMXSpinTimerHandle = null;
        }

        void BuildGoboArray()
        {
            if (GoboArray != null) { Object.Destroy(GoboArray); GoboArray = null; }

            GoboCount = goboTextures != null ? goboTextures.Length : 0;
            if (GoboCount == 0) return;

            GoboArray = new Texture2DArray(GoboResolution, GoboResolution, GoboCount,
                TextureFormat.RGBA32, false) { hideFlags = HideFlags.HideAndDontSave };

            var tmp      = RenderTexture.GetTemporary(GoboResolution, GoboResolution, 0,
                               RenderTextureFormat.ARGB32, RenderTextureReadWrite.Linear);
            var readback = new Texture2D(GoboResolution, GoboResolution, TextureFormat.RGBA32, false);
            var prevRT   = RenderTexture.active;

            for (int i = 0; i < GoboCount; i++)
            {
                if (goboTextures[i] == null) continue;
                Graphics.Blit(goboTextures[i], tmp);
                RenderTexture.active = tmp;
                readback.ReadPixels(new Rect(0, 0, GoboResolution, GoboResolution), 0, 0);
                readback.Apply();
                GoboArray.SetPixels(readback.GetPixels(), i);
            }

            RenderTexture.active = prevRT;
            Object.Destroy(readback);
            RenderTexture.ReleaseTemporary(tmp);
            GoboArray.Apply();
        }

        void ReleaseBuffers()
        {
            FixtureConfigBuffer?.Release(); FixtureConfigBuffer = null;
            LightDataBuffer?.Release();     LightDataBuffer     = null;
            if (GoboArray != null) { Object.Destroy(GoboArray); GoboArray = null; }
        }
    }
}
