using System.Runtime.InteropServices;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Rendering;

namespace VRSL
{
    /// <summary>
    /// Singleton manager for the AudioLink-driven GPU realtime light path.
    ///
    /// Discovers every VRStageLighting_AudioLink_RealtimeLight in the scene,
    /// uploads their per-frame config (position, forward direction, AudioLink params)
    /// to a GPU StructuredBuffer, and exposes the buffers and AudioLink RTHandle that
    /// VRSLAudioLinkRealtimeLightFeature drives through the render graph.
    ///
    /// Unlike VRSL_GPULightManager (DMX path), the config buffer is re-uploaded every
    /// frame because pan/tilt transforms are animated and their world-space forward
    /// direction changes continuously.
    ///
    /// Setup: add this component to any persistent scene GameObject, then assign the
    /// VRSLAudioLinkLightUpdate compute shader and Hidden/VRSL/DeferredLighting shader.
    /// </summary>
    [AddComponentMenu("VRSL/AudioLink GPU Light Manager")]
    public class VRSL_AudioLinkGPULightManager : MonoBehaviour
    {
        public static VRSL_AudioLinkGPULightManager Instance { get; private set; }

        [Header("Compute")]
        public ComputeShader computeShader;

        [Header("Lighting")]
        [Tooltip("Assign Hidden/VRSL/DeferredLighting (the VRSLDeferredLighting shader asset).")]
        public Shader lightingShader;

        [Header("Volumetric")]
        [Tooltip("Assign Hidden/VRSL/VolumetricLighting (the VRSLVolumetricLighting shader asset). "
               + "The volumetric raymarch pass runs whenever this is assigned and the renderer "
               + "feature is active — there is no separate enable toggle since the GPU prefab "
               + "path has no legacy mesh-cone shader to fall back to.")]
        public Shader volumetricShader;

        [Range(8, 64)]
        [Tooltip("Number of integration steps along each view ray. Higher = smoother, more cost. "
               + "Cost scales linearly with step count and active fixture count.")]
        public int volumetricStepCount = 32;

        [Range(0f, 2f)]
        [Tooltip("Base scattering density. Lower = subtler shafts; higher = denser haze. "
               + "Tune relative to scene scale.")]
        public float volumetricDensity = 0.1f;

        [Range(-0.95f, 0.95f)]
        [Tooltip("Henyey–Greenstein anisotropy. 0 = isotropic; positive values brighten when "
               + "looking down the beam; negative values back-scatter.")]
        public float volumetricAnisotropy = 0.2f;

        [Tooltip("Colour tint applied to the accumulated in-scattering. White = no tint.")]
        [ColorUsage(showAlpha: false, hdr: false)]
        public Color volumetricTint = Color.white;

        [Range(0f, 8f)]
        [Tooltip("Global intensity multiplier for the volumetric contribution. Multiplies on "
               + "top of the per-light intensity already encoded in _VRSLLights.")]
        public float volumetricIntensity = 1f;

        [Header("Gobo Wheel")]
        [Tooltip("Gobo textures shared by all AudioLink fixtures. Packed into a Texture2DArray. "
               + "Each fixture selects a slot via its Gobo Index field. -1 = no gobo (open beam).")]
        public Texture2D[] goboTextures;

        // ── Public API for the renderer feature ───────────────────────────────
        public GraphicsBuffer FixtureConfigBuffer { get; private set; }
        public GraphicsBuffer LightDataBuffer     { get; private set; }
        public RTHandle       AudioLinkHandle     { get; private set; }
        public Texture2DArray GoboArray           { get; private set; }
        public int  FixtureCount  { get; private set; }
        public int  GoboCount     { get; private set; }
        public int  ComputeKernel { get; private set; }
        public Material LightingMaterial   { get; private set; }
        public Material VolumetricMaterial { get; private set; }

        public Vector4 VolumetricStepParams =>
            new Vector4(volumetricStepCount, 0f, 0f, volumetricAnisotropy);
        public Vector4 VolumetricDensityParams =>
            new Vector4(volumetricDensity, 0f, 0f, 0f);
        public Vector4 VolumetricFogTintParams =>
            new Vector4(volumetricTint.r, volumetricTint.g, volumetricTint.b, volumetricIntensity);

        const int GoboResolution = 256;

        // ── Structs — must match VRSLLightingLibrary.hlsl exactly ─────────────
        // VRSLALFixtureConfig: 7 × float4 = 112 bytes
        [StructLayout(LayoutKind.Sequential)]
        internal struct VRSLALFixtureConfig
        {
            public Vector4 positionAndRange;  // xyz=world pos (per-frame), w=range
            public Vector4 forwardAndType;    // xyz=world forward (per-frame), w=light type
            public Vector4 intensityParams;   // x=maxIntensity, y=finalIntensity, zw=unused
            public Vector4 spotAngles;        // x=innerHalf(deg), y=outerHalf(deg), zw=unused
            public Vector4 alParams;          // x=band, y=delay, z=bandMultiplier, w=colorMode
            public Vector4 emissionColor;     // xyz=linear RGB, w=unused
            public Vector4 reserved;
        }

        // VRSLLightData stride mirror — 5 × float4 = 80 bytes.
        // Content is written by the compute shader; we only need the size here.
        [StructLayout(LayoutKind.Sequential)]
        struct LightDataStride
        {
            Vector4 a, b, c, d, e;
        }

        List<VRStageLighting_AudioLink_RealtimeLight> _fixtures = new();
        RenderTexture _cachedAudioTex;

        // ── Lifecycle ─────────────────────────────────────────────────────────
        void Awake()
        {
            if (Instance != null && Instance != this) { Destroy(this); return; }
            Instance = this;
        }

        void OnEnable()
        {
            RefreshFixtures();
        }

        void OnDisable()
        {
            ReleaseBuffers();
            ReleaseAudioLinkHandle();
        }

        void OnDestroy()
        {
            if (Instance == this) Instance = null;
        }

        void LateUpdate()
        {
            // Re-upload every frame — animated transforms change direction each frame.
            UploadFixtureConfigs();
            // Refresh RTHandle if AudioLink RenderTexture reference changed.
            TryRefreshAudioLinkHandle();
        }

        // ── Public ────────────────────────────────────────────────────────────
        /// <summary>Re-scan the scene for AudioLink realtime light fixtures and rebuild GPU buffers.
        /// Call after adding or removing fixture GameObjects at runtime.</summary>
        public void RefreshFixtures()
        {
            _fixtures.Clear();
            _fixtures.AddRange(FindObjectsByType<VRStageLighting_AudioLink_RealtimeLight>(
                FindObjectsSortMode.None));

            FixtureCount = _fixtures.Count;
            if (FixtureCount == 0) return;

            ReleaseBuffers();

            FixtureConfigBuffer = new GraphicsBuffer(
                GraphicsBuffer.Target.Structured,
                FixtureCount,
                Marshal.SizeOf<VRSLALFixtureConfig>());   // 112 bytes

            LightDataBuffer = new GraphicsBuffer(
                GraphicsBuffer.Target.Structured,
                FixtureCount,
                Marshal.SizeOf<LightDataStride>());        // 64 bytes

            if (computeShader != null)
                ComputeKernel = computeShader.FindKernel("UpdateLights");

            if (lightingShader != null && LightingMaterial == null)
                LightingMaterial = new Material(lightingShader) { hideFlags = HideFlags.HideAndDontSave };

            if (volumetricShader != null && VolumetricMaterial == null)
                VolumetricMaterial = new Material(volumetricShader) { hideFlags = HideFlags.HideAndDontSave };

            BuildGoboArray();
            TryRefreshAudioLinkHandle();
        }

        // ── Internal ──────────────────────────────────────────────────────────
        void UploadFixtureConfigs()
        {
            if (FixtureConfigBuffer == null || FixtureCount == 0) return;

            var configs = new VRSLALFixtureConfig[FixtureCount];
            for (int i = 0; i < FixtureCount; i++)
                configs[i] = BuildConfig(_fixtures[i]);
            FixtureConfigBuffer.SetData(configs);
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

        VRSLALFixtureConfig BuildConfig(VRStageLighting_AudioLink_RealtimeLight f)
        {
            Vector3 pos     = f.GetWorldPosition();
            Vector3 forward = f.GetWorldForward();

            int   lightType = f.isPointLight ? 1 : 0;
            float outerHalf = f.spotAngle * 0.5f;
            float innerHalf = outerHalf   * 0.5f;

            // Emission color must be in linear space to match the lighting shader's expectation.
            Color linearEmission = f.emissionColor.linear;

            return new VRSLALFixtureConfig
            {
                positionAndRange = new Vector4(pos.x, pos.y, pos.z, f.range),
                forwardAndType   = new Vector4(forward.x, forward.y, forward.z, lightType),
                // z = AudioLink active flag: 1 = sample AudioLink amplitude, 0 = static full intensity
                intensityParams  = new Vector4(
                    f.maxIntensity,
                    f.finalIntensity,
                    f.enableAudioLink ? 1f : 0f,
                    0f),
                spotAngles       = new Vector4(innerHalf, outerHalf, 0f, 0f),
                alParams         = new Vector4(
                    (int)f.band,
                    f.delay,
                    f.bandMultiplier,
                    (int)f.colorMode),
                emissionColor    = new Vector4(linearEmission.r, linearEmission.g, linearEmission.b, 0f),
                // reserved.x = gobo array index (0+ = slot); the inspector field is 1-based to
                // match the established AudioLink Static convention (1 = open beam).
                reserved         = new Vector4(f.goboIndex - 1f, f.goboSpinSpeed, 0f, 0f),
            };
        }

        void TryRefreshAudioLinkHandle()
        {
            var tex = Shader.GetGlobalTexture("_AudioTexture") as RenderTexture;
            if (tex == _cachedAudioTex) return;

            ReleaseAudioLinkHandle();
            _cachedAudioTex = tex;
            if (_cachedAudioTex != null)
                AudioLinkHandle = RTHandles.Alloc(_cachedAudioTex);
        }

        void ReleaseAudioLinkHandle()
        {
            RTHandles.Release(AudioLinkHandle);
            AudioLinkHandle  = null;
            _cachedAudioTex  = null;
        }

        void ReleaseBuffers()
        {
            FixtureConfigBuffer?.Release(); FixtureConfigBuffer = null;
            LightDataBuffer?.Release();     LightDataBuffer     = null;
            if (GoboArray != null) { Object.Destroy(GoboArray); GoboArray = null; }
        }

#if UNITY_EDITOR
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
    }
}
