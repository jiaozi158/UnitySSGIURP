#ifndef URP_SCREEN_SPACE_GLOBAL_ILLUMINATION_INPUT_HLSL
#define URP_SCREEN_SPACE_GLOBAL_ILLUMINATION_INPUT_HLSL

#include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/DeclareDepthTexture.hlsl"

// TODO: clean up

// Do not change, from URP's GBuffer hlsl.
//===================================================================================================================================
// Light flags (can shader graph access stencil buffer?)
#define kLightingInvalid  -1  // No dynamic lighting: can aliase any other material type as they are skipped using stencil
#define kLightingLit       1  // lit shader
#define kLightingSimpleLit 2  // Simple lit shader
#define kLightFlagSubtractiveMixedLighting    4 // The light uses subtractive mixed lighting.

// Material flags (customize Lit shader to add new lighting model?)
#define kMaterialFlagReceiveShadowsOff        1 // Does not receive dynamic shadows
#define kMaterialFlagSpecularHighlightsOff    2 // Does not receivce specular
#define kMaterialFlagSubtractiveMixedLighting 4 // The geometry uses subtractive mixed lighting
#define kMaterialFlagSpecularSetup            8 // Lit material use specular setup instead of metallic setup

TEXTURE2D_X_HALF(_GBuffer0); // color.rgb + materialFlags.a
TEXTURE2D_X_HALF(_GBuffer1); // specular.rgb + oclusion.a
TEXTURE2D_X_HALF(_GBuffer2); // normalWS.rgb + smoothness.a
// _GBuffer3                 // indirectLighting.rgb (B10G11R11 / R16G16B16A16)

TEXTURE2D_X_FLOAT(_CameraBackDepthTexture);
SAMPLER(sampler_CameraBackDepthTexture);

TEXTURE2D_X_HALF(_CameraBackOpaqueTexture);

// GBuffer 3 is the current render target, which means inaccessible.
// It's also the Emission GBuffer when there's no lighting in scene.
//TEXTURE2D_X(_BlitTexture);   // indirectLighting.rgb (B10G11R11 / R16G16B16A16)

SAMPLER(my_point_clamp_sampler);
SAMPLER(my_linear_clamp_sampler);

#if _RENDER_PASS_ENABLED

#define GBUFFER0 0
#define GBUFFER1 1
#define GBUFFER2 2

FRAMEBUFFER_INPUT_HALF(GBUFFER0);
FRAMEBUFFER_INPUT_HALF(GBUFFER1);
FRAMEBUFFER_INPUT_HALF(GBUFFER2);
#endif

///////////////////////////////////////////////////////////////////////////////////

half _MaxSteps;
half _StepSize;
half _SmallStepSize;
half _MediumStepSize;
half _MaxBounce;
half _RayCount;
half _Dither_Intensity;
half _Dithering;
half _Seed;
half _TemporalIntensity;
half _Sample;
half _MaxSample;
half _MaxBrightness;
half _IndirectDiffuseLightingMultiplier;
uint _IndirectDiffuseRenderingLayers;

half _BackDepthEnabled;
half _IsProbeCamera;

#ifndef _FP_REFL_PROBE_ATLAS
TEXTURECUBE(_SpecCube0);
SAMPLER(sampler_SpecCube0);
float4 _SpecCube0_ProbePosition;
float3 _SpecCube0_BoxMin;
float3 _SpecCube0_BoxMax;
half4 _SpecCube0_HDR;
TEXTURECUBE(_SpecCube1);
SAMPLER(sampler_SpecCube1);
float4 _SpecCube1_ProbePosition;
float3 _SpecCube1_BoxMin;
float3 _SpecCube1_BoxMax;
half4 _SpecCube1_HDR;
half _ProbeWeight;
half _ProbeSet;
#endif

// URP pre-defined the following variable on 2023.2+.
#if UNITY_VERSION < 202320
float4 _BlitTexture_TexelSize;
#endif

// Camera or Per Object motion vectors.
TEXTURE2D_X(_MotionVectorTexture);
float4 _MotionVectorTexture_TexelSize;

TEXTURE2D_X(_HistoryIndirectDiffuseTexture);
TEXTURE2D_X(_SSGISampleTexture);
TEXTURE2D_X(_SSGIHistorySampleTexture);
TEXTURE2D_X_FLOAT(_SSGIHistoryDepthTexture);
TEXTURE2D_X(_IndirectDiffuseTexture);
TEXTURE2D_X(_SSGIHistoryCameraColorTexture);
TEXTURE2D_X(_APVLightingTexture);
float4 _IndirectDiffuseTexture_TexelSize;

half4 ssgi_SHAr;
half4 ssgi_SHAg;
half4 ssgi_SHAb;
half4 ssgi_SHBr;
half4 ssgi_SHBg;
half4 ssgi_SHBb;
half4 ssgi_SHC;

half _DownSample;
float _FrameIndex;
half _MaxMediumSteps;
half _MaxSmallSteps;
half _HistoryTextureValid;
half _Thickness;
half _Thickness_Increment;

float4x4 _PrevInvViewProjMatrix;
float3 _PrevCameraPositionWS;
half _PixelSpreadAngleTangent;

half _AggressiveDenoise;
half4 _ReBlurBlurRotator;
half _ReBlurDenoiserRadius;
#endif