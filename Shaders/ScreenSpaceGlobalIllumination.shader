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
		// Pass 2: Reproject GI
		// Pass 3: Spatial Filtering
		// Pass 4: Temporal Stabilization
		// Pass 5: Copy History Depth
		// Pass 6: Combine & Upscale GI

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

		#if UNITY_VERSION >= 202310
			#pragma multi_compile_fragment _ PROBE_VOLUMES_L1 PROBE_VOLUMES_L2
		#endif

			#include "Packages/com.jiaozi158.unityssgiurp/Shaders/SSGI.hlsl"

			half4 frag(Varyings input) : SV_Target
			{
				UNITY_SETUP_STEREO_EYE_INDEX_POST_VERTEX(input);
				float2 screenUV = input.texcoord;

				float depth = SAMPLE_TEXTURE2D_X_LOD(_CameraDepthTexture, my_point_clamp_sampler, screenUV, 0).r;
				half4 directLighting = SAMPLE_TEXTURE2D_X_LOD(_BlitTexture, my_point_clamp_sampler, screenUV, 0).rgba;

				// If the current pixel is sky
				bool isBackground = depth == UNITY_RAW_FAR_CLIP_VALUE ? true : false;

				if (isBackground)
					return directLighting;

				UpdateAmbientSH();

				half4 gbuffer0 = SAMPLE_TEXTURE2D_X_LOD(_GBuffer0, my_point_clamp_sampler, screenUV, 0);
				half3 albedo = gbuffer0.rgb;
				half4 normalSmoothness = SAMPLE_TEXTURE2D_X_LOD(_GBuffer2, my_point_clamp_sampler, screenUV, 0);

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

			#include "Packages/com.jiaozi158.unityssgiurp/Shaders/SSGIDenoise.hlsl"
			#include "Packages/com.jiaozi158.unityssgiurp/Shaders/SSGI.hlsl"
            
            half4 frag(Varyings input) : SV_Target
            {
                UNITY_SETUP_STEREO_EYE_INDEX_POST_VERTEX(input);
                float2 screenUV = input.texcoord;

				half4 lightingDistance = half4(0.0, 0.0, 0.0, 1.0); // indirectDiffuse.rgb + distance.a

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
				// If the history sample for a pixel will be invalid, we sample the reflection probes multiple times and use the results instead.
				
				// TODO: find a way to calculate it only once. (SSGI pass & Temporal Reprojection pass)
				float3 positionWS = ComputeWorldSpacePosition(screenUV, depth, UNITY_MATRIX_I_VP);
				float3 cameraPositionWS = GetCameraPositionWS();
				half3 viewDirectionWS = normalize(cameraPositionWS - positionWS);

				half2 velocity = SAMPLE_TEXTURE2D_X_LOD(_MotionVectorTexture, my_linear_clamp_sampler, screenUV, 0).xy;
				float2 prevUV = screenUV - velocity;

				half4 normalSmoothness = SAMPLE_TEXTURE2D_X_LOD(_GBuffer2, my_point_clamp_sampler, screenUV, 0).xyzw;
				half maxRadius = ComputeMaxReprojectionWorldRadius(positionWS, viewDirectionWS, normalSmoothness.xyz, _PixelSpreadAngleTangent);
				float prevDeviceDepth = SAMPLE_TEXTURE2D_X_LOD(_CameraDepthTexture, my_point_clamp_sampler, prevUV, 0).r;

				float3 prevPositionWS = ComputeWorldSpacePosition(prevUV, prevDeviceDepth, _PrevInvViewProjMatrix);
				half radius = length(prevPositionWS - positionWS) / maxRadius;

				// Is it too far from the current position?
				bool traceProbes = (prevUV.x > 1.0 || prevUV.x < 0.0 || prevUV.y > 1.0 || prevUV.y < 0.0 || radius > 1.0 || !_HistoryTextureValid);
				

				Ray ray;
				ray.position = cameraPositionWS;
				ray.direction = -viewDirectionWS; // viewDirectionWS points to the camera.

				// Calculate screenHit data only once
				RayHit screenHit = InitializeRayHit();
				screenHit.distance = length(cameraPositionWS - positionWS);
				screenHit.position = positionWS;
				screenHit.normal = SAMPLE_TEXTURE2D_X_LOD(_GBuffer2, my_point_clamp_sampler, screenUV, 0).xyz;

			#if defined(_GBUFFER_NORMALS_OCT)
				half2 remappedOctNormalWS = half2(Unpack888ToFloat2(screenHit.normal));                // values between [ 0, +1]
				half2 octNormalWS = remappedOctNormalWS.xy * half(2.0) - half(1.0);                    // values between [-1, +1]
				screenHit.normal = half3(UnpackNormalOctQuadEncode(octNormalWS));                      // values between [-1, +1]
			#endif
				
				UNITY_BRANCH
			    if (!traceProbes)
				{
					// Set the hit distance to 0
					lightingDistance.a = 0.0;

					half dither = (GenerateRandomValue(screenUV) * 0.3 - 0.15);

					UNITY_LOOP
					for (int i = 0; i < RAY_COUNT; i++)
					{
						RayHit rayHit = screenHit;

						// Generate a new sample direction
						ray.direction = SampleHemisphereCosine(GenerateRandomValue(screenUV), GenerateRandomValue(screenUV), rayHit.normal);
						ray.position = rayHit.position;
						
						// Find the intersection of the ray with scene geometries
						rayHit = RayMarching(ray, dither, viewDirectionWS);

						bool hitSuccessful = rayHit.distance > REAL_EPS;

						UNITY_BRANCH
						if (hitSuccessful)
						{
							lightingDistance.rgb += rayHit.emission * rcp(RAY_COUNT);
							lightingDistance.a += rayHit.distance * rcp(RAY_COUNT);
						}
						else
						{
							lightingDistance.rgb += SampleReflectionProbes(ray.direction, positionWS, 1.0h, screenUV) * rcp(RAY_COUNT);
							lightingDistance.a = 1.0;
						}
					}
				}
				else
				{
					const half rayCount = 16;
					UNITY_LOOP
					for (int i = 0; i < rayCount; i++)
					{
						ray.direction = SampleHemisphereCosine(GenerateRandomValue(screenUV), GenerateRandomValue(screenUV), screenHit.normal);
						lightingDistance.rgb += SampleReflectionProbes(ray.direction, positionWS, 1.0h, screenUV) * rcp(rayCount);
					}
				}

				// Reduce noise and fireflies by limiting the maximum brightness
				lightingDistance.xyz = RgbToHsv(lightingDistance.xyz);
				lightingDistance.z = clamp(lightingDistance.z, 0.0, _MaxBrightness);
				lightingDistance.xyz = HsvToRgb(lightingDistance.xyz);

                return lightingDistance;
            }
            ENDHLSL
        }
		
		Pass
		{
			Name "Reproject GI"
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

			#include "Packages/com.jiaozi158.unityssgiurp/Shaders/SSGI.hlsl"

			void frag(Varyings input, out half4 denoiseOutput : SV_Target0, out half currentSample : SV_Target1)
			{
				UNITY_SETUP_STEREO_EYE_INDEX_POST_VERTEX(input);
				float2 screenUV = input.texcoord;

				half2 velocity = SAMPLE_TEXTURE2D_X_LOD(_MotionVectorTexture, sampler_LinearClamp, screenUV, 0).xy;
				float2 prevUV = screenUV - velocity;

				float deviceDepth = SAMPLE_TEXTURE2D_X_LOD(_CameraDepthTexture, my_point_clamp_sampler, screenUV, 0).r;
				float prevDeviceDepth = SAMPLE_TEXTURE2D_X_LOD(_CameraDepthTexture, my_point_clamp_sampler, prevUV, 0).r;

				half4 normalSmoothness = SAMPLE_TEXTURE2D_X_LOD(_GBuffer2, my_point_clamp_sampler, screenUV, 0).xyzw;

				// Fetch the current and history values and apply the exposition to it.
				half4 currentColor = SAMPLE_TEXTURE2D_X_LOD(_BlitTexture, sampler_LinearClamp, screenUV, 0).rgba;

			#if defined(_GBUFFER_NORMALS_OCT)
				half2 remappedOctNormalWS = half2(Unpack888ToFloat2(normalSmoothness.xyz));                // values between [ 0, +1]
				half2 octNormalWS = remappedOctNormalWS.xy * half(2.0) - half(1.0);                        // values between [-1, +1]
				normalSmoothness.xyz = half3(UnpackNormalOctQuadEncode(octNormalWS));                      // values between [-1, +1]
			#endif 

				bool isSky = deviceDepth == UNITY_RAW_FAR_CLIP_VALUE? true : false;

				bool canBeReprojected = true;
				if (isSky || prevUV.x > 1.0 || prevUV.x < 0.0 || prevUV.y > 1.0 || prevUV.y < 0.0)
				{
					canBeReprojected = false;
				}

				float3 positionWS = ComputeWorldSpacePosition(screenUV, deviceDepth, UNITY_MATRIX_I_VP);
				float3 prevPositionWS = ComputeWorldSpacePosition(prevUV, prevDeviceDepth, _PrevInvViewProjMatrix); // "UNITY_MATRIX_PREV_I_VP" doesn't exist in URP yet

				half3 viewDirWS = normalize(GetCameraPositionWS() - positionWS);

				// Re-projected color from last frame.
				half historySample = SAMPLE_TEXTURE2D_X_LOD(_SSGIHistorySampleTexture, my_point_clamp_sampler, prevUV, 0).r;

				// Compute the max world radius that we consider acceptable for history reprojection
				float linearDepth = Linear01Depth(deviceDepth, _ZBufferParams);
				half maxRadius = ComputeMaxReprojectionWorldRadius(positionWS, viewDirWS, normalSmoothness.xyz, _PixelSpreadAngleTangent);
				half radius = length(prevPositionWS - positionWS) / maxRadius;

				// Is it too far from the current position?
				if (radius > 1.0 || !_HistoryTextureValid)
				{
					canBeReprojected = false;
				}

				// Depending on the roughness of the surface run one or the other temporal reprojection
				half sampleCount = historySample;

				half3 result;
				if (canBeReprojected && sampleCount != 0.0)
				{
					half3 prevColor = SAMPLE_TEXTURE2D_X_LOD(_HistoryIndirectDiffuseTexture, sampler_LinearClamp, prevUV, 0).rgb;

					half accumulationFactor = (sampleCount >= MAX_ACCUM_FRAME_NUM ? _TemporalIntensity : (sampleCount / (sampleCount + 1.0)));
						
					result = (currentColor.rgb * (1.0 - accumulationFactor) + prevColor.rgb * accumulationFactor);

					sampleCount = clamp(sampleCount + 1.0, 0.0, MAX_ACCUM_FRAME_NUM);
				}
				else
				{
					result = currentColor.rgb;
					sampleCount = 8.0; // multiple samples from reflection probe, almost no noise
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

			#include "Packages/com.jiaozi158.unityssgiurp/Shaders/SSGI.hlsl"

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

					float depth = LinearEyeDepth(SAMPLE_TEXTURE2D_X_LOD(_CameraDepthTexture, my_point_clamp_sampler, uv, 0).r, _ZBufferParams);

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

			#include "Packages/com.jiaozi158.unityssgiurp/Shaders/SSGI.hlsl"

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
				half3 prevColor = SAMPLE_TEXTURE2D_LOD(_HistoryIndirectDiffuseTexture, sampler_LinearClamp, prevUV, 0).rgb;

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
            
            float frag(Varyings input) : SV_Target
            {
                UNITY_SETUP_STEREO_EYE_INDEX_POST_VERTEX(input);
                float2 screenUV = input.texcoord;

                float depth = SAMPLE_TEXTURE2D_X_LOD(_CameraDepthTexture, my_point_clamp_sampler, screenUV, 0).r;
				//float depth = LOAD_TEXTURE2D_X(_CameraDepthTexture, uint2(screenUV * _ScreenSize.xy)).r; // This should be a bit faster

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

		#if defined(_USE_RENDERING_LAYERS)
			#include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/DeclareRenderingLayerTexture.hlsl"
		#endif

			#include "Packages/com.jiaozi158.unityssgiurp/Shaders/SSGI.hlsl"

			half3 BilateralUpscale(float2 screenUV, float deviceDepth)
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
					LinearEyeDepth(SAMPLE_TEXTURE2D_X_LOD(_CameraDepthTexture, my_point_clamp_sampler, uv0, 0).x, _ZBufferParams),
					LinearEyeDepth(SAMPLE_TEXTURE2D_X_LOD(_CameraDepthTexture, my_point_clamp_sampler, uv1, 0).x, _ZBufferParams),
					LinearEyeDepth(SAMPLE_TEXTURE2D_X_LOD(_CameraDepthTexture, my_point_clamp_sampler, uv2, 0).x, _ZBufferParams),
					LinearEyeDepth(SAMPLE_TEXTURE2D_X_LOD(_CameraDepthTexture, my_point_clamp_sampler, uv3, 0).x, _ZBufferParams));

				half3 normal0 = SAMPLE_TEXTURE2D_X_LOD(_GBuffer2, my_point_clamp_sampler, uv0, 0).xyz;
				half3 normal1 = SAMPLE_TEXTURE2D_X_LOD(_GBuffer2, my_point_clamp_sampler, uv1, 0).xyz;
				half3 normal2 = SAMPLE_TEXTURE2D_X_LOD(_GBuffer2, my_point_clamp_sampler, uv2, 0).xyz;
				half3 normal3 = SAMPLE_TEXTURE2D_X_LOD(_GBuffer2, my_point_clamp_sampler, uv3, 0).xyz;

			#if defined(_GBUFFER_NORMALS_OCT)
				half2 remappedOctNormalWS = half2(Unpack888ToFloat2(centerNormal));           // values between [ 0, +1]
				half2 octNormalWS = remappedOctNormalWS.xy * half(2.0) - half(1.0);           // values between [-1, +1]
				centerNormal = half3(UnpackNormalOctQuadEncode(octNormalWS));                 // values between [-1, +1]

				remappedOctNormalWS = half2(Unpack888ToFloat2(normal0));					  // values between [ 0, +1]
				octNormalWS = remappedOctNormalWS.xy * half(2.0) - half(1.0);				  // values between [-1, +1]
				normal0 = half3(UnpackNormalOctQuadEncode(octNormalWS));                      // values between [-1, +1]

				remappedOctNormalWS = half2(Unpack888ToFloat2(normal1));					  // values between [ 0, +1]
				octNormalWS = remappedOctNormalWS.xy * half(2.0) - half(1.0);				  // values between [-1, +1]
				normal1 = half3(UnpackNormalOctQuadEncode(octNormalWS));                      // values between [-1, +1]

				remappedOctNormalWS = half2(Unpack888ToFloat2(normal2));					  // values between [ 0, +1]
				octNormalWS = remappedOctNormalWS.xy * half(2.0) - half(1.0);				  // values between [-1, +1]
				normal2 = half3(UnpackNormalOctQuadEncode(octNormalWS));                      // values between [-1, +1]

				remappedOctNormalWS = half2(Unpack888ToFloat2(normal3));					  // values between [ 0, +1]
				octNormalWS = remappedOctNormalWS.xy * half(2.0) - half(1.0);				  // values between [-1, +1]
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

			half4 frag(Varyings input) : SV_Target
			{
				UNITY_SETUP_STEREO_EYE_INDEX_POST_VERTEX(input);
				float2 screenUV = input.texcoord;

				float depth = SAMPLE_TEXTURE2D_X_LOD(_CameraDepthTexture, my_point_clamp_sampler, screenUV, 0).r;

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
				half3 albedo = gbuffer0.rgb;
				half4 gbuffer1 = SAMPLE_TEXTURE2D_X_LOD(_GBuffer1, my_point_clamp_sampler, screenUV, 0);
				half metallic = (gbuffer0.a == kMaterialFlagSpecularSetup) ? MetallicFromReflectivity(ReflectivitySpecular(gbuffer1.rgb)) : gbuffer1.r;

				half3 indirectLighting;

				UNITY_BRANCH
				if (_DownSample == 1.0)
					indirectLighting = SAMPLE_TEXTURE2D_X_LOD(_IndirectDiffuseTexture, my_linear_clamp_sampler, screenUV, 0).rgb * albedo * (1.0 - metallic);
				else
				    indirectLighting = BilateralUpscale(screenUV, depth) * albedo * (1.0 - metallic);

				half4 directLighting = SAMPLE_TEXTURE2D_X_LOD(_BlitTexture, my_point_clamp_sampler, screenUV, 0).rgba;

				// Apply the indirect lighting multiplier
				indirectLighting *= _IndirectDiffuseLightingMultiplier;

				return half4(directLighting.rgb + indirectLighting, directLighting.a);
			}
			ENDHLSL
		}	
    }
}
