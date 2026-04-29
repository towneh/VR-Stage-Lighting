#ifndef VRSL_LIGHTING_LIBRARY_INCLUDED
#define VRSL_LIGHTING_LIBRARY_INCLUDED

#define VRSL_PI 3.14159265359

// ──────────────────────────────────────────────────────────────────────────────
// GPU data layout — must exactly match the C# structs in VRSL_GPULightManager.cs
// ──────────────────────────────────────────────────────────────────────────────

// Per-fixture static configuration (CPU → GPU once, or on change)
struct VRSLFixtureConfig
{
    float4 positionAndRange;    // xyz = world position,  w = attenuation range
    float4 forwardAndType;      // xyz = base forward dir, w = light type (0=spot, 1=point)
    float4 rightAndMaxIntensity;// xyz = local +X in world space (tilt rotation axis), w = max intensity scalar
    float4 spotAngles;          // x = inner half-angle (deg), y = max outer half-angle (deg),
                                // z = finalIntensity cap,    w = min outer half-angle (deg)
    float4 dmxChannel;          // x = absolute DMX channel, y = enableStrobe,
                                // z = enablePanTilt, w = enableFineChannels
    float4 panSettings;         // x = maxMinPan (deg), y = panOffset (deg),
                                // z = invertPan (0/1), w = enableGoboSpin (0/1)
    float4 tiltSettings;        // x = maxMinTilt (deg), y = tiltOffset (deg),
                                // z = invertTilt (0/1), w = enableGobo (0/1)
};

// Per-fixture light state computed by the compute shader every frame
struct VRSLLightData
{
    float4 positionAndRange;    // xyz = world position, w = range
    float4 directionAndType;    // xyz = normalised direction (spot), w = type (0=spot,1=point)
    float4 colorAndIntensity;   // xyz = linear RGB, w = combined intensity
    float4 spotCosines;         // x = cos(inner half-angle), y = cos(outer half-angle),
                                // z = active flag (0 = skip this light), w = unused
    float4 goboAndSpin;         // x = gobo array index (-1 = no gobo, 0+ = slice in _VRSLGobos),
                                // y = gobo spin speed (bipolar: 0 = no spin, negative = CCW, positive = CW, ±10 max),
                                // zw = unused
};

// ──────────────────────────────────────────────────────────────────────────────
// Light evaluation (fragment shader use)
// ──────────────────────────────────────────────────────────────────────────────

// Distance attenuation — matches URP's smoothed inverse-square falloff
float VRSL_DistanceAttenuation(float distSq, float range)
{
    float rangeRcp = 1.0 / max(range, 0.0001);
    float d2 = distSq * rangeRcp * rangeRcp;
    float f = saturate(1.0 - d2 * d2);
    return (f * f) / max(distSq, 0.0001);
}

// Spot cone attenuation — matches URP's GetAngleAttenuation
float VRSL_SpotAttenuation(float3 lightDir, float3 toLight, float cosInner, float cosOuter)
{
    float cosAngle = dot(-lightDir, normalize(toLight));
    float t = saturate((cosAngle - cosOuter) / max(cosInner - cosOuter, 0.0001));
    return t * t;
}

// Evaluate a single VRSL light at a world-space surface point with a normal
float3 VRSL_EvaluateLight(VRSLLightData light, float3 posWS, float3 normalWS)
{
    if (light.spotCosines.z < 0.5) return 0;

    float3 toLight  = light.positionAndRange.xyz - posWS;
    float  distSq   = dot(toLight, toLight);
    float  range    = light.positionAndRange.w;

    float distAtten = VRSL_DistanceAttenuation(distSq, range);

    float spotAtten = 1.0;
    if (light.directionAndType.w < 0.5)
        spotAtten = VRSL_SpotAttenuation(
            light.directionAndType.xyz, toLight,
            light.spotCosines.x, light.spotCosines.y);

    float NdotL = max(0.0, dot(normalWS, normalize(toLight)));

    return light.colorAndIntensity.xyz * light.colorAndIntensity.w
           * distAtten * spotAtten * NdotL;
}

// ──────────────────────────────────────────────────────────────────────────────
// Volumetric (in-scattering) evaluation
// ──────────────────────────────────────────────────────────────────────────────

// Henyey–Greenstein phase function. g controls anisotropy:
//   g = 0    isotropic
//   g > 0    forward-scatter (bright when looking down the beam)
//   g < 0    back-scatter
float VRSL_HenyeyGreenstein(float cosTheta, float g)
{
    float g2 = g * g;
    float denom = 1.0 + g2 - 2.0 * g * cosTheta;
    return (1.0 - g2) / (4.0 * VRSL_PI * pow(max(denom, 0.0001), 1.5));
}

// Evaluate a single VRSL light's contribution at a point inside the volume.
// viewToCamera is the unit vector pointing from samplePos back toward the camera.
// Returns radiance per unit density per unit length — caller multiplies by
// (density * stepSize) to integrate along the view ray.
float3 VRSL_EvaluateLightVolumetric(VRSLLightData light, float3 samplePos,
                                    float3 viewToCamera, float anisotropy)
{
    if (light.spotCosines.z < 0.5) return 0;

    float3 toLight = light.positionAndRange.xyz - samplePos;
    float  distSq  = dot(toLight, toLight);
    float  range   = light.positionAndRange.w;
    if (distSq > range * range) return 0;

    float distAtten = VRSL_DistanceAttenuation(distSq, range);

    float spotAtten = 1.0;
    if (light.directionAndType.w < 0.5)
        spotAtten = VRSL_SpotAttenuation(
            light.directionAndType.xyz, toLight,
            light.spotCosines.x, light.spotCosines.y);
    if (spotAtten < 0.0001) return 0;

    // Phase: angle between the view ray (toward camera) and the direction
    // from the sample to the light source.
    float3 toLightN = toLight * rsqrt(max(distSq, 0.0001));
    float  cosTheta = dot(viewToCamera, toLightN);
    float  phase    = VRSL_HenyeyGreenstein(cosTheta, anisotropy);

    return light.colorAndIntensity.xyz * light.colorAndIntensity.w
           * distAtten * spotAtten * phase;
}

// ──────────────────────────────────────────────────────────────────────────────
// Gobo projection — shared by surface lighting and volumetric scattering
// ──────────────────────────────────────────────────────────────────────────────

// Gobo texture array — one slice per unique gobo texture. Slice index lives in
// VRSLLightData.goboAndSpin.x (-1 means no gobo).
Texture2DArray _VRSLGobos;
SamplerState   sampler_linear_clamp;

// Project a world-space point onto the light's gobo texture and return the
// resulting [0,1] grayscale mask (1.0 when no gobo is assigned). spinAngle is
// the fully-integrated rotation in radians, wrapped to [-2π, 2π] by the
// compute shader (see VRSLDMXLightUpdate.compute) — this makes the gobo
// position stay continuous across DMX rate changes.
float SampleGobo(float goboIdx, float spinAngle, float3 posWS,
                 float3 lightPos, float3 lightDir, float cosOuter)
{
    if (goboIdx < -0.5) return 1.0;

    float3 toPixel = posWS - lightPos;
    float  depth   = dot(toPixel, lightDir);
    if (depth <= 0.0) return 0.0;

    // Switch up-reference near vertical to avoid degenerate cross product.
    float3 worldUp = abs(lightDir.y) < 0.99 ? float3(0, 1, 0) : float3(0, 0, 1);
    float3 right   = normalize(cross(worldUp, lightDir));
    float3 up      = cross(lightDir, right);

    // tan(outerHalfAngle) from the stored cosine — avoids acos / radians.
    float sinOuter = sqrt(max(0.0, 1.0 - cosOuter * cosOuter));
    float tanHalf  = sinOuter / max(cosOuter, 0.0001);

    float u = dot(toPixel, right) / (depth * tanHalf) * 0.5 + 0.5;
    float v = dot(toPixel, up)    / (depth * tanHalf) * 0.5 + 0.5;

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

// ──────────────────────────────────────────────────────────────────────────────
// AudioLink GPU light path — per-fixture config written by VRSL_AudioLinkGPULightManager
// Must exactly match VRSLALFixtureConfig in VRSL_AudioLinkGPULightManager.cs
// ──────────────────────────────────────────────────────────────────────────────
struct VRSLALFixtureConfig
{
    float4 positionAndRange;  // xyz = world pos (updated per-frame), w = range
    float4 forwardAndType;    // xyz = world forward (from tiltTransform or light, per-frame),
                              // w = light type (0=spot, 1=point)
    float4 intensityParams;   // x = maxIntensity, y = finalIntensity,
                              // z = AudioLink active (1=sample AL, 0=static full intensity), w = unused
    float4 spotAngles;        // x = inner half-angle (deg), y = outer half-angle (deg),
                              // z = unused, w = unused
    float4 alParams;          // x = band (0–3), y = delay (0–127), z = bandMultiplier,
                              // w = colorMode (0=emission, 1–4=theme0–3, 5=colorChord,
                              //                6=colorTexture (HSV-normalised),
                              //                7=colorTextureTraditional (raw))
    float4 emissionColor;     // xyz = linear RGB (used when colorMode == 0), w = unused
    float4 reserved;          // x = gobo slot index (-1 = no gobo, 0+ = slice in _VRSLGobos),
                              // y = gobo spin speed (bipolar: 0 = no spin, negative = CCW, positive = CW),
                              // zw = textureSamplingCoordinates UV (used when colorMode == 6)
};

#endif // VRSL_LIGHTING_LIBRARY_INCLUDED
