#ifndef URP_SCREEN_SPACE_GLOBAL_ILLUMINATION_DENOISE_HLSL
#define URP_SCREEN_SPACE_GLOBAL_ILLUMINATION_DENOISE_HLSL

#include "Packages/com.unity.render-pipelines.core/ShaderLibrary/Color.hlsl"
#include "Packages/com.jiaozi158.unityssgiurp/Shaders/SSGIUtilities.hlsl"
#include "Packages/com.jiaozi158.unityssgiurp/Shaders/SSGIConfig.hlsl"

#define _DenoiserFilterRadius 0.6
float ComputeMaxDenoisingRadius(float3 positionWS)
{
    // Compute the distance to the pixel
    float distanceToPoint = length(positionWS);
    // This is purely empirical, values were obtained  while experimenting with various scenes and these values give good visual results.
    // The world space radius for sample picking goes from distance/10.0 to distance/50.0 linearly until reaching 500.0 meters away from the camera
    // and it is always 20.0f (or two pixels if subpixel.
    // TODO: @Anis, I have a bunch of idea how to make this better and less empirical but it's for any other day
    return distanceToPoint * _DenoiserFilterRadius / lerp(5.0, 50.0, saturate(distanceToPoint / 500.0));
}

half ComputeMaxReprojectionWorldRadius(float3 positionWS, half3 viewDirWS, half3 normalWS, half pixelSpreadAngleTangent, half maxDistance, half pixelTolerance)
{
    half parallelPixelFootPrint = pixelSpreadAngleTangent * length(positionWS - GetCameraPositionWS());
    half realPixelFootPrint = parallelPixelFootPrint / max(abs(dot(normalWS, viewDirWS)), PROJECTION_EPSILON);
    return max(maxDistance, realPixelFootPrint * pixelTolerance);
}

half ComputeMaxReprojectionWorldRadius(float3 positionWS, half3 viewDirWS, half3 normalWS, half pixelSpreadAngleTangent)
{
    return ComputeMaxReprojectionWorldRadius(positionWS, viewDirWS, normalWS, pixelSpreadAngleTangent, MAX_REPROJECTION_DISTANCE, MAX_PIXEL_TOLERANCE);
}

// From Playdead's TAA
// (half version of HDRP impl)
half3 SampleColorPoint(Texture2D _BlitTexture, float2 uv, float2 texelOffset)
{
    return SAMPLE_TEXTURE2D_X_LOD(_BlitTexture, my_point_clamp_sampler, uv + _BlitTexture_TexelSize.xy * texelOffset, 0).xyz;
}

void AdjustColorBox(inout half3 boxMin, inout half3 boxMax, inout half3 moment1, inout half3 moment2, float2 uv, half currX, half currY)
{
    half3 color = SampleColorPoint(_BlitTexture, uv, float2(currX, currY));
    boxMin = min(color, boxMin);
    boxMax = max(color, boxMax);
    moment1 += color;
    moment2 += color * color;
}

half3 DirectClipToAABB(half3 history, half3 minimum, half3 maximum)
{
    // note: only clips towards aabb center (but fast!)
    half3 center = 0.5 * (maximum + minimum);
    half3 extents = 0.5 * (maximum - minimum);

    // This is actually `distance`, however the keyword is reserved
    half3 offset = history - center;
    half3 v_unit = offset.xyz / extents.xyz;
    half3 absUnit = abs(v_unit);
    half maxUnit = Max3(absUnit.x, absUnit.y, absUnit.z);
    if (maxUnit > 1.0)
        return center + (offset / maxUnit);
    else
        return history;
}
#endif