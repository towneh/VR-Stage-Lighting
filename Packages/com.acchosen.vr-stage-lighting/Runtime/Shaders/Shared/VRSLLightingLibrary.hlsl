#ifndef VRSL_LIGHTING_LIBRARY_INCLUDED
#define VRSL_LIGHTING_LIBRARY_INCLUDED

// ──────────────────────────────────────────────────────────────────────────────
// GPU data layout — must exactly match the C# structs in VRSL_GPULightManager.cs
// ──────────────────────────────────────────────────────────────────────────────

// Per-fixture static configuration (CPU → GPU once, or on change)
struct VRSLFixtureConfig
{
    float4 positionAndRange;    // xyz = world position,  w = attenuation range
    float4 forwardAndType;      // xyz = base forward dir, w = light type (0=spot, 1=point)
    float4 upAndMaxIntensity;   // xyz = pan axis (world up by default), w = max intensity scalar
    float4 spotAngles;          // x = inner half-angle (deg), y = outer half-angle (deg),
                                // z = finalIntensity cap, w = unused
    float4 dmxChannel;          // x = absolute DMX channel, y = enableStrobe,
                                // z = enablePanTilt, w = enableFineChannels
    float4 panSettings;         // x = maxMinPan (deg), y = panOffset (deg),
                                // z = invertPan (0/1), w = unused
    float4 tiltSettings;        // x = maxMinTilt (deg), y = tiltOffset (deg),
                                // z = invertTilt (0/1), w = unused
};

// Per-fixture light state computed by the compute shader every frame
struct VRSLLightData
{
    float4 positionAndRange;    // xyz = world position, w = range
    float4 directionAndType;    // xyz = normalised direction (spot), w = type (0=spot,1=point)
    float4 colorAndIntensity;   // xyz = linear RGB, w = combined intensity
    float4 spotCosines;         // x = cos(inner half-angle), y = cos(outer half-angle),
                                // z = active flag (0 = skip this light),
                                // w = cookie array index (-1 = no cookie, 0+ = slice)
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
                              // w = colorMode (0=emission,1–4=theme0–3,5=colorChord)
    float4 emissionColor;     // xyz = linear RGB (used when colorMode == 0), w = unused
    float4 reserved;          // x = cookie array index (-1 = no cookie, 0+ = slice in _VRSLCookies)
};

#endif // VRSL_LIGHTING_LIBRARY_INCLUDED
