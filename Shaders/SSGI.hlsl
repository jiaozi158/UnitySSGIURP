#ifndef URP_SCREEN_SPACE_GLOBAL_ILLUMINATION_HLSL
#define URP_SCREEN_SPACE_GLOBAL_ILLUMINATION_HLSL

#include "./SSGIUtilities.hlsl"
#include "./SSGIDenoise.hlsl"

#ifdef UNITY_COLORSPACE_GAMMA
#define unity_ColorSpaceDielectricSpec half4(0.220916301, 0.220916301, 0.220916301, 1.0 - 0.220916301)
#else // Linear values
#define unity_ColorSpaceDielectricSpec half4(0.04, 0.04, 0.04, 1.0 - 0.04) // standard dielectric reflectivity coef at incident angle (= 4%)
#endif

float3 DepthToWorldPositionV1(float2 screenPos)
{
    //screenPos / screenPos.w就是【0,1】的归一化屏幕坐标  //_CameraDepthTexture是获取的深度图
    //Linear01Depth将采样的非线性深度图变成线性的
    float depth = Linear01Depth(SAMPLE_TEXTURE2D_X_LOD(_CameraDepthTexture, my_point_clamp_sampler, screenPos, 0).r, _ZBufferParams);
    //将【0，1】映射到【-1， 1】上，得到ndcPos的x，y坐标
    float2 ndcPosXY = screenPos * 2 - 1;
    //float3的z值补了一个1，代表远平面的NDC坐标  _ProjectionParams代表view空间的远平面, 我们知道裁剪空间的w和view空间的z相等，
    //相当于做了一次逆向透视除法，得到了远平面的clipPos
    float3 clipPos = float3(ndcPosXY.x, ndcPosXY.y, 1) * _ProjectionParams.z;

    float3 viewPos = mul(unity_CameraInvProjection, clipPos.xyzz).xyz * depth;  //远平面的clipPos转回远平面的viewPos， 再利用depth获取该点在viewPos里真正的位置
    //补一个1变成其次坐标，然后逆的V矩阵变回worldPos
    float3 worldPos = mul(UNITY_MATRIX_I_V, float4(viewPos, 1)).xyz;
    return worldPos;
}

float3 FresnelSchlickRoughness(float cosTheta, float3 F0, float roughness)
{
	return F0 + (max(float3(1.0 - roughness, 1.0 - roughness, 1.0 - roughness), F0) - F0) * pow(1.0 - cosTheta, 5.0);
}

// If no intersection, "rayHit.distance" will remain "REAL_EPS".
RayHit RayMarching(Ray ray, float2 screenUV, half dither, half3 viewDirectionWS)
{
    RayHit rayHit = InitializeRayHit();

    // True:  The ray points to the scene objects.
    // False: The ray points to the camera plane.
    bool isFrontRay = (dot(ray.direction, viewDirectionWS) <= 0.0) ? true : false;

    // Store a frequently used material property
    half stepSize = STEP_SIZE;

    // Initialize small step ray marching settings
    half thickness = MARCHING_THICKNESS_SMALL_STEP;
    half currStepSize = SMALL_STEP_SIZE;

    // Minimum thickness of scene objects without backface depth
    half marchingThickness = MARCHING_THICKNESS;

    // Initialize current ray position.
    float3 rayPositionWS = ray.position;

    // Interpolate the intersecting position using the depth difference.
    //float lastDepthDiff = 0.0;
    float2 lastRayPositionNDC = screenUV;
    float3 lastRayPositionWS = ray.position; // avoid using 0 for the first interpolation

    bool startBinarySearch = false;

    bool isBackBuffer = false;

    UNITY_LOOP
    for (int i = 1; i <= MAX_STEP; i++)
    {
        // Adaptive Ray Marching
        // Near: Use smaller step size to improve accuracy.
        // Far:  Use larger step size to fill the scene.
        if (i > MAX_SMALL_STEP && i <= MAX_MEDIUM_STEP)
        {
            currStepSize = (startBinarySearch) ? currStepSize : MEDIUM_STEP_SIZE;
            thickness = (startBinarySearch) ? thickness : MARCHING_THICKNESS_MEDIUM_STEP;
            marchingThickness = MARCHING_THICKNESS;
        }
        else if (i > MAX_MEDIUM_STEP)
        {
            // [Far] Use a small step size only when objects are close to the camera.
            currStepSize = (startBinarySearch) ? currStepSize : stepSize;
            thickness = (startBinarySearch) ? thickness : MARCHING_THICKNESS;
            marchingThickness = MARCHING_THICKNESS;
        }

        // Update current ray position.
        rayPositionWS += (currStepSize + currStepSize * dither) * ray.direction;

        float3 rayPositionNDC = ComputeNormalizedDeviceCoordinatesWithZ(rayPositionWS, GetWorldToHClipMatrix());
        //float3 lastRayPositionNDC = ComputeNormalizedDeviceCoordinatesWithZ(lastRayPositionWS, GetWorldToHClipMatrix());

        // Move to the next step if the current ray moves less than 1 pixel across the screen.
        //if (i <= MAX_MEDIUM_STEP && abs(rayPositionNDC.x - lastRayPositionNDC.x) < _BlitTexture_TexelSize.x && abs(rayPositionNDC.y - lastRayPositionNDC.y) < _BlitTexture_TexelSize.y)
            //continue;

    #if (UNITY_REVERSED_Z == 0) // OpenGL platforms
        rayPositionNDC.z = rayPositionNDC.z * 0.5 + 0.5; // -1..1 to 0..1
    #endif

        // Stop marching the ray when outside screen space.
        bool isScreenSpace = rayPositionNDC.x > 0.0 && rayPositionNDC.y > 0.0 && rayPositionNDC.x < 1.0 && rayPositionNDC.y < 1.0 ? true : false;
        if (!isScreenSpace)
            break;

        // Sample opaque front depth
        float deviceDepth = SAMPLE_TEXTURE2D_X_LOD(_CameraDepthTexture, my_point_clamp_sampler, rayPositionNDC.xy, 0).r;

        // Convert Z-Depth to Linear Eye Depth
        // Value Range: Camera Near Plane -> Camera Far Plane
        float sceneDepth = LinearEyeDepth(deviceDepth, _ZBufferParams);
        float hitDepth = LinearEyeDepth(rayPositionNDC.z, _ZBufferParams); // Non-GL (DirectX): rayPositionNDC.z is (near to far) 1..0

        // Calculate (front) depth difference
        // Positive: ray is in front of the front-faces of object.
        // Negative: ray is behind the front-faces of object.
        float depthDiff = sceneDepth - hitDepth;

        // Initialize variables
        float deviceBackDepth = 0.0; // z buffer (back) depth
        float sceneBackDepth = 0.0;

        // Calculate (back) depth difference
        // Positive: ray is in front of the back-faces of object.
        // Negative: ray is behind the back-faces of object.
        float backDepthDiff = 0.0;

        // Avoid infinite thickness for objects with no thickness (ex. Plane).
        // 1. Back-face depth value is not from sky
        // 2. Back-faces should be behind front-faces.
        bool backDepthValid = false; 
    #if defined(_BACKFACE_TEXTURES)
        deviceBackDepth = SAMPLE_TEXTURE2D_X_LOD(_CameraBackDepthTexture, my_point_clamp_sampler, rayPositionNDC.xy, 0).r;
        sceneBackDepth = LinearEyeDepth(deviceBackDepth, _ZBufferParams);

        backDepthValid = (deviceBackDepth != UNITY_RAW_FAR_CLIP_VALUE) && (sceneBackDepth >= sceneDepth);
        backDepthDiff = backDepthValid ? (hitDepth - sceneBackDepth) : (depthDiff - marchingThickness);
    #endif

        // Binary Search Sign is used to flip the ray marching direction.
        // Sign is positive : ray is in front of the actual intersection.
        // Sign is negative : ray is behind the actual intersection.
        bool isBackSearch = (!isFrontRay && hitDepth > sceneBackDepth && backDepthValid);
        half Sign = isBackSearch ? FastSign(backDepthDiff) : FastSign(depthDiff);

        // Disable binary search:
        // 1. The ray points to the camera plane, but is in front of all objects.
        // 2. The ray leaves the camera plane, but is behind all objects.
        bool cannotBinarySearch = !startBinarySearch && (isFrontRay ? hitDepth > sceneBackDepth : hitDepth < sceneDepth);

        // Start binary search when the ray is behind the actual intersection.
        startBinarySearch = !cannotBinarySearch && (startBinarySearch || (Sign == -1)) ? true : false;

        // Half the step size each time when binary search starts.
        // If the ray passes through the intersection, we flip the sign of step size.
        if (startBinarySearch)
        {
            currStepSize *= (FastSign(currStepSize) == Sign) ? 0.5 : -0.5;
        }

        // Do not reflect sky, use reflection probe fallback.
        bool isSky = sceneDepth == UNITY_RAW_FAR_CLIP_VALUE ? true : false;

        // [No minimum step limit] The current implementation focuses on performance, so the ray will stop marching once it hits something.
        // Rules of ray hit:
        // 1. Ray is behind the front-faces of object. (sceneDepth <= hitDepth)
        // 2. Ray is in front of back-faces of object. (sceneBackDepth >= hitDepth) or (sceneDepth + marchingThickness >= hitDepth)
        // 3. Ray does not hit sky. (!isSky)
        bool hitSuccessful;

        // Ignore the incorrect "backDepthDiff" when objects (ex. Plane with front face only) has no thickness and blocks the backface depth rendering of objects behind it.
    #if defined(_BACKFACE_TEXTURES)
        if (backDepthValid)
        {
            // It's difficult to find the intersection of thin objects in several steps with large step sizes, so we add a minimum thickness to all objects to make it visually better.
            sceneBackDepth = max(sceneBackDepth, sceneDepth + thickness);
            hitSuccessful = ((depthDiff <= 0.0) && (hitDepth <= sceneBackDepth) && !isSky) ? true : false;
        }
        else
    #endif
        {
            hitSuccessful = ((depthDiff <= 0.0) && (depthDiff >= -marchingThickness) && !isSky) ? true : false;
        }

        // If we find the intersection.
        if (hitSuccessful)
        {
            rayHit.position = rayPositionWS;
            rayHit.distance = length(rayPositionWS - ray.position);

            UNITY_BRANCH
            if (_BackDepthEnabled = 2.0 && isBackBuffer)
                rayHit.emission = SAMPLE_TEXTURE2D_X_LOD(_CameraBackOpaqueTexture, my_point_clamp_sampler, rayPositionNDC.xy, 0).rgb;
            else
                rayHit.emission = SAMPLE_TEXTURE2D_X_LOD(_SSGIHistoryCameraColorTexture, my_point_clamp_sampler, rayPositionNDC.xy, 0).rgb;

            break;
        }
        // [Optimization] Exponentially increase the stepSize when the ray hasn't passed through the intersection.
        // From https://blog.voxagon.se/2018/01/03/screen-space-path-tracing-diffuse.html
        else if (!startBinarySearch)
        {
            // As the distance increases, the accuracy of ray intersection test becomes less important.
            currStepSize += currStepSize * 0.1;
            marchingThickness += _Thickness_Increment;
        }

        // Update last step's depth difference.
        //lastDepthDiff = (isBackSearch) ? backDepthDiff : depthDiff;
        isBackBuffer = backDepthValid && _BackDepthEnabled == 2.0 ? backDepthDiff > 0.0 : false;
        lastRayPositionNDC = rayPositionNDC.xy;
        lastRayPositionWS = rayPositionWS.xyz;
    }
    return rayHit;
}
#endif