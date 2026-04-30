// Fullscreen additive lighting pass for VRSL URP realtime lights.
// Runs after URP opaque rendering. Reconstructs world-space position from depth
// and normals from _CameraNormalsTexture (requires Depth Normals Prepass enabled
// in the URP Renderer asset), then evaluates each VRSL light.
Shader "Hidden/VRSL/DeferredLighting"
{
    SubShader
    {
        Tags { "RenderType" = "Transparent" "RenderPipeline" = "UniversalPipeline" }

        Pass
        {
            Name "VRSL_Lighting"

            // Additive — output is added on top of the existing frame color
            Blend One One
            ZWrite Off
            ZTest Off
            Cull Off

            HLSLPROGRAM
            #pragma vertex   vert
            #pragma fragment frag
            #pragma target   4.5

            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/DeclareDepthTexture.hlsl"
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/DeclareNormalsTexture.hlsl"
            #include "Shared/VRSLLightingLibrary.hlsl"

            // Set by the manager's LightingPass via SetGlobal* before DrawProcedural
            StructuredBuffer<VRSLLightData> _VRSLLights;
            uint _VRSLLightCount;

            struct Varyings
            {
                float4 positionCS : SV_POSITION;
                float2 uv         : TEXCOORD0;
            };

            // Full-screen triangle — no vertex buffer needed
            Varyings vert(uint vertexID : SV_VertexID)
            {
                Varyings o;
                o.uv         = float2((vertexID << 1) & 2, vertexID & 2);
                o.positionCS = float4(o.uv * 2.0 - 1.0, 0.0, 1.0);
#if UNITY_UV_STARTS_AT_TOP
                o.uv.y = 1.0 - o.uv.y;
#endif
                return o;
            }

            float4 frag(Varyings i) : SV_Target
            {
                float2 uv = i.uv;

                // Skip skybox / far-plane pixels
                float rawDepth = SampleSceneDepth(uv);
#if UNITY_REVERSED_Z
                if (rawDepth < 0.0001) return 0;
#else
                if (rawDepth > 0.9999) return 0;
#endif

                // Reconstruct world position from depth
                float3 posWS = ComputeWorldSpacePosition(uv, rawDepth, UNITY_MATRIX_I_VP);

                // World-space normal from the URP depth-normals prepass
                float3 normalWS = SampleSceneNormals(uv);
                if (dot(normalWS, normalWS) < 0.1) return 0; // missing normal data

                // Accumulate contributions from all VRSL lights
                float3 lighting = 0;
                for (uint li = 0; li < _VRSLLightCount; li++)
                {
                    VRSLLightData light = _VRSLLights[li];
                    float3 contrib = VRSL_EvaluateLight(light, posWS, normalWS);

                    // Apply gobo projection (goboAndSpin.x = slice index, .y = spin speed)
                    contrib *= SampleGobo(light.goboAndSpin.x, light.goboAndSpin.y,
                                          posWS,
                                          light.positionAndRange.xyz,
                                          light.directionAndType.xyz,
                                          light.spotCosines.y);

                    lighting += contrib;
                }

                return float4(lighting, 0.0);
            }
            ENDHLSL
        }
    }
}
