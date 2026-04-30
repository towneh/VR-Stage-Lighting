using UnityEngine;
using UnityEngine.Experimental.Rendering;
using UnityEngine.Rendering;
using UnityEngine.Rendering.Universal;
using UnityEngine.Rendering.RenderGraphModule;

namespace VRSL
{
    /// <summary>
    /// Holds the three Render Graph pass classes that make up the VRSL URP
    /// AudioLink realtime-light pipeline:
    ///
    ///   1. ComputePass — dispatches VRSLAudioLinkLightUpdate.compute, samples the
    ///      AudioLink texture for amplitude and colour, reads world-space fixture
    ///      config from the StructuredBuffer, and writes VRSLLightData entries.
    ///
    ///   2. LightingPass — reuses Hidden/VRSL/DeferredLighting; identical to the
    ///      DMX realtime light path. The two paths share the same lighting shader
    ///      and the same _VRSLLights / _VRSLLightCount globals.
    ///
    ///   3. VolumetricPass — three Render Graph sub-passes (depth downsample,
    ///      half-res raymarch, bilateral upsample composite) using
    ///      Hidden/VRSL/VolumetricLighting.
    ///
    /// VRSL_AudioLinkURPLightManager subscribes to
    /// RenderPipelineManager.beginCameraRendering at runtime and enqueues these
    /// pass instances per camera, so this feature does not need to be added to
    /// the URP Renderer asset for the pipeline to run. The feature shell remains
    /// as a dormant fallback path; AddRenderPasses early-outs when the manager's
    /// useRuntimePassInjection flag is true (the default).
    ///
    /// Note: running the AudioLink path simultaneously with the DMX path on the
    /// same camera is not currently supported — both write to _VRSLLights /
    /// _VRSLLightCount and the last pass to execute wins. A future merged-buffer
    /// path can address this.
    /// </summary>
    [System.Serializable]
    public class VRSLAudioLinkRealtimeLightFeature : ScriptableRendererFeature
    {
        // ── Compute pass: AudioLink → light buffer ─────────────────────────────
        // Public so VRSL_AudioLinkURPLightManager can instantiate it directly when
        // running the runtime-injection path (RenderPipelineManager.beginCameraRendering)
        // instead of being driven through this feature.
        public class ComputePass : ScriptableRenderPass
        {
            class PassData
            {
                public BufferHandle  fixtureConfigBuffer;
                public BufferHandle  lightDataBuffer;
                public TextureHandle audioLinkTex;
                public ComputeShader cs;
                public int           kernel;
                public int           fixtureCount;
                public float         time;
            }

            public override void RecordRenderGraph(RenderGraph rg, ContextContainer frame)
            {
                var mgr = VRSL_AudioLinkURPLightManager.Instance;
                if (mgr == null || mgr.FixtureCount == 0
                    || mgr.computeShader    == null
                    || mgr.FixtureConfigBuffer == null
                    || mgr.AudioLinkHandle  == null) return;

                using var builder = rg.AddComputePass<PassData>("VRSL AudioLink Light Compute", out var d);

                d.fixtureConfigBuffer = rg.ImportBuffer(mgr.FixtureConfigBuffer);
                d.lightDataBuffer     = rg.ImportBuffer(mgr.LightDataBuffer);
                d.audioLinkTex        = rg.ImportTexture(mgr.AudioLinkHandle);
                d.cs                  = mgr.computeShader;
                d.kernel              = mgr.ComputeKernel;
                d.fixtureCount        = mgr.FixtureCount;
                // Captured here so the render graph lambda doesn't read Time.* off thread.
                // timeSinceLevelLoad resets on scene reload, which is the desirable behaviour
                // for gobo spin — phase restarts cleanly with the scene.
                d.time                = Time.timeSinceLevelLoad;

                builder.UseBuffer( d.fixtureConfigBuffer, AccessFlags.Read);
                builder.UseBuffer( d.lightDataBuffer,     AccessFlags.Write);
                builder.UseTexture(d.audioLinkTex,        AccessFlags.Read);

                builder.SetRenderFunc((PassData p, ComputeGraphContext ctx) =>
                {
                    var cmd = ctx.cmd;
                    cmd.SetComputeIntParam(    p.cs,           "_FixtureCount",       p.fixtureCount);
                    cmd.SetComputeFloatParam(  p.cs,           "_VRSLTime",           p.time);
                    cmd.SetComputeBufferParam( p.cs, p.kernel, "_ALFixtureConfigs",   p.fixtureConfigBuffer);
                    cmd.SetComputeBufferParam( p.cs, p.kernel, "_LightData",          p.lightDataBuffer);
                    cmd.SetComputeTextureParam(p.cs, p.kernel, "_AudioTexture",       p.audioLinkTex);
                    cmd.DispatchCompute(p.cs, p.kernel, Mathf.CeilToInt(p.fixtureCount / 64f), 1, 1);
                });
            }
        }

        // ── Fullscreen additive pass: illuminate scene geometry ────────────────
        // Identical to the DMX path's LightingPass — the deferred lighting shader
        // reads _VRSLLights / _VRSLLightCount regardless of which compute pass wrote them.
        public class LightingPass : ScriptableRenderPass
        {
            class PassData
            {
                public BufferHandle  lightDataBuffer;
                public TextureHandle depthTexture;
                public TextureHandle normalsTexture;
                public Material      material;
                public int           lightCount;
            }

            public override void RecordRenderGraph(RenderGraph rg, ContextContainer frame)
            {
                var mgr = VRSL_AudioLinkURPLightManager.Instance;
                if (mgr == null || mgr.FixtureCount == 0
                    || mgr.LightingMaterial == null
                    || mgr.LightDataBuffer  == null) return;

                var resources = frame.Get<UniversalResourceData>();

                if (!resources.cameraNormalsTexture.IsValid())
                {
                    Debug.LogWarning("[VRSL] AudioLink URP lighting requires 'Depth Normals Prepass' "
                        + "enabled on the URP Renderer asset.");
                    return;
                }

                using var builder = rg.AddRasterRenderPass<PassData>("VRSL AudioLink Lighting Pass", out var d);

                d.lightDataBuffer = rg.ImportBuffer(mgr.LightDataBuffer);
                d.depthTexture    = resources.cameraDepthTexture;
                d.normalsTexture  = resources.cameraNormalsTexture;
                d.material        = mgr.LightingMaterial;
                d.lightCount      = mgr.FixtureCount;

                builder.SetRenderAttachment(resources.activeColorTexture, 0, AccessFlags.ReadWrite);
                builder.UseBuffer( d.lightDataBuffer, AccessFlags.Read);
                builder.UseTexture(d.depthTexture,    AccessFlags.Read);
                builder.UseTexture(d.normalsTexture,  AccessFlags.Read);
                builder.AllowGlobalStateModification(true);

                builder.SetRenderFunc((PassData p, RasterGraphContext ctx) =>
                {
                    var cmd = ctx.cmd;
                    cmd.SetGlobalBuffer( "_VRSLLights",     p.lightDataBuffer);
                    cmd.SetGlobalInteger("_VRSLLightCount", p.lightCount);
                    cmd.DrawProcedural(Matrix4x4.identity, p.material, 0,
                        MeshTopology.Triangles, 3, 1);
                });
            }
        }

        // ── Volumetric pass: raymarched in-scattering ──────────────────────────
        // Mirrors VRSLRealtimeLightFeature.VolumetricPass — same shader, same
        // sub-pass structure. Records three Render Graph sub-passes (depth
        // downsample → half-res raymarch → bilateral upsample) reading the
        // _VRSLLights buffer the AudioLink ComputePass already wrote.
        public class VolumetricPass : ScriptableRenderPass
        {
            class DownsampleData
            {
                public Material      material;
                public TextureHandle fullDepth;
            }

            class RaymarchData
            {
                public Material      material;
                public TextureHandle halfDepth;
                public BufferHandle  lightDataBuffer;
                public int           lightCount;
                public Vector4       stepParams;
                public Vector4       densityParams;
                public Vector4       fogTintParams;
            }

            class UpsampleData
            {
                public Material      material;
                public TextureHandle halfRT;
                public TextureHandle halfDepth;
            }

            public override void RecordRenderGraph(RenderGraph rg, ContextContainer frame)
            {
                var mgr = VRSL_AudioLinkURPLightManager.Instance;
                if (mgr == null
                    || mgr.FixtureCount == 0
                    || mgr.VolumetricMaterial == null
                    || mgr.LightDataBuffer == null) return;

                if (mgr.VolumetricUseNoise)
                    mgr.VolumetricMaterial.EnableKeyword("_VRSL_VOLUMETRIC_NOISE");
                else
                    mgr.VolumetricMaterial.DisableKeyword("_VRSL_VOLUMETRIC_NOISE");

                var resources = frame.Get<UniversalResourceData>();
                var camData   = frame.Get<UniversalCameraData>();

                if (!resources.cameraDepthTexture.IsValid()) return;

                BufferHandle lightDataHandle = rg.ImportBuffer(mgr.LightDataBuffer);

                if (mgr.VolumetricUseFullRes)
                {
                    // Full-res path — single raymarch pass that samples the full
                    // depth texture and additive-blends onto the camera colour.
                    // Skips the depth downsample and bilateral upsample passes.
                    using (var builder = rg.AddRasterRenderPass<RaymarchData>(
                        "VRSL Vol Raymarch FullRes", out var d))
                    {
                        d.material        = mgr.VolumetricMaterial;
                        d.halfDepth       = TextureHandle.nullHandle;
                        d.lightDataBuffer = lightDataHandle;
                        d.lightCount      = mgr.FixtureCount;
                        d.stepParams      = mgr.VolumetricStepParams;
                        d.densityParams   = mgr.VolumetricDensityParams;
                        d.fogTintParams   = mgr.VolumetricFogTintParams;

                        builder.SetRenderAttachment(resources.activeColorTexture, 0, AccessFlags.ReadWrite);
                        builder.UseBuffer(d.lightDataBuffer, AccessFlags.Read);
                        builder.UseTexture(resources.cameraDepthTexture, AccessFlags.Read);
                        builder.AllowGlobalStateModification(true);

                        builder.SetRenderFunc((RaymarchData p, RasterGraphContext ctx) =>
                        {
                            var cmd = ctx.cmd;
                            cmd.SetGlobalBuffer( "_VRSLLights",       p.lightDataBuffer);
                            cmd.SetGlobalInteger("_VRSLLightCount",   p.lightCount);
                            cmd.SetGlobalVector( "_VRSLVolStepCount", p.stepParams);
                            cmd.SetGlobalVector( "_VRSLVolDensity",   p.densityParams);
                            cmd.SetGlobalVector( "_VRSLVolFogTint",   p.fogTintParams);
                            cmd.DrawProcedural(Matrix4x4.identity, p.material, 3,
                                MeshTopology.Triangles, 3, 1);
                        });
                    }
                    return;
                }

                int halfW = Mathf.Max(1, camData.cameraTargetDescriptor.width  / 2);
                int halfH = Mathf.Max(1, camData.cameraTargetDescriptor.height / 2);

                var halfDepthDesc = new TextureDesc(halfW, halfH)
                {
                    name        = "VRSL Volumetric Half Depth",
                    format      = GraphicsFormat.R32_SFloat,
                    clearBuffer = false,
                    filterMode  = FilterMode.Point,
                };
                var halfRTDesc = new TextureDesc(halfW, halfH)
                {
                    name        = "VRSL Volumetric Half RT",
                    format      = GraphicsFormat.R16G16B16A16_SFloat,
                    clearBuffer = true,
                    clearColor  = Color.clear,
                    filterMode  = FilterMode.Point,
                };
                TextureHandle halfDepth = rg.CreateTexture(halfDepthDesc);
                TextureHandle halfRT    = rg.CreateTexture(halfRTDesc);

                using (var builder = rg.AddRasterRenderPass<DownsampleData>(
                    "VRSL Vol Depth Downsample", out var d))
                {
                    d.material  = mgr.VolumetricMaterial;
                    d.fullDepth = resources.cameraDepthTexture;

                    builder.SetRenderAttachment(halfDepth, 0, AccessFlags.Write);
                    builder.UseTexture(d.fullDepth, AccessFlags.Read);

                    builder.SetRenderFunc((DownsampleData p, RasterGraphContext ctx) =>
                    {
                        ctx.cmd.DrawProcedural(Matrix4x4.identity, p.material, 0,
                            MeshTopology.Triangles, 3, 1);
                    });
                }

                using (var builder = rg.AddRasterRenderPass<RaymarchData>(
                    "VRSL Vol Raymarch", out var d))
                {
                    d.material        = mgr.VolumetricMaterial;
                    d.halfDepth       = halfDepth;
                    d.lightDataBuffer = lightDataHandle;
                    d.lightCount      = mgr.FixtureCount;
                    d.stepParams      = mgr.VolumetricStepParams;
                    d.densityParams   = mgr.VolumetricDensityParams;
                    d.fogTintParams   = mgr.VolumetricFogTintParams;

                    builder.SetRenderAttachment(halfRT, 0, AccessFlags.Write);
                    builder.UseTexture(d.halfDepth, AccessFlags.Read);
                    builder.UseBuffer(d.lightDataBuffer, AccessFlags.Read);
                    builder.AllowGlobalStateModification(true);

                    builder.SetRenderFunc((RaymarchData p, RasterGraphContext ctx) =>
                    {
                        var cmd = ctx.cmd;
                        cmd.SetGlobalBuffer( "_VRSLLights",            p.lightDataBuffer);
                        cmd.SetGlobalInteger("_VRSLLightCount",        p.lightCount);
                        cmd.SetGlobalTexture("_VRSLVolHalfResDepth",   p.halfDepth);
                        cmd.SetGlobalVector( "_VRSLVolStepCount",      p.stepParams);
                        cmd.SetGlobalVector( "_VRSLVolDensity",        p.densityParams);
                        cmd.SetGlobalVector( "_VRSLVolFogTint",        p.fogTintParams);
                        cmd.DrawProcedural(Matrix4x4.identity, p.material, 1,
                            MeshTopology.Triangles, 3, 1);
                    });
                }

                using (var builder = rg.AddRasterRenderPass<UpsampleData>(
                    "VRSL Vol Upsample", out var d))
                {
                    d.material  = mgr.VolumetricMaterial;
                    d.halfRT    = halfRT;
                    d.halfDepth = halfDepth;

                    builder.SetRenderAttachment(resources.activeColorTexture, 0, AccessFlags.ReadWrite);
                    builder.UseTexture(d.halfRT,    AccessFlags.Read);
                    builder.UseTexture(d.halfDepth, AccessFlags.Read);
                    builder.UseTexture(resources.cameraDepthTexture, AccessFlags.Read);
                    builder.AllowGlobalStateModification(true);

                    builder.SetRenderFunc((UpsampleData p, RasterGraphContext ctx) =>
                    {
                        var cmd = ctx.cmd;
                        cmd.SetGlobalTexture("_VRSLVolumetricRT",    p.halfRT);
                        cmd.SetGlobalTexture("_VRSLVolHalfResDepth", p.halfDepth);
                        cmd.DrawProcedural(Matrix4x4.identity, p.material, 2,
                            MeshTopology.Triangles, 3, 1);
                    });
                }
            }
        }

        // ── ScriptableRendererFeature ──────────────────────────────────────────
        ComputePass    _computePass;
        LightingPass   _lightingPass;
        VolumetricPass _volumetricPass;

        public override void Create()
        {
            _computePass = new ComputePass
            {
                renderPassEvent = RenderPassEvent.BeforeRenderingOpaques
            };
            _lightingPass = new LightingPass
            {
                renderPassEvent = RenderPassEvent.AfterRenderingOpaques
            };
            _volumetricPass = new VolumetricPass
            {
                renderPassEvent = (RenderPassEvent)((int)RenderPassEvent.AfterRenderingOpaques + 1)
            };
        }

        public override void AddRenderPasses(ScriptableRenderer renderer, ref RenderingData renderingData)
        {
            var mgr = VRSL_AudioLinkURPLightManager.Instance;
            if (mgr == null) return;
            // Manager owns pass injection via RenderPipelineManager.beginCameraRendering
            // when the runtime-injection path is enabled — skip here to avoid duplicate
            // passes if both the manager and this feature are wired up simultaneously.
            if (mgr.useRuntimePassInjection) return;
            // Request the depth normals prepass so _CameraNormalsTexture is populated.
            // In Unity 6 URP the prepass has no Inspector toggle — it activates when a
            // pass declares this requirement before being enqueued.
            _lightingPass.ConfigureInput(ScriptableRenderPassInput.Normal | ScriptableRenderPassInput.Depth);
            _volumetricPass.ConfigureInput(ScriptableRenderPassInput.Depth);
            // Gobo array is a plain Texture2DArray — set as a global here (CPU path)
            // rather than inside the render graph where only TextureHandle is accepted.
            if (mgr.GoboArray != null)
                Shader.SetGlobalTexture("_VRSLGobos", mgr.GoboArray);
            renderer.EnqueuePass(_computePass);
            renderer.EnqueuePass(_lightingPass);
            if (mgr.VolumetricMaterial != null)
                renderer.EnqueuePass(_volumetricPass);
        }
    }
}
