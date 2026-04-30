using UnityEngine;
using UnityEngine.Experimental.Rendering;
using UnityEngine.Rendering;
using UnityEngine.Rendering.Universal;
using UnityEngine.Rendering.RenderGraphModule;

namespace VRSL
{
    /// <summary>
    /// URP ScriptableRendererFeature (Unity 6 Render Graph API) that drives the
    /// VRSL URP realtime-light pipeline in three phases per frame:
    ///
    ///   1. Compute pass  — dispatches VRSLDMXLightUpdate.compute, which reads the
    ///      three DMX RenderTextures and writes a VRSLLightData StructuredBuffer.
    ///      Runs before opaque rendering so shadow maps can pick up light positions.
    ///
    ///   2. Fullscreen additive pass — after opaque rendering, reconstructs world
    ///      position from depth + normals and adds each GPU-decoded light's
    ///      contribution to the frame (Hidden/VRSL/DeferredLighting shader).
    ///
    ///   3. Volumetric pass — three Render Graph sub-passes that depth-
    ///      downsample, raymarch in-scattering at half resolution, and
    ///      bilaterally composite the result onto the camera colour target
    ///      (Hidden/VRSL/VolumetricLighting shader). Runs whenever the
    ///      manager has a volumetric shader assigned.
    ///
    /// Requirements:
    ///   • A VRSL_URPLightManager in the scene with the textures and shaders assigned.
    ///   • "Depth Normals Prepass" enabled on this URP Renderer asset.
    ///   • This feature added to the same URP Renderer asset.
    /// </summary>
    [System.Serializable]
    public class VRSLRealtimeLightFeature : ScriptableRendererFeature
    {
        // ── Compute pass: decode DMX → light buffer ────────────────────────────
        class ComputePass : ScriptableRenderPass
        {
            class PassData
            {
                public BufferHandle  fixtureConfigBuffer;
                public BufferHandle  lightDataBuffer;
                public TextureHandle dmxMainTex;
                public TextureHandle dmxMovementTex;
                public TextureHandle dmxStrobeTex;
                public TextureHandle dmxSpinTimerTex;
                public ComputeShader cs;
                public int           kernel;
                public int           fixtureCount;
                public int           goboCount;
                public Vector4       texelSize;
            }

            public override void RecordRenderGraph(RenderGraph rg, ContextContainer frame)
            {
                var mgr = VRSL_URPLightManager.Instance;
                if (mgr == null || mgr.FixtureCount == 0
                    || mgr.computeShader == null
                    || mgr.FixtureConfigBuffer == null
                    || mgr.DMXMainHandle == null) return;

                using var builder = rg.AddComputePass<PassData>("VRSL DMX Light Compute", out var d);

                d.fixtureConfigBuffer = rg.ImportBuffer(mgr.FixtureConfigBuffer);
                d.lightDataBuffer     = rg.ImportBuffer(mgr.LightDataBuffer);
                d.dmxMainTex          = rg.ImportTexture(mgr.DMXMainHandle);
                d.dmxMovementTex      = mgr.DMXMovementHandle != null
                    ? rg.ImportTexture(mgr.DMXMovementHandle)
                    : TextureHandle.nullHandle;
                d.dmxStrobeTex        = mgr.DMXStrobeHandle != null
                    ? rg.ImportTexture(mgr.DMXStrobeHandle)
                    : TextureHandle.nullHandle;
                d.dmxSpinTimerTex     = mgr.DMXSpinTimerHandle != null
                    ? rg.ImportTexture(mgr.DMXSpinTimerHandle)
                    : TextureHandle.nullHandle;

                d.cs           = mgr.computeShader;
                d.kernel       = mgr.ComputeKernel;
                d.fixtureCount = mgr.FixtureCount;
                d.goboCount    = mgr.GoboCount;
                d.texelSize    = new Vector4(
                    1f / mgr.dmxMainTexture.width,
                    1f / mgr.dmxMainTexture.height,
                    mgr.dmxMainTexture.width,
                    mgr.dmxMainTexture.height);

                builder.UseBuffer(d.fixtureConfigBuffer, AccessFlags.Read);
                builder.UseBuffer(d.lightDataBuffer,     AccessFlags.Write);
                builder.UseTexture(d.dmxMainTex,         AccessFlags.Read);
                if (d.dmxMovementTex.IsValid())
                    builder.UseTexture(d.dmxMovementTex, AccessFlags.Read);
                if (d.dmxStrobeTex.IsValid())
                    builder.UseTexture(d.dmxStrobeTex,   AccessFlags.Read);
                if (d.dmxSpinTimerTex.IsValid())
                    builder.UseTexture(d.dmxSpinTimerTex, AccessFlags.Read);

                builder.SetRenderFunc((PassData p, ComputeGraphContext ctx) =>
                {
                    var cmd = ctx.cmd;
                    cmd.SetComputeVectorParam( p.cs,           "_VRSLDMXTexelSize", p.texelSize);
                    cmd.SetComputeIntParam(    p.cs,           "_FixtureCount",     p.fixtureCount);
                    cmd.SetComputeIntParam(    p.cs,           "_VRSLGoboCount",    p.goboCount);
                    cmd.SetComputeBufferParam( p.cs, p.kernel, "_FixtureConfigs",   p.fixtureConfigBuffer);
                    cmd.SetComputeBufferParam( p.cs, p.kernel, "_LightData",        p.lightDataBuffer);
                    cmd.SetComputeTextureParam(p.cs, p.kernel, "_DMXMainTex",       p.dmxMainTex);

                    if (p.dmxMovementTex.IsValid())
                        cmd.SetComputeTextureParam(p.cs, p.kernel, "_DMXMovementTex", p.dmxMovementTex);
                    if (p.dmxStrobeTex.IsValid())
                        cmd.SetComputeTextureParam(p.cs, p.kernel, "_DMXStrobeTex",   p.dmxStrobeTex);
                    if (p.dmxSpinTimerTex.IsValid())
                        cmd.SetComputeTextureParam(p.cs, p.kernel, "_DMXSpinTimerTex", p.dmxSpinTimerTex);

                    cmd.DispatchCompute(p.cs, p.kernel, Mathf.CeilToInt(p.fixtureCount / 64f), 1, 1);
                });
            }
        }

        // ── Fullscreen additive pass: light the scene ──────────────────────────
        class LightingPass : ScriptableRenderPass
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
                var mgr = VRSL_URPLightManager.Instance;
                if (mgr == null || mgr.FixtureCount == 0
                    || mgr.LightingMaterial == null
                    || mgr.LightDataBuffer  == null) return;

                var resources = frame.Get<UniversalResourceData>();

                if (!resources.cameraNormalsTexture.IsValid())
                {
                    Debug.LogWarning("[VRSL] URP lighting requires 'Depth Normals Prepass' "
                        + "enabled on the URP Renderer asset.");
                    return;
                }

                using var builder = rg.AddRasterRenderPass<PassData>("VRSL Lighting Pass", out var d);

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
                    // Full-screen triangle: 3 vertices, no vertex buffer needed
                    cmd.DrawProcedural(Matrix4x4.identity, p.material, 0,
                        MeshTopology.Triangles, 3, 1);
                });
            }
        }

        // ── Volumetric pass: raymarched in-scattering ──────────────────────────
        // Records three Render Graph sub-passes. Half-res transient RTs are
        // created with rg.CreateTexture so they live exactly for this frame.
        class VolumetricPass : ScriptableRenderPass
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
                var mgr = VRSL_URPLightManager.Instance;
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

                // Sub-pass 1 — depth downsample
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

                // Sub-pass 2 — raymarch
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

                // Sub-pass 3 — bilateral upsample composite onto camera colour
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
                // After opaques so the additive contribution lands on top of
                // lit geometry but before transparents
                renderPassEvent = RenderPassEvent.AfterRenderingOpaques
            };
            _volumetricPass = new VolumetricPass
            {
                // After the surface lighting pass, before transparents and skybox
                renderPassEvent = (RenderPassEvent)((int)RenderPassEvent.AfterRenderingOpaques + 1)
            };
        }

        public override void AddRenderPasses(ScriptableRenderer renderer, ref RenderingData renderingData)
        {
            var mgr = VRSL_URPLightManager.Instance;
            if (mgr == null) return;
            _lightingPass.ConfigureInput(ScriptableRenderPassInput.Normal | ScriptableRenderPassInput.Depth);
            _volumetricPass.ConfigureInput(ScriptableRenderPassInput.Depth);
            // Gobo array is a plain Texture2DArray — set as a global here rather than
            // inside the render graph where only TextureHandle is accepted.
            if (mgr.GoboArray != null)
                Shader.SetGlobalTexture("_VRSLGobos", mgr.GoboArray);
            renderer.EnqueuePass(_computePass);
            renderer.EnqueuePass(_lightingPass);
            if (mgr.VolumetricMaterial != null)
                renderer.EnqueuePass(_volumetricPass);
        }
    }
}
