// Fullscreen additive lighting pass for VRSL GPU realtime lights.
// Runs after URP opaque rendering. Reconstructs world-space position from depth
// and normals from _CameraNormalsTexture (requires Depth Normals Prepass enabled
// in the URP Renderer asset), then evaluates each GPU-driven VRSL light.
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

            // Set by VRSLRealtimeLightFeature via SetGlobal* before DrawProcedural
            StructuredBuffer<VRSLLightData> _VRSLLights;
            uint _VRSLLightCount;

            // Gobo texture array — one slice per unique gobo texture.
            // Slice index is stored in VRSLLightData.goboAndSpin.x (-1 = no gobo).
            Texture2DArray _VRSLGobos;
            SamplerState sampler_linear_clamp;

            // Project a world-space surface point onto the light's gobo texture.
            // Returns a [0,1] greyscale mask value (1.0 when no gobo assigned).
            // spinAngle is the fully-integrated rotation in radians, wrapped to
            // [-2π, 2π] by the compute shader — see VRSLDMXLightUpdate.compute.
            float SampleGobo(float goboIdx, float spinAngle, float3 posWS,
                             float3 lightPos, float3 lightDir, float cosOuter)
            {
                if (goboIdx < -0.5) return 1.0;

                float3 toPixel = posWS - lightPos;
                float  depth   = dot(toPixel, lightDir);
                if (depth <= 0.0) return 0.0;

                // Derive light-space right/up from the direction vector.
                // Switch up-reference near vertical to avoid degenerate cross product.
                float3 worldUp = abs(lightDir.y) < 0.99 ? float3(0, 1, 0) : float3(0, 0, 1);
                float3 right   = normalize(cross(worldUp, lightDir));
                float3 up      = cross(lightDir, right);

                // tan(outerHalfAngle) from the stored cosine — avoids acos/radians
                float sinOuter = sqrt(max(0.0, 1.0 - cosOuter * cosOuter));
                float tanHalf  = sinOuter / max(cosOuter, 0.0001);

                float u = dot(toPixel, right) / (depth * tanHalf) * 0.5 + 0.5;
                float v = dot(toPixel, up)    / (depth * tanHalf) * 0.5 + 0.5;

                // Apply the pre-integrated spin angle directly. No _Time.w multiplication
                // here: the compute shader reads the SpinnerTimer CRT (which accumulates
                // phase over time), so rate changes never retroactively re-interpret past
                // rotation and the gobo position stays continuous across DMX transitions.
                if (spinAngle != 0.0)
                {
                    float s = sin(spinAngle), c = cos(spinAngle);
                    float cu = u - 0.5, cv = v - 0.5;
                    u = c * cu - s * cv + 0.5;
                    v = s * cu + c * cv + 0.5;
                }

                return _VRSLGobos.SampleLevel(sampler_linear_clamp,
                           float3(u, v, goboIdx), 0).r;
            }

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
