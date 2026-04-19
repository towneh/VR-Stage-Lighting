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

        // ── Public API for the renderer feature ───────────────────────────────
        public GraphicsBuffer FixtureConfigBuffer { get; private set; }
        public GraphicsBuffer LightDataBuffer     { get; private set; }
        public RTHandle DMXMainHandle      { get; private set; }
        public RTHandle DMXMovementHandle  { get; private set; }
        public RTHandle DMXStrobeHandle    { get; private set; }
        public int  FixtureCount   { get; private set; }
        public int  ComputeKernel  { get; private set; }
        public Material LightingMaterial { get; private set; }

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

        // 4 × float4 = 64 bytes
        [StructLayout(LayoutKind.Sequential)]
        internal struct VRSLLightData
        {
            public Vector4 positionAndRange;
            public Vector4 directionAndType;
            public Vector4 colorAndIntensity;
            public Vector4 spotCosines;
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
                Marshal.SizeOf<VRSLLightData>());       // 64 bytes

            if (computeShader != null)
                ComputeKernel = computeShader.FindKernel("UpdateLights");

            if (lightingShader != null && LightingMaterial == null)
                LightingMaterial = new Material(lightingShader) { hideFlags = HideFlags.HideAndDontSave };

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
            Vector3 pos = f.enablePanTilt && f.panTransform != null
                ? f.panTransform.position
                : f.transform.position;

            // Base forward: pointing down for hanging fixtures (VRSL default).
            // Overridden by the associated Light component when present.
            Vector3 baseForward = -f.transform.up;
            Vector3 panAxis     = Vector3.up;

            int   lightType = 0;
            float range     = 20f;
            float innerHalf = 15f;
            float outerHalf = 30f;

            if (f.realtimeLight != null)
            {
                lightType   = f.realtimeLight.type == LightType.Spot ? 0 : 1;
                range       = f.realtimeLight.range;
                baseForward = f.realtimeLight.transform.forward;

                if (f.realtimeLight.type == LightType.Spot)
                {
                    outerHalf = f.realtimeLight.spotAngle * 0.5f;
                    innerHalf = f.realtimeLight.innerSpotAngle * 0.5f;
                }
            }

            return new VRSLFixtureConfig
            {
                positionAndRange  = new Vector4(pos.x, pos.y, pos.z, range),
                forwardAndType    = new Vector4(baseForward.x, baseForward.y, baseForward.z, lightType),
                upAndMaxIntensity = new Vector4(panAxis.x, panAxis.y, panAxis.z, f.maxIntensity),
                spotAngles        = new Vector4(innerHalf, outerHalf, f.finalIntensity, 0f),
                dmxChannel        = new Vector4(
                    f.ComputeAbsoluteChannel(),
                    f.enableStrobe       ? 1f : 0f,
                    f.enablePanTilt      ? 1f : 0f,
                    f.enableFineChannels ? 1f : 0f),
                panSettings  = new Vector4(f.maxMinPan,  f.panOffset,  f.invertPan  ? 1f : 0f, 0f),
                tiltSettings = new Vector4(f.maxMinTilt, f.tiltOffset, f.invertTilt ? 1f : 0f, 0f),
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

        void ReleaseBuffers()
        {
            FixtureConfigBuffer?.Release(); FixtureConfigBuffer = null;
            LightDataBuffer?.Release();     LightDataBuffer     = null;
        }
    }
}
