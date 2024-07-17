#ifndef URP_SCREEN_SPACE_GLOBAL_ILLUMINATION_HLSL
#define URP_SCREEN_SPACE_GLOBAL_ILLUMINATION_HLSL

#include "Packages/com.jiaozi158.unityssgiurp/Shaders/SSGIUtilities.hlsl"
#include "Packages/com.jiaozi158.unityssgiurp/Shaders/SSGIDenoise.hlsl"

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

    // Adaptive Ray Marching
    // Near: Use smaller step size to improve accuracy.
    // Far:  Use larger step size to fill the scene.
    bool activeSamplingSmall = true;
    bool activeSamplingMedium = true;

    bool isBackBuffer = false;

    UNITY_LOOP
    for (int i = 1; i <= MAX_STEP; i++)
    {
        UNITY_BRANCH
        if (i > MAX_SMALL_STEP && i <= MAX_MEDIUM_STEP && activeSamplingSmall)
        {
            activeSamplingSmall = false;
            currStepSize = (startBinarySearch) ? currStepSize : MEDIUM_STEP_SIZE;
            thickness = (startBinarySearch) ? thickness : MARCHING_THICKNESS_MEDIUM_STEP;
            marchingThickness = MARCHING_THICKNESS;
        }
        else if (i > MAX_MEDIUM_STEP && !activeSamplingSmall && activeSamplingMedium)
        {
            activeSamplingMedium = false;
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
        if (i <= MAX_MEDIUM_STEP && abs(rayPositionNDC.x - lastRayPositionNDC.x) < _BlitTexture_TexelSize.x && abs(rayPositionNDC.y - lastRayPositionNDC.y) < _BlitTexture_TexelSize.y)
            continue;

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
        // 2. Back-faces should behind Front-faces.
        bool backDepthValid = false; 
    #if defined(_BACKFACE_TEXTURES)
        deviceBackDepth = SAMPLE_TEXTURE2D_X_LOD(_CameraBackDepthTexture, my_point_clamp_sampler, rayPositionNDC.xy, 0).r;
        sceneBackDepth = LinearEyeDepth(deviceBackDepth, _ZBufferParams);

        backDepthValid = (deviceBackDepth != UNITY_RAW_FAR_CLIP_VALUE) && (sceneBackDepth >= sceneDepth);

        if (backDepthValid)
            backDepthDiff = hitDepth - sceneBackDepth;
        else
            backDepthDiff = depthDiff - marchingThickness;
    #endif

        // Binary Search Sign is used to flip the ray marching direction.
        // Sign is positive : ray is in front of the actual intersection.
        // Sign is negative : ray is behind the actual intersection.
        half Sign;
        bool isBackSearch = (!isFrontRay && hitDepth > sceneBackDepth && backDepthValid);
        if (isBackSearch)
            Sign = FastSign(backDepthDiff);
        else
            Sign = FastSign(depthDiff);

        // Disable binary search:
        // 1. The ray points to the camera plane, but is in front of all objects.
        // 2. The ray leaves the camera plane, but is behind all objects.
        // 3. The ray is an outgoing (refracted) ray. (we only have 3-layer depth)
        bool cannotBinarySearch = !startBinarySearch && (isFrontRay ? hitDepth > sceneBackDepth : hitDepth < sceneDepth);

        // Start binary search when the ray is behind the actual intersection.
        startBinarySearch = !cannotBinarySearch && (startBinarySearch || (Sign == -1)) ? true : false;

        // Half the step size each time when binary search starts.
        // If the ray passes through the intersection, we flip the sign of step size.
        if (startBinarySearch)
        {
            currStepSize *= 0.5;
            currStepSize = (FastSign(currStepSize) == Sign) ? currStepSize : -currStepSize;
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
        UNITY_BRANCH
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
        UNITY_BRANCH
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
            half multiplier = 1.0;
            currStepSize = (currStepSize + currStepSize * 0.1) * multiplier;
            marchingThickness += MARCHING_THICKNESS * 0.25 * multiplier;
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