#ifndef URP_SCREEN_SPACE_GLOBAL_ILLUMINATION_UTILITIES_HLSL
#define URP_SCREEN_SPACE_GLOBAL_ILLUMINATION_UTILITIES_HLSL

#include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Lighting.hlsl"
#include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/BRDF.hlsl"
#include "Packages/com.unity.render-pipelines.core/ShaderLibrary/SpaceTransforms.hlsl"
#include "Packages/com.unity.render-pipelines.core/ShaderLibrary/AmbientProbe.hlsl"

#if UNITY_VERSION >= 202310
#if defined(PROBE_VOLUMES_L1) || defined(PROBE_VOLUMES_L2)
#include "Packages/com.unity.render-pipelines.core/Runtime/Lighting/ProbeVolume/ProbeVolume.hlsl"

void SSGIEvaluateAdaptiveProbeVolume(in float3 posWS, in float3 normalWS, in float3 viewDir, in float2 positionSS, in uint renderingLayer,
    out float3 bakeDiffuseLighting, out float4 probeOcclusion)
{
    bakeDiffuseLighting = float3(0.0, 0.0, 0.0);

    posWS = AddNoiseToSamplingPosition(posWS, positionSS, viewDir);

    APVSample apvSample = SampleAPV(posWS, normalWS, renderingLayer, viewDir);
#ifdef USE_APV_PROBE_OCCLUSION
    probeOcclusion = apvSample.probeOcclusion;
#else
    probeOcclusion = 1;
#endif

    EvaluateAdaptiveProbeVolume(apvSample, normalWS, bakeDiffuseLighting);
}

#endif
#endif

#include "Packages/com.jiaozi158.unityssgiurp/Shaders/SSGIConfig.hlsl"
#include "Packages/com.jiaozi158.unityssgiurp/Shaders/SSGIInput.hlsl"
#include "Packages/com.jiaozi158.unityssgiurp/Shaders/SSGIFallback.hlsl" // Reflection Probes Sampling

void UpdateAmbientSH()
{
    unity_SHAr = ssgi_SHAr;
    unity_SHAg = ssgi_SHAg;
    unity_SHAb = ssgi_SHAb;
    unity_SHBr = ssgi_SHBr;
    unity_SHBg = ssgi_SHBg;
    unity_SHBb = ssgi_SHBb;
    unity_SHC = ssgi_SHC;
}

half3 SSGIEvaluateAmbientProbe(half3 normalWS)
{
    // Linear + constant polynomial terms
    half3 res = SHEvalLinearL0L1(normalWS, ssgi_SHAr, ssgi_SHAg, ssgi_SHAb);

    // Quadratic polynomials
    res += SHEvalLinearL2(normalWS, ssgi_SHBr, ssgi_SHBg, ssgi_SHBb, ssgi_SHC);

    return res;
}

half3 SSGISampleProbeVolumePixel(in float3 absolutePositionWS, in float3 normalWS, in float3 viewDir, in float2 screenUV, out half4 probeOcclusion)
{
    probeOcclusion = 1.0;

#if defined(EVALUATE_SH_VERTEX) || defined(EVALUATE_SH_MIXED)
    return half3(0.0, 0.0, 0.0);
#elif defined(PROBE_VOLUMES_L1) || defined(PROBE_VOLUMES_L2)
    half3 bakedGI;
    if (_EnableProbeVolumes)
    {
        // TODO: get the actual rendering layer
        uint meshRenderingLayer = 0xFFFFFFFF; // RenderingLayerMask.Everything

        SSGIEvaluateAdaptiveProbeVolume(absolutePositionWS, normalWS, viewDir, screenUV * _ScreenSize.xy, meshRenderingLayer, bakedGI, probeOcclusion);
    }
    else
    {
        bakedGI = SSGIEvaluateAmbientProbe(normalWS);
    }
#ifdef UNITY_COLORSPACE_GAMMA
    bakedGI = LinearToSRGB(bakedGI);
#endif
    return bakedGI;
#else
    return half3(0, 0, 0);
#endif
}

half3 SSGIEvaluateAmbientProbeSRGB(half3 normalWS)
{
    half3 res = SSGIEvaluateAmbientProbe(normalWS);
#ifdef UNITY_COLORSPACE_GAMMA
    res = LinearToSRGB(res);
#endif
    return res;
}

#ifndef kDieletricSpec
#define kDieletricSpec half4(0.04, 0.04, 0.04, 1.0 - 0.04) // standard dielectric reflectivity coef at incident angle (= 4%)
#endif

// position  : world space ray origin
// direction : world space ray direction
struct Ray
{
    float3 position;
    half3  direction;
};

// position  : world space hit position
// distance  : distance that ray travels
// ...       : surfaceData of hit position
struct RayHit
{
    float3 position;
    float  distance;
    half3  normal;
    half3  emission;
};

// position : the intersection between Ray and Scene.
// distance : the distance from Ray's starting position to intersection.
// normal   : the normal direction of the intersection.
// ...      : material information from GBuffer.
RayHit InitializeRayHit()
{
    RayHit rayHit;
    rayHit.position = float3(0.0, 0.0, 0.0);
    rayHit.distance = REAL_EPS;
    rayHit.normal = half3(0.0, 0.0, 0.0);
    rayHit.emission = half3(0.0, 0.0, 0.0);
    return rayHit;
}

uint UnpackMaterialFlags(float packedMaterialFlags)
{
    return uint((packedMaterialFlags * 255.0h) + 0.5h);
}

// Generate a random value according to the current noise method.
// Counter is built into the function. (_Seed)
float GenerateRandomValue(float2 screenUV)
{
    //float time = unity_DeltaTime.y * _Time.y;
    _Seed += 1.0;
    return GenerateHashedRandomFloat(uint3(screenUV * _ScreenSize.xy, _FrameIndex + _Seed));
}

void HitSurfaceDataFromGBuffer(float2 screenUV, inout RayHit rayHit, bool isBackHit = false)
{
#if defined(_FOVEATED_RENDERING_NON_UNIFORM_RASTER)
    screenUV = (screenUV * 2.0 - 1.0) * _ScreenSize.zw;
#endif

    
#if _RENDER_PASS_ENABLED // Unused
    half4 gbuffer0 = LOAD_FRAMEBUFFER_INPUT(GBUFFER0, screenUV);
    half4 gbuffer1 = LOAD_FRAMEBUFFER_INPUT(GBUFFER1, screenUV);
    half4 gbuffer2 = LOAD_FRAMEBUFFER_INPUT(GBUFFER2, screenUV);
#else
    // Using SAMPLE_TEXTURE2D is faster than using LOAD_TEXTURE2D on iOS platforms (5% faster shader).
    // Possible reason: HLSLcc upcasts Load() operation to float, which doesn't happen for Sample()?
    half4 gbuffer0 = SAMPLE_TEXTURE2D_X_LOD(_GBuffer0, my_point_clamp_sampler, screenUV, 0);
    half4 gbuffer1 = SAMPLE_TEXTURE2D_X_LOD(_GBuffer1, my_point_clamp_sampler, screenUV, 0);
    half4 gbuffer2 = SAMPLE_TEXTURE2D_X_LOD(_GBuffer2, my_point_clamp_sampler, screenUV, 0);
#endif

    half3 gbuffer3;
    if (!isBackHit || _BackDepthEnabled != 2.0)
    {
        gbuffer3 = SAMPLE_TEXTURE2D_X_LOD(_BlitTexture, my_point_clamp_sampler, screenUV, 0).rgb;
        //half3 prevColor = SAMPLE_TEXTURE2D_X_LOD(_HistoryIndirectDiffuseTexture, my_point_clamp_sampler, screenUV, 0).rgb;
        //gbuffer3 += prevColor * gbuffer0.rgb; // (1.0 - metallic)
    }
    else
        gbuffer3 = SAMPLE_TEXTURE2D_X_LOD(_CameraBackOpaqueTexture, my_point_clamp_sampler, screenUV, 0).rgb;
        
    rayHit.normal = gbuffer2.rgb;
        
#if defined(_GBUFFER_NORMALS_OCT)
    half2 remappedOctNormalWS = half2(Unpack888ToFloat2(rayHit.normal));                // values between [ 0, +1]
    half2 octNormalWS = remappedOctNormalWS.xy * half(2.0) - half(1.0);                 // values between [-1, +1]
    rayHit.normal = half3(UnpackNormalOctQuadEncode(octNormalWS));                      // values between [-1, +1]
#endif
    rayHit.normal = isBackHit ? -rayHit.normal : rayHit.normal;

    rayHit.emission = gbuffer3.rgb;
}
#endif