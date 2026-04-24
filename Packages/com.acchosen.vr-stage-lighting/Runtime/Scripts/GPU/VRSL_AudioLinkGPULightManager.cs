using System.Runtime.InteropServices;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Rendering;
using System.Linq;

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

        // ── Public API for the renderer feature ───────────────────────────────
        public GraphicsBuffer FixtureConfigBuffer { get; private set; }
        public GraphicsBuffer LightDataBuffer     { get; private set; }
        public RTHandle       AudioLinkHandle     { get; private set; }
        public Texture2DArray CookieArray         { get; private set; }
        public int  FixtureCount  { get; private set; }
        public int  ComputeKernel { get; private set; }
        public Material LightingMaterial { get; private set; }

        const int CookieResolution = 256;

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
        Dictionary<Texture2D, int> _cookieIndexMap = new();
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

            BuildCookieArray();
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

        void BuildCookieArray()
        {
            _cookieIndexMap.Clear();
            if (CookieArray != null) { Object.Destroy(CookieArray); CookieArray = null; }

            var unique = _fixtures
                .Select(f => f.cookieTexture)
                .Where(t => t != null)
                .Distinct()
                .ToList();

            int slices = Mathf.Max(1, unique.Count);
            CookieArray = new Texture2DArray(CookieResolution, CookieResolution, slices,
                TextureFormat.RGBA32, false) { hideFlags = HideFlags.HideAndDontSave };

            if (unique.Count == 0)
            {
                // Single white slice — no fixture has a cookie but shader still needs a valid binding
                var white = new Color[CookieResolution * CookieResolution];
                for (int i = 0; i < white.Length; i++) white[i] = Color.white;
                CookieArray.SetPixels(white, 0);
            }
            else
            {
                var tmp = RenderTexture.GetTemporary(CookieResolution, CookieResolution, 0,
                    RenderTextureFormat.ARGB32, RenderTextureReadWrite.Linear);
                var readback = new Texture2D(CookieResolution, CookieResolution,
                    TextureFormat.RGBA32, false);
                var prevRT = RenderTexture.active;

                for (int i = 0; i < unique.Count; i++)
                {
                    _cookieIndexMap[unique[i]] = i;
                    Graphics.Blit(unique[i], tmp);
                    RenderTexture.active = tmp;
                    readback.ReadPixels(new Rect(0, 0, CookieResolution, CookieResolution), 0, 0);
                    readback.Apply();
                    CookieArray.SetPixels(readback.GetPixels(), i);
                }

                RenderTexture.active = prevRT;
                Object.Destroy(readback);
                RenderTexture.ReleaseTemporary(tmp);
            }

            CookieArray.Apply();
        }

        VRSLALFixtureConfig BuildConfig(VRStageLighting_AudioLink_RealtimeLight f)
        {
            Vector3 pos     = f.GetWorldPosition();
            Vector3 forward = f.GetWorldForward();

            int   lightType = f.isPointLight ? 1 : 0;
            float outerHalf = f.spotAngle * 0.5f;
            float innerHalf = outerHalf   * 0.5f;

            // Emission color must be in linear space to match the lighting shader's expectation
            Color linearEmission = f.emissionColor.linear;

            return new VRSLALFixtureConfig
            {
                positionAndRange = new Vector4(pos.x, pos.y, pos.z, f.range),
                forwardAndType   = new Vector4(forward.x, forward.y, forward.z, lightType),
                // z = AudioLink active flag: 1 = sample AudioLink amplitude, 0 = static full intensity
                intensityParams  = new Vector4(f.maxIntensity, f.finalIntensity, f.enableAudioLink ? 1f : 0f, 0f),
                spotAngles       = new Vector4(innerHalf, outerHalf, 0f, 0f),
                alParams         = new Vector4((int)f.band, f.delay, f.bandMultiplier, (int)f.colorMode),
                emissionColor    = new Vector4(linearEmission.r, linearEmission.g, linearEmission.b, 0f),
                // reserved.x = cookie slice index (-1 = no cookie), reserved.y = gobo spin speed
                reserved         = new Vector4(
                    f.cookieTexture != null && _cookieIndexMap.TryGetValue(f.cookieTexture, out int ci)
                        ? ci : -1f,
                    f.cookieSpinSpeed,
                    0f, 0f),
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
            if (CookieArray != null) { Object.Destroy(CookieArray); CookieArray = null; }
        }
    }
}
