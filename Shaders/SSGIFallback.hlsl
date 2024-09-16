#ifndef URP_SCREEN_SPACE_GLOBAL_ILLUMINATION_FALLBACK_HLSL
#define URP_SCREEN_SPACE_GLOBAL_ILLUMINATION_FALLBACK_HLSL

#include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"
#include "./SSGIUtilities.hlsl"

half3 BoxProjectedDirection(half3 reflectVector, float3 positionWS, float3 probePosition, half3 boxMin, half3 boxMax)
{
    float3 factors = ((reflectVector > 0 ? boxMax : boxMin) - positionWS) * rcp(reflectVector);
    float scalar = min(min(factors.x, factors.y), factors.z);
    half3 sampleVector = reflectVector * scalar + (positionWS - probePosition);
    return sampleVector;
}

// TODO: Remove the "_FP_REFL_PROBE_ATLAS" local keyword and use "_FORWARD_PLUS" instead
// This is needed for SSPT (shader graph version) because of some shader variable redefinitions.

// Forward+ Reflection Probe Atlas
#if defined(_FP_REFL_PROBE_ATLAS)

// MAX_VISIBLE_LIGHTS is moved to URP-config package (configurable by users) starts from 2023.3.
#if UNITY_VERSION < 202330

// Must match: UniversalRenderPipeline.maxVisibleAdditionalLights
#ifndef MAX_VISIBLE_LIGHTS
#if defined(SHADER_API_MOBILE) && (defined(SHADER_API_GLES) || defined(SHADER_API_GLES30))
#define MAX_VISIBLE_LIGHTS 16
#elif defined(SHADER_API_MOBILE) || (defined(SHADER_API_GLCORE) && !defined(SHADER_API_SWITCH)) || defined(SHADER_API_GLES) || defined(SHADER_API_GLES3) // Workaround because SHADER_API_GLCORE is also defined when SHADER_API_SWITCH is
#define MAX_VISIBLE_LIGHTS 32
#else
#define MAX_VISIBLE_LIGHTS 256
#endif

#endif
#endif

// Match with values in UniversalRenderPipeline.cs
#define MAX_ZBIN_VEC4S 1024
#if MAX_VISIBLE_LIGHTS <= 32
#define MAX_LIGHTS_PER_TILE 32
#define MAX_TILE_VEC4S 1024
#else
#define MAX_LIGHTS_PER_TILE MAX_VISIBLE_LIGHTS
#define MAX_TILE_VEC4S 4096
#endif

#ifndef MAX_REFLECTION_PROBES
#define MAX_REFLECTION_PROBES (min(MAX_VISIBLE_LIGHTS, 64))
#endif

// The name of ZBinBuffer is different between 2022.x and 2023.x
#if UNITY_VERSION >= 202310
CBUFFER_START(urp_ZBinBuffer) // 2023.x
float4 urp_ZBins[MAX_ZBIN_VEC4S];
CBUFFER_END
#define URP_ZBins urp_ZBins
#else
CBUFFER_START(URP_ZBinBuffer) // 2022.x
float4 URP_ZBins[MAX_ZBIN_VEC4S];
CBUFFER_END
#define urp_ZBins URP_ZBins
#endif

CBUFFER_START(urp_TileBuffer)
float4 urp_Tiles[MAX_TILE_VEC4S];
CBUFFER_END

TEXTURE2D(urp_ReflProbes_Atlas);
SAMPLER(samplerurp_ReflProbes_Atlas);
float urp_ReflProbes_Count;

#ifndef SHADER_API_GLES3
CBUFFER_START(urp_ReflectionProbeBuffer)
#endif
//half4 urp_ReflProbes_HDR[MAX_REFLECTION_PROBES];
float4 urp_ReflProbes_BoxMax[MAX_REFLECTION_PROBES];          // w contains the blend distance
float4 urp_ReflProbes_BoxMin[MAX_REFLECTION_PROBES];          // w contains the importance
float4 urp_ReflProbes_ProbePosition[MAX_REFLECTION_PROBES];   // w is positive for box projection, |w| is max mip level
float4 urp_ReflProbes_MipScaleOffset[MAX_REFLECTION_PROBES * 7];
#ifndef SHADER_API_GLES3
CBUFFER_END
#endif

float4 _FPParams0;
float4 _FPParams1;
float4 _FPParams2;

#define URP_FP_ZBIN_SCALE (_FPParams0.x)
#define URP_FP_ZBIN_OFFSET (_FPParams0.y)
#define URP_FP_PROBES_BEGIN ((uint)_FPParams0.z)
// Directional lights would be in all clusters, so they don't go into the cluster structure.
// Instead, they are stored first in the light buffer.
#define URP_FP_DIRECTIONAL_LIGHTS_COUNT ((uint)_FPParams0.w)

// Scale from screen-space UV [0, 1] to tile coordinates [0, tile resolution].
#define URP_FP_TILE_SCALE ((float2)_FPParams1.xy)
#define URP_FP_TILE_COUNT_X ((uint)_FPParams1.z)
#define URP_FP_WORDS_PER_TILE ((uint)_FPParams1.w)

#define URP_FP_ZBIN_COUNT ((uint)_FPParams2.x)
#define URP_FP_TILE_COUNT ((uint)_FPParams2.y)

// Debug switches for disabling parts of the algorithm. Not implemented for mobile.
#define URP_FP_DISABLE_ZBINNING 0
#define URP_FP_DISABLE_TILING 0

struct ClusterIterator
{
    uint tileOffset;
    uint zBinOffset;
    uint tileMask;
    // Stores the next light index in first 16 bits, and the max light index in the last 16 bits.
    uint entityIndexNextMax;
};

// internal
ClusterIterator ClusterInit(float2 normalizedScreenSpaceUV, float3 positionWS, int headerIndex)
{
    ClusterIterator state = (ClusterIterator)0;

    uint2 tileId = uint2(normalizedScreenSpaceUV * URP_FP_TILE_SCALE);
    state.tileOffset = tileId.y * URP_FP_TILE_COUNT_X + tileId.x;
#if defined(USING_STEREO_MATRICES)
    state.tileOffset += URP_FP_TILE_COUNT * unity_StereoEyeIndex;
#endif
    state.tileOffset *= URP_FP_WORDS_PER_TILE;

    float viewZ = dot(GetViewForwardDir(), positionWS - GetCameraPositionWS());
    uint zBinBaseIndex = (uint)((IsPerspectiveProjection() ? log2(viewZ) : viewZ) * URP_FP_ZBIN_SCALE + URP_FP_ZBIN_OFFSET);
#if defined(USING_STEREO_MATRICES)
    zBinBaseIndex += URP_FP_ZBIN_COUNT * unity_StereoEyeIndex;
#endif
    zBinBaseIndex = min(4*MAX_ZBIN_VEC4S - 1, zBinBaseIndex) * (2 + URP_FP_WORDS_PER_TILE);

    uint zBinHeaderIndex = zBinBaseIndex + headerIndex;
    state.zBinOffset = zBinBaseIndex + 2;

#if !URP_FP_DISABLE_ZBINNING
    uint header = Select4(asuint(urp_ZBins[zBinHeaderIndex / 4]), zBinHeaderIndex % 4);
#else
    uint header = headerIndex == 0 ? ((URP_FP_PROBES_BEGIN - 1) << 16) : (((URP_FP_WORDS_PER_TILE * 32 - 1) << 16) | URP_FP_PROBES_BEGIN);
#endif
#if MAX_LIGHTS_PER_TILE > 32
    state.entityIndexNextMax = header;
#else
    uint tileIndex = state.tileOffset;
    uint zBinIndex = state.zBinOffset;
    if (URP_FP_WORDS_PER_TILE > 0)
    {
        state.tileMask =
            Select4(asuint(urp_Tiles[tileIndex / 4]), tileIndex % 4) &
            Select4(asuint(urp_ZBins[zBinIndex / 4]), zBinIndex % 4) &
            (0xFFFFFFFFu << (header & 0x1F)) & (0xFFFFFFFFu >> (31 - (header >> 16)));
    }
#endif

    return state;
}

// internal
bool ClusterNext(inout ClusterIterator it, out uint entityIndex)
{
#if MAX_LIGHTS_PER_TILE > 32
    uint maxIndex = it.entityIndexNextMax >> 16;
    while (it.tileMask == 0 && (it.entityIndexNextMax & 0xFFFF) <= maxIndex)
    {
        // Extract the lower 16 bits and shift by 5 to divide by 32.
        uint wordIndex = ((it.entityIndexNextMax & 0xFFFF) >> 5);
        uint tileIndex = it.tileOffset + wordIndex;
        uint zBinIndex = it.zBinOffset + wordIndex;
        it.tileMask =
#if !URP_FP_DISABLE_TILING
            Select4(asuint(urp_Tiles[tileIndex / 4]), tileIndex % 4) &
#endif
#if !URP_FP_DISABLE_ZBINNING
            Select4(asuint(urp_ZBins[zBinIndex / 4]), zBinIndex % 4) &
#endif
            // Mask out the beginning and end of the word.
            (0xFFFFFFFFu << (it.entityIndexNextMax & 0x1F)) & (0xFFFFFFFFu >> (31 - min(31, maxIndex - wordIndex * 32)));
        // The light index can start at a non-multiple of 32, but the following iterations should always be multiples of 32.
        // So we add 32 and mask out the lower bits.
        it.entityIndexNextMax = (it.entityIndexNextMax + 32) & ~31;
    }
#endif
    bool hasNext = it.tileMask != 0;
    uint bitIndex = FIRST_BIT_LOW(it.tileMask);
    it.tileMask ^= (1 << bitIndex);
#if MAX_LIGHTS_PER_TILE > 32
    // Subtract 32 because it stores the index of the _next_ word to fetch, but we want the current.
    // The upper 16 bits and bits representing values < 32 are masked out. The latter is due to the fact that it will be
    // included in what FIRST_BIT_LOW returns.
    entityIndex = (((it.entityIndexNextMax - 32) & (0xFFFF & ~31))) + bitIndex;
#else
    entityIndex = bitIndex;
#endif
    return hasNext;
}

// used by Forward+
half3 SampleReflectionProbesAtlas(half3 reflectVector, float3 positionWS, half mipLevel, float2 normalizedScreenSpaceUV)
{
    half3 irradiance = half3(0.0h, 0.0h, 0.0h);

    float totalWeight = 0.0f;

#if defined(_RAYMARCHING_FALLBACK_REFLECTION_PROBES)
    uint probeIndex;
    ClusterIterator it = ClusterInit(normalizedScreenSpaceUV, positionWS, 1);
    [loop] while (ClusterNext(it, probeIndex) && totalWeight < 0.99f && probeIndex <= 32)
    {
        probeIndex -= URP_FP_PROBES_BEGIN;

        float weight = CalculateProbeWeight(positionWS, urp_ReflProbes_BoxMin[probeIndex], urp_ReflProbes_BoxMax[probeIndex]);
        weight = min(weight, 1.0f - totalWeight);

        half3 sampleVector = reflectVector;
//#ifdef _REFLECTION_PROBE_BOX_PROJECTION
        sampleVector = BoxProjectedDirection(reflectVector, positionWS, urp_ReflProbes_ProbePosition[probeIndex].xyz, urp_ReflProbes_BoxMin[probeIndex].xyz, urp_ReflProbes_BoxMax[probeIndex].xyz);
//#endif // _REFLECTION_PROBE_BOX_PROJECTION

        uint maxMip = (uint)abs(urp_ReflProbes_ProbePosition[probeIndex].w) - 1;
        half probeMip = min(mipLevel, maxMip);
        float2 uv = saturate(PackNormalOctQuadEncode(sampleVector) * 0.5 + 0.5);

        // URP already fixed the flipped reflection probes on GL platforms when using Forward+.
        // Commit: "https://github.com/Unity-Technologies/Graphics/commit/43866acc92f39d326711bda6aee9d656836de1d8"
        // You can uncomment the following lines to resolve this issue if using an old version of URP.
        
        // [GL]: Flip the uv first.
    //#if !UNITY_REVERSED_Z
        //uv.y = 1.0 - uv.y;
    //#endif

        float mip0 = floor(probeMip);
        float mip1 = mip0 + 1;
        float mipBlend = probeMip - mip0;
        float4 scaleOffset0 = urp_ReflProbes_MipScaleOffset[probeIndex * 7 + (uint)mip0];
        float4 scaleOffset1 = urp_ReflProbes_MipScaleOffset[probeIndex * 7 + (uint)mip1];

        float2 uv0 = uv * scaleOffset0.xy + scaleOffset0.zw;
        float2 uv1 = uv * scaleOffset1.xy + scaleOffset1.zw;

        // URP already fixed the flipped reflection probes on GL platforms when using Forward+.
        // Commit: "https://github.com/Unity-Technologies/Graphics/commit/43866acc92f39d326711bda6aee9d656836de1d8"
        // You can uncomment the following lines to resolve this issue if using an old version of URP.
        
        // [GL]: Flip back after applying atlas offsets.
    //#if !UNITY_REVERSED_Z
        //uv0.y = 1.0 - uv0.y;
        //uv1.y = 1.0 - uv1.y;
    //#endif

        half3 encodedIrradiance0 = half3(SAMPLE_TEXTURE2D_LOD(urp_ReflProbes_Atlas, samplerurp_ReflProbes_Atlas, uv0, 0).rgb);
        half3 encodedIrradiance1 = half3(SAMPLE_TEXTURE2D_LOD(urp_ReflProbes_Atlas, samplerurp_ReflProbes_Atlas, uv1, 0).rgb);
        //real4 hdr = urp_ReflProbes_HDR[probeIndex];
        //irradiance += weight * DecodeHDREnvironment(lerp(encodedIrradiance0, encodedIrradiance1, mipBlend), hdr);
        //totalWeight += weight;
        irradiance += weight * lerp(encodedIrradiance0, encodedIrradiance1, mipBlend);
        totalWeight += weight;
    }
#endif

#if defined(_RAYMARCHING_FALLBACK_SKY)
    if (totalWeight < 1.0f)
    {
        UpdateAmbientSH();
    #if defined(PROBE_VOLUMES_L1) || defined(PROBE_VOLUMES_L2)
        half3 viewDirectionWS = normalize(GetCameraPositionWS() - positionWS);
        half4 probeOcclusion;
        half3 ambientLighting = SSGISampleProbeVolumePixel(positionWS, reflectVector, viewDirectionWS, normalizedScreenSpaceUV, probeOcclusion);
        irradiance += ambientLighting * probeOcclusion.rgb * (1.0 - totalWeight);
    #else
        irradiance += SSGIEvaluateAmbientProbeSRGB(reflectVector) * (1.0 - totalWeight);
    #endif
    }
#endif

    return irradiance;
}

#else // (_FP_REFL_PROBE_ATLAS)

// used by Forward or Deferred
half3 SampleReflectionProbesCubemap(half3 reflectVector, float3 positionWS, half mipLevel)
{
    half3 color = half3(0.0, 0.0, 0.0);

    // Check if the reflection probes are correctly set.
    // We don't support probe blending in Forward & Deferred path yet.
#if defined(_RAYMARCHING_FALLBACK_SKY)
    if (!_ProbeSet)
    {
        UpdateAmbientSH();
        color = SSGIEvaluateAmbientProbeSRGB(reflectVector.xyz);
        return color;
    }
#else
    if (!_ProbeSet)
        return color;
#endif

#if defined(_RAYMARCHING_FALLBACK_REFLECTION_PROBES)
    half3 uvw = reflectVector;

    if (_SpecCube0_ProbePosition.w > 0.0) // Box Projection Probe
    {
        float3 factors = ((reflectVector > 0 ? _SpecCube0_BoxMax.xyz : _SpecCube0_BoxMin.xyz) - positionWS) * rcp(reflectVector);
        float scalar = min(min(factors.x, factors.y), factors.z);
        uvw = reflectVector * scalar + (positionWS - _SpecCube0_ProbePosition.xyz);
    }

    color = DecodeHDREnvironment(SAMPLE_TEXTURECUBE_LOD(_SpecCube0, sampler_SpecCube0, uvw, mipLevel), _SpecCube0_HDR).rgb;

    // TODO: Implement a better reflection probe blending for Forward & Deferred path
    /*
    UNITY_BRANCH
    if (_ProbeWeight > 0.0) // Probe Blending Enabled
    {
        half3 probe2Color = half3(0.0, 0.0, 0.0);
        UNITY_BRANCH
        if (_SpecCube1_ProbePosition.w > 0.0) // Box Projection Probe
        {
            float3 factors = ((reflectVector > 0 ? _SpecCube1_BoxMax.xyz : _SpecCube1_BoxMin.xyz) - positionWS) * rcp(reflectVector);
            float scalar = min(min(factors.x, factors.y), factors.z);
            float3 uvw = reflectVector * scalar + (positionWS - _SpecCube1_ProbePosition.xyz);
            probe2Color = DecodeHDREnvironment(SAMPLE_TEXTURECUBE_LOD(_SpecCube1, sampler_SpecCube1, uvw, mipLevel), _SpecCube1_HDR).rgb;
        }
        else
        {
            probe2Color = DecodeHDREnvironment(SAMPLE_TEXTURECUBE_LOD(_SpecCube1, sampler_SpecCube1, reflectVector, mipLevel), _SpecCube1_HDR).rgb;
        }
        // Blend the probes if necessary.
        color = lerp(color, probe2Color, _ProbeWeight).rgb;
    }
    */
#endif

    return color;
}
#endif

half3 SampleReflectionProbes(half3 reflectVector, float3 positionWS, half mipLevel, float2 normalizedScreenSpaceUV)
{
    half3 color = half3(0.0, 0.0, 0.0);

    #if defined(_FP_REFL_PROBE_ATLAS)
        color = ClampToFloat16Max(SampleReflectionProbesAtlas(reflectVector, positionWS, mipLevel, normalizedScreenSpaceUV));
    #else
        color = SampleReflectionProbesCubemap(reflectVector, positionWS, mipLevel);
    #endif
    
    // Limit the intensity of SSGI results accumulated in reflection probe
    return _IsProbeCamera ? color * 0.3 : color;
}
#endif