using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.Universal;
using UnityEngine.Rendering.RenderGraphModule;

namespace VRSL
{
    /// <summary>
    /// URP ScriptableRendererFeature (Unity 6 Render Graph API) that drives the
    /// VRSL GPU realtime-light pipeline in two phases per frame:
    ///
    ///   1. Compute pass  — dispatches VRSLDMXLightUpdate.compute, which reads the
    ///      three DMX RenderTextures and writes a VRSLLightData StructuredBuffer.
    ///      Runs before opaque rendering so shadow maps can pick up light positions.
    ///
    ///   2. Fullscreen additive pass — after opaque rendering, reconstructs world
    ///      position from depth + normals and adds each GPU-decoded light's
    ///      contribution to the frame (Hidden/VRSL/DeferredLighting shader).
    ///
    /// Requirements:
    ///   • A VRSL_GPULightManager in the scene with the textures and shaders assigned.
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
                public ComputeShader cs;
                public int           kernel;
                public int           fixtureCount;
                public int           goboCount;
                public Vector4       texelSize;
            }

            public override void RecordRenderGraph(RenderGraph rg, ContextContainer frame)
            {
                var mgr = VRSL_GPULightManager.Instance;
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
                var mgr = VRSL_GPULightManager.Instance;
                if (mgr == null || mgr.FixtureCount == 0
                    || mgr.LightingMaterial == null
                    || mgr.LightDataBuffer  == null) return;

                var resources = frame.Get<UniversalResourceData>();

                if (!resources.cameraNormalsTexture.IsValid())
                {
                    Debug.LogWarning("[VRSL] GPU lighting requires 'Depth Normals Prepass' "
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

        // ── ScriptableRendererFeature ──────────────────────────────────────────
        ComputePass  _computePass;
        LightingPass _lightingPass;

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
        }

        public override void AddRenderPasses(ScriptableRenderer renderer, ref RenderingData renderingData)
        {
            var mgr = VRSL_GPULightManager.Instance;
            if (mgr == null) return;
            _lightingPass.ConfigureInput(ScriptableRenderPassInput.Normal | ScriptableRenderPassInput.Depth);
            // Gobo array is a plain Texture2DArray — set as a global here rather than
            // inside the render graph where only TextureHandle is accepted.
            if (mgr.GoboArray != null)
                Shader.SetGlobalTexture("_VRSLGobos", mgr.GoboArray);
            renderer.EnqueuePass(_computePass);
            renderer.EnqueuePass(_lightingPass);
        }
    }
}
