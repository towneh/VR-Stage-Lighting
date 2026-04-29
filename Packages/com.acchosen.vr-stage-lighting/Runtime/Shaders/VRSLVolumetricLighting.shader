// Raymarched volumetric in-scattering for VRSL GPU realtime lights.
// Runs after VRSLDeferredLighting in the same renderer feature, reading the
// same _VRSLLights StructuredBuffer the surface pass produces. Three sub-passes:
//
//   Pass 0 — Depth Downsample. Full-res _CameraDepthTexture → half-res depth
//            (min-depth in linear, max raw value in reversed-Z) so the
//            raymarch terminates correctly at silhouettes.
//
//   Pass 1 — Raymarch. Half-res in-scattering accumulation along each pixel's
//            view ray. Per step: density × Σ(light cone × distance × phase ×
//            gobo) over all VRSL lights. Output: half-res HDR in-scattering RT.
//
//   Pass 2 — Bilateral Upsample. Edge-aware reconstruction to full resolution,
//            additive blend onto the camera color target.
Shader "Hidden/VRSL/VolumetricLighting"
{
    SubShader
    {
        Tags { "RenderType" = "Transparent" "RenderPipeline" = "UniversalPipeline" }

        HLSLINCLUDE
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/DeclareDepthTexture.hlsl"

            struct Varyings
            {
                float4 positionCS : SV_POSITION;
                float2 uv         : TEXCOORD0;
            };

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

            SamplerState sampler_point_clamp;
        ENDHLSL

        // ── Pass 0 ───────────────────────────────────────────────────────────
        // Depth downsample: emit the depth that is closest to the camera within
        // each 2×2 source quad. In reversed-Z that is the maximum raw value.
        // Using min-depth keeps the raymarch tight to foreground silhouettes;
        // any half-res taps that lose background coverage are recovered by the
        // bilateral upsample in pass 2.
        Pass
        {
            Name "VRSL_Vol_DepthDownsample"
            Blend Off
            ZWrite Off
            ZTest  Off
            Cull   Off

            HLSLPROGRAM
            #pragma vertex   vert
            #pragma fragment frag
            #pragma target   4.5

            float4 frag(Varyings i) : SV_Target
            {
                float2 ts = _CameraDepthTexture_TexelSize.xy;
                float d0 = SampleSceneDepth(i.uv);
                float d1 = SampleSceneDepth(i.uv + float2(ts.x, 0));
                float d2 = SampleSceneDepth(i.uv + float2(0, ts.y));
                float d3 = SampleSceneDepth(i.uv + float2(ts.x, ts.y));
            #if UNITY_REVERSED_Z
                float d = max(max(d0, d1), max(d2, d3));
            #else
                float d = min(min(d0, d1), min(d2, d3));
            #endif
                return float4(d, 0, 0, 0);
            }
            ENDHLSL
        }

        // ── Pass 1 ───────────────────────────────────────────────────────────
        // Raymarch the visible portion of each view ray, accumulating
        // in-scattering from every VRSL light. Reads the half-res depth from
        // pass 0; emits a half-res HDR colour buffer that pass 2 composites.
        Pass
        {
            Name "VRSL_Vol_Raymarch"
            Blend Off
            ZWrite Off
            ZTest  Off
            Cull   Off

            HLSLPROGRAM
            #pragma vertex   vert
            #pragma fragment frag
            #pragma target   4.5

            #include "Shared/VRSLLightingLibrary.hlsl"

            StructuredBuffer<VRSLLightData> _VRSLLights;
            uint   _VRSLLightCount;
            Texture2D _VRSLVolHalfResDepth;

            // x = step count, w = HG anisotropy g
            float4 _VRSLVolStepCount;
            // x = base density (manager-driven), other components reserved
            float4 _VRSLVolDensity;
            // xyz = colour tint, w = global intensity multiplier
            float4 _VRSLVolFogTint;

            // Hash-based interleaved gradient noise — bypasses any blue-noise
            // texture asset dependency. Stable per-pixel, varies with frame
            // index via fmod-time so successive frames decorrelate softly.
            float VRSL_IGN(float2 pixelCoord)
            {
                return frac(52.9829189
                          * frac(dot(pixelCoord, float2(0.06711056, 0.00583715))));
            }

            float4 frag(Varyings i) : SV_Target
            {
                float rawDepth = _VRSLVolHalfResDepth.SampleLevel(
                    sampler_point_clamp, i.uv, 0).r;

            #if UNITY_REVERSED_Z
                if (rawDepth < 0.0001) return 0;   // skybox / far plane
            #else
                if (rawDepth > 0.9999) return 0;
            #endif

                float3 surfaceWS = ComputeWorldSpacePosition(
                    i.uv, rawDepth, UNITY_MATRIX_I_VP);
                float3 cameraWS  = _WorldSpaceCameraPos.xyz;

                float3 viewDelta = surfaceWS - cameraWS;
                float  maxDist   = length(viewDelta);
                float3 viewDir   = viewDelta / max(maxDist, 0.0001);
                float3 toCamera  = -viewDir;

                int   stepCount = max(1, (int)_VRSLVolStepCount.x);
                float stepSize  = maxDist / stepCount;

                float jitter   = VRSL_IGN(i.positionCS.xy);
                float density  = _VRSLVolDensity.x;
                float anisotropy = _VRSLVolStepCount.w;

                float3 accumulated = 0;

                [loop]
                for (int s = 0; s < stepCount; s++)
                {
                    float  t         = (s + jitter) * stepSize;
                    float3 samplePos = cameraWS + viewDir * t;

                    float3 inscatter = 0;
                    for (uint li = 0; li < _VRSLLightCount; li++)
                    {
                        VRSLLightData light = _VRSLLights[li];
                        float3 contrib = VRSL_EvaluateLightVolumetric(
                            light, samplePos, toCamera, anisotropy);
                        contrib *= SampleGobo(
                            light.goboAndSpin.x, light.goboAndSpin.y,
                            samplePos,
                            light.positionAndRange.xyz,
                            light.directionAndType.xyz,
                            light.spotCosines.y);
                        inscatter += contrib;
                    }

                    accumulated += inscatter * density * stepSize;
                }

                float3 result = accumulated * _VRSLVolFogTint.xyz * _VRSLVolFogTint.w;
                return float4(result, 0);
            }
            ENDHLSL
        }

        // ── Pass 2 ───────────────────────────────────────────────────────────
        // Bilateral upsample composite. For each full-res pixel: sample the four
        // nearest half-res taps, weight bilinear × exp(-|depthDiff|), and add
        // the weighted-average to the camera colour. Edge-aware reconstruction
        // means a foreground silhouette doesn't fringe-bleed half-res values
        // sampled from background neighbours.
        Pass
        {
            Name "VRSL_Vol_Upsample"
            Blend One One
            ZWrite Off
            ZTest  Off
            Cull   Off

            HLSLPROGRAM
            #pragma vertex   vert
            #pragma fragment frag
            #pragma target   4.5

            Texture2D _VRSLVolumetricRT;
            Texture2D _VRSLVolHalfResDepth;

            float4 frag(Varyings i) : SV_Target
            {
                float fullDepth = SampleSceneDepth(i.uv);
            #if UNITY_REVERSED_Z
                if (fullDepth < 0.0001) return 0;
            #else
                if (fullDepth > 0.9999) return 0;
            #endif

                uint hw, hh;
                _VRSLVolHalfResDepth.GetDimensions(hw, hh);
                float2 halfTexel = float2(1.0 / hw, 1.0 / hh);

                // Pixel coordinate in half-res space (centres at integer + 0.5)
                float2 halfPos  = i.uv * float2(hw, hh);
                float2 halfBase = floor(halfPos - 0.5) + 0.5;
                float2 frac01   = halfPos - halfBase;
                float2 baseUV   = halfBase * halfTexel;

                float bilinearW[4] = {
                    (1.0 - frac01.x) * (1.0 - frac01.y),
                    frac01.x         * (1.0 - frac01.y),
                    (1.0 - frac01.x) * frac01.y,
                    frac01.x         * frac01.y
                };
                float2 offs[4] = {
                    float2(0, 0),
                    float2(halfTexel.x, 0),
                    float2(0, halfTexel.y),
                    halfTexel
                };

                float fullEye = LinearEyeDepth(fullDepth, _ZBufferParams);

                float4 sum  = 0;
                float  wSum = 0;

                [unroll]
                for (int j = 0; j < 4; j++)
                {
                    float2 uv = baseUV + offs[j];
                    float halfDepth = _VRSLVolHalfResDepth.SampleLevel(
                        sampler_point_clamp, uv, 0).r;
                    float halfEye  = LinearEyeDepth(halfDepth, _ZBufferParams);
                    float depthDiff = abs(fullEye - halfEye);
                    float bilateral = 1.0 / (0.0001 + depthDiff);
                    float w = bilinearW[j] * bilateral;
                    sum  += _VRSLVolumetricRT.SampleLevel(
                        sampler_point_clamp, uv, 0) * w;
                    wSum += w;
                }

                return sum / max(wSum, 0.0001);
            }
            ENDHLSL
        }
    }
}
