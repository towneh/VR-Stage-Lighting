using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.Universal;
using UnityEngine.Rendering.RenderGraphModule;

namespace VRSL
{
    /// <summary>
    /// URP ScriptableRendererFeature (Unity 6 Render Graph API) for the
    /// AudioLink-driven GPU realtime light pipeline.
    ///
    /// Schedules two passes per frame:
    ///   1. Compute pass (BeforeRenderingOpaques) — dispatches VRSLAudioLinkLightUpdate.compute,
    ///      which samples the AudioLink texture for amplitude and color, reads world-space
    ///      fixture config from the StructuredBuffer, and writes VRSLLightData entries.
    ///
    ///   2. Fullscreen additive pass (AfterRenderingOpaques) — reuses Hidden/VRSL/DeferredLighting,
    ///      identical to the DMX realtime light path. The two paths share the same lighting shader
    ///      and the same _VRSLLights / _VRSLLightCount globals.
    ///
    /// Requirements:
    ///   • A VRSL_AudioLinkGPULightManager in the scene with compute and lighting shaders assigned.
    ///   • "Depth Normals Prepass" enabled on this URP Renderer asset.
    ///   • This feature added to the same URP Renderer asset.
    ///   • AudioLink active in the scene (sets _AudioTexture as a global RenderTexture).
    ///
    /// Note: running this feature simultaneously with VRSLRealtimeLightFeature (DMX path) is
    /// not currently supported — both write to _VRSLLights / _VRSLLightCount and the last pass
    /// to execute wins. A future merged-buffer path can address this.
    /// </summary>
    [System.Serializable]
    public class VRSLAudioLinkRealtimeLightFeature : ScriptableRendererFeature
    {
        // ── Compute pass: AudioLink → light buffer ─────────────────────────────
        class ComputePass : ScriptableRenderPass
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
                var mgr = VRSL_AudioLinkGPULightManager.Instance;
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
                var mgr = VRSL_AudioLinkGPULightManager.Instance;
                if (mgr == null || mgr.FixtureCount == 0
                    || mgr.LightingMaterial == null
                    || mgr.LightDataBuffer  == null) return;

                var resources = frame.Get<UniversalResourceData>();

                if (!resources.cameraNormalsTexture.IsValid())
                {
                    Debug.LogWarning("[VRSL] AudioLink GPU lighting requires 'Depth Normals Prepass' "
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
                renderPassEvent = RenderPassEvent.AfterRenderingOpaques
            };
        }

        public override void AddRenderPasses(ScriptableRenderer renderer, ref RenderingData renderingData)
        {
            var mgr = VRSL_AudioLinkGPULightManager.Instance;
            if (mgr == null) return;
            // Request the depth normals prepass so _CameraNormalsTexture is populated.
            // In Unity 6 URP the prepass has no Inspector toggle — it activates when a
            // renderer feature declares this requirement before enqueueing its pass.
            _lightingPass.ConfigureInput(ScriptableRenderPassInput.Normal | ScriptableRenderPassInput.Depth);
            // Gobo array is a plain Texture2DArray — set as a global here (CPU path)
            // rather than inside the render graph where only TextureHandle is accepted.
            if (mgr.GoboArray != null)
                Shader.SetGlobalTexture("_VRSLGobos", mgr.GoboArray);
            renderer.EnqueuePass(_computePass);
            renderer.EnqueuePass(_lightingPass);
        }
    }
}
