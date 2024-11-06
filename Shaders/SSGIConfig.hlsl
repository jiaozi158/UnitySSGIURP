#ifndef URP_SCREEN_SPACE_GLOBAL_ILLUMINATION_CONFIG_HLSL
#define URP_SCREEN_SPACE_GLOBAL_ILLUMINATION_CONFIG_HLSL

#define MAX_STEP              _MaxSteps
#define MAX_SMALL_STEP        _MaxSmallSteps
#define MAX_MEDIUM_STEP       _MaxMediumSteps

#define STEP_SIZE             _StepSize
#define SMALL_STEP_SIZE		  _SmallStepSize
#define MEDIUM_STEP_SIZE	  _MediumStepSize

// Minimum thickness of scene objects (in meters)
#define MARCHING_THICKNESS				_Thickness
#define MARCHING_THICKNESS_SMALL_STEP   0.01
#define MARCHING_THICKNESS_MEDIUM_STEP  0.1

#define RAY_COUNT             _RayCount

// Temporal Accumulation maximum history samples
#define MAX_ACCUM_FRAME_NUM			8

// Temporal re-projection rejection threshold
#define MAX_REPROJECTION_DISTANCE	0.1
#define MAX_PIXEL_TOLERANCE			4
#define PROJECTION_EPSILON			0.000001

#define CLAMP_MAX       65472.0 // HALF_MAX minus one (2 - 2^-9) * 2^15

// It seems that some developers use shader graph to create the skybox, but cannot disable depth write due to Unity (shader graph) issue
// For better compatibility with different skybox shaders, we add a depth comparision threshold
#define RAW_FAR_CLIP_THRESHOLD 1e-7

#endif