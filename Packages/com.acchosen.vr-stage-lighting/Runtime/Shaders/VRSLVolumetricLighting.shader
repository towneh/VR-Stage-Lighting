// Raymarched volumetric in-scattering for VRSL URP realtime lights.
// Runs immediately after VRSLDeferredLighting in the same per-camera schedule, reading the
// same _VRSLLights StructuredBuffer the surface pass produces. The raymarch
// itself is shared between two execution modes selected by the manager:
//
//   Half-res mode (default) — three sub-passes:
//     Pass 0 — Depth Downsample. Full-res _CameraDepthTexture → half-res depth
//              (min-depth in linear, max raw value in reversed-Z) so the
//              raymarch terminates correctly at silhouettes.
//     Pass 1 — Raymarch (half-res). Half-res in-scattering accumulation along
//              each pixel's view ray; output is a half-res HDR colour buffer.
//     Pass 2 — Bilateral Upsample. Edge-aware reconstruction to full resolution,
//              additive blend onto the camera color target.
//
//   Full-res mode — single pass:
//     Pass 3 — Raymarch (full-res additive). Samples _CameraDepthTexture
//              directly and additive-blends onto the camera color target. ~4×
//              the per-pixel cost of half-res but no resolution-mismatch
//              artefacts. Targets cinematic capture and high-perf desktops.
Shader "Hidden/VRSL/VolumetricLighting"
{
    SubShader
    {
        Tags { "RenderType" = "Transparent" "RenderPipeline" = "UniversalPipeline" }

        HLSLINCLUDE
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/DeclareDepthTexture.hlsl"
            #include "Shared/VRSLLightingLibrary.hlsl"

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

            // ── Raymarch globals (shared by half-res and full-res passes) ────
            StructuredBuffer<VRSLLightData> _VRSLLights;
            uint   _VRSLLightCount;

            // x = step count, y = couple-to-scene-fog flag (0/1),
            // w = HG anisotropy g
            float4 _VRSLVolStepCount;
            // x = base density, y = noise scale, z = noise scroll speed,
            // w = noise strength (modulated variant only)
            float4 _VRSLVolDensity;
            // xyz = colour tint, w = global intensity multiplier
            float4 _VRSLVolFogTint;

            // R2 (plastic-constant) quasi-random sequence — gives a spatially
            // uniform low-discrepancy distribution that reads perceptually as
            // fine grain rather than the structured banding produced by
            // interleaved gradient noise. The frame-indexed offset decorrelates
            // the pattern across frames so head/fixture motion averages it out.
            float VRSL_Jitter(float2 pixelCoord)
            {
                const float2 alpha = float2(0.7548776662, 0.5698402910);
                float frameIdx = _Time.y * 60.0;
                float2 p = pixelCoord * alpha + fmod(frameIdx, 64.0) * alpha;
                return frac(p.x + p.y);
            }

        #ifdef _VRSL_VOLUMETRIC_NOISE
            // Dave Hoskins-style 3D hash. ~6 ALU per call.
            float VRSL_Hash3D(float3 p)
            {
                p = frac(p * float3(0.1031, 0.1030, 0.0973));
                p += dot(p, p.yzx + 33.33);
                return frac((p.x + p.y) * p.z);
            }

            // Smoothed 3D value noise on a unit grid. 8 hash taps + trilinear
            // smoothstep interpolation — ~50 ALU per sample. Output range [0,1].
            float VRSL_ValueNoise3D(float3 p)
            {
                float3 i = floor(p);
                float3 f = frac(p);
                f = f * f * (3.0 - 2.0 * f);

                float n000 = VRSL_Hash3D(i);
                float n100 = VRSL_Hash3D(i + float3(1, 0, 0));
                float n010 = VRSL_Hash3D(i + float3(0, 1, 0));
                float n110 = VRSL_Hash3D(i + float3(1, 1, 0));
                float n001 = VRSL_Hash3D(i + float3(0, 0, 1));
                float n101 = VRSL_Hash3D(i + float3(1, 0, 1));
                float n011 = VRSL_Hash3D(i + float3(0, 1, 1));
                float n111 = VRSL_Hash3D(i + float3(1, 1, 1));

                float n00 = lerp(n000, n100, f.x);
                float n10 = lerp(n010, n110, f.x);
                float n01 = lerp(n001, n101, f.x);
                float n11 = lerp(n011, n111, f.x);
                float n0  = lerp(n00,  n10,  f.y);
                float n1  = lerp(n01,  n11,  f.y);
                return lerp(n0, n1, f.z);
            }
        #endif

            // Shared raymarch — accumulate VRSL light in-scattering from the
            // camera through the pixel out to rawDepth. Returns RGB radiance
            // with alpha = 0 (so half-res mode can write into a fresh RT and
            // full-res mode can additive-blend onto camera colour without
            // disturbing the destination alpha).
            float4 VRSL_Raymarch(float rawDepth, float2 uv, float2 pixelCS)
            {
            #if UNITY_REVERSED_Z
                if (rawDepth < 0.0001) return 0;   // skybox / far plane
            #else
                if (rawDepth > 0.9999) return 0;
            #endif

                float3 surfaceWS = ComputeWorldSpacePosition(
                    uv, rawDepth, UNITY_MATRIX_I_VP);
                float3 cameraWS  = _WorldSpaceCameraPos.xyz;

                float3 viewDelta = surfaceWS - cameraWS;
                float  maxDist   = length(viewDelta);
                float3 viewDir   = viewDelta / max(maxDist, 0.0001);
                float3 toCamera  = -viewDir;

                int   stepCount = max(1, (int)_VRSLVolStepCount.x);
                float stepSize  = maxDist / stepCount;

                float jitter   = VRSL_Jitter(pixelCS);
                float density  = _VRSLVolDensity.x;
                float3 tint    = _VRSLVolFogTint.xyz;
                float anisotropy = _VRSLVolStepCount.w;

                // Optional URP scene-fog coupling. unity_FogParams.x is the scene
                // fog coefficient (≈ density / sqrt(ln 2) for Exp2 mode); folding
                // it in lets a VolumeProfile globally drive shaft brightness, and
                // disabling fog suppresses the volumetric entirely.
                if (_VRSLVolStepCount.y > 0.5)
                {
                    density *= max(unity_FogParams.x, 0.0);
                    tint    *= unity_FogColor.rgb;
                }

            #ifdef _VRSL_VOLUMETRIC_NOISE
                float noiseScale    = _VRSLVolDensity.y;
                float noiseScroll   = _VRSLVolDensity.z;
                float noiseStrength = _VRSLVolDensity.w;
            #endif

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

                #ifdef _VRSL_VOLUMETRIC_NOISE
                    float3 noisePos = samplePos * noiseScale;
                    noisePos.y -= _Time.y * noiseScroll;
                    float n = VRSL_ValueNoise3D(noisePos);
                    float modulation = lerp(1.0, n, noiseStrength);
                #else
                    float modulation = 1.0;
                #endif

                    accumulated += inscatter * density * modulation * stepSize;
                }

                float3 result = accumulated * tint * _VRSLVolFogTint.w;
                return float4(result, 0);
            }
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
            #pragma multi_compile _ _VRSL_VOLUMETRIC_NOISE

            Texture2D _VRSLVolHalfResDepth;

            float4 frag(Varyings i) : SV_Target
            {
                float rawDepth = _VRSLVolHalfResDepth.SampleLevel(
                    sampler_point_clamp, i.uv, 0).r;
                return VRSL_Raymarch(rawDepth, i.uv, i.positionCS.xy);
            }
            ENDHLSL
        }

        // ── Pass 2 ───────────────────────────────────────────────────────────
        // Bilateral upsample composite. For each full-res pixel: sample a 3×3
        // neighbourhood of half-res taps, weight Gaussian × exp(-|depthDiff|),
        // and add the weighted-average to the camera colour. The 9-tap footprint
        // doubles as a low-pass filter that smooths the half-res raymarch
        // jitter pattern; the bilateral term keeps foreground silhouettes from
        // fringe-bleeding half-res values sampled from background neighbours.
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

                // Centre of the 3×3 half-res neighbourhood, snapped to texel.
                float2 halfPos  = i.uv * float2(hw, hh);
                float2 halfCtr  = floor(halfPos) + 0.5;
                float2 ctrUV    = halfCtr * halfTexel;

                // 3×3 Gaussian kernel (1,2,1; 2,4,2; 1,2,1) / 16.
                const float gauss[9] = {
                    1.0/16.0, 2.0/16.0, 1.0/16.0,
                    2.0/16.0, 4.0/16.0, 2.0/16.0,
                    1.0/16.0, 2.0/16.0, 1.0/16.0
                };
                const float2 offs[9] = {
                    float2(-1,-1), float2(0,-1), float2(1,-1),
                    float2(-1, 0), float2(0, 0), float2(1, 0),
                    float2(-1, 1), float2(0, 1), float2(1, 1)
                };

                float fullEye = LinearEyeDepth(fullDepth, _ZBufferParams);

                float4 sum  = 0;
                float  wSum = 0;

                [unroll]
                for (int j = 0; j < 9; j++)
                {
                    float2 uv = ctrUV + offs[j] * halfTexel;
                    float halfDepth = _VRSLVolHalfResDepth.SampleLevel(
                        sampler_point_clamp, uv, 0).r;
                    float halfEye  = LinearEyeDepth(halfDepth, _ZBufferParams);
                    float depthDiff = abs(fullEye - halfEye);
                    float bilateral = 1.0 / (0.0001 + depthDiff);
                    float w = gauss[j] * bilateral;
                    sum  += _VRSLVolumetricRT.SampleLevel(
                        sampler_point_clamp, uv, 0) * w;
                    wSum += w;
                }

                return sum / max(wSum, 0.0001);
            }
            ENDHLSL
        }

        // ── Pass 3 ───────────────────────────────────────────────────────────
        // Full-resolution raymarch with additive blend onto the camera colour
        // target. Samples _CameraDepthTexture directly so no half-res depth
        // downsample or bilateral upsample is needed — the half-res pipeline's
        // pass 0 and pass 2 are skipped in full-res mode. Cost is roughly 4×
        // per-pixel vs the half-res path; chosen by the manager's
        // VolumetricResolution setting.
        Pass
        {
            Name "VRSL_Vol_RaymarchFullRes"
            Blend One One
            ZWrite Off
            ZTest  Off
            Cull   Off

            HLSLPROGRAM
            #pragma vertex   vert
            #pragma fragment frag
            #pragma target   4.5
            #pragma multi_compile _ _VRSL_VOLUMETRIC_NOISE

            float4 frag(Varyings i) : SV_Target
            {
                float rawDepth = SampleSceneDepth(i.uv);
                return VRSL_Raymarch(rawDepth, i.uv, i.positionCS.xy);
            }
            ENDHLSL
        }
    }
}
