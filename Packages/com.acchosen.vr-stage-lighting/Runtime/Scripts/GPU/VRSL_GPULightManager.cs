using System.Runtime.InteropServices;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Rendering;

namespace VRSL
{
    /// <summary>
    /// Singleton manager for the GPU-driven VRSL realtime light path.
    ///
    /// Collects every VRStageLighting_DMX_RealtimeLight in the scene, uploads
    /// their static configuration to a GPU StructuredBuffer once (and again
    /// whenever a fixture's settings change), and exposes the two persistent
    /// GraphicsBuffers and the three DMX texture RTHandles that
    /// VRSLRealtimeLightFeature drives through the render graph.
    ///
    /// Setup: add this component to any scene object, assign the three VRSL
    /// CustomRenderTextures, and assign the VRSLDMXLightUpdate compute shader.
    /// </summary>
    [AddComponentMenu("VRSL/GPU Light Manager")]
    public class VRSL_GPULightManager : MonoBehaviour
    {
        public static VRSL_GPULightManager Instance { get; private set; }

        // ── Inspector ─────────────────────────────────────────────────────────
        [Header("DMX Render Textures")]
        public RenderTexture dmxMainTexture;
        public RenderTexture dmxMovementTexture;
        public RenderTexture dmxStrobeTexture;

        [Header("Compute")]
        public ComputeShader computeShader;

        [Header("Lighting")]
        [Tooltip("Assign Hidden/VRSL/DeferredLighting (the VRSLDeferredLighting shader asset).")]
        public Shader lightingShader;

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
        public Texture2DArray  CookieArray         { get; private set; }
        public int  FixtureCount   { get; private set; }
        public int  GoboCount      { get; private set; }
        public int  ComputeKernel  { get; private set; }
        public Material LightingMaterial { get; private set; }

        const int CookieResolution = 256;

        // ── Structs — must match VRSLLightingLibrary.hlsl exactly ─────────────
        // 7 × float4 = 112 bytes
        [StructLayout(LayoutKind.Sequential)]
        internal struct VRSLFixtureConfig
        {
            public Vector4 positionAndRange;    // xyz=pos,     w=range
            public Vector4 forwardAndType;      // xyz=forward, w=lightType(0=spot,1=point)
            public Vector4 upAndMaxIntensity;   // xyz=panAxis, w=maxIntensity
            public Vector4 spotAngles;          // x=innerHalf(deg), y=outerHalf(deg),
                                               //   z=finalIntensity, w=unused
            public Vector4 dmxChannel;          // x=absChannel, y=enableStrobe,
                                               //   z=enablePanTilt, w=enableFineChannels
            public Vector4 panSettings;         // x=maxMinPan, y=panOffset, z=invertPan, w=unused
            public Vector4 tiltSettings;        // x=maxMinTilt,y=tiltOffset,z=invertTilt,w=unused
        }

        // 5 × float4 = 80 bytes
        [StructLayout(LayoutKind.Sequential)]
        internal struct VRSLLightData
        {
            public Vector4 positionAndRange;
            public Vector4 directionAndType;
            public Vector4 colorAndIntensity;
            public Vector4 spotCosines;
            public Vector4 cookieAndSpin;
        }

        List<VRStageLighting_DMX_RealtimeLight> _fixtures = new();
        bool _configDirty = true;

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

            // transform.forward = world -Y for a standard 90° X root fixture (no Light needed).
            Vector3 baseForward = f.transform.forward;
            Vector3 panAxis     = Vector3.up;

            int   lightType    = f.isPointLight ? 1 : 0;
            float minOuterHalf = f.minSpotAngle * 0.5f;
            float outerHalf    = f.maxSpotAngle * 0.5f;
            float innerHalf    = outerHalf * 0.5f;

            return new VRSLFixtureConfig
            {
                positionAndRange  = new Vector4(pos.x, pos.y, pos.z, f.range),
                forwardAndType    = new Vector4(baseForward.x, baseForward.y, baseForward.z, lightType),
                upAndMaxIntensity = new Vector4(panAxis.x, panAxis.y, panAxis.z, f.maxIntensity),
                // spotAngles.y = max outer half-angle, spotAngles.w = min outer half-angle.
                // The compute shader lerps between w and y based on DMX ch+4.
                spotAngles        = new Vector4(innerHalf, outerHalf, f.finalIntensity, minOuterHalf),
                dmxChannel        = new Vector4(
                    f.ComputeAbsoluteChannel(),
                    f.enableStrobe       ? 1f : 0f,
                    f.enablePanTilt      ? 1f : 0f,
                    f.enableFineChannels ? 1f : 0f),
                panSettings  = new Vector4(f.maxMinPan,  f.panOffset,  f.invertPan  ? 1f : 0f, 0f),
                // Subtract 90° from tiltOffset: baseForward already points down (world -Y)
                // via transform.forward, so the 90° Rodrigues default must not be re-applied.
                tiltSettings = new Vector4(f.maxMinTilt, f.tiltOffset - 90f, f.invertTilt ? 1f : 0f, 0f),
            };
        }

        void CreateTextureHandles()
        {
            ReleaseTextureHandles();
            if (dmxMainTexture     != null) DMXMainHandle     = RTHandles.Alloc(dmxMainTexture);
            if (dmxMovementTexture != null) DMXMovementHandle = RTHandles.Alloc(dmxMovementTexture);
            if (dmxStrobeTexture   != null) DMXStrobeHandle   = RTHandles.Alloc(dmxStrobeTexture);
        }

        void ReleaseTextureHandles()
        {
            RTHandles.Release(DMXMainHandle);     DMXMainHandle     = null;
            RTHandles.Release(DMXMovementHandle); DMXMovementHandle = null;
            RTHandles.Release(DMXStrobeHandle);   DMXStrobeHandle   = null;
        }

        void BuildGoboArray()
        {
            if (CookieArray != null) { Object.Destroy(CookieArray); CookieArray = null; }

            GoboCount = goboTextures != null ? goboTextures.Length : 0;
            if (GoboCount == 0) return;

            CookieArray = new Texture2DArray(CookieResolution, CookieResolution, GoboCount,
                TextureFormat.RGBA32, false) { hideFlags = HideFlags.HideAndDontSave };

            var tmp      = RenderTexture.GetTemporary(CookieResolution, CookieResolution, 0,
                               RenderTextureFormat.ARGB32, RenderTextureReadWrite.Linear);
            var readback = new Texture2D(CookieResolution, CookieResolution, TextureFormat.RGBA32, false);
            var prevRT   = RenderTexture.active;

            for (int i = 0; i < GoboCount; i++)
            {
                if (goboTextures[i] == null) continue;
                Graphics.Blit(goboTextures[i], tmp);
                RenderTexture.active = tmp;
                readback.ReadPixels(new Rect(0, 0, CookieResolution, CookieResolution), 0, 0);
                readback.Apply();
                CookieArray.SetPixels(readback.GetPixels(), i);
            }

            RenderTexture.active = prevRT;
            Object.Destroy(readback);
            RenderTexture.ReleaseTemporary(tmp);
            CookieArray.Apply();
        }

        void ReleaseBuffers()
        {
            FixtureConfigBuffer?.Release(); FixtureConfigBuffer = null;
            LightDataBuffer?.Release();     LightDataBuffer     = null;
            if (CookieArray != null) { Object.Destroy(CookieArray); CookieArray = null; }
        }
    }
}
