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
        [Tooltip("SpinnerTimer CRT (the CRT fed by DMXRTShader-SpinnerTimer). The GPU path "
               + "samples its accumulated phase to drive gobo spin, matching the volumetric "
               + "shader's getGoboSpinSpeed() so rate changes don't cause visible jumps.")]
        public RenderTexture dmxSpinTimerTexture;

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
        public RTHandle        DMXSpinTimerHandle  { get; private set; }
        public Texture2DArray  GoboArray           { get; private set; }
        public int  FixtureCount   { get; private set; }
        public int  GoboCount      { get; private set; }
        public int  ComputeKernel  { get; private set; }
        public Material LightingMaterial { get; private set; }

        const int GoboResolution = 256;

        // ── Structs — must match VRSLLightingLibrary.hlsl exactly ─────────────
        // 7 × float4 = 112 bytes
        [StructLayout(LayoutKind.Sequential)]
        internal struct VRSLFixtureConfig
        {
            public Vector4 positionAndRange;    // xyz=pos,     w=range
            public Vector4 forwardAndType;      // xyz=forward, w=lightType(0=spot,1=point)
            public Vector4 rightAndMaxIntensity;// xyz=local +X in world space (tilt axis), w=maxIntensity
            public Vector4 spotAngles;          // x=innerHalf(deg), y=maxOuterHalf(deg),
                                               //   z=finalIntensity, w=minOuterHalf(deg)
            public Vector4 dmxChannel;          // x=absChannel, y=enableStrobe,
                                               //   z=enablePanTilt, w=enableFineChannels
            public Vector4 panSettings;         // x=maxMinPan, y=panOffset, z=invertPan, w=enableGoboSpin
            public Vector4 tiltSettings;        // x=maxMinTilt,y=tiltOffset,z=invertTilt,w=enableGobo
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
            float minOuterHalf = f.minSpotAngle * 0.5f;
            float outerHalf    = f.maxSpotAngle * 0.5f;
            float innerHalf    = outerHalf * 0.5f;

            return new VRSLFixtureConfig
            {
                positionAndRange     = new Vector4(pos.x, pos.y, pos.z, f.range),
                forwardAndType       = new Vector4(baseForward.x, baseForward.y, baseForward.z, lightType),
                rightAndMaxIntensity = new Vector4(baseRight.x, baseRight.y, baseRight.z, f.maxIntensity),
                // spotAngles.y = max outer half-angle, spotAngles.w = min outer half-angle.
                // The compute shader lerps between w and y based on DMX ch+4.
                spotAngles        = new Vector4(innerHalf, outerHalf, f.finalIntensity, minOuterHalf),
                dmxChannel        = new Vector4(
                    f.ComputeAbsoluteChannel(),
                    f.enableStrobe       ? 1f : 0f,
                    f.enablePanTilt      ? 1f : 0f,
                    f.enableFineChannels ? 1f : 0f),
                panSettings  = new Vector4(f.maxMinPan,  f.panOffset,  f.invertPan  ? 1f : 0f, f.enableGoboSpin ? 1f : 0f),
                // Subtract 90° from tiltOffset: baseForward for moving heads already points
                // world -Y (via the 90° X root rotation), so the Rodrigues default is not re-applied.
                tiltSettings = new Vector4(f.maxMinTilt, f.tiltOffset - 90f, f.invertTilt ? 1f : 0f, f.enableGobo ? 1f : 0f),
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
