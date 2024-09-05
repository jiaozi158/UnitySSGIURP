Shader "Hidden/Lighting/ScreenSpaceGlobalIllumination"
{
    Properties
    {
        
    }

    SubShader
    {
        Cull Off ZWrite Off ZTest Always

		// Pass 0: Copy Direct Lighting
		// Pass 1: SSGI
		// Pass 2: Temporal Reprojection
		// Pass 3: Edge-Avoiding Spatial Denoise
		// Pass 4: Temporal Stabilization
		// Pass 5: Copy History Depth
		// Pass 6: Combine & Upscale GI
		// Pass 7: [Editor only] Camera Motion Vectors
		// Pass 8: Poisson Disk Recurrent Denoise
		// Pass 9: Blit Color Texture

		Pass
		{
			Name "Copy Direct Lighting"
			Tags { "LightMode" = "Screen Space Global Illumination" }

			Blend One Zero

			HLSLPROGRAM
			#include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"
			// The Blit.hlsl file provides the vertex shader (Vert),
			// input structure (Attributes) and output structure (Varyings)
			#include "Packages/com.unity.render-pipelines.core/Runtime/Utilities/Blit.hlsl"

			#pragma vertex Vert
			#pragma fragment frag

			#pragma target 3.5

			#pragma multi_compile_fragment _ _GBUFFER_NORMALS_OCT

		#if UNITY_VERSION >= 202310
			#pragma multi_compile_fragment _ PROBE_VOLUMES_L1 PROBE_VOLUMES_L2
		#endif

			#include "./SSGI.hlsl"

			half4 frag(Varyings input) : SV_Target
			{
				UNITY_SETUP_STEREO_EYE_INDEX_POST_VERTEX(input);
				float2 screenUV = input.texcoord;

				float depth = SAMPLE_TEXTURE2D_X_LOD(_CameraDepthTexture, my_point_clamp_sampler, screenUV, 0).r;

			#if !UNITY_REVERSED_Z
                depth = lerp(UNITY_NEAR_CLIP_VALUE, 1, depth);
            #endif

				half4 directLighting = SAMPLE_TEXTURE2D_X_LOD(_BlitTexture, my_point_clamp_sampler, screenUV, 0).rgba;

				// If the current pixel is sky
				bool isBackground = depth == UNITY_RAW_FAR_CLIP_VALUE ? true : false;

				if (isBackground)
					return directLighting;

				UpdateAmbientSH();

				half4 gbuffer0 = SAMPLE_TEXTURE2D_X_LOD(_GBuffer0, my_point_clamp_sampler, screenUV, 0);
				half3 albedo = gbuffer0.rgb;
				half4 normalSmoothness = SAMPLE_TEXTURE2D_X_LOD(_GBuffer2, my_point_clamp_sampler, screenUV, 0);

			#if defined(_GBUFFER_NORMALS_OCT)
				half2 remappedOctNormalWS = half2(Unpack888ToFloat2(normalSmoothness.xyz));                // values between [ 0, +1]
				half2 octNormalWS = remappedOctNormalWS.xy * half(2.0) - half(1.0);                        // values between [-1, +1]
				normalSmoothness.xyz = half3(UnpackNormalOctQuadEncode(octNormalWS));                      // values between [-1, +1]
			#endif 

				half4 gbuffer1 = SAMPLE_TEXTURE2D_X_LOD(_GBuffer1, my_point_clamp_sampler, screenUV, 0);
				half metallic = (gbuffer0.a == kMaterialFlagSpecularSetup) ? MetallicFromReflectivity(ReflectivitySpecular(gbuffer1.rgb)) : gbuffer1.r;

			#if defined(PROBE_VOLUMES_L1) || defined(PROBE_VOLUMES_L2)
				float3 positionWS = ComputeWorldSpacePosition(screenUV, depth, UNITY_MATRIX_I_VP);
				float3 viewDirectionWS = normalize(GetCameraPositionWS() - positionWS);
				half4 probeOcclusion;
				half3 ambientLighting = SSGISampleProbeVolumePixel(positionWS, normalSmoothness.xyz, viewDirectionWS, screenUV, probeOcclusion);
				ambientLighting = ambientLighting * probeOcclusion.rgb * albedo * (1.0 - metallic);
			#else
				half3 ambientLighting = SSGIEvaluateAmbientProbeSRGB(normalSmoothness.xyz) * albedo * (1.0 - metallic);
			#endif
				
				directLighting.rgb = max(directLighting.rgb - ambientLighting, half3(0.0, 0.0, 0.0));

				return directLighting;
				//return half4(ambientLighting, directLighting.a); // debug ambient lighting
			}
			ENDHLSL
		}
		
        Pass
        {
            Name "Screen Space Global Illumination"
            Tags { "LightMode" = "Screen Space Global Illumination" }

            Blend One Zero
			
            HLSLPROGRAM
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"
            // The Blit.hlsl file provides the vertex shader (Vert),
            // input structure (Attributes) and output structure (Varyings)
            #include "Packages/com.unity.render-pipelines.core/Runtime/Utilities/Blit.hlsl"
			
            #pragma vertex Vert
            #pragma fragment frag

            #pragma target 3.5

            #pragma multi_compile_local_fragment _ _FP_REFL_PROBE_ATLAS
            #pragma multi_compile_local_fragment _ _BACKFACE_TEXTURES

            #pragma multi_compile_fragment _ _GBUFFER_NORMALS_OCT

			#include "./SSGIDenoise.hlsl"
			#include "./SSGI.hlsl"
            
			half4 frag(Varyings input) : SV_Target
			{
				UNITY_SETUP_STEREO_EYE_INDEX_POST_VERTEX(input);
				float2 screenUV = input.texcoord;

				half4 lightingDistance = half4(0.0, 0.0, 0.0, 0.0); // indirectDiffuse.rgb + distance.a

				float depth = SAMPLE_TEXTURE2D_X_LOD(_CameraDepthTexture, my_point_clamp_sampler, screenUV, 0).r;

				// If the current pixel is sky
				bool isBackground = depth == UNITY_RAW_FAR_CLIP_VALUE ? true : false;

				// Don't clear the render target, we use them to fill in the border gaps when rendering low resolution GI.
				if (isBackground)
					discard;

			#if !UNITY_REVERSED_Z
				depth = lerp(UNITY_NEAR_CLIP_VALUE, 1, depth);
			#endif

				// Start denoising before tracing SSGI
				// If the history sample for a pixel will be invalid, we increase the number of samples and reduce ray marching quality.

				float3 positionWS = ComputeWorldSpacePosition(screenUV, depth, UNITY_MATRIX_I_VP);
				float3 cameraPositionWS = GetCameraPositionWS();
				half3 viewDirectionWS = normalize(cameraPositionWS - positionWS);

				half2 velocity = SAMPLE_TEXTURE2D_X_LOD(_MotionVectorTexture, my_linear_clamp_sampler, screenUV, 0).xy;
				float2 prevUV = screenUV - velocity;

				half4 normalSmoothness = SAMPLE_TEXTURE2D_X_LOD(_GBuffer2, my_point_clamp_sampler, screenUV, 0).xyzw;

			#if defined(_GBUFFER_NORMALS_OCT)
				half2 remappedOctNormalWS = half2(Unpack888ToFloat2(normalSmoothness.xyz));                // values between [ 0, +1]
				half2 octNormalWS = remappedOctNormalWS.xy * half(2.0) - half(1.0);                        // values between [-1, +1]
				normalSmoothness.xyz = half3(UnpackNormalOctQuadEncode(octNormalWS));                      // values between [-1, +1]
			#endif 

				half maxRadius = ComputeMaxReprojectionWorldRadius(positionWS, viewDirectionWS, normalSmoothness.xyz, _PixelSpreadAngleTangent);
				float prevDeviceDepth = SAMPLE_TEXTURE2D_X_LOD(_SSGIHistoryDepthTexture, my_point_clamp_sampler, prevUV, 0).r;

			#if !UNITY_REVERSED_Z
				prevDeviceDepth = lerp(UNITY_NEAR_CLIP_VALUE, 1, prevDeviceDepth);
			#endif

				float3 prevPositionWS = ComputeWorldSpacePosition(prevUV, prevDeviceDepth, _PrevInvViewProjMatrix);
				half radius = length(prevPositionWS - positionWS) / maxRadius;

				bool canBeReprojected = (prevUV.x <= 1.0 && prevUV.x >= 0.0 && prevUV.y <= 1.0 && prevUV.y >= 0.0 && radius <= 1.0 && _HistoryTextureValid);

				Ray ray;
				ray.position = cameraPositionWS;
				ray.direction = -viewDirectionWS; // viewDirectionWS points to the camera.

				// Calculate screenHit data only once
				RayHit screenHit = InitializeRayHit();
				screenHit.distance = length(cameraPositionWS - positionWS);
				screenHit.position = positionWS;
				screenHit.normal = normalSmoothness.xyz;

				// If reprojection fails, we increase the number of samples and reduce ray marching quality.
				if (!canBeReprojected && _TemporalIntensity != 0.0)
				{
					MAX_STEP = 8;
					MAX_SMALL_STEP = 0;
					MAX_MEDIUM_STEP = 3;
					STEP_SIZE = 0.6;
					MEDIUM_STEP_SIZE = 0.1;
					RAY_COUNT = max(4, RAY_COUNT);
				}

				half dither = (GenerateRandomValue(screenUV) * 0.3 - 0.15);

				half sampleWeight = rcp(RAY_COUNT);

				for (int i = 0; i < RAY_COUNT; i++)
				{
					RayHit rayHit = screenHit;

					// Generate a new sample direction
					ray.direction = SampleHemisphereCosine(GenerateRandomValue(screenUV), GenerateRandomValue(screenUV), rayHit.normal);
					ray.position = rayHit.position;

					// Find the intersection of the ray with scene geometries
					rayHit = RayMarching(ray, screenUV, dither, viewDirectionWS);

					bool hitSuccessful = rayHit.distance > REAL_EPS;

					UNITY_BRANCH
					if (hitSuccessful)
					{
						lightingDistance.rgb += rayHit.emission * sampleWeight;
						lightingDistance.a += rayHit.distance * sampleWeight;
					}
					else
					{
						lightingDistance.rgb += SampleReflectionProbes(ray.direction, positionWS, 1.0h, screenUV) * sampleWeight;
						lightingDistance.a += sampleWeight; // 1.0 * sampleWeight
					}
				}

				// Reduce noise and fireflies by limiting the maximum brightness
				lightingDistance.xyz = RgbToHsv(lightingDistance.xyz);
				lightingDistance.z = clamp(lightingDistance.z, 0.0, _MaxBrightness);
				lightingDistance.xyz = HsvToRgb(lightingDistance.xyz);

				// Set it to negative to pass "canBeReprojected" to the denoising pass
				lightingDistance.w = canBeReprojected ? lightingDistance.w : -lightingDistance.w;

				return lightingDistance;
			}
            ENDHLSL
        }
		
		Pass
		{
			Name "Temporal Reprojection"
			Tags { "LightMode" = "Screen Space Global Illumination" }

			Blend One Zero

			HLSLPROGRAM
			#include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"
			// The Blit.hlsl file provides the vertex shader (Vert),
			// input structure (Attributes) and output structure (Varyings)
			#include "Packages/com.unity.render-pipelines.core/Runtime/Utilities/Blit.hlsl"

			#pragma vertex Vert
			#pragma fragment frag

			#pragma target 3.5

			#pragma multi_compile_fragment _ _GBUFFER_NORMALS_OCT

			#include "./SSGI.hlsl"

			void frag(Varyings input, out half4 denoiseOutput : SV_Target0, out half currentSample : SV_Target1)
			{
				UNITY_SETUP_STEREO_EYE_INDEX_POST_VERTEX(input);
				float2 screenUV = input.texcoord;

				half2 velocity = SAMPLE_TEXTURE2D_X_LOD(_MotionVectorTexture, sampler_LinearClamp, screenUV, 0).xy;

				float2 prevUV = screenUV - velocity;

				float deviceDepth = SAMPLE_TEXTURE2D_X_LOD(_CameraDepthTexture, my_point_clamp_sampler, screenUV, 0).r;

				// Fetch the current and history values and apply the exposition to it.
				half4 currentColor = SAMPLE_TEXTURE2D_X_LOD(_BlitTexture, sampler_LinearClamp, screenUV, 0).rgba;

				half historySample = SAMPLE_TEXTURE2D_X_LOD(_SSGIHistorySampleTexture, my_point_clamp_sampler, prevUV, 0).r;

				// Extract the "canBeReprojected" variable.
				bool canBeReprojected = FastSign(currentColor.a) == 1.0;
				currentColor.a = abs(currentColor.a);

				bool isSky = deviceDepth == UNITY_RAW_FAR_CLIP_VALUE? true : false;
				canBeReprojected = isSky || historySample == 0.0 ? false : canBeReprojected;

				// Re-projected color from last frame.
				half3 prevColor = SAMPLE_TEXTURE2D_X_LOD(_HistoryIndirectDiffuseTexture, sampler_LinearClamp, prevUV, 0).rgb;

				half accumulationFactor = (historySample >= MAX_ACCUM_FRAME_NUM ? _TemporalIntensity : (historySample / (historySample + 1.0)));

				half sampleCount = clamp(historySample + 1.0, 0.0, MAX_ACCUM_FRAME_NUM);

				half3 result;

				UNITY_BRANCH
				if (canBeReprojected)
				{
					result = (currentColor.rgb * (1.0 - accumulationFactor) + prevColor.rgb * accumulationFactor);
				}
				else if (_AggressiveDenoise)
				{
					// Performance cost here can be reduced by removing less important operations.
					
					// Color Variance
					half3 boxMax = currentColor.rgb;
					half3 boxMin = currentColor.rgb;
					half3 moment1 = currentColor.rgb;
					half3 moment2 = currentColor.rgb * currentColor.rgb;

					// adjacent pixels
					AdjustColorBox(boxMin, boxMax, moment1, moment2, screenUV, 0.0, -1.0);
					AdjustColorBox(boxMin, boxMax, moment1, moment2, screenUV, -1.0, 0.0);
					AdjustColorBox(boxMin, boxMax, moment1, moment2, screenUV, 1.0, 0.0);
					AdjustColorBox(boxMin, boxMax, moment1, moment2, screenUV, 0.0, 1.0);

					/*
					// remaining pixels in a 9x9 square (excluding center)
					AdjustColorBox(boxMin, boxMax, moment1, moment2, screenUV, -1.0, -1.0);
					AdjustColorBox(boxMin, boxMax, moment1, moment2, screenUV, 1.0, -1.0);
					AdjustColorBox(boxMin, boxMax, moment1, moment2, screenUV, -1.0, 1.0);
					AdjustColorBox(boxMin, boxMax, moment1, moment2, screenUV, 1.0, 1.0);
					*/

					// Can be replace by clamp() to reduce performance cost.
					//prevColor = DirectClipToAABB(prevColor, boxMin, boxMax);
					prevColor = clamp(prevColor, boxMin, boxMax);

					// We still try to reuse (clamped) history samples even if they are invalid
					result = (currentColor.rgb * (1.0 - accumulationFactor) + prevColor.rgb * accumulationFactor);
				}
				else
				{
					result = currentColor.rgb;
					sampleCount = 1.0;
				}

				denoiseOutput = half4(result, currentColor.a);
				//denoiseOutput = half4(historySample.xxx * rcp(MAX_ACCUM_FRAME_NUM) - rcp(MAX_ACCUM_FRAME_NUM), currentColor.a); // debug sample count
				currentSample = sampleCount;
			}
			ENDHLSL
		}
		
        Pass
		{
			Name "Edge-Avoiding Spatial Denoise"
			Tags { "LightMode" = "Screen Space Global Illumination" }

			Blend One Zero

			HLSLPROGRAM
			#include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"
			// The Blit.hlsl file provides the vertex shader (Vert),
			// input structure (Attributes) and output structure (Varyings)
			#include "Packages/com.unity.render-pipelines.core/Runtime/Utilities/Blit.hlsl"
			#include "Packages/com.unity.render-pipelines.core/ShaderLibrary/CommonLighting.hlsl"

			#include "./SSGI.hlsl"

			#pragma vertex Vert
			#pragma fragment frag

			#pragma target 3.5

			#pragma multi_compile_fragment _ _GBUFFER_NORMALS_OCT

			half4 frag(Varyings input) : SV_Target
			{
				// Edge-Avoiding A-TrousWavelet Transform for denoising
				// Modified from "https://www.shadertoy.com/view/ldKBzG"
				// feel free to use it

				UNITY_SETUP_STEREO_EYE_INDEX_POST_VERTEX(input);
				float2 screenUV = input.texcoord;

				float centerDepth = SAMPLE_TEXTURE2D_X_LOD(_CameraDepthTexture, my_point_clamp_sampler, screenUV, 0).r;

				// If the current pixel is sky
				bool isBackground = centerDepth == UNITY_RAW_FAR_CLIP_VALUE ? true : false;

				if (isBackground)
					discard;

			#if !UNITY_REVERSED_Z
				centerDepth = lerp(UNITY_NEAR_CLIP_VALUE, 1, centerDepth);
            #endif

				centerDepth = LinearEyeDepth(centerDepth, _ZBufferParams);

				half4 colorDistance = SAMPLE_TEXTURE2D_X_LOD(_BlitTexture, my_point_clamp_sampler, screenUV, 0).rgba;
				half3 centerColor = colorDistance.rgb;
				half hitDistance = colorDistance.a;

				// Dynamic dilation rate
				// This reduces repetitive artifacts of A-Trous filtering.

				// Reduce blur intensity if the hit distance is small
				half blurAmount = hitDistance < 1.0 ? 0.05 : 1.0;
				
				half minRange = max(2.0 * _DownSample, 2.0);
				half maxRange = max(5.0 * _DownSample, minRange + 4.0);

				half random = GenerateHashedRandomFloat(uint3(screenUV * _BlitTexture_TexelSize.zw, 1));
				float2 intensity = floor(lerp(minRange, maxRange, random)) * _BlitTexture_TexelSize.xy;
				
				// 3x3 gaussian kernel texel offset, excluding center
				const half2 offset[8] =
				{
					half2(-1.0, -1.0), half2(0.0, -1.0), half2(1.0, -1.0),  // offset[0]..[2]
					half2(-1.0, 0.0), /*half2(0.0, 0.0),*/ half2(1.0, 0.0), // offset[3]..[5], excluding center
					half2(-1.0, 1.0), half2(0.0, 1.0), half2(1.0, 1.0)      // offset[6]..[8]
				};

				// 3x3 approximate gaussian kernel, excluding center
				const half kernel[8] =
				{
					half(0.0625), half(0.125), half(0.0625),  // kernel[0]..[2]
					half(0.125), /*half(0.25),*/ half(0.125), // kernel[3]..[5], excluding center
					half(0.0625), half(0.125), half(0.0625)   // kernel[6]..[8]
				};

				half3 centerNormal = SAMPLE_TEXTURE2D_X_LOD(_GBuffer2, my_point_clamp_sampler, screenUV, 0).rgb;

			#if defined(_GBUFFER_NORMALS_OCT)
				half2 remappedOctNormalWS = half2(Unpack888ToFloat2(centerNormal));                // values between [ 0, +1]
				half2 octNormalWS = remappedOctNormalWS.xy * half(2.0) - half(1.0);                // values between [-1, +1]
				centerNormal = half3(UnpackNormalOctQuadEncode(octNormalWS));                      // values between [-1, +1]
			#endif

				// Add the center weight
				half sumWeight = 0.25;
				half3 sumColor = centerColor * sumWeight;
				
				// 3x3, excluding center
				for (uint i = 0; i < 8; i++)
				{
					float2 uv = saturate(screenUV + offset[i] * intensity);

					half3 color = SAMPLE_TEXTURE2D_X_LOD(_BlitTexture, my_point_clamp_sampler, uv, 0).rgb;
					half3 normal = SAMPLE_TEXTURE2D_X_LOD(_GBuffer2, my_point_clamp_sampler, uv, 0).rgb;

				#if defined(_GBUFFER_NORMALS_OCT)
					half2 remappedOctNormalWS = half2(Unpack888ToFloat2(normal));                // values between [ 0, +1]
					half2 octNormalWS = remappedOctNormalWS.xy * half(2.0) - half(1.0);          // values between [-1, +1]
					normal = half3(UnpackNormalOctQuadEncode(octNormalWS));                      // values between [-1, +1]
				#endif

					half3 diff = centerNormal - normal;
					half distance = max(dot(diff, diff), 0.0);
					half normalWeight = min(exp(-distance * 20.0), 1.0);

					float depth = SAMPLE_TEXTURE2D_X_LOD(_CameraDepthTexture, my_point_clamp_sampler, uv, 0).r;

				#if !UNITY_REVERSED_Z
					depth = lerp(UNITY_NEAR_CLIP_VALUE, 1, depth);
				#endif

					depth = LinearEyeDepth(depth, _ZBufferParams);

					diff.x = centerDepth - depth;
					distance = dot(diff.x, diff.x);
					half depthWeight = min(exp(-distance), 1.0);

					half weight = normalWeight * depthWeight * kernel[i];

					sumColor += color * weight;
					sumWeight += weight;
				}

				return half4(lerp(centerColor, sumColor * rcp(sumWeight), blurAmount), hitDistance);
			}
			ENDHLSL
		}
		
		Pass
		{
			Name "Temporal Stabilization"
			Tags { "LightMode" = "Screen Space Global Illumination" }

			Blend One Zero

			HLSLPROGRAM
			#include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"
			// The Blit.hlsl file provides the vertex shader (Vert),
			// input structure (Attributes) and output structure (Varyings)
			#include "Packages/com.unity.render-pipelines.core/Runtime/Utilities/Blit.hlsl"
			#include "Packages/com.unity.render-pipelines.core/ShaderLibrary/CommonLighting.hlsl"

			#include "./SSGI.hlsl"

			#pragma vertex Vert
			#pragma fragment frag

			#pragma target 3.5

			half4 frag(Varyings input) : SV_Target
			{
				UNITY_SETUP_STEREO_EYE_INDEX_POST_VERTEX(input);
				float2 screenUV = input.texcoord;

				half4 indirectDiffuse = SAMPLE_TEXTURE2D_X_LOD(_BlitTexture, my_point_clamp_sampler, screenUV, 0).xyzw;
				half3 colorCenter = indirectDiffuse.xyz;

				// Unity motion vectors are forward motion vectors in screen UV space
				half2 velocity = SAMPLE_TEXTURE2D_X_LOD(_MotionVectorTexture, sampler_LinearClamp, screenUV, 0).xy;
				float2 prevUV = screenUV - velocity;

				float deviceDepth = SAMPLE_TEXTURE2D_X_LOD(_CameraDepthTexture, my_point_clamp_sampler, screenUV, 0).r;

				bool isSky = deviceDepth == UNITY_RAW_FAR_CLIP_VALUE ? true : false;;

				if (isSky || prevUV.x > 1.0 || prevUV.x < 0.0 || prevUV.y > 1.0 || prevUV.y < 0.0)
					discard;

			#if !UNITY_REVERSED_Z
				deviceDepth = lerp(UNITY_NEAR_CLIP_VALUE, 1, deviceDepth);
			#endif
				
				// Performance cost here can be reduced by removing less important operations.

				// Color Variance
				half3 boxMax = colorCenter;
				half3 boxMin = colorCenter;
				half3 moment1 = colorCenter;
				half3 moment2 = colorCenter * colorCenter;

				// adjacent pixels
				AdjustColorBox(boxMin, boxMax, moment1, moment2, screenUV, 0.0, -1.0);
				AdjustColorBox(boxMin, boxMax, moment1, moment2, screenUV, -1.0, 0.0);
				AdjustColorBox(boxMin, boxMax, moment1, moment2, screenUV, 1.0, 0.0);
				AdjustColorBox(boxMin, boxMax, moment1, moment2, screenUV, 0.0, 1.0);

				/*
				// remaining pixels in a 9x9 square (excluding center)
				AdjustColorBox(boxMin, boxMax, moment1, moment2, screenUV, -1.0, -1.0);
				AdjustColorBox(boxMin, boxMax, moment1, moment2, screenUV, 1.0, -1.0);
				AdjustColorBox(boxMin, boxMax, moment1, moment2, screenUV, -1.0, 1.0);
				AdjustColorBox(boxMin, boxMax, moment1, moment2, screenUV, 1.0, 1.0);
				*/

				// Re-projected color from last frame.
				half3 prevColor = SAMPLE_TEXTURE2D_X_LOD(_HistoryIndirectDiffuseTexture, sampler_LinearClamp, prevUV, 0).rgb;

				// Can be replace by clamp() to reduce performance cost.
				//prevColor = DirectClipToAABB(prevColor, boxMin, boxMax);
				prevColor = clamp(prevColor, boxMin, boxMax);
					
				half intensity = saturate(min(_TemporalIntensity - (abs(velocity.x)) * _TemporalIntensity, _TemporalIntensity - (abs(velocity.y)) * _TemporalIntensity));

				half3 finalColor = lerp(colorCenter, prevColor, intensity);

				return half4(finalColor, indirectDiffuse.w);
			}
			ENDHLSL
		}
		
        Pass
        {
            Name "Copy History Depth"
			Tags { "LightMode" = "Screen Space Global Illumination" }

            Blend One Zero
			//ZWrite On
			
			HLSLPROGRAM
			#include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"
			// The Blit.hlsl file provides the vertex shader (Vert),
			// input structure (Attributes) and output structure (Varyings)
			#include "Packages/com.unity.render-pipelines.core/Runtime/Utilities/Blit.hlsl"
			
			#pragma vertex Vert
			#pragma fragment frag

            #pragma target 3.5

            // URP pre-defined the following variable on 2023.2+.
        #if UNITY_VERSION < 202320
            float4 _BlitTexture_TexelSize;
        #endif

            TEXTURE2D_X(_CameraDepthTexture);
			SAMPLER(my_point_clamp_sampler);
            
			//float frag(Varyings input, out float outDepth : SV_Depth) : SV_Target
            float frag(Varyings input) : SV_Target
            {
                UNITY_SETUP_STEREO_EYE_INDEX_POST_VERTEX(input);
                float2 screenUV = input.texcoord;

                float depth = SAMPLE_TEXTURE2D_X_LOD(_CameraDepthTexture, my_point_clamp_sampler, screenUV, 0).r;
				//float depth = LOAD_TEXTURE2D_X(_CameraDepthTexture, uint2(screenUV * _ScreenSize.xy)).r; // This should be a bit faster
				
				//outDepth = depth;

                return depth;
            }
            ENDHLSL
        }

		Pass
		{
			Name "Combine GI"
			Tags { "LightMode" = "Screen Space Global Illumination" }

			Blend One Zero

			HLSLPROGRAM
			#include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"
			// The Blit.hlsl file provides the vertex shader (Vert),
			// input structure (Attributes) and output structure (Varyings)
			#include "Packages/com.unity.render-pipelines.core/Runtime/Utilities/Blit.hlsl"
			
			#pragma vertex Vert
			#pragma fragment frag

			#pragma target 3.5

			#pragma multi_compile_fragment _ _GBUFFER_NORMALS_OCT

			#pragma multi_compile_local_fragment _ _USE_RENDERING_LAYERS
		    #pragma multi_compile_local_fragment _ _DEPTH_NORMALS_UPSCALE

		#if defined(_USE_RENDERING_LAYERS)
			#include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/DeclareRenderingLayerTexture.hlsl"
		#endif

			#include "./SSGI.hlsl"

			// Nearest-depth upscaling
			// Refer to "https://developer.download.nvidia.com/assets/gamedev/files/sdk/11/OpacityMappingSDKWhitePaper.pdf".
            
			half3 DepthNormalsUpscale(float2 screenUV, float deviceDepth)
			{
				float2 offsetUV = screenUV;
				offsetUV.y -= _IndirectDiffuseTexture_TexelSize.y;

				half3 centerNormal = SAMPLE_TEXTURE2D_X_LOD(_GBuffer2, my_point_clamp_sampler, screenUV, 0).xyz;
				float centerDepth = LinearEyeDepth(deviceDepth, _ZBufferParams);

				half3 resultColor = half3(0.0, 0.0, 0.0);

				float2 uv0 = offsetUV + float2(0.0, _IndirectDiffuseTexture_TexelSize.y);
				float2 uv1 = offsetUV + _IndirectDiffuseTexture_TexelSize.xy;
				float2 uv2 = offsetUV + float2(_IndirectDiffuseTexture_TexelSize.x, 0.0);
				float2 uv3 = offsetUV + float2(0.0, 0.0);

				// We can use a gather here but that requires shader model 5.0
				float4 neighborDepth = float4(
					SAMPLE_TEXTURE2D_X_LOD(_CameraDepthTexture, my_point_clamp_sampler, uv0, 0).x,
					SAMPLE_TEXTURE2D_X_LOD(_CameraDepthTexture, my_point_clamp_sampler, uv1, 0).x,
					SAMPLE_TEXTURE2D_X_LOD(_CameraDepthTexture, my_point_clamp_sampler, uv2, 0).x,
					SAMPLE_TEXTURE2D_X_LOD(_CameraDepthTexture, my_point_clamp_sampler, uv3, 0).x);

			#if !UNITY_REVERSED_Z
				neighborDepth = lerp(UNITY_NEAR_CLIP_VALUE.xxxx, float4(1.0, 1.0, 1.0, 1.0), neighborDepth);
            #endif

				neighborDepth = float4(
					LinearEyeDepth(neighborDepth.x, _ZBufferParams),
					LinearEyeDepth(neighborDepth.y, _ZBufferParams),
					LinearEyeDepth(neighborDepth.z, _ZBufferParams),
					LinearEyeDepth(neighborDepth.w, _ZBufferParams));

				half3 normal0 = SAMPLE_TEXTURE2D_X_LOD(_GBuffer2, my_point_clamp_sampler, uv0, 0).xyz;
				half3 normal1 = SAMPLE_TEXTURE2D_X_LOD(_GBuffer2, my_point_clamp_sampler, uv1, 0).xyz;
				half3 normal2 = SAMPLE_TEXTURE2D_X_LOD(_GBuffer2, my_point_clamp_sampler, uv2, 0).xyz;
				half3 normal3 = SAMPLE_TEXTURE2D_X_LOD(_GBuffer2, my_point_clamp_sampler, uv3, 0).xyz;

			#if defined(_GBUFFER_NORMALS_OCT)
				half2 remappedOctNormalWS = half2(Unpack888ToFloat2(centerNormal));           // values between [ 0, +1]
				half2 octNormalWS = remappedOctNormalWS.xy * half(2.0) - half(1.0);           // values between [-1, +1]
				centerNormal = half3(UnpackNormalOctQuadEncode(octNormalWS));                 // values between [-1, +1]

				remappedOctNormalWS = half2(Unpack888ToFloat2(normal0));                      // values between [ 0, +1]
				octNormalWS = remappedOctNormalWS.xy * half(2.0) - half(1.0);                 // values between [-1, +1]
				normal0 = half3(UnpackNormalOctQuadEncode(octNormalWS));                      // values between [-1, +1]

				remappedOctNormalWS = half2(Unpack888ToFloat2(normal1));                      // values between [ 0, +1]
				octNormalWS = remappedOctNormalWS.xy * half(2.0) - half(1.0);                 // values between [-1, +1]
				normal1 = half3(UnpackNormalOctQuadEncode(octNormalWS));                      // values between [-1, +1]

				remappedOctNormalWS = half2(Unpack888ToFloat2(normal2));                      // values between [ 0, +1]
				octNormalWS = remappedOctNormalWS.xy * half(2.0) - half(1.0);                 // values between [-1, +1]
				normal2 = half3(UnpackNormalOctQuadEncode(octNormalWS));                      // values between [-1, +1]

				remappedOctNormalWS = half2(Unpack888ToFloat2(normal3));                      // values between [ 0, +1]
				octNormalWS = remappedOctNormalWS.xy * half(2.0) - half(1.0);                 // values between [-1, +1]
				normal3 = half3(UnpackNormalOctQuadEncode(octNormalWS));                      // values between [-1, +1]
			#endif

				half4 distances;
				distances.x = distance(neighborDepth.x, centerDepth);
				distances.y = distance(neighborDepth.y, centerDepth);
				distances.z = distance(neighborDepth.z, centerDepth);
				distances.w = distance(neighborDepth.w, centerDepth);

				distances.x *= (1 - saturate(dot(normal0, centerNormal)));
				distances.y *= (1 - saturate(dot(normal1, centerNormal)));
				distances.z *= (1 - saturate(dot(normal2, centerNormal)));
				distances.w *= (1 - saturate(dot(normal3, centerNormal)));

				half bestDistance = min(min(min(distances.x, distances.y), distances.z), distances.w);

				float2 bestUV = bestDistance == distances.x ? uv0 : bestDistance == distances.y ? uv1 : bestDistance == distances.z ? uv2 : uv3;

				resultColor = SAMPLE_TEXTURE2D_X_LOD(_IndirectDiffuseTexture, my_linear_clamp_sampler, bestUV, 0).xyz;

				return resultColor;
			}
            
			half3 DepthUpscale(float2 screenUV, float deviceDepth)
			{
				float2 offsetUV = screenUV;
				offsetUV.y -= _IndirectDiffuseTexture_TexelSize.y;

				half3 centerNormal = SAMPLE_TEXTURE2D_X_LOD(_GBuffer2, my_point_clamp_sampler, screenUV, 0).xyz;
				float centerDepth = Linear01Depth(deviceDepth, _ZBufferParams);

				half3 resultColor = half3(0.0, 0.0, 0.0);

				float2 uv0 = offsetUV + float2(0.0, _IndirectDiffuseTexture_TexelSize.y);
				float2 uv1 = offsetUV + _IndirectDiffuseTexture_TexelSize.xy;
				float2 uv2 = offsetUV + float2(_IndirectDiffuseTexture_TexelSize.x, 0.0);
				float2 uv3 = offsetUV + float2(0.0, 0.0);

				// We can use a gather here but that requires shader model 5.0
				float4 neighborDepth = float4(
					SAMPLE_TEXTURE2D_X_LOD(_CameraDepthTexture, my_point_clamp_sampler, uv0, 0).x,
					SAMPLE_TEXTURE2D_X_LOD(_CameraDepthTexture, my_point_clamp_sampler, uv1, 0).x,
					SAMPLE_TEXTURE2D_X_LOD(_CameraDepthTexture, my_point_clamp_sampler, uv2, 0).x,
					SAMPLE_TEXTURE2D_X_LOD(_CameraDepthTexture, my_point_clamp_sampler, uv3, 0).x);

			#if !UNITY_REVERSED_Z
				neighborDepth = lerp(UNITY_NEAR_CLIP_VALUE.xxxx, float4(1.0, 1.0, 1.0, 1.0), neighborDepth);
            #endif

				neighborDepth = float4(
					Linear01Depth(neighborDepth.x, _ZBufferParams),
					Linear01Depth(neighborDepth.y, _ZBufferParams),
					Linear01Depth(neighborDepth.z, _ZBufferParams),
					Linear01Depth(neighborDepth.w, _ZBufferParams));

				half4 distances;
				distances.x = abs(neighborDepth.x - centerDepth);
				distances.y = abs(neighborDepth.y - centerDepth);
				distances.z = abs(neighborDepth.z - centerDepth);
				distances.w = abs(neighborDepth.w - centerDepth);

				half bestDistance = min(min(min(distances.x, distances.y), distances.z), distances.w);

				float2 bestUV = bestDistance == distances.x ? uv0 : bestDistance == distances.y ? uv1 : bestDistance == distances.z ? uv2 : uv3;

				const half depthThreshold = 0.01;

				if (distances.x < depthThreshold && distances.y < depthThreshold && distances.z < depthThreshold && distances.w < depthThreshold)
				    resultColor = SAMPLE_TEXTURE2D_X_LOD(_IndirectDiffuseTexture, my_linear_clamp_sampler, bestUV, 0).xyz;
				else
					resultColor = SAMPLE_TEXTURE2D_X_LOD(_IndirectDiffuseTexture, my_point_clamp_sampler, screenUV, 0).xyz;

				return resultColor;
			}

			half4 frag(Varyings input) : SV_Target
			{
				UNITY_SETUP_STEREO_EYE_INDEX_POST_VERTEX(input);
				float2 screenUV = input.texcoord;

				float depth = SAMPLE_TEXTURE2D_X_LOD(_CameraDepthTexture, my_point_clamp_sampler, screenUV, 0).r;

		    #if !UNITY_REVERSED_Z
				depth = lerp(UNITY_NEAR_CLIP_VALUE, 1, depth);
            #endif

				// If the current pixel is sky
				bool isBackground = depth == UNITY_RAW_FAR_CLIP_VALUE ? true : false;

				if (isBackground)
					discard;

			#if defined(_USE_RENDERING_LAYERS)
				uint meshRenderingLayers = SampleSceneRenderingLayer(screenUV);
				if(!IsMatchingLightLayer(_IndirectDiffuseRenderingLayers, meshRenderingLayers))
					discard;
			#endif

				half4 gbuffer0 = SAMPLE_TEXTURE2D_X_LOD(_GBuffer0, my_point_clamp_sampler, screenUV, 0);
				half4 gbuffer1 = SAMPLE_TEXTURE2D_X_LOD(_GBuffer1, my_point_clamp_sampler, screenUV, 0);

				half3 albedo = gbuffer0.rgb;
				half metallic = (gbuffer0.a == kMaterialFlagSpecularSetup) ? MetallicFromReflectivity(ReflectivitySpecular(gbuffer1.rgb)) : gbuffer1.r;

				half3 indirectLighting;

				UNITY_BRANCH
				if (_DownSample == 1.0)
					indirectLighting = SAMPLE_TEXTURE2D_X_LOD(_IndirectDiffuseTexture, my_point_clamp_sampler, screenUV, 0).rgb;
				else
				#ifdef _DEPTH_NORMALS_UPSCALE
					indirectLighting = DepthNormalsUpscale(screenUV, depth);
                #else
				    indirectLighting = DepthUpscale(screenUV, depth);
                #endif

				half4 directLighting = SAMPLE_TEXTURE2D_X_LOD(_BlitTexture, my_point_clamp_sampler, screenUV, 0).rgba;

				indirectLighting *= albedo * (1.0 - metallic);

				// Apply the indirect lighting multiplier
				indirectLighting *= _IndirectDiffuseLightingMultiplier;

				return half4(directLighting.rgb + indirectLighting, directLighting.a);
			}
			ENDHLSL
		}

		Pass
		{
			Name "Scene View Camera Motion Vectors"
			Tags { "LightMode" = "Screen Space Global Illumination" }

			Blend One Zero
			ZTest Always
			ZWrite Off

			HLSLPROGRAM
			#include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"
			// The Blit.hlsl file provides the vertex shader (Vert),
			// input structure (Attributes) and output structure (Varyings)
			#include "Packages/com.unity.render-pipelines.core/Runtime/Utilities/Blit.hlsl"

			#pragma vertex Vert
			#pragma fragment frag

			#pragma target 3.5

			#include "./SSGI.hlsl"

			half4 frag(Varyings input) : SV_Target
			{
				UNITY_SETUP_STEREO_EYE_INDEX_POST_VERTEX(input);
				float2 screenUV = input.texcoord;

				float depth = SAMPLE_TEXTURE2D_X_LOD(_CameraDepthTexture, my_point_clamp_sampler, screenUV, 0).r;
                
            #if !UNITY_REVERSED_Z
                depth = lerp(UNITY_NEAR_CLIP_VALUE, 1, depth);
            #endif
                
                // Reconstruct world position
                float3 posWS = ComputeWorldSpacePosition(screenUV, depth, UNITY_MATRIX_I_VP);

                // Multiply with current and previous non-jittered view projection
                float4 posCS = mul(_NonJitteredViewProjMatrix, float4(posWS.xyz, 1.0));
                float4 prevPosCS = mul(_PrevViewProjMatrix, float4(posWS.xyz, 1.0));

                // Non-uniform raster needs to keep the posNDC values in float to avoid additional conversions
                // since uv remap functions use floats
                float2 posNDC = posCS.xy * rcp(posCS.w);
                float2 prevPosNDC = prevPosCS.xy * rcp(prevPosCS.w);
				
                // Calculate forward velocity
				half2 velocity = (posNDC - prevPosNDC);
				
                // TODO: test that velocity.y is correct
            #if UNITY_UV_STARTS_AT_TOP
                velocity.y = -velocity.y;
            #endif
				
                // Convert velocity from NDC space (-1..1) to screen UV 0..1 space
                // Note: It doesn't mean we don't have negative values, we store negative or positive offset in the UV space.
                // Note: ((posNDC * 0.5 + 0.5) - (prevPosNDC * 0.5 + 0.5)) = (velocity * 0.5)
                velocity.xy *= 0.5;
				
                return float4(velocity, 0, 0);
			}
			ENDHLSL
		}

		Pass
		{
		    // Modified from HDRP's ReBLUR denoiser
			Name "Poisson Disk Recurrent Denoise"
			Tags { "LightMode" = "Screen Space Global Illumination" }

			Blend One Zero

			HLSLPROGRAM
			#include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"
			// The Blit.hlsl file provides the vertex shader (Vert),
			// input structure (Attributes) and output structure (Varyings)
			#include "Packages/com.unity.render-pipelines.core/Runtime/Utilities/Blit.hlsl"
			#include "Packages/com.unity.render-pipelines.core/ShaderLibrary/CommonLighting.hlsl"

			#include "./SSGI.hlsl"

			#pragma vertex Vert
			#pragma fragment frag

			#pragma target 3.5

			#pragma multi_compile_fragment _ _GBUFFER_NORMALS_OCT

			half4 frag(Varyings input) : SV_Target
			{
				UNITY_SETUP_STEREO_EYE_INDEX_POST_VERTEX(input);
				float2 screenUV = input.texcoord;

				float centerDepth = SAMPLE_TEXTURE2D_X_LOD(_CameraDepthTexture, my_point_clamp_sampler, screenUV, 0).r;

			#if !UNITY_REVERSED_Z
				centerDepth = lerp(UNITY_NEAR_CLIP_VALUE, 1, centerDepth);
			#endif

				// If the current pixel is sky
				bool isBackground = centerDepth == UNITY_RAW_FAR_CLIP_VALUE ? true : false;

				if (isBackground)
					discard;

				float3 positionWS = ComputeWorldSpacePosition(screenUV, centerDepth, UNITY_MATRIX_I_VP);
				centerDepth = Linear01Depth(centerDepth, _ZBufferParams);

				// Center Signal
				half4 centerSignal = SAMPLE_TEXTURE2D_X_LOD(_BlitTexture, my_point_clamp_sampler, screenUV, 0);

				// Number of accumulated frames
				//half accumulationFactor = SAMPLE_TEXTURE2D_X_LOD(_SSGISampleTexture, my_point_clamp_sampler, screenUV, 0).r;

				// Evaluate the position and view vectors
				float3 cameraPositionWS = GetCameraPositionWS();
				half3 viewDirectionWS = normalize(cameraPositionWS - positionWS);

				half3 centerNormal = SAMPLE_TEXTURE2D_X_LOD(_GBuffer2, my_point_clamp_sampler, screenUV, 0).xyz;

			#if defined(_GBUFFER_NORMALS_OCT)
				half2 remappedOctNormalWS = half2(Unpack888ToFloat2(centerNormal.xyz));                    // values between [ 0, +1]
				half2 octNormalWS = remappedOctNormalWS.xy * half(2.0) - half(1.0);                        // values between [-1, +1]
				centerNormal.xyz = half3(UnpackNormalOctQuadEncode(octNormalWS));                          // values between [-1, +1]
			#endif

				// Convert both directions to view space
				//half NdotV = abs(dot(centerNormal, viewDirectionWS));

				// Get the dominant direction
				half4 dominantWS = GetSpecularDominantDirection(centerNormal, viewDirectionWS);

				// Evaluate the blur radius
				//float distanceToCamera = length(positionWS - cameraPositionWS);
				//half blurRadius = ComputeBlurRadius(1.0, BLUR_MAX_RADIUS) * _ReBlurDenoiserRadius;
				half blurRadius = _ReBlurDenoiserRadius; // * BLUR_MAX_RADIUS;
				//blurRadius *= max(1.0 - saturate(accumulationFactor / MAX_ACCUM_FRAME_NUM), 1.0);
				//blurRadius *= HitDistanceAttenuation(centerRoughness, distanceToCamera, centerSignal.w);
				//blurRadius *= lerp(saturate((distanceToCamera - MIN_BLUR_DISTANCE) / BLUR_OUT_RANGE), 0.0, 1.0);

				// Evalute the local basis
				half2x3 TvBv = GetKernelBasis(dominantWS.xyz, centerNormal);
				TvBv[0] *= blurRadius;
				TvBv[1] *= blurRadius;

				// Loop through the samples
				float4 signalSum = 0.0; // requires full precision float
				float sumWeight = 0.0;
				for (int sampleIndex = 0; sampleIndex < POISSON_SAMPLE_COUNT; ++sampleIndex)
				{
					// Pick the next sample value
					half3 offset = k_PoissonDiskSamples[sampleIndex];

					// Evaluate the tap uv
					float2 uv = GetKernelSampleCoordinates(offset, positionWS, TvBv[0], TvBv[1], _ReBlurBlurRotator);

					// Is the target pixel on the screen?
					bool isInScreen = uv.x <= 1.0 && uv.x >= 0.0 && uv.y <= 1.0 && uv.y >= 0.0;
					if (!isInScreen)
						continue;

					// Sample weights
					half depthWeight = 1.0;
					half normalWeight = 1.0;
					half planeWeight = 1.0;

					float sampleDepth = SAMPLE_TEXTURE2D_X_LOD(_CameraDepthTexture, my_point_clamp_sampler, uv, 0).r;

				#if !UNITY_REVERSED_Z
					sampleDepth = lerp(UNITY_NEAR_CLIP_VALUE, 1, sampleDepth);
				#endif

					float3 samplePositionWS = ComputeWorldSpacePosition(uv, sampleDepth, UNITY_MATRIX_I_VP);

					sampleDepth = Linear01Depth(sampleDepth, _ZBufferParams);

					half3 sampleNormal = SAMPLE_TEXTURE2D_X_LOD(_GBuffer2, my_point_clamp_sampler, uv, 0).xyz;
					
				#if defined(_GBUFFER_NORMALS_OCT)
					half2 remappedOctNormalWS = half2(Unpack888ToFloat2(sampleNormal.xyz));                    // values between [ 0, +1]
					half2 octNormalWS = remappedOctNormalWS.xy * half(2.0) - half(1.0);                        // values between [-1, +1]
					sampleNormal.xyz = half3(UnpackNormalOctQuadEncode(octNormalWS));                          // values between [-1, +1]
				#endif

					depthWeight = max(0.0, 1.0 - abs(sampleDepth - centerDepth));

					const half normalCloseness = sqr(sqr(max(0.0, dot(sampleNormal, centerNormal))));
					const half normalError = 1.0 - normalCloseness;
					normalWeight = max(0.0, (1.0 - normalError));

					// Change in position in camera space
					const half3 dq = positionWS - samplePositionWS;

					// How far away is this point from the original sample
					// in camera space? (Max value is unbounded)
					const half distance2 = dot(dq, dq);

					// How far off the expected plane (on the perpendicular) is this point? Max value is unbounded.
					const half planeError = max(abs(dot(dq, sampleNormal)), abs(dot(dq, centerNormal)));

					planeWeight = (distance2 < 0.0001) ? 1.0 :
					pow(max(0.0, 1.0 - 2.0 * planeError / sqrt(distance2)), 2.0);

					half w = k_GaussianWeight[sampleIndex]; //GetGaussianWeight(offset.z);
					w *= depthWeight * normalWeight * planeWeight;
					w = (sampleDepth != 1.0) && isInScreen ? w : 0.0;

					// Fetch the full resolution depth
					float4 tapSignal = SAMPLE_TEXTURE2D_X_LOD(_BlitTexture, my_point_clamp_sampler, uv, 0);
					tapSignal = w ? tapSignal : 0.0;

					// Accumulate
					signalSum += tapSignal * w;
					sumWeight += w;
				}

				// Normalize the samples (or the central one if we didn't get any valid samples)
				signalSum = sumWeight != 0.0 ? signalSum / sumWeight : centerSignal;

				// Normalize the result
				return max(signalSum, half4(0.0, 0.0, 0.0, 0.0));
			}
			ENDHLSL
		}

		Pass
        {
            Name "Blit Color Texture"
			Tags { "LightMode" = "Screen Space Global Illumination" }

            Blend One Zero
			
			HLSLPROGRAM
			#include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"
			// The Blit.hlsl file provides the vertex shader (Vert),
			// input structure (Attributes) and output structure (Varyings)
			#include "Packages/com.unity.render-pipelines.core/Runtime/Utilities/Blit.hlsl"
			
			#pragma vertex Vert
			#pragma fragment frag

            #pragma target 3.5

            // URP pre-defined the following variable on 2023.2+.
        #if UNITY_VERSION < 202320
            float4 _BlitTexture_TexelSize;
        #endif

			SAMPLER(my_point_clamp_sampler);
            
            half4 frag(Varyings input) : SV_Target
            {
                UNITY_SETUP_STEREO_EYE_INDEX_POST_VERTEX(input);
                float2 screenUV = input.texcoord;

                return SAMPLE_TEXTURE2D_X_LOD(_BlitTexture, my_point_clamp_sampler, screenUV, 0).rgba;
            }
            ENDHLSL
        }
    }
}
