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

            // Cookie (gobo) texture array — one slice per unique cookie texture.
            // Slice index is stored in VRSLLightData.spotCosines.w (-1 = no cookie).
            Texture2DArray _VRSLCookies;
            SamplerState sampler_linear_clamp;

            // Project a world-space surface point onto the light's cookie texture.
            // Returns a [0,1] greyscale mask value (1.0 when no cookie assigned).
            // spinSpeed matches the volumetric shader's _SpinSpeed (range 0–10);
            // uses the same formula: angle = _Time.w * 10 * spinSpeed (degrees).
            float SampleCookie(float cookieIdx, float spinSpeed, float3 posWS,
                               float3 lightPos, float3 lightDir, float cosOuter)
            {
                if (cookieIdx < -0.5) return 1.0;

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

                // Spin: rotate UV around the centre, matching volumetric _SpinSpeed formula
                if (spinSpeed != 0.0)
                {
                    float angle = radians(_Time.w * 10.0 * spinSpeed);
                    float s = sin(angle), c = cos(angle);
                    float cu = u - 0.5, cv = v - 0.5;
                    u = c * cu - s * cv + 0.5;
                    v = s * cu + c * cv + 0.5;
                }

                return _VRSLCookies.SampleLevel(sampler_linear_clamp,
                           float3(u, v, cookieIdx), 0).r;
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

                    // Apply cookie projection (cookieAndSpin.x = slice index, .y = spin speed)
                    contrib *= SampleCookie(light.cookieAndSpin.x, light.cookieAndSpin.y,
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
