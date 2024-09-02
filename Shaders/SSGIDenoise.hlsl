#ifndef URP_SCREEN_SPACE_GLOBAL_ILLUMINATION_DENOISE_HLSL
#define URP_SCREEN_SPACE_GLOBAL_ILLUMINATION_DENOISE_HLSL

#include "Packages/com.unity.render-pipelines.core/ShaderLibrary/Color.hlsl"
#include "./SSGIUtilities.hlsl"
#include "./SSGIConfig.hlsl"

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
half3 SampleColorPoint(float2 uv, float2 texelOffset)
{
    return SAMPLE_TEXTURE2D_X_LOD(_BlitTexture, my_point_clamp_sampler, uv + _BlitTexture_TexelSize.xy * texelOffset, 0).xyz;
}

void AdjustColorBox(inout half3 boxMin, inout half3 boxMax, inout half3 moment1, inout half3 moment2, float2 uv, half currX, half currY)
{
    half3 color = SampleColorPoint(uv, float2(currX, currY));
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

half sqr(half value)
{
	return value * value;
}

#define K 0.5

half HitDistanceAttenuation(half linearRoughness, float cameraDistance, float hitDistance)
{
	half f = hitDistance / (hitDistance + cameraDistance);
	return lerp(K * linearRoughness, 1.0, f);
}

half GetSpecularDominantFactor(half NoV, half linearRoughness)
{
	half a = 0.298475 * log(39.4115 - 39.0029 * linearRoughness);
	half dominantFactor = pow(saturate(1.0 - NoV), 10.8649) * (1.0 - a) + a;
	return saturate(dominantFactor);
}

// Optimized for roughness = 1.0
half GetSpecularDominantFactor(half NoV)
{
	half a = 0.298475 * log(39.4115 - 39.0029 * 1.0);
	half dominantFactor = pow(saturate(1.0 - NoV), 10.8649) * (1.0 - a) + a;
	return saturate(dominantFactor);
}

half3 GetSpecularDominantDirectionWithFactor(half3 N, half3 V, half dominantFactor)
{
	half3 R = reflect(-V, N);
	half3 D = lerp(N, R, dominantFactor);

	return normalize(D);
}

half4 GetSpecularDominantDirection(half3 N, half3 V, half linearRoughness)
{
	half NoV = abs(dot(N, V));
	half dominantFactor = GetSpecularDominantFactor(NoV, linearRoughness);

	return half4(GetSpecularDominantDirectionWithFactor(N, V, dominantFactor), dominantFactor);
}

// Optimized for roughness = 1.0
half4 GetSpecularDominantDirection(half3 N, half3 V)
{
	half NoV = abs(dot(N, V));
	half dominantFactor = GetSpecularDominantFactor(NoV);

	return half4(GetSpecularDominantDirectionWithFactor(N, V, dominantFactor), dominantFactor);
}

half2x3 GetKernelBasis(half3 V, half3 N, half linearRoughness)
{
	half3x3 basis = GetLocalFrame(N);
	half3 T = basis[0];
	half3 B = basis[1];
	half NoV = abs(dot(N, V));
	half f = GetSpecularDominantFactor(NoV, linearRoughness);
	half3 R = reflect(-V, N);
	half3 D = normalize(lerp(N, R, f));
	half NoD = abs(dot(N, D));

	if (NoD < 0.999 && linearRoughness != 1.0)
	{
		half3 Dreflected = reflect(-D, N);
		T = normalize(cross(N, Dreflected));
		B = cross(Dreflected, T);

		half NoV = abs(dot(N, V));
		half acos01sq = saturate(1.0 - NoV);
		half skewFactor = lerp(1.0, linearRoughness, sqrt(acos01sq));
		T *= skewFactor;
	}

	return half2x3(T, B);
}

// Optimized for roughness = 1.0
half2x3 GetKernelBasis(half3 V, half3 N)
{
	half3x3 basis = GetLocalFrame(N);
	half3 T = basis[0];
	half3 B = basis[1];
	half NoV = abs(dot(N, V));
	half f = GetSpecularDominantFactor(NoV);
	half3 R = reflect(-V, N);
	half3 D = normalize(lerp(N, R, f));
	half NoD = abs(dot(N, D));

	return half2x3(T, B);
}

#define POISSON_SAMPLE_COUNT 8
static const half3 k_PoissonDiskSamples[POISSON_SAMPLE_COUNT] =
{
	// https://www.desmos.com/calculator/abaqyvswem
	half3(-1.00             ,  0.00             , 1.0),
	half3(0.00             ,  1.00             , 1.0),
	half3(1.00             ,  0.00             , 1.0),
	half3(0.00             , -1.00             , 1.0),
	half3(-0.25 * sqrt(2.0) ,  0.25 * sqrt(2.0) , 0.5),
	half3(0.25 * sqrt(2.0) ,  0.25 * sqrt(2.0) , 0.5),
	half3(0.25 * sqrt(2.0) , -0.25 * sqrt(2.0) , 0.5),
	half3(-0.25 * sqrt(2.0) , -0.25 * sqrt(2.0) , 0.5)
};

half GetGaussianWeight(half r)
{
	return exp(-0.66 * r * r); // assuming r is normalized to 1
}

static const half k_GaussianWeight[POISSON_SAMPLE_COUNT] =
{
	GetGaussianWeight(k_PoissonDiskSamples[0].z),
	GetGaussianWeight(k_PoissonDiskSamples[1].z),
	GetGaussianWeight(k_PoissonDiskSamples[2].z),
	GetGaussianWeight(k_PoissonDiskSamples[3].z),
	GetGaussianWeight(k_PoissonDiskSamples[4].z),
	GetGaussianWeight(k_PoissonDiskSamples[5].z),
	GetGaussianWeight(k_PoissonDiskSamples[6].z),
	GetGaussianWeight(k_PoissonDiskSamples[7].z)
};

half GetSpecularLobeHalfAngle(half linearRoughness, half percentOfVolume = 0.75)
{
	half m = linearRoughness * linearRoughness;
	// https://seblagarde.files.wordpress.com/2015/07/course_notes_moving_frostbite_to_pbr_v32.pdf (page 72)
	// TODO: % of NDF volume - is it the trimming factor from VNDF sampling?
	return atan(m * percentOfVolume / (1.0 - percentOfVolume));
}

half GetSpecMagicCurve2(half roughness, half percentOfVolume = 0.987)
{
	half angle = GetSpecularLobeHalfAngle(roughness, percentOfVolume);
	half almostHalfPi = GetSpecularLobeHalfAngle(1.0, percentOfVolume);
	return saturate(angle / almostHalfPi);
}

half ComputeBlurRadius(half roughness, half maxRadius)
{
	return maxRadius * GetSpecMagicCurve2(roughness);
}

half2 RotateVector(half4 rotator, half2 v)
{
	return v.x * rotator.xz + v.y * rotator.yw;
}

float2 GetKernelSampleCoordinates(half3 offset, float3 X, half3 T, half3 B, half4 rotator)
{
	// We can't rotate T and B instead, because T is skewed
	offset.xy = RotateVector(rotator, offset.xy);

	// Compute the world space position
	float3 wsPos = X + T * offset.x + B * offset.y;

	// Evaluate the NDC position
	float4 hClip = TransformWorldToHClip(wsPos);
	hClip.xyz /= hClip.w;

	// Convert it to screen sample space
	float2 nDC = hClip.xy * 0.5 + 0.5;
#if UNITY_UV_STARTS_AT_TOP
	nDC.y = 1.0 - nDC.y;
#endif
	return nDC;
}

// Maximum world space radius of the blur
#define BLUR_MAX_RADIUS 0.04
#define MIN_BLUR_DISTANCE 0.03
#define BLUR_OUT_RANGE 0.05

#endif