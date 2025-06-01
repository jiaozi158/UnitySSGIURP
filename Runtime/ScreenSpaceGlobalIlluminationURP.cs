using System;
using System.Reflection;
using System.Collections.Generic;
using Unity.Collections;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.Universal;
using UnityEngine.Rendering.RendererUtils;
using UnityEngine.Experimental.Rendering;

#if UNITY_6000_0_OR_NEWER
using UnityEngine.Rendering.RenderGraphModule;
#endif

[DisallowMultipleRendererFeature("Screen Space Global Illumination")]
[Tooltip("The Screen Space Global Illumination uses the depth and color buffer of the screen to calculate diffuse light bounces.")]
[HelpURL("https://github.com/jiaozi158/UnitySSGIURP/blob/main")]
public class ScreenSpaceGlobalIlluminationURP : ScriptableRendererFeature
{
    private Material m_SSGIMaterial;

    [Header("Setup")]
    [Tooltip("The shader of screen space global illumination.")]
    [SerializeField] private Shader m_Shader;
    [Tooltip("Specifies if URP computes screen space global illumination in Rendering Debugger view. \nThis is disabled by default to avoid affecting the individual lighting previews.")]
    [SerializeField] private bool m_RenderingDebugger = false;

    [Header("Performance")]
    [Tooltip("Specifies if URP computes screen space global illumination in both real-time and baked reflection probes. \nScreen space global illumination in real-time reflection probes may reduce performace.")]
    [SerializeField] private bool m_ReflectionProbes = true;
    [Tooltip("Enables high-quality upscaling for screen space global illumination. \nThis may impact performance.")]
    [SerializeField] private bool m_HighQualityUpscaling = false;

    [Header("Lighting")]
    [Tooltip("Specifies if screen space global illumination overrides ambient lighting. \nThis ensures the accuracy of indirect lighting from SSGI.")]
    [SerializeField] private bool m_OverrideAmbientLighting = true;

    [Header("Advanced")]
    [Tooltip("Renders back-face lighting when using automatic thickness mode. \nThis improves accuracy in some cases, but may severely impact performance.")]
    [SerializeField] private bool m_BackfaceLighting = false;

    /// <summary>
    /// Get the material of screen space global illumination shader.
    /// </summary>
    /// <value>
    /// The material of screen space global illumination shader.
    /// </value>
    public Material SSGIMaterial
    {
        get { return m_SSGIMaterial; }
    }

    /// <summary>
    /// Gets or sets the screen space global illumination shader.
    /// </summary>
    /// <value>
    /// The screen space global illumination shader.
    /// </value>
    public Shader SSGIShader
    {
        get { return m_Shader; }
        set { m_Shader = value == Shader.Find(m_SSGIShaderName) ? value : m_Shader; }
    }

    /// <summary>
    /// Gets or sets a value indicating whether to compute screen space global illumination in Rendering Debugger view.
    /// </summary>
    /// <remarks>
    /// This is disabled by default to avoid affecting the individual lighting previews.
    /// </remarks>
    public bool RenderingDebugger
    {
        get { return m_RenderingDebugger; }
        set { m_RenderingDebugger = value; }
    }

    /// <summary>
    /// Gets or sets a value indicating whether to compute screen space global illumination in both real-time and baked reflection probes.
    /// </summary>
    /// <remarks>
    /// Screen space global illumination in real-time reflection probes may reduce performace.
    /// </remarks>
    public bool ReflectionProbes
    {
        get { return m_ReflectionProbes; }
        set { m_ReflectionProbes = value; }
    }

    /// <summary>
    /// Gets or sets a value indicating whether to enable high-quality upscaling for screen space global illumination.
    /// </summary>
    public bool HighQualityUpscaling
    {
        get { return m_HighQualityUpscaling; }
        set { m_HighQualityUpscaling = value; }
    }

    /// <summary>
    /// Gets or sets a value indicating whether screen space global illumination overrides ambient lighting.
    /// </summary>
    /// <remarks>
    /// Enable this to ensure the accuracy of indirect lighting from SSGI.
    /// </remarks>
    public bool OverrideAmbientLighting
    {
        get { return m_OverrideAmbientLighting; }
        set { m_OverrideAmbientLighting = value; }
    }

    /// <summary>
    /// Renders back-face lighting when using automatic thickness mode.
    /// </summary>
    /// <remarks>
    /// This improves accuracy in some cases, but may severely impact performance.
    /// </remarks>
    public bool BackfaceLighting
    {
        get { return m_BackfaceLighting; }
        set { m_BackfaceLighting = value; }
    }

    private const string m_SSGIShaderName = "Hidden/Lighting/ScreenSpaceGlobalIllumination";
    private readonly string[] m_GBufferPassNames = new string[] { "UniversalGBuffer" };
    private PreRenderScreenSpaceGlobalIlluminationPass m_PreRenderSSGIPass;
    private ScreenSpaceGlobalIlluminationPass m_SSGIPass;
    private BackfaceDataPass m_BackfaceDataPass;
    private ForwardGBufferPass m_ForwardGBufferPass;

    // Used in Forward GBuffer render pass
    private readonly static FieldInfo gBufferFieldInfo = typeof(UniversalRenderer).GetField("m_GBufferPass", BindingFlags.NonPublic | BindingFlags.Instance);

    private readonly static FieldInfo motionVectorPassFieldInfo = typeof(UniversalRenderer).GetField("m_MotionVectorPass", BindingFlags.NonPublic | BindingFlags.Instance);

    // [Resolve Later] The "_CameraNormalsTexture" still exists after disabling DepthNormals Prepass, which may cause issue during rendering.
    // So instead of checking the RTHandle, we need to check if DepthNormals Prepass is enqueued.
    //private readonly static FieldInfo normalsTextureFieldInfo = typeof(UniversalRenderer).GetField("m_NormalsTexture", BindingFlags.NonPublic | BindingFlags.Instance);

    // Avoid printing messages every frame
    private bool isShaderMismatchLogPrinted = false;
    private bool isDebuggerLogPrinted = false;
    private bool isBackfaceLightingLogPrinted = false;

    // SSGI Shader Property IDs
    private static readonly int _MaxSteps = Shader.PropertyToID("_MaxSteps");
    private static readonly int _MaxSmallSteps = Shader.PropertyToID("_MaxSmallSteps");
    private static readonly int _MaxMediumSteps = Shader.PropertyToID("_MaxMediumSteps");
    private static readonly int _Thickness = Shader.PropertyToID("_Thickness");
    private static readonly int _Thickness_Increment = Shader.PropertyToID("_Thickness_Increment");
    private static readonly int _StepSize = Shader.PropertyToID("_StepSize");
    private static readonly int _SmallStepSize = Shader.PropertyToID("_SmallStepSize");
    private static readonly int _MediumStepSize = Shader.PropertyToID("_MediumStepSize");
    private static readonly int _RayCount = Shader.PropertyToID("_RayCount");
    private static readonly int _TemporalIntensity = Shader.PropertyToID("_TemporalIntensity");
    private static readonly int _MaxBrightness = Shader.PropertyToID("_MaxBrightness");
    private static readonly int _IsProbeCamera = Shader.PropertyToID("_IsProbeCamera");
    private static readonly int _BackDepthEnabled = Shader.PropertyToID("_BackDepthEnabled");
    private static readonly int _PrevInvViewProjMatrix = Shader.PropertyToID("_PrevInvViewProjMatrix");
    private static readonly int _PrevCameraPositionWS = Shader.PropertyToID("_PrevCameraPositionWS");
    private static readonly int _PixelSpreadAngleTangent = Shader.PropertyToID("_PixelSpreadAngleTangent");
    private static readonly int _HistoryTextureValid = Shader.PropertyToID("_HistoryTextureValid");
    private static readonly int _IndirectDiffuseLightingMultiplier = Shader.PropertyToID("_IndirectDiffuseLightingMultiplier");
    private static readonly int _IndirectDiffuseRenderingLayers = Shader.PropertyToID("_IndirectDiffuseRenderingLayers");
    private static readonly int _AggressiveDenoise = Shader.PropertyToID("_AggressiveDenoise");
    private static readonly int _ReBlurBlurRotator = Shader.PropertyToID("_ReBlurBlurRotator");
    private static readonly int _ReBlurDenoiserRadius = Shader.PropertyToID("_ReBlurDenoiserRadius");

    private const string _CameraDepthTexture = "_CameraDepthTexture";
    private const string _IndirectDiffuseTexture = "_IndirectDiffuseTexture";
    private const string _IntermediateIndirectDiffuseTexture = "_IntermediateIndirectDiffuseTexture";
    private const string _IntermediateCameraColorTexture = "_IntermediateCameraColorTexture";
    private const string _SSGIHistoryDepthTexture = "_SSGIHistoryDepthTexture";
    private const string _CameraBackDepthTexture = "_CameraBackDepthTexture";
    private const string _CameraBackOpaqueTexture = "_CameraBackOpaqueTexture";
    private const string _HistoryIndirectDiffuseTexture = "_HistoryIndirectDiffuseTexture";
    private const string _SSGISampleTexture = "_SSGISampleTexture";
    private const string _SSGIHistorySampleTexture = "_SSGIHistorySampleTexture";
    private const string _SSGIHistoryCameraColorTexture = "_SSGIHistoryCameraColorTexture";
    private const string _APVLightingTexture = "_APVLightingTexture";

    private static readonly int cameraDepthTexture = Shader.PropertyToID(_CameraDepthTexture);
    private static readonly int indirectDiffuseTexture = Shader.PropertyToID(_IndirectDiffuseTexture);
    //private static readonly int intermediateIndirectDiffuseTexture = Shader.PropertyToID(_IntermediateIndirectDiffuseTexture);
    //private static readonly int intermediateCameraColorTexture = Shader.PropertyToID(_IntermediateCameraColorTexture);
    private static readonly int ssgiHistoryDepthTexture = Shader.PropertyToID(_SSGIHistoryDepthTexture);
    private static readonly int cameraBackDepthTexture = Shader.PropertyToID(_CameraBackDepthTexture);
    private static readonly int cameraBackOpaqueTexture = Shader.PropertyToID(_CameraBackOpaqueTexture);
    private static readonly int historyIndirectDiffuseTexture = Shader.PropertyToID(_HistoryIndirectDiffuseTexture);
    private static readonly int ssgiSampleTexture = Shader.PropertyToID(_SSGISampleTexture);
    private static readonly int ssgiHistorySampleTexture = Shader.PropertyToID(_SSGIHistorySampleTexture);
    private static readonly int ssgiHistoryCameraColorTexture = Shader.PropertyToID(_SSGIHistoryCameraColorTexture);
    private static readonly int apvLightingTexture = Shader.PropertyToID(_APVLightingTexture);

    private const string _GBuffer0 = "_GBuffer0";
    private const string _GBuffer1 = "_GBuffer1";
    private const string _GBuffer2 = "_GBuffer2";
    private const string _GBufferDepth = "_GBufferDepthTexture";

    private static readonly int gBuffer0 = Shader.PropertyToID(_GBuffer0);
    private static readonly int gBuffer1 = Shader.PropertyToID(_GBuffer1);
    private static readonly int gBuffer2 = Shader.PropertyToID(_GBuffer2);
    //private static readonly int gBufferDepth = Shader.PropertyToID(_GBufferDepth);

    private static readonly int specCube0 = Shader.PropertyToID("_SpecCube0");
    private static readonly int specCube0_HDR = Shader.PropertyToID("_SpecCube0_HDR");
    private static readonly int specCube0_BoxMin = Shader.PropertyToID("_SpecCube0_BoxMin");
    private static readonly int specCube0_BoxMax = Shader.PropertyToID("_SpecCube0_BoxMax");
    private static readonly int specCube0_ProbePosition = Shader.PropertyToID("_SpecCube0_ProbePosition");
    private static readonly int probeWeight = Shader.PropertyToID("_ProbeWeight");
    private static readonly int probeSet = Shader.PropertyToID("_ProbeSet");

    private static readonly int downSample = Shader.PropertyToID("_DownSample");
    private static readonly int frameIndex = Shader.PropertyToID("_FrameIndex");

    // unity_SH is not available when performing full screen blit pass
    private static readonly int shAr = Shader.PropertyToID("ssgi_SHAr");
    private static readonly int shAg = Shader.PropertyToID("ssgi_SHAg");
    private static readonly int shAb = Shader.PropertyToID("ssgi_SHAb");
    private static readonly int shBr = Shader.PropertyToID("ssgi_SHBr");
    private static readonly int shBg = Shader.PropertyToID("ssgi_SHBg");
    private static readonly int shBb = Shader.PropertyToID("ssgi_SHBb");
    private static readonly int shC = Shader.PropertyToID("ssgi_SHC");

    // Local Keywords
    private const string _FP_REFL_PROBE_ATLAS = "_FP_REFL_PROBE_ATLAS";
    private const string _RAYMARCHING_FALLBACK_SKY = "_RAYMARCHING_FALLBACK_SKY";
    private const string _RAYMARCHING_FALLBACK_REFLECTION_PROBES = "_RAYMARCHING_FALLBACK_REFLECTION_PROBES";
    private const string _BACKFACE_TEXTURES = "_BACKFACE_TEXTURES";
    private const string _FORWARD_PLUS = "_FORWARD_PLUS";
#if UNITY_6000_1_OR_NEWER
    private const string _CLUSTER_LIGHT_LOOP = "_CLUSTER_LIGHT_LOOP";
    private const string _REFLECTION_PROBE_ATLAS = "_REFLECTION_PROBE_ATLAS";
#endif
    private const string _WRITE_RENDERING_LAYERS = "_WRITE_RENDERING_LAYERS";
    private const string _USE_RENDERING_LAYERS = "_USE_RENDERING_LAYERS";
    private const string _DEPTH_NORMALS_UPSCALE = "_DEPTH_NORMALS_UPSCALE";
    private const string PROBE_VOLUMES_L1 = "PROBE_VOLUMES_L1";
    private const string PROBE_VOLUMES_L2 = "PROBE_VOLUMES_L2";
    private const string _APV_LIGHTING_BUFFER = "_APV_LIGHTING_BUFFER";

    // Global Keywords
    private const string SSGI_RENDER_GBUFFER = "SSGI_RENDER_GBUFFER";
    private const string SSGI_RENDER_BACKFACE_DEPTH = "SSGI_RENDER_BACKFACE_DEPTH";
    private const string SSGI_RENDER_BACKFACE_COLOR = "SSGI_RENDER_BACKFACE_COLOR";

    // From "SSGIDenoise.hlsl"
    private const float k_BlurMaxRadius = 0.04f;

    private static readonly Vector4 m_ScaleBias = new Vector4(1.0f, 1.0f, 0.0f, 0.0f);

    public override void Create()
    {
        if (m_Shader != Shader.Find(m_SSGIShaderName))
        {
        #if UNITY_EDITOR || DEBUG
            Debug.LogErrorFormat("Screen Space Global Illumination URP: Material is not using {0} shader.", m_SSGIShaderName);
            isShaderMismatchLogPrinted = true;
        #endif
            return;
        }
        else
        {
            isShaderMismatchLogPrinted = false;
        }

        m_SSGIMaterial = CoreUtils.CreateEngineMaterial(m_Shader);

        if (m_PreRenderSSGIPass == null)
        {
            m_PreRenderSSGIPass = new PreRenderScreenSpaceGlobalIlluminationPass();
        #if UNITY_6000_0_OR_NEWER
            m_PreRenderSSGIPass.renderPassEvent = RenderPassEvent.BeforeRenderingPrePasses;
        #else
            m_PreRenderSSGIPass.renderPassEvent = RenderPassEvent.BeforeRenderingTransparents - 1;
        #endif
        }

        if (m_SSGIPass == null)
        {
            m_SSGIPass = new ScreenSpaceGlobalIlluminationPass(m_SSGIMaterial);
        #if UNITY_6000_0_OR_NEWER
            bool enableRenderGraph = !GraphicsSettings.GetRenderPipelineSettings<RenderGraphSettings>().enableRenderCompatibilityMode;
            m_SSGIPass.renderPassEvent = enableRenderGraph ? RenderPassEvent.AfterRenderingSkybox : RenderPassEvent.BeforeRenderingTransparents;
        #else
            m_SSGIPass.renderPassEvent = RenderPassEvent.BeforeRenderingTransparents; // We cannot move to after skybox because of the motion vectors issue
        #endif
        }
        m_SSGIPass.m_SSGIMaterial = m_SSGIMaterial;

        if (m_BackfaceDataPass == null)
        {
            m_BackfaceDataPass = new BackfaceDataPass();
            m_BackfaceDataPass.renderPassEvent = RenderPassEvent.AfterRenderingOpaques - 1;
        }

        if (m_ForwardGBufferPass == null)
        {
            m_ForwardGBufferPass = new ForwardGBufferPass(m_GBufferPassNames);
            // Set this to "After Opaques" so that we can enable GBuffers Depth Priming on non-GL platforms.
            m_ForwardGBufferPass.renderPassEvent = RenderPassEvent.AfterRenderingOpaques;
        }
    }

    protected override void Dispose(bool disposing)
    {
        if (m_PreRenderSSGIPass != null)
            m_PreRenderSSGIPass.Dispose();
        
        if (m_SSGIPass != null)
            m_SSGIPass.Dispose();

        if (m_BackfaceDataPass != null)
        {
            // Turn off accurate thickness since the render pass is disabled.
            if (m_SSGIMaterial != null) { m_SSGIMaterial.SetFloat(_BackDepthEnabled, 0.0f); }
            m_BackfaceDataPass.Dispose();
        }

        if (m_ForwardGBufferPass != null)
            m_ForwardGBufferPass.Dispose();

        if (m_SSGIMaterial != null)
            CoreUtils.Destroy(m_SSGIMaterial);
    }

    public override void AddRenderPasses(ScriptableRenderer renderer, ref RenderingData renderingData)
    {
        // Do not add render passes if any error occurs.
        if (isShaderMismatchLogPrinted)
            return;

        if (renderingData.cameraData.camera.cameraType == CameraType.Preview)
            return;

        var stack = VolumeManager.instance.stack;
        ScreenSpaceGlobalIlluminationVolume ssgiVolume = stack.GetComponent<ScreenSpaceGlobalIlluminationVolume>();
        bool isActive = ssgiVolume != null && ssgiVolume.IsActive();
        bool isDebugger = DebugManager.instance.isAnyDebugUIActive;
        bool shouldDisable = !m_ReflectionProbes && renderingData.cameraData.camera.cameraType == CameraType.Reflection;
        shouldDisable |= ssgiVolume.indirectDiffuseLightingMultiplier.value == 0.0f && !m_OverrideAmbientLighting;
        shouldDisable |= renderingData.cameraData.renderType == CameraRenderType.Overlay;

        if (!isActive || shouldDisable)
            return;

    #if UNITY_EDITOR || DEBUG
        if (isDebugger && !m_RenderingDebugger)
        {
            if (!isDebuggerLogPrinted) { Debug.Log("Screen Space Global Illumination URP: Disable effect to avoid affecting rendering debugging."); isDebuggerLogPrinted = true; }
        }
        else
            isDebuggerLogPrinted = false;
    #endif

        // Per 8 steps: 1 small steps, 2 medium steps, 5 large steps
        bool lowStepCount = ssgiVolume.maxRaySteps.value <= 16;
        int groupsCount = ssgiVolume.maxRaySteps.value / 8;
        int smallSteps = lowStepCount ? 0 : Mathf.Max(groupsCount, 4);
        int mediumSteps = lowStepCount ? groupsCount + 2 : smallSteps + groupsCount * 2;

        // For high resolution: Use a lower accumulation factor to help reduce latency.
        // For low resolution: Use a higher accumulation factor to improve denoising.
        float resolutionScale = ssgiVolume.fullResolutionSS.value ? 1.0f : ssgiVolume.resolutionScaleSS.value;
        float temporalIntensity = Mathf.Lerp(ssgiVolume.denoiseIntensitySS.value + 0.02f, ssgiVolume.denoiseIntensitySS.value - 0.04f, resolutionScale);

        // TODO: Expose more settings
        m_SSGIMaterial.SetFloat(_MaxSteps, ssgiVolume.maxRaySteps.value);
        m_SSGIMaterial.SetFloat(_MaxSmallSteps, smallSteps);
        m_SSGIMaterial.SetFloat(_MaxMediumSteps, mediumSteps);
        m_SSGIMaterial.SetFloat(_StepSize, lowStepCount ? 0.5f : 0.4f);
        m_SSGIMaterial.SetFloat(_SmallStepSize, smallSteps < 4 ? 0.05f : 0.015f);
        m_SSGIMaterial.SetFloat(_MediumStepSize, lowStepCount ? 0.1f : 0.05f);
        m_SSGIMaterial.SetFloat(_Thickness, ssgiVolume.depthBufferThickness.value);
        m_SSGIMaterial.SetFloat(_Thickness_Increment, ssgiVolume.depthBufferThickness.value * 0.25f);
        m_SSGIMaterial.SetFloat(_RayCount, ssgiVolume.sampleCount.value);
        m_SSGIMaterial.SetFloat(_TemporalIntensity, temporalIntensity);
        m_SSGIMaterial.SetFloat(_ReBlurDenoiserRadius, ssgiVolume.denoiserRadiusSS.value * 2.0f * k_BlurMaxRadius); // Optimized for roughness = 1.0
        m_SSGIMaterial.SetFloat(_IndirectDiffuseLightingMultiplier, ssgiVolume.indirectDiffuseLightingMultiplier.value);
        m_SSGIMaterial.SetFloat(_MaxBrightness, 7.0f);
        m_SSGIMaterial.SetFloat(_AggressiveDenoise, ssgiVolume.denoiserAlgorithmSS.value == ScreenSpaceGlobalIlluminationVolume.DenoiserAlgorithm.Aggressive ? 1.0f : 0.0f);

    #if UNITY_2023_3_OR_NEWER
        bool enableRenderingLayers = Shader.IsKeywordEnabled(_WRITE_RENDERING_LAYERS) && ssgiVolume.indirectDiffuseRenderingLayers.value.value != 0xFFFF;
        if (enableRenderingLayers)
        {
            m_SSGIMaterial.EnableKeyword(_USE_RENDERING_LAYERS);
            m_SSGIMaterial.SetInteger(_IndirectDiffuseRenderingLayers, (int)ssgiVolume.indirectDiffuseRenderingLayers.value.value);
        }
        else
        m_SSGIMaterial.DisableKeyword(_USE_RENDERING_LAYERS);
    #else
        bool enableRenderingLayers = false;
        m_SSGIMaterial.DisableKeyword(_USE_RENDERING_LAYERS);
    #endif

        m_SSGIPass.ssgiVolume = ssgiVolume;
        m_SSGIPass.enableRenderingLayers = enableRenderingLayers;
        m_SSGIPass.overrideAmbientLighting = m_OverrideAmbientLighting;

        bool skyFallback = ssgiVolume.IsFallbackSky();
        if (skyFallback) { m_SSGIMaterial.EnableKeyword(_RAYMARCHING_FALLBACK_SKY); }
        else { m_SSGIMaterial.DisableKeyword(_RAYMARCHING_FALLBACK_SKY); }

    #if UNITY_2023_1_OR_NEWER
        bool outputAPVLighting = m_OverrideAmbientLighting && skyFallback && (Shader.IsKeywordEnabled(PROBE_VOLUMES_L1) || Shader.IsKeywordEnabled(PROBE_VOLUMES_L2));
        if (outputAPVLighting) { m_SSGIMaterial.EnableKeyword(_APV_LIGHTING_BUFFER); }
        else { m_SSGIMaterial.DisableKeyword(_APV_LIGHTING_BUFFER); }
        m_SSGIPass.outputAPVLighting = outputAPVLighting;
    #else
        // APV is not supported on URP 14
        m_SSGIPass.outputAPVLighting = false;
        m_SSGIMaterial.DisableKeyword(_APV_LIGHTING_BUFFER);
    #endif
        bool reflectionProbesFallback = ssgiVolume.IsFallbackReflectionProbes();
        if (reflectionProbesFallback){ m_SSGIMaterial.EnableKeyword(_RAYMARCHING_FALLBACK_REFLECTION_PROBES); }
        else { m_SSGIMaterial.DisableKeyword(_RAYMARCHING_FALLBACK_REFLECTION_PROBES); }
        
    #if UNITY_6000_1_OR_NEWER
        bool hasProbeAtlas = Shader.IsKeywordEnabled(_CLUSTER_LIGHT_LOOP) && Shader.IsKeywordEnabled(_REFLECTION_PROBE_ATLAS);
    #else
        bool hasProbeAtlas = Shader.IsKeywordEnabled(_FORWARD_PLUS);
    #endif
        if (hasProbeAtlas && reflectionProbesFallback) { m_SSGIMaterial.EnableKeyword(_FP_REFL_PROBE_ATLAS); } // TODO: change to URP's keyword
        else { m_SSGIMaterial.DisableKeyword(_FP_REFL_PROBE_ATLAS); }
        m_SSGIPass.hasProbeAtlas = hasProbeAtlas;

        bool isReflectionProbe = renderingData.cameraData.camera.cameraType == CameraType.Reflection;
        m_SSGIMaterial.SetFloat(_IsProbeCamera, isReflectionProbe ? 1.0f : 0.0f);

        if (m_HighQualityUpscaling)
            m_SSGIMaterial.EnableKeyword(_DEPTH_NORMALS_UPSCALE);
        else
            m_SSGIMaterial.DisableKeyword(_DEPTH_NORMALS_UPSCALE);

    #if UNITY_EDITOR
        // [Editor Only] Motion vectors in scene view don't get updated each frame when not entering play mode.
        // So we manually set them in a pass before rendering motion vectors
        if (renderingData.cameraData.camera.cameraType == CameraType.SceneView)
        {
            m_PreRenderSSGIPass.m_SSGIMaterial = m_SSGIMaterial;
            renderer.EnqueuePass(m_PreRenderSSGIPass);
        }
            
    #endif

        if (renderingData.cameraData.camera.cameraType != CameraType.Preview && (!isDebugger || m_RenderingDebugger))
            renderer.EnqueuePass(m_SSGIPass);

        // For Unity 6.1+:
        // TODO: the following code will cause issues when using "Deferred+" and disabling Render Graph (URP will fall back to "Forward+")
        // Solution: when using "Deferred+" & "RG Compatibility Mode", we should enqueue the Forward GBuffer pass

        // If GBuffer exists, URP is in Deferred path. (Actual rendering mode can be different from settings, such as URP forces Forward on OpenGL)
        bool isUsingDeferred = gBufferFieldInfo.GetValue(renderer) != null;
        // OpenGL won't use deferred path.
        isUsingDeferred &= (SystemInfo.graphicsDeviceType != GraphicsDeviceType.OpenGLES3) & (SystemInfo.graphicsDeviceType != GraphicsDeviceType.OpenGLCore);  // GLES 2 is deprecated.

        bool renderBackfaceData = ssgiVolume.thicknessMode.value != ScreenSpaceGlobalIlluminationVolume.ThicknessMode.Constant;
        if (renderBackfaceData)
        {
            // Backface lighting is only supported on Forward(+) rendering path.
            bool supportBackfaceLighting = m_BackfaceLighting && !isUsingDeferred;
            m_BackfaceDataPass.backfaceLighting = supportBackfaceLighting;

            renderer.EnqueuePass(m_BackfaceDataPass);

            m_SSGIMaterial.EnableKeyword(_BACKFACE_TEXTURES);
            Shader.EnableKeyword(SSGI_RENDER_BACKFACE_DEPTH);
            if (supportBackfaceLighting)
            {
                m_SSGIMaterial.SetFloat(_BackDepthEnabled, 2.0f); // Depth + Color
                Shader.EnableKeyword(SSGI_RENDER_BACKFACE_COLOR);
            }
            else
            {
                m_SSGIMaterial.SetFloat(_BackDepthEnabled, 1.0f); // Depth
                Shader.DisableKeyword(SSGI_RENDER_BACKFACE_COLOR);
            }
        }
        else
        {
            m_SSGIMaterial.DisableKeyword(_BACKFACE_TEXTURES);
            Shader.DisableKeyword(SSGI_RENDER_BACKFACE_DEPTH);
            Shader.DisableKeyword(SSGI_RENDER_BACKFACE_COLOR);
            m_SSGIMaterial.SetFloat(_BackDepthEnabled, 0.0f);
        }

    #if UNITY_EDITOR || DEBUG
        if (m_BackfaceLighting && isUsingDeferred)
        {
            if (!isBackfaceLightingLogPrinted) { Debug.LogError("Screen Space Global Illumination URP: Backface Lighting is only supported on Forward(+) rendering path."); isBackfaceLightingLogPrinted = true; }
        }
        else
            isBackfaceLightingLogPrinted = false;
    #endif

        // Render Forward GBuffer pass if the current device supports MRT.
        // Assuming the current device supports at least 4 MRTs since we require Unity shader model 3.5
        if (!isUsingDeferred)
        {
            renderer.EnqueuePass(m_ForwardGBufferPass);
            Shader.EnableKeyword(SSGI_RENDER_GBUFFER);
        }
        else
        {
            Shader.DisableKeyword(SSGI_RENDER_GBUFFER);
        }
    }
    public class PreRenderScreenSpaceGlobalIlluminationPass : ScriptableRenderPass
    {
        /// Motion vectors may not render correctly in the scene view
        /// This pass is used to "fix" camera motion vectors to improve scene view denoising

        private const string m_ProfilerTag = "Prepare Screen Space Global Illumination";
        private readonly ProfilingSampler m_ProfilingSampler = new ProfilingSampler(m_ProfilerTag);

        public Material m_SSGIMaterial;

        private Matrix4x4 camVPMatrix;
        private Matrix4x4 prevCamVPMatrix;

        // This pass is editor only
        const string _PrevViewProjMatrix = "_PrevViewProjMatrix";
        const string _NonJitteredViewProjMatrix = "_NonJitteredViewProjMatrix";
        const string motionColorHandleName = "m_Color";
        const string motionDepthHandleName = "m_Depth";
        public PreRenderScreenSpaceGlobalIlluminationPass() { }

        #region Non Render Graph Pass
    #if UNITY_6000_0_OR_NEWER
        [Obsolete]
    #endif
        public override void Execute(ScriptableRenderContext context, ref RenderingData renderingData)
        {
            CommandBuffer cmd = CommandBufferPool.Get();
            // Fix scene view motion vectors
            using (new ProfilingScope(cmd, m_ProfilingSampler))
            {
                cmd.SetGlobalMatrix(_PrevViewProjMatrix, prevCamVPMatrix);
                cmd.SetGlobalMatrix(_NonJitteredViewProjMatrix, camVPMatrix);
                prevCamVPMatrix = camVPMatrix;
                var motionVectorPass = motionVectorPassFieldInfo.GetValue(renderingData.cameraData.renderer);
                if (motionVectorPass != null)
                {
                    FieldInfo colorFieldInfo = motionVectorPass.GetType().GetField(motionColorHandleName, BindingFlags.NonPublic | BindingFlags.Instance);
                    FieldInfo depthFieldInfo = motionVectorPass.GetType().GetField(motionDepthHandleName, BindingFlags.NonPublic | BindingFlags.Instance);
                    if (colorFieldInfo != null && depthFieldInfo != null)
                    {
                        if (colorFieldInfo.GetValue(motionVectorPass) is RTHandle motionColorHandle && depthFieldInfo.GetValue(motionVectorPass) is RTHandle motionDepthHandle)
                        {
                            cmd.SetRenderTarget(motionColorHandle, motionDepthHandle);
                            Blitter.BlitTexture(cmd, motionColorHandle, m_ScaleBias, m_SSGIMaterial, pass: 7);
                        }
                    }
                }
            }

            context.ExecuteCommandBuffer(cmd);
            cmd.Clear();
            CommandBufferPool.Release(cmd);
        }

    #if UNITY_6000_0_OR_NEWER
        [Obsolete]
    #endif
        public override void OnCameraSetup(CommandBuffer cmd, ref RenderingData renderingData)
        {
            var cameraData = renderingData.cameraData;
            var camera = cameraData.camera;
            camVPMatrix = GL.GetGPUProjectionMatrix(camera.nonJitteredProjectionMatrix, true) * cameraData.GetViewMatrix();
            prevCamVPMatrix = prevCamVPMatrix == null ? camera.previousViewProjectionMatrix : prevCamVPMatrix;
        }
        #endregion

    #if UNITY_6000_0_OR_NEWER
        #region Render Graph Pass
        // This class stores the data needed by the pass, passed as parameter to the delegate function that executes the pass
        private class PassData
        {
            internal Matrix4x4 prevCamVPMatrix;
            internal Matrix4x4 camVPMatrix;
        }

        // This static method is used to execute the pass and passed as the RenderFunc delegate to the RenderGraph render pass
        static void ExecutePass(PassData data, UnsafeGraphContext context)
        {
            CommandBuffer cmd = CommandBufferHelpers.GetNativeCommandBuffer(context.cmd);

            // Fix scene view motion vectors
            cmd.SetGlobalMatrix(_PrevViewProjMatrix, data.prevCamVPMatrix);
            cmd.SetGlobalMatrix(_NonJitteredViewProjMatrix, data.camVPMatrix);
        }

        // This is where the renderGraph handle can be accessed.
        // Each ScriptableRenderPass can use the RenderGraph handle to add multiple render passes to the render graph
        public override void RecordRenderGraph(RenderGraph renderGraph, ContextContainer frameData)
        {
            // add an unsafe render pass to the render graph, specifying the name and the data type that will be passed to the ExecutePass function
            using (var builder = renderGraph.AddUnsafePass<PassData>(m_ProfilerTag, out var passData))
            {
                // UniversalResourceData contains all the texture handles used by the renderer, including the active color and depth textures
                // The active color and depth textures are the main color and depth buffers that the camera renders into
                UniversalCameraData cameraData = frameData.Get<UniversalCameraData>();

                var camera = cameraData.camera;
                camVPMatrix = GL.GetGPUProjectionMatrix(camera.nonJitteredProjectionMatrix, true) * cameraData.GetViewMatrix();
                passData.camVPMatrix = camVPMatrix;
                passData.prevCamVPMatrix = prevCamVPMatrix == null ? camera.previousViewProjectionMatrix : prevCamVPMatrix;
                prevCamVPMatrix = camVPMatrix;

                // This pass is editor only
                builder.AllowGlobalStateModification(true);
                builder.AllowPassCulling(false);

                // Assign the ExecutePass function to the render pass delegate, which will be called by the render graph when executing the pass
                builder.SetRenderFunc((PassData data, UnsafeGraphContext context) => ExecutePass(data, context));
            }
        }
        #endregion
    #endif

        #region Shared
        public void Dispose()
        {

        }
        #endregion
    }

    public class ScreenSpaceGlobalIlluminationPass : ScriptableRenderPass
    {
        private const string m_ProfilerTag = "Screen Space Global Illumination";
        private readonly ProfilingSampler m_ProfilingSampler = new ProfilingSampler(m_ProfilerTag);

        public ScreenSpaceGlobalIlluminationVolume ssgiVolume;
        public bool enableRenderingLayers;
        public bool overrideAmbientLighting;
        public bool hasProbeAtlas;
        public bool outputAPVLighting;
        public Material m_SSGIMaterial;

        private RTHandle m_IntermediateCameraColorHandle;
        private RTHandle m_DiffuseHandle;
        private RTHandle m_IntermediateDiffuseHandle;
        private RTHandle m_AccumulateSampleHandle;
        private RTHandle m_APVLightingHandle;

        // Render Graph Pass
        // Persistent RTHandles
        private RTHandle m_HistoryDepthHandle;
        private RTHandle m_HistoryCameraColorHandle;
        private RTHandle m_HistoryIndirectDiffuseHandle;
        private RTHandle m_AccumulateHistorySampleHandle;

        private readonly RenderTargetIdentifier[] rTHandles = new RenderTargetIdentifier[2];

        private bool isHistoryTextureValid;
        private bool enableDenoise;
        private int frameCount = 0;
        private float resolutionScale = 1.0f;

        public static readonly float[] k_PreBlurRands = new float[] { 0.840188f, 0.394383f, 0.783099f, 0.79844f, 0.911647f, 0.197551f, 0.335223f, 0.76823f, 0.277775f, 0.55397f, 0.477397f, 0.628871f, 0.364784f, 0.513401f, 0.95223f, 0.916195f, 0.635712f, 0.717297f, 0.141603f, 0.606969f, 0.0163006f, 0.242887f, 0.137232f, 0.804177f, 0.156679f, 0.400944f, 0.12979f, 0.108809f, 0.998924f, 0.218257f, 0.512932f, 0.839112f };
        public static readonly float[] k_BlurRands = new float[] { 0.61264f, 0.296032f, 0.637552f, 0.524287f, 0.493583f, 0.972775f, 0.292517f, 0.771358f, 0.526745f, 0.769914f, 0.400229f, 0.891529f, 0.283315f, 0.352458f, 0.807725f, 0.919026f, 0.0697553f, 0.949327f, 0.525995f, 0.0860558f, 0.192214f, 0.663227f, 0.890233f, 0.348893f, 0.0641713f, 0.020023f, 0.457702f, 0.0630958f, 0.23828f, 0.970634f, 0.902208f, 0.85092f };
        public static readonly float[] k_PostBlurRands = new float[] { 0.266666f, 0.53976f, 0.375207f, 0.760249f, 0.512535f, 0.667724f, 0.531606f, 0.0392803f, 0.437638f, 0.931835f, 0.93081f, 0.720952f, 0.284293f, 0.738534f, 0.639979f, 0.354049f, 0.687861f, 0.165974f, 0.440105f, 0.880075f, 0.829201f, 0.330337f, 0.228968f, 0.893372f, 0.35036f, 0.68667f, 0.956468f, 0.58864f, 0.657304f, 0.858676f, 0.43956f, 0.92397f };

        public ScreenSpaceGlobalIlluminationPass(Material material)
        {
            m_SSGIMaterial = material;
        }

        #region Non Render Graph Pass

        // The index of current camera in the "CameraHistoryData[]"
        private int cameraHistoryIndex;

    #if UNITY_6000_0_OR_NEWER
        [Obsolete]
    #endif
        public override void Execute(ScriptableRenderContext context, ref RenderingData renderingData)
        {
            RTHandle colorHandle = renderingData.cameraData.renderer.cameraColorTargetHandle;

            // Get the persistent textures for the current camera
            var m_HistoryDepthHandle = cameraHistoryData[cameraHistoryIndex].historyDepthHandle;
            var m_HistoryCameraColorHandle = cameraHistoryData[cameraHistoryIndex].historyCameraColorHandle;
            var m_HistoryIndirectDiffuseHandle = cameraHistoryData[cameraHistoryIndex].historyIndirectDiffuseHandle;
            var m_AccumulateHistorySampleHandle = cameraHistoryData[cameraHistoryIndex].accumulateHistorySampleHandle;

            CommandBuffer cmd = CommandBufferPool.Get();
            using (new ProfilingScope(cmd, m_ProfilingSampler))
            {
                // Copy Direct Lighting
                if (overrideAmbientLighting)
                {
                    if (outputAPVLighting)
                    {
                        rTHandles[0] = m_IntermediateCameraColorHandle;
                        rTHandles[1] = m_APVLightingHandle;
                        // RT-1: direct lighting
                        // RT-2: indirect lighting (APV)
                        cmd.SetRenderTarget(rTHandles, m_IntermediateCameraColorHandle);
                        Blitter.BlitTexture(cmd, colorHandle, m_ScaleBias, m_SSGIMaterial, pass: 0);
                        m_SSGIMaterial.SetTexture(apvLightingTexture, m_APVLightingHandle);
                    }
                    else
                        Blitter.BlitCameraTexture(cmd, colorHandle, m_IntermediateCameraColorHandle, m_SSGIMaterial, pass: 0);
                }
                else
                    Blitter.BlitCameraTexture(cmd, colorHandle, m_IntermediateCameraColorHandle);

                if (enableDenoise)
                {
                    // Render SSGI
                    Blitter.BlitCameraTexture(cmd, m_IntermediateCameraColorHandle, m_IntermediateDiffuseHandle, RenderBufferLoadAction.Load, RenderBufferStoreAction.Store, m_SSGIMaterial, pass: 1);
                    m_SSGIMaterial.SetTexture(indirectDiffuseTexture, m_DiffuseHandle);

                    // Reproject GI
                    cmd.SetRenderTarget(
                            m_AccumulateSampleHandle,
                            RenderBufferLoadAction.Load,
                            RenderBufferStoreAction.Store,
                            m_AccumulateSampleHandle,
                            RenderBufferLoadAction.DontCare,
                            RenderBufferStoreAction.DontCare);

                    rTHandles[0] = m_DiffuseHandle;
                    rTHandles[1] = m_AccumulateSampleHandle;
                    // RT-1: accumulated results
                    // RT-2: accumulated sample count
                    cmd.SetRenderTarget(rTHandles, m_AccumulateSampleHandle);
                    Blitter.BlitTexture(cmd, m_IntermediateDiffuseHandle, m_ScaleBias, m_SSGIMaterial, pass: 2);


                    if (ssgiVolume.denoiserAlgorithmSS.value == ScreenSpaceGlobalIlluminationVolume.DenoiserAlgorithm.Aggressive)
                    {
                        Blitter.BlitCameraTexture(cmd, m_DiffuseHandle, m_IntermediateDiffuseHandle, m_SSGIMaterial, pass: 8);
                        Blitter.BlitCameraTexture(cmd, m_IntermediateDiffuseHandle, m_DiffuseHandle, m_SSGIMaterial, pass: 8);

                        //Blitter.BlitCameraTexture(cmd, m_DiffuseHandle, m_IntermediateDiffuseHandle, m_SSGIMaterial, pass: 8);
                        //Blitter.BlitCameraTexture(cmd, m_IntermediateDiffuseHandle, m_DiffuseHandle, m_SSGIMaterial, pass: 8);
                    }

                    if (ssgiVolume.secondDenoiserPassSS.value)
                    {
                        //Blitter.BlitCameraTexture(cmd, m_DiffuseHandle, m_IntermediateDiffuseHandle, m_SSGIMaterial, pass: 3);
                        //Blitter.BlitCameraTexture(cmd, m_IntermediateDiffuseHandle, m_DiffuseHandle, m_SSGIMaterial, pass: 3);

                        Blitter.BlitCameraTexture(cmd, m_DiffuseHandle, m_IntermediateDiffuseHandle, m_SSGIMaterial, pass: 3);
                        Blitter.BlitCameraTexture(cmd, m_IntermediateDiffuseHandle, m_DiffuseHandle, m_SSGIMaterial, pass: 4);
                    }

                    // Update History Color
                    //Blitter.BlitCameraTexture(cmd, m_DiffuseHandle, m_HistoryIndirectDiffuseHandle);
                    cmd.CopyTexture(m_DiffuseHandle, m_HistoryIndirectDiffuseHandle);

                    // Update History Depth
                    Blitter.BlitCameraTexture(cmd, m_HistoryDepthHandle, m_HistoryDepthHandle, RenderBufferLoadAction.DontCare, RenderBufferStoreAction.Store, m_SSGIMaterial, pass: 5);

                    // Update History sample count
                    //Blitter.BlitCameraTexture(cmd, m_AccumulateSampleHandle, m_AccumulateHistorySampleHandle);
                    cmd.CopyTexture(m_AccumulateSampleHandle, m_AccumulateHistorySampleHandle);
                }
                else
                {
                    // SSGI
                    Blitter.BlitCameraTexture(cmd, m_IntermediateCameraColorHandle, m_DiffuseHandle, RenderBufferLoadAction.Load, RenderBufferStoreAction.Store, m_SSGIMaterial, pass: 1);
                    m_SSGIMaterial.SetTexture(indirectDiffuseTexture, m_DiffuseHandle);

                    // Update History Depth
                    Blitter.BlitCameraTexture(cmd, m_HistoryDepthHandle, m_HistoryDepthHandle, RenderBufferLoadAction.DontCare, RenderBufferStoreAction.Store, m_SSGIMaterial, pass: 5);
                }
                
                // Combine
                Blitter.BlitCameraTexture(cmd, m_IntermediateCameraColorHandle, colorHandle, m_SSGIMaterial, pass: 6);

                // Copy History Scene Color
                Blitter.BlitCameraTexture(cmd, colorHandle, m_HistoryCameraColorHandle, RenderBufferLoadAction.DontCare, RenderBufferStoreAction.Store, m_SSGIMaterial, pass: 9);
            }
            context.ExecuteCommandBuffer(cmd);
            cmd.Clear();
            CommandBufferPool.Release(cmd);
        }

    #if UNITY_6000_0_OR_NEWER
        [Obsolete]
    #endif
        public override void OnCameraSetup(CommandBuffer cmd, ref RenderingData renderingData)
        {
            var visibleReflectionProbes = renderingData.cullResults.visibleReflectionProbes;
            var camera = renderingData.cameraData.camera;
            bool isReflectionCamera = camera.cameraType == CameraType.Reflection;

            int currentCameraHash = camera.GetHashCode();
            cameraHistoryIndex = GetCameraHistoryDataIndex(currentCameraHash);

            if (!hasProbeAtlas)
                UpdateReflectionProbe(visibleReflectionProbes, camera.transform.position);
            else
                m_SSGIMaterial.SetFloat(probeSet, 0.0f);

            m_SSGIMaterial.SetFloat(frameIndex, frameCount);
            m_SSGIMaterial.SetVector(_ReBlurBlurRotator, EvaluateRotator(k_BlurRands[frameCount % 32]));
            frameCount += 33;
            frameCount %= 64000;

            int width = (int)(camera.scaledPixelWidth * renderingData.cameraData.renderScale);
            int height = (int)(camera.scaledPixelHeight * renderingData.cameraData.renderScale);

            bool denoiseStateChanged = ssgiVolume.denoiseSS.value != enableDenoise;
            bool resolutionStateChanged = ssgiVolume.fullResolutionSS.value ? resolutionScale != 1.0f : ssgiVolume.resolutionScaleSS.value != resolutionScale;
            bool cameraHasChanged = cameraHistoryIndex == -1;

            // Reorder the history data array when camera is new
            UpdateCameraHistoryData(cameraHasChanged);
            // Assign the data to index 0 for the new camera
            cameraHistoryIndex = cameraHasChanged ? 0 : cameraHistoryIndex;

            ref var m_HistoryDepthHandle = ref cameraHistoryData[cameraHistoryIndex].historyDepthHandle;
            ref var m_HistoryCameraColorHandle = ref cameraHistoryData[cameraHistoryIndex].historyCameraColorHandle;
            ref var m_HistoryIndirectDiffuseHandle = ref cameraHistoryData[cameraHistoryIndex].historyIndirectDiffuseHandle;
            ref var m_AccumulateHistorySampleHandle = ref cameraHistoryData[cameraHistoryIndex].accumulateHistorySampleHandle;

            ref var prevCamInvVPMatrix = ref cameraHistoryData[cameraHistoryIndex].prevCamInvVPMatrix;
            ref var prevCameraPositionWS = ref cameraHistoryData[cameraHistoryIndex].prevCameraPositionWS;
            ref var historyCameraHash = ref cameraHistoryData[cameraHistoryIndex].hash;

            if (prevCamInvVPMatrix != null)
                m_SSGIMaterial.SetMatrix(_PrevInvViewProjMatrix, prevCamInvVPMatrix);
            else
                m_SSGIMaterial.SetMatrix(_PrevInvViewProjMatrix, camera.previousViewProjectionMatrix.inverse);

            if (prevCameraPositionWS != null)
                m_SSGIMaterial.SetVector(_PrevCameraPositionWS, prevCameraPositionWS);
            else
                m_SSGIMaterial.SetVector(_PrevCameraPositionWS, camera.transform.position);

            prevCamInvVPMatrix = (GL.GetGPUProjectionMatrix(camera.projectionMatrix, true) * renderingData.cameraData.GetViewMatrix()).inverse;
            prevCameraPositionWS = camera.transform.position;
            historyCameraHash = currentCameraHash;

            // The spread angle is used to compute the world space pixel footprint during denoising.
            // We use low FOV for orthographic cameras as a temporary solution.
            float fieldOfView = camera.orthographic ? 1.0f : camera.fieldOfView;
            m_SSGIMaterial.SetFloat(_PixelSpreadAngleTangent, Mathf.Tan(fieldOfView * Mathf.Deg2Rad * 0.5f) * 2.0f / Mathf.Min(Mathf.FloorToInt(camera.scaledPixelWidth * resolutionScale), Mathf.FloorToInt(camera.scaledPixelHeight * resolutionScale)));

            ref float historyCameraScaledWidth = ref cameraHistoryData[cameraHistoryIndex].scaledWidth;
            ref float historyCameraScaledHeight = ref cameraHistoryData[cameraHistoryIndex].scaledHeight;

            resolutionStateChanged |= (historyCameraScaledWidth != width) || (historyCameraScaledHeight != height);
            if (!cameraHasChanged && (denoiseStateChanged || resolutionStateChanged))
                isHistoryTextureValid = false;

            historyCameraScaledWidth = width;
            historyCameraScaledHeight = height;

            resolutionScale = ssgiVolume.fullResolutionSS.value ? 1.0f : ssgiVolume.resolutionScaleSS.value;
            m_SSGIMaterial.SetFloat(downSample, resolutionScale);

            enableDenoise = ssgiVolume.denoiseSS.value;

            if (overrideAmbientLighting)
            {
                SphericalHarmonicsL2 ambientProbe = RenderSettings.ambientProbe;

                m_SSGIMaterial.SetVector(shAr, new Vector4(ambientProbe[0, 3], ambientProbe[0, 1], ambientProbe[0, 2], ambientProbe[0, 0] - ambientProbe[0, 6]));
                m_SSGIMaterial.SetVector(shAg, new Vector4(ambientProbe[1, 3], ambientProbe[1, 1], ambientProbe[1, 2], ambientProbe[1, 0] - ambientProbe[1, 6]));
                m_SSGIMaterial.SetVector(shAb, new Vector4(ambientProbe[2, 3], ambientProbe[2, 1], ambientProbe[2, 2], ambientProbe[2, 0] - ambientProbe[2, 6]));
                m_SSGIMaterial.SetVector(shBr, new Vector4(ambientProbe[0, 4], ambientProbe[0, 5], ambientProbe[0, 6] * 3, ambientProbe[0, 7]));
                m_SSGIMaterial.SetVector(shBg, new Vector4(ambientProbe[1, 4], ambientProbe[1, 5], ambientProbe[1, 6] * 3, ambientProbe[1, 7]));
                m_SSGIMaterial.SetVector(shBb, new Vector4(ambientProbe[2, 4], ambientProbe[2, 5], ambientProbe[2, 6] * 3, ambientProbe[2, 7]));
                m_SSGIMaterial.SetVector(shC, new Vector4(ambientProbe[0, 8], ambientProbe[1, 8], ambientProbe[2, 8], 1));
            }

            RenderTextureDescriptor desc = renderingData.cameraData.cameraTargetDescriptor;
            if (desc.width != width)
                desc = new RenderTextureDescriptor(width, height);
            desc.graphicsFormat = GraphicsFormat.B10G11R11_UFloatPack32;
            desc.depthBufferBits = 0; // Color and depth cannot be combined in RTHandles
            desc.stencilFormat = GraphicsFormat.None;
            desc.depthStencilFormat = GraphicsFormat.None;
            desc.msaaSamples = 1;
            desc.bindMS = false;
            RenderTextureDescriptor depthDesc = desc;

        #if UNITY_6000_0_OR_NEWER
            RenderingUtils.ReAllocateHandleIfNeeded(ref m_IntermediateCameraColorHandle, desc, FilterMode.Point, TextureWrapMode.Clamp, name: _IntermediateCameraColorTexture);
        #else
            RenderingUtils.ReAllocateIfNeeded(ref m_IntermediateCameraColorHandle, desc, FilterMode.Point, TextureWrapMode.Clamp, name: _IntermediateCameraColorTexture);
        #endif

        #if UNITY_6000_0_OR_NEWER
            RenderingUtils.ReAllocateHandleIfNeeded(ref m_APVLightingHandle, desc, FilterMode.Point, TextureWrapMode.Clamp, name: _APVLightingTexture);
        #else
            RenderingUtils.ReAllocateIfNeeded(ref m_APVLightingHandle, desc, FilterMode.Point, TextureWrapMode.Clamp, name: _APVLightingTexture);
        #endif

            desc.width = Mathf.FloorToInt(desc.width * resolutionScale);
            desc.height = Mathf.FloorToInt(desc.height * resolutionScale);

        #if UNITY_6000_0_OR_NEWER
            RenderingUtils.ReAllocateHandleIfNeeded(ref m_HistoryCameraColorHandle, desc, FilterMode.Point, TextureWrapMode.Clamp, name: _SSGIHistoryCameraColorTexture);
        #else
            RenderingUtils.ReAllocateIfNeeded(ref m_HistoryCameraColorHandle, desc, FilterMode.Point, TextureWrapMode.Clamp, name: _SSGIHistoryCameraColorTexture);
        #endif
            
            // Avoid reprojecting from uninitialized history texture
            if (isHistoryTextureValid)
            {
                m_SSGIMaterial.SetFloat(_HistoryTextureValid, 1.0f);
                m_SSGIMaterial.SetTexture(ssgiHistoryCameraColorTexture, m_HistoryCameraColorHandle);
            }
            else
            {
                m_SSGIMaterial.SetFloat(_HistoryTextureValid, 0.0f);
                m_SSGIMaterial.SetTexture(ssgiHistoryCameraColorTexture, m_IntermediateCameraColorHandle);
                isHistoryTextureValid = true;
            }

            desc.graphicsFormat = GraphicsFormat.R16G16B16A16_SFloat;
            depthDesc.graphicsFormat = GraphicsFormat.R32_SFloat;

        #if UNITY_6000_0_OR_NEWER
            RenderingUtils.ReAllocateHandleIfNeeded(ref m_DiffuseHandle, desc, FilterMode.Point, TextureWrapMode.Clamp, name: _IndirectDiffuseTexture);
            RenderingUtils.ReAllocateHandleIfNeeded(ref m_IntermediateDiffuseHandle, desc, FilterMode.Point, TextureWrapMode.Clamp, name: _IntermediateIndirectDiffuseTexture);
            RenderingUtils.ReAllocateHandleIfNeeded(ref m_HistoryIndirectDiffuseHandle, desc, FilterMode.Point, TextureWrapMode.Clamp, name: _HistoryIndirectDiffuseTexture);
            RenderingUtils.ReAllocateHandleIfNeeded(ref m_HistoryDepthHandle, depthDesc, FilterMode.Point, TextureWrapMode.Clamp, name: _SSGIHistoryDepthTexture);
        #else
            RenderingUtils.ReAllocateIfNeeded(ref m_DiffuseHandle, desc, FilterMode.Point, TextureWrapMode.Clamp, name: _IndirectDiffuseTexture);
            RenderingUtils.ReAllocateIfNeeded(ref m_IntermediateDiffuseHandle, desc, FilterMode.Point, TextureWrapMode.Clamp, name: _IntermediateIndirectDiffuseTexture);
            RenderingUtils.ReAllocateIfNeeded(ref m_HistoryIndirectDiffuseHandle, desc, FilterMode.Point, TextureWrapMode.Clamp, name: _HistoryIndirectDiffuseTexture);
            RenderingUtils.ReAllocateIfNeeded(ref m_HistoryDepthHandle, depthDesc, FilterMode.Point, TextureWrapMode.Clamp, name: _SSGIHistoryDepthTexture);
        #endif

            desc.graphicsFormat = GraphicsFormat.R16_SFloat;

        #if UNITY_6000_0_OR_NEWER
            RenderingUtils.ReAllocateHandleIfNeeded(ref m_AccumulateSampleHandle, desc, FilterMode.Point, TextureWrapMode.Clamp, name: _SSGISampleTexture);
            RenderingUtils.ReAllocateHandleIfNeeded(ref m_AccumulateHistorySampleHandle, desc, FilterMode.Point, TextureWrapMode.Clamp, name: _SSGIHistorySampleTexture);
        #else
            RenderingUtils.ReAllocateIfNeeded(ref m_AccumulateSampleHandle, desc, FilterMode.Point, TextureWrapMode.Clamp, name: _SSGISampleTexture);
            RenderingUtils.ReAllocateIfNeeded(ref m_AccumulateHistorySampleHandle, desc, FilterMode.Point, TextureWrapMode.Clamp, name: _SSGIHistorySampleTexture);
        #endif

            m_SSGIMaterial.SetTexture(ssgiHistoryDepthTexture, m_HistoryDepthHandle);
            m_SSGIMaterial.SetTexture(historyIndirectDiffuseTexture, m_HistoryIndirectDiffuseHandle);
            m_SSGIMaterial.SetTexture(ssgiSampleTexture, m_AccumulateSampleHandle);
            m_SSGIMaterial.SetTexture(ssgiHistorySampleTexture, m_AccumulateHistorySampleHandle);

            ConfigureInput(ScriptableRenderPassInput.Depth | ScriptableRenderPassInput.Motion);
        }
        #endregion

    #if UNITY_6000_0_OR_NEWER
        #region Render Graph Pass
        // This class stores the data needed by the pass, passed as parameter to the delegate function that executes the pass
        private class PassData
        {
            internal Material ssgiMaterial;

            internal RenderTargetIdentifier[] rTHandles;

            // Camera color & direct lighting color
            internal TextureHandle cameraColorTargetHandle;
            internal TextureHandle cameraDepthTextureHandle;

            internal TextureHandle intermediateCameraColorHandle;
            internal TextureHandle historyCameraColorHandle;
            internal TextureHandle apvLightingHandle;

            // SSGI diffuse lighting
            internal TextureHandle diffuseHandle;
            internal TextureHandle intermediateDiffuseHandle;

            // Denoising
            internal TextureHandle historyDiffuseHandle;
            internal TextureHandle historyDepthHandle;
            internal TextureHandle accumulateSampleHandle;
            internal TextureHandle accumulateHistorySampleHandle;

            // GBuffers created by URP
            internal bool localGBuffers;
            internal TextureHandle gBuffer0Handle;
            internal TextureHandle gBuffer1Handle;
            internal TextureHandle gBuffer2Handle;

            internal bool denoise;
            internal bool secondDenoise;
            internal bool aggressiveDenoise;
            internal bool overrideAmbientLighting;
            internal bool outputAPVLighting;
        }

        // This static method is used to execute the pass and passed as the RenderFunc delegate to the RenderGraph render pass
        static void ExecutePass(PassData data, UnsafeGraphContext context)
        {
            CommandBuffer cmd = CommandBufferHelpers.GetNativeCommandBuffer(context.cmd);

            data.ssgiMaterial.SetTexture(cameraDepthTexture, data.cameraDepthTextureHandle);

            if (data.localGBuffers)
            {
                data.ssgiMaterial.SetTexture(gBuffer0, data.gBuffer0Handle);
                data.ssgiMaterial.SetTexture(gBuffer1, data.gBuffer1Handle);
                data.ssgiMaterial.SetTexture(gBuffer2, data.gBuffer2Handle);
            }
            else
            {
                // Global gbuffer textures
                data.ssgiMaterial.SetTexture(gBuffer0, null);
                data.ssgiMaterial.SetTexture(gBuffer1, null);
                data.ssgiMaterial.SetTexture(gBuffer2, null);
            }

            // Copy Direct Lighting
            if (data.overrideAmbientLighting)
            {
                if (data.outputAPVLighting)
                {
                    data.rTHandles[0] = data.intermediateCameraColorHandle;
                    data.rTHandles[1] = data.apvLightingHandle;
                    // RT-1: direct lighting
                    // RT-2: indirect lighting (APV)
                    cmd.SetRenderTarget(data.rTHandles, data.intermediateCameraColorHandle);
                    Blitter.BlitTexture(cmd, data.cameraColorTargetHandle, m_ScaleBias, data.ssgiMaterial, pass: 0);
                    data.ssgiMaterial.SetTexture(apvLightingTexture, data.apvLightingHandle);
                }
                else
                    Blitter.BlitCameraTexture(cmd, data.cameraColorTargetHandle, data.intermediateCameraColorHandle, data.ssgiMaterial, pass: 0);
            }
            else
                Blitter.BlitCameraTexture(cmd, data.cameraColorTargetHandle, data.intermediateCameraColorHandle);


            if (data.denoise)
            {
                // Render SSGI
                Blitter.BlitCameraTexture(cmd, data.intermediateCameraColorHandle, data.intermediateDiffuseHandle, RenderBufferLoadAction.Load, RenderBufferStoreAction.Store, data.ssgiMaterial, pass: 1);
                data.ssgiMaterial.SetTexture(indirectDiffuseTexture, data.diffuseHandle);

                // Reproject GI
                cmd.SetRenderTarget(
                        data.accumulateSampleHandle,
                        RenderBufferLoadAction.Load,
                        RenderBufferStoreAction.Store,
                        data.accumulateSampleHandle,
                        RenderBufferLoadAction.DontCare,
                        RenderBufferStoreAction.DontCare);

                data.rTHandles[0] = data.diffuseHandle;
                data.rTHandles[1] = data.accumulateSampleHandle;
                // RT-1: accumulated results
                // RT-2: accumulated sample count
                cmd.SetRenderTarget(data.rTHandles, data.accumulateSampleHandle);
                Blitter.BlitTexture(cmd, data.intermediateDiffuseHandle, m_ScaleBias, data.ssgiMaterial, pass: 2);


                if (data.aggressiveDenoise)
                {
                    Blitter.BlitCameraTexture(cmd, data.diffuseHandle, data.intermediateDiffuseHandle, data.ssgiMaterial, pass: 8);
                    Blitter.BlitCameraTexture(cmd, data.intermediateDiffuseHandle, data.diffuseHandle, data.ssgiMaterial, pass: 8);

                    //Blitter.BlitCameraTexture(cmd, data.diffuseHandle, data.intermediateDiffuseHandle, data.ssgiMaterial, pass: 8);
                    //Blitter.BlitCameraTexture(cmd, data.intermediateDiffuseHandle, data.diffuseHandle, data.ssgiMaterial, pass: 8);
                }

                if (data.secondDenoise)
                {
                    //Blitter.BlitCameraTexture(cmd, data.diffuseHandle, data.intermediateDiffuseHandle, data.ssgiMaterial, pass: 3);
                    //Blitter.BlitCameraTexture(cmd, data.intermediateDiffuseHandle, data.diffuseHandle, data.ssgiMaterial, pass: 3);

                    Blitter.BlitCameraTexture(cmd, data.diffuseHandle, data.intermediateDiffuseHandle, data.ssgiMaterial, pass: 3);
                    Blitter.BlitCameraTexture(cmd, data.intermediateDiffuseHandle, data.diffuseHandle, data.ssgiMaterial, pass: 4);
                }

                // Update History Color
                //Blitter.BlitCameraTexture(cmd, data.diffuseHandle, data.historyDiffuseHandle);
                cmd.CopyTexture(data.diffuseHandle, data.historyDiffuseHandle);

                // Update History Depth
                Blitter.BlitCameraTexture(cmd, data.historyDepthHandle, data.historyDepthHandle, data.ssgiMaterial, pass: 5);

                // Update History sample count
                //Blitter.BlitCameraTexture(cmd, data.accumulateSampleHandle, data.accumulateHistorySampleHandle);
                cmd.CopyTexture(data.accumulateSampleHandle, data.accumulateHistorySampleHandle);
            }
            else
            {
                // SSGI
                Blitter.BlitCameraTexture(cmd, data.intermediateCameraColorHandle, data.diffuseHandle, RenderBufferLoadAction.Load, RenderBufferStoreAction.Store, data.ssgiMaterial, pass: 1);
                data.ssgiMaterial.SetTexture(indirectDiffuseTexture, data.diffuseHandle);

                // Update History Depth
                Blitter.BlitCameraTexture(cmd, data.historyDepthHandle, data.historyDepthHandle, data.ssgiMaterial, pass: 5);
            }

            // Combine
            Blitter.BlitCameraTexture(cmd, data.intermediateCameraColorHandle, data.cameraColorTargetHandle, data.ssgiMaterial, pass: 6);

            // Copy History Scene Color
            Blitter.BlitCameraTexture(cmd, data.cameraColorTargetHandle, data.historyCameraColorHandle, RenderBufferLoadAction.DontCare, RenderBufferStoreAction.Store, data.ssgiMaterial, pass: 9);
        }

        // This is where the renderGraph handle can be accessed.
        // Each ScriptableRenderPass can use the RenderGraph handle to add multiple render passes to the render graph
        public override void RecordRenderGraph(RenderGraph renderGraph, ContextContainer frameData)
        {
            // add an unsafe render pass to the render graph, specifying the name and the data type that will be passed to the ExecutePass function
            using (var builder = renderGraph.AddUnsafePass<PassData>(m_ProfilerTag, out var passData))
            {
                // UniversalResourceData contains all the texture handles used by the renderer, including the active color and depth textures
                // The active color and depth textures are the main color and depth buffers that the camera renders into
                UniversalResourceData resourceData = frameData.Get<UniversalResourceData>();
                UniversalCameraData cameraData = frameData.Get<UniversalCameraData>();
                UniversalLightData lightData = frameData.Get<UniversalLightData>();
                UniversalRenderingData renderingData = frameData.Get<UniversalRenderingData>();

                var visibleReflectionProbes = renderingData.cullResults.visibleReflectionProbes;
                var camera = cameraData.camera;
                bool isReflectionCamera = camera.cameraType == CameraType.Reflection;

                int currentCameraHash = camera.GetHashCode();
                int cameraHistoryIndex = GetCameraHistoryDataIndex(currentCameraHash);

                if (!hasProbeAtlas)
                    UpdateReflectionProbe(visibleReflectionProbes, camera.transform.position);
                else
                    m_SSGIMaterial.SetFloat(probeSet, 0.0f);

                m_SSGIMaterial.SetFloat(frameIndex, frameCount);
                m_SSGIMaterial.SetVector(_ReBlurBlurRotator, EvaluateRotator(k_BlurRands[frameCount % 32]));
                frameCount += 33;
                frameCount %= 64000;

                int width = (int)(camera.scaledPixelWidth * cameraData.renderScale);
                int height = (int)(camera.scaledPixelHeight * cameraData.renderScale);

                bool denoiseStateChanged = ssgiVolume.denoiseSS.value != enableDenoise;
                bool resolutionStateChanged = ssgiVolume.fullResolutionSS.value ? resolutionScale != 1.0f : ssgiVolume.resolutionScaleSS.value != resolutionScale;
                bool cameraHasChanged = cameraHistoryIndex == -1;

                // Reorder the history data array when camera is new
                UpdateCameraHistoryData(cameraHasChanged);
                // Assign the data to index 0 for the new camera
                cameraHistoryIndex = cameraHasChanged ? 0 : cameraHistoryIndex;

                ref var prevCamInvVPMatrix = ref cameraHistoryData[cameraHistoryIndex].prevCamInvVPMatrix;
                ref var prevCameraPositionWS = ref cameraHistoryData[cameraHistoryIndex].prevCameraPositionWS;
                ref var historyCameraHash = ref cameraHistoryData[cameraHistoryIndex].hash;

                if (prevCamInvVPMatrix != null)
                    m_SSGIMaterial.SetMatrix(_PrevInvViewProjMatrix, prevCamInvVPMatrix);
                else
                    m_SSGIMaterial.SetMatrix(_PrevInvViewProjMatrix, camera.previousViewProjectionMatrix.inverse);

                if (prevCameraPositionWS != null)
                    m_SSGIMaterial.SetVector(_PrevCameraPositionWS, prevCameraPositionWS);
                else
                    m_SSGIMaterial.SetVector(_PrevCameraPositionWS, camera.transform.position);

                prevCamInvVPMatrix = (GL.GetGPUProjectionMatrix(camera.projectionMatrix, true) * cameraData.GetViewMatrix()).inverse;
                prevCameraPositionWS = camera.transform.position;
                historyCameraHash = currentCameraHash;

                // The spread angle is used to compute the world space pixel footprint during denoising.
                // We use low FOV for orthographic cameras as a temporary solution.
                float fieldOfView = camera.orthographic ? 1.0f : camera.fieldOfView;
                m_SSGIMaterial.SetFloat(_PixelSpreadAngleTangent, Mathf.Tan(fieldOfView * Mathf.Deg2Rad * 0.5f) * 2.0f / Mathf.Min(Mathf.FloorToInt(camera.scaledPixelWidth * resolutionScale), Mathf.FloorToInt(camera.scaledPixelHeight * resolutionScale)));

                ref float historyCameraScaledWidth = ref cameraHistoryData[cameraHistoryIndex].scaledWidth;
                ref float historyCameraScaledHeight = ref cameraHistoryData[cameraHistoryIndex].scaledHeight;

                resolutionStateChanged |= (historyCameraScaledWidth != width) || (historyCameraScaledHeight != height);
                if (!cameraHasChanged && (denoiseStateChanged || resolutionStateChanged))
                    isHistoryTextureValid = false;

                historyCameraScaledWidth = width;
                historyCameraScaledHeight = height;

                resolutionScale = ssgiVolume.fullResolutionSS.value ? 1.0f : ssgiVolume.resolutionScaleSS.value;
                m_SSGIMaterial.SetFloat(downSample, resolutionScale);

                enableDenoise = ssgiVolume.denoiseSS.value;

                passData.denoise = enableDenoise;
                passData.secondDenoise = ssgiVolume.secondDenoiserPassSS.value;
                passData.aggressiveDenoise = ssgiVolume.denoiserAlgorithmSS.value == ScreenSpaceGlobalIlluminationVolume.DenoiserAlgorithm.Aggressive;
                passData.overrideAmbientLighting = overrideAmbientLighting;
                passData.outputAPVLighting = outputAPVLighting;

                if (overrideAmbientLighting)
                {
                    SphericalHarmonicsL2 ambientProbe = RenderSettings.ambientProbe;

                    m_SSGIMaterial.SetVector(shAr, new Vector4(ambientProbe[0, 3], ambientProbe[0, 1], ambientProbe[0, 2], ambientProbe[0, 0] - ambientProbe[0, 6]));
                    m_SSGIMaterial.SetVector(shAg, new Vector4(ambientProbe[1, 3], ambientProbe[1, 1], ambientProbe[1, 2], ambientProbe[1, 0] - ambientProbe[1, 6]));
                    m_SSGIMaterial.SetVector(shAb, new Vector4(ambientProbe[2, 3], ambientProbe[2, 1], ambientProbe[2, 2], ambientProbe[2, 0] - ambientProbe[2, 6]));
                    m_SSGIMaterial.SetVector(shBr, new Vector4(ambientProbe[0, 4], ambientProbe[0, 5], ambientProbe[0, 6] * 3, ambientProbe[0, 7]));
                    m_SSGIMaterial.SetVector(shBg, new Vector4(ambientProbe[1, 4], ambientProbe[1, 5], ambientProbe[1, 6] * 3, ambientProbe[1, 7]));
                    m_SSGIMaterial.SetVector(shBb, new Vector4(ambientProbe[2, 4], ambientProbe[2, 5], ambientProbe[2, 6] * 3, ambientProbe[2, 7]));
                    m_SSGIMaterial.SetVector(shC, new Vector4(ambientProbe[0, 8], ambientProbe[1, 8], ambientProbe[2, 8], 1));
                }

                RenderTextureDescriptor desc = cameraData.cameraTargetDescriptor;
                desc.graphicsFormat = GraphicsFormat.B10G11R11_UFloatPack32;
                desc.depthBufferBits = 0; // Color and depth cannot be combined in RTHandles
                desc.stencilFormat = GraphicsFormat.None;
                desc.msaaSamples = 1;
                desc.bindMS = false;

                TextureHandle intermediateCameraColorHandle = UniversalRenderer.CreateRenderGraphTexture(renderGraph, desc, name: _IntermediateCameraColorTexture, false, FilterMode.Point, TextureWrapMode.Clamp);
                TextureHandle apvLightingHandle = UniversalRenderer.CreateRenderGraphTexture(renderGraph, desc, name: _APVLightingTexture, false, FilterMode.Point, TextureWrapMode.Clamp);
                RenderTextureDescriptor depthDesc = desc;

                desc.width = Mathf.FloorToInt(desc.width * resolutionScale);
                desc.height = Mathf.FloorToInt(desc.height * resolutionScale);

                desc.graphicsFormat = GraphicsFormat.R16G16B16A16_SFloat;
                TextureHandle diffuseHandle = UniversalRenderer.CreateRenderGraphTexture(renderGraph, desc, name: _IndirectDiffuseTexture, false, FilterMode.Point, TextureWrapMode.Clamp);

                TextureHandle intermediateDiffuseHandle = UniversalRenderer.CreateRenderGraphTexture(renderGraph, desc, name: _IntermediateIndirectDiffuseTexture, false, FilterMode.Point, TextureWrapMode.Clamp);

                RenderingUtils.ReAllocateHandleIfNeeded(ref m_HistoryCameraColorHandle, desc, FilterMode.Point, TextureWrapMode.Clamp, name: _SSGIHistoryCameraColorTexture);
                m_SSGIMaterial.SetTexture(ssgiHistoryCameraColorTexture, m_HistoryCameraColorHandle);
                TextureHandle historyCameraColorHandle = renderGraph.ImportTexture(m_HistoryCameraColorHandle);
                passData.historyCameraColorHandle = historyCameraColorHandle;
                builder.UseTexture(historyCameraColorHandle, AccessFlags.ReadWrite);

                // Avoid reprojecting from uninitialized history texture
                if (isHistoryTextureValid)
                {
                    m_SSGIMaterial.SetFloat(_HistoryTextureValid, 1.0f);
                    m_SSGIMaterial.SetTexture(ssgiHistoryCameraColorTexture, m_HistoryCameraColorHandle);
                }
                else
                {
                    m_SSGIMaterial.SetFloat(_HistoryTextureValid, 0.0f);
                    isHistoryTextureValid = true;
                }

                depthDesc.colorFormat = RenderTextureFormat.RFloat;
                //depthDesc.graphicsFormat = GraphicsFormat.None;
                //if (resourceData.activeDepthTexture.IsValid())
                    //depthDesc.depthBufferBits = (int)resourceData.activeDepthTexture.GetDescriptor(renderGraph).depthBufferBits;
                RenderingUtils.ReAllocateHandleIfNeeded(ref m_HistoryDepthHandle, depthDesc, FilterMode.Point, TextureWrapMode.Clamp, name: _SSGIHistoryDepthTexture);
                m_SSGIMaterial.SetTexture(ssgiHistoryDepthTexture, m_HistoryDepthHandle);
                TextureHandle historyDepthHandle = renderGraph.ImportTexture(m_HistoryDepthHandle);
                passData.historyDepthHandle = historyDepthHandle;
                builder.UseTexture(historyDepthHandle, AccessFlags.ReadWrite);

                RenderingUtils.ReAllocateHandleIfNeeded(ref m_HistoryIndirectDiffuseHandle, desc, FilterMode.Point, TextureWrapMode.Clamp, name: _HistoryIndirectDiffuseTexture);
                m_SSGIMaterial.SetTexture(historyIndirectDiffuseTexture, m_HistoryIndirectDiffuseHandle);
                TextureHandle historyDiffuseHandle = renderGraph.ImportTexture(m_HistoryIndirectDiffuseHandle);

                desc.colorFormat = RenderTextureFormat.RHalf;
                RenderingUtils.ReAllocateHandleIfNeeded(ref m_AccumulateSampleHandle, desc, FilterMode.Point, TextureWrapMode.Clamp, name: _SSGISampleTexture);
                RenderingUtils.ReAllocateHandleIfNeeded(ref m_AccumulateHistorySampleHandle, desc, FilterMode.Point, TextureWrapMode.Clamp, name: _SSGIHistorySampleTexture);
                m_SSGIMaterial.SetTexture(ssgiSampleTexture, m_AccumulateSampleHandle);
                m_SSGIMaterial.SetTexture(ssgiHistorySampleTexture, m_AccumulateHistorySampleHandle);
                TextureHandle accumulateSampleHandle = renderGraph.ImportTexture(m_AccumulateSampleHandle);
                TextureHandle accumulateHistorySampleHandle = renderGraph.ImportTexture(m_AccumulateHistorySampleHandle);

                ConfigureInput(ScriptableRenderPassInput.Depth | ScriptableRenderPassInput.Motion);

                // Fill up the passData with the data needed by the pass
                passData.ssgiMaterial = m_SSGIMaterial;
                passData.cameraColorTargetHandle = resourceData.activeColorTexture;
                passData.cameraDepthTextureHandle = resourceData.cameraDepthTexture;
                passData.diffuseHandle = diffuseHandle;
                passData.historyDiffuseHandle = historyDiffuseHandle;
                passData.intermediateDiffuseHandle = intermediateDiffuseHandle;
                passData.historyDepthHandle = historyDepthHandle;
                passData.accumulateSampleHandle = accumulateSampleHandle;
                passData.accumulateHistorySampleHandle = accumulateHistorySampleHandle;
                passData.intermediateCameraColorHandle = intermediateCameraColorHandle;
                passData.apvLightingHandle = apvLightingHandle;
                passData.rTHandles = rTHandles;

                // UnsafePasses don't setup the outputs using UseTextureFragment/UseTextureFragmentDepth, you should specify your writes with UseTexture instead
                builder.UseTexture(passData.cameraColorTargetHandle, AccessFlags.ReadWrite);
                builder.UseTexture(passData.cameraDepthTextureHandle, AccessFlags.Read);
                builder.UseTexture(passData.diffuseHandle, AccessFlags.ReadWrite);
                builder.UseTexture(passData.historyDiffuseHandle, AccessFlags.ReadWrite);
                builder.UseTexture(passData.intermediateDiffuseHandle, AccessFlags.Write);
                builder.UseTexture(passData.intermediateCameraColorHandle, AccessFlags.ReadWrite);
                builder.UseTexture(passData.apvLightingHandle, AccessFlags.Write);
                builder.UseTexture(resourceData.motionVectorColor, AccessFlags.Read);
                //if (enableRenderingLayers) { builder.UseTexture(resourceData.renderingLayersTexture, AccessFlags.Read); }

                passData.localGBuffers = resourceData.gBuffer[0].IsValid();

                if (passData.localGBuffers)
                {
                    passData.gBuffer0Handle = resourceData.gBuffer[0];
                    passData.gBuffer1Handle = resourceData.gBuffer[1];
                    passData.gBuffer2Handle = resourceData.gBuffer[2];

                    builder.UseTexture(passData.gBuffer0Handle, AccessFlags.Read);
                    builder.UseTexture(passData.gBuffer1Handle, AccessFlags.Read);
                    builder.UseTexture(passData.gBuffer2Handle, AccessFlags.Read);
                }

                // Assign the ExecutePass function to the render pass delegate, which will be called by the render graph when executing the pass
                builder.SetRenderFunc((PassData data, UnsafeGraphContext context) => ExecutePass(data, context));
            }
        }
        #endregion
    #endif

        #region Shared
        public void Dispose()
        {
            m_IntermediateCameraColorHandle?.Release();
            m_DiffuseHandle?.Release();
            m_IntermediateDiffuseHandle?.Release();
            m_AccumulateSampleHandle?.Release();
            m_APVLightingHandle?.Release();

            // Render Graph Pass
            m_HistoryDepthHandle?.Release();
            m_HistoryCameraColorHandle?.Release();
            m_HistoryIndirectDiffuseHandle?.Release();
            m_AccumulateHistorySampleHandle?.Release();
        }

        Vector4 EvaluateRotator(float rand)
        {
            float ca = Mathf.Cos(rand);
            float sa = Mathf.Sin(rand);
            return new Vector4(ca, sa, -sa, ca);
        }

        // Per Camera History Data
        private struct CameraHistoryData
        {
            public int hash;
            public Matrix4x4 prevCamInvVPMatrix;
            public Vector3 prevCameraPositionWS;
            public float scaledWidth;
            public float scaledHeight;

            // Non Render Graph Pass
            public RTHandle historyDepthHandle;
            public RTHandle historyCameraColorHandle;
            public RTHandle historyIndirectDiffuseHandle;
            public RTHandle accumulateHistorySampleHandle;
        }

        private const int MAX_CAMERA_COUNT = 4; // must be >= 2
        private readonly CameraHistoryData[] cameraHistoryData = new CameraHistoryData[MAX_CAMERA_COUNT];

        private int GetCameraHistoryDataIndex(int cameraHash)
        {
            // Unroll manually for MAX_CAMERA_COUNT = 4
            if (cameraHistoryData[0].hash == cameraHash) return 0;
            if (cameraHistoryData[1].hash == cameraHash) return 1;
            if (cameraHistoryData[2].hash == cameraHash) return 2;
            if (cameraHistoryData[3].hash == cameraHash) return 3;
            return -1; // new camera
        }

        private void UpdateCameraHistoryData(bool cameraHashChanged)
        {
            if (cameraHashChanged)
            {
                const int lastIndex = MAX_CAMERA_COUNT - 1;

                // Non Render Graph Pass
                // Release the persistent textures for the last camera
                cameraHistoryData[lastIndex].historyDepthHandle?.Release();
                cameraHistoryData[lastIndex].historyCameraColorHandle?.Release();
                cameraHistoryData[lastIndex].historyIndirectDiffuseHandle?.Release();
                cameraHistoryData[lastIndex].accumulateHistorySampleHandle?.Release();

                // Shift the camera history data back by one
                Array.Copy(cameraHistoryData, 0, cameraHistoryData, 1, lastIndex);
            }
        }

        private void UpdateReflectionProbe(NativeArray<VisibleReflectionProbe> visibleReflectionProbes, Vector3 cameraPosition)
        {
            if (ssgiVolume.IsFallbackReflectionProbes() && !Shader.IsKeywordEnabled(_FORWARD_PLUS))
            {
                var reflectionProbe = GetClosestProbe(visibleReflectionProbes, cameraPosition);
                if (reflectionProbe != null)
                {
                    m_SSGIMaterial.SetTexture(specCube0, reflectionProbe.texture);
                    m_SSGIMaterial.SetVector(specCube0_HDR, reflectionProbe.textureHDRDecodeValues);
                    bool isBoxProjected = reflectionProbe.boxProjection;
                    if (isBoxProjected)
                    {
                        Vector3 probe0Position = reflectionProbe.transform.position;
                        float probe0Mode = isBoxProjected ? 1.0f : 0.0f;
                        m_SSGIMaterial.SetVector(specCube0_BoxMin, reflectionProbe.bounds.min);
                        m_SSGIMaterial.SetVector(specCube0_BoxMax, reflectionProbe.bounds.max);
                        m_SSGIMaterial.SetVector(specCube0_ProbePosition, new Vector4(probe0Position.x, probe0Position.y, probe0Position.z, probe0Mode));
                    }
                    m_SSGIMaterial.SetFloat(probeWeight, 0.0f);
                    m_SSGIMaterial.SetFloat(probeSet, 1.0f);
                }
                else
                {
                    m_SSGIMaterial.SetFloat(probeSet, 0.0f);
                }
            }
            else
            {
                m_SSGIMaterial.SetFloat(probeSet, 0.0f);
            }
        }

        private static ReflectionProbe GetClosestProbe(NativeArray<VisibleReflectionProbe> visibleReflectionProbes, Vector3 cameraPosition)
        {
            ReflectionProbe closestProbe = null;
            float closestDistance = float.MaxValue;
            int highestImportance = int.MinValue;
            float smallestBoundsSize = float.MaxValue;

            foreach (var visibleProbe in visibleReflectionProbes)
            {
                ReflectionProbe probe = visibleProbe.reflectionProbe;
                Bounds probeBounds = probe.bounds;
                int probeImportance = probe.importance;
                float boundsSize = probeBounds.size.magnitude;

                if (probeBounds.Contains(cameraPosition))
                {
                    float distance = Vector3.Distance(cameraPosition, probe.transform.position);

                    bool isMoreImportant = probeImportance > highestImportance;
                    bool isSizeSmaller = probeImportance == highestImportance && boundsSize < smallestBoundsSize;
                    bool isDistanceCloser = boundsSize == smallestBoundsSize && distance < closestDistance;

                    // Rules:
                    // 1. Find the probe(s) with highest importance index
                    // 2. Find the probe(s) with a smallest box size
                    // 3. Find the probe(s) with a closer distance to the camera
                    bool isCloserProbe = isMoreImportant || isSizeSmaller || isDistanceCloser;

                    if (isCloserProbe)
                    {
                        closestDistance = distance;
                        highestImportance = probeImportance;
                        smallestBoundsSize = boundsSize;
                        closestProbe = probe;
                    }
                }
            }
            // Returns null if we cannot find a probe
            return closestProbe;
        }
        #endregion
    }

    public class BackfaceDataPass : ScriptableRenderPass
    {
        const string m_ProfilerTag = "Render Backface Data";
        private readonly ProfilingSampler m_ProfilingSampler = new ProfilingSampler(m_ProfilerTag);

        private RTHandle m_BackDepthHandle;
        private RTHandle m_BackColorHandle;
        public bool backfaceLighting;

        private RenderStateBlock m_DepthRenderStateBlock = new RenderStateBlock(RenderStateMask.Nothing);
        private readonly ShaderTagId[] m_LitTags = new ShaderTagId[2];

        private const string k_DepthOnly = "DepthOnly";
        private const string k_UniversalForward = "UniversalForward";
        private const string k_UniversalForwardOnly = "UniversalForwardOnly";
        private readonly ShaderTagId depthOnly = new ShaderTagId(k_DepthOnly);
        private readonly ShaderTagId universalForward = new ShaderTagId(k_UniversalForward);
        private readonly ShaderTagId universalForwardOnly = new ShaderTagId(k_UniversalForwardOnly);

        #region Non Render Graph Pass

    #if UNITY_6000_0_OR_NEWER
        [Obsolete]
    #endif
        public override void OnCameraSetup(CommandBuffer cmd, ref RenderingData renderingData)
        {
            var depthDesc = renderingData.cameraData.cameraTargetDescriptor;
            depthDesc.msaaSamples = 1;
            depthDesc.bindMS = false;
            depthDesc.graphicsFormat = GraphicsFormat.None;

            if (!backfaceLighting)
            {
            #if UNITY_6000_0_OR_NEWER
                RenderingUtils.ReAllocateHandleIfNeeded(ref m_BackDepthHandle, depthDesc, FilterMode.Point, TextureWrapMode.Clamp, name: _CameraBackDepthTexture);
            #else
                RenderingUtils.ReAllocateIfNeeded(ref m_BackDepthHandle, depthDesc, FilterMode.Point, TextureWrapMode.Clamp, name: _CameraBackDepthTexture);
            #endif
                cmd.SetGlobalTexture(cameraBackDepthTexture, m_BackDepthHandle);

                ConfigureTarget(m_BackDepthHandle, m_BackDepthHandle);
                ConfigureClear(ClearFlag.Depth, Color.clear);
            }
            else
            {
                var colorDesc = renderingData.cameraData.cameraTargetDescriptor;
                colorDesc.depthStencilFormat = GraphicsFormat.None;
                colorDesc.msaaSamples = 1;
                colorDesc.bindMS = false;
                colorDesc.graphicsFormat = GraphicsFormat.B10G11R11_UFloatPack32;

            #if UNITY_6000_0_OR_NEWER
                RenderingUtils.ReAllocateHandleIfNeeded(ref m_BackColorHandle, colorDesc, FilterMode.Point, TextureWrapMode.Clamp, name: _CameraBackOpaqueTexture);
                RenderingUtils.ReAllocateHandleIfNeeded(ref m_BackDepthHandle, depthDesc, FilterMode.Point, TextureWrapMode.Clamp, name: _CameraBackDepthTexture);
            #else
                RenderingUtils.ReAllocateIfNeeded(ref m_BackColorHandle, colorDesc, FilterMode.Point, TextureWrapMode.Clamp, name: _CameraBackOpaqueTexture);
                RenderingUtils.ReAllocateIfNeeded(ref m_BackDepthHandle, depthDesc, FilterMode.Point, TextureWrapMode.Clamp, name: _CameraBackDepthTexture);
            #endif

                cmd.SetGlobalTexture(cameraBackDepthTexture, m_BackDepthHandle);
                cmd.SetGlobalTexture(cameraBackOpaqueTexture, m_BackColorHandle);

                ConfigureTarget(m_BackColorHandle, m_BackDepthHandle);
                ConfigureClear(ClearFlag.Color | ClearFlag.Depth, Color.clear);
            }
        }

    #if UNITY_6000_0_OR_NEWER
        [Obsolete]
    #endif
        public override void Execute(ScriptableRenderContext context, ref RenderingData renderingData)
        {
            CommandBuffer cmd = CommandBufferPool.Get();

            // Render backface depth
            if (!backfaceLighting)
            {
                using (new ProfilingScope(cmd, m_ProfilingSampler))
                {
                    RendererListDesc rendererListDesc = new RendererListDesc(depthOnly, renderingData.cullResults, renderingData.cameraData.camera);
                    m_DepthRenderStateBlock.depthState = new DepthState(true, CompareFunction.LessEqual);
                    m_DepthRenderStateBlock.mask |= RenderStateMask.Depth;
                    m_DepthRenderStateBlock.rasterState = new RasterState(CullMode.Front);
                    m_DepthRenderStateBlock.mask |= RenderStateMask.Raster;
                    rendererListDesc.stateBlock = m_DepthRenderStateBlock;
                    rendererListDesc.sortingCriteria = renderingData.cameraData.defaultOpaqueSortFlags;
                    rendererListDesc.renderQueueRange = RenderQueueRange.opaque;
                    RendererList rendererList = context.CreateRendererList(rendererListDesc);

                    cmd.DrawRendererList(rendererList);
                }
            }
            // Render backface depth + color
            else
            {
                using (new ProfilingScope(cmd, m_ProfilingSampler))
                {
                    m_LitTags[0] = universalForward;
                    m_LitTags[1] = universalForwardOnly;

                    RendererListDesc rendererListDesc = new RendererListDesc(m_LitTags, renderingData.cullResults, renderingData.cameraData.camera);
                    m_DepthRenderStateBlock.depthState = new DepthState(true, CompareFunction.LessEqual);
                    m_DepthRenderStateBlock.mask |= RenderStateMask.Depth;
                    m_DepthRenderStateBlock.rasterState = new RasterState(CullMode.Front);
                    m_DepthRenderStateBlock.mask |= RenderStateMask.Raster;
                    rendererListDesc.stateBlock = m_DepthRenderStateBlock;
                    rendererListDesc.sortingCriteria = renderingData.cameraData.defaultOpaqueSortFlags;
                    rendererListDesc.renderQueueRange = RenderQueueRange.opaque;
                    RendererList rendererList = context.CreateRendererList(rendererListDesc);

                    cmd.DrawRendererList(rendererList);
                }
            }
            context.ExecuteCommandBuffer(cmd);
            cmd.Clear();
            CommandBufferPool.Release(cmd);
        }
        #endregion

    #if UNITY_6000_0_OR_NEWER
        #region Render Graph Pass
        // This class stores the data needed by the pass, passed as parameter to the delegate function that executes the pass
        private class PassData
        {
            internal RendererListHandle rendererListHandle;
        }

        // This static method is used to execute the pass and passed as the RenderFunc delegate to the RenderGraph render pass
        static void ExecutePass(PassData data, RasterGraphContext context)
        {
            context.cmd.DrawRendererList(data.rendererListHandle);
        }

        // This is where the renderGraph handle can be accessed.
        // Each ScriptableRenderPass can use the RenderGraph handle to add multiple render passes to the render graph
        public override void RecordRenderGraph(RenderGraph renderGraph, ContextContainer frameData)
        {
            // add a raster render pass to the render graph, specifying the name and the data type that will be passed to the ExecutePass function
            using (var builder = renderGraph.AddRasterRenderPass<PassData>(m_ProfilerTag, out var passData))
            {
                // UniversalResourceData contains all the texture handles used by the renderer, including the active color and depth textures
                // The active color and depth textures are the main color and depth buffers that the camera renders into
                UniversalResourceData resourceData = frameData.Get<UniversalResourceData>();
                UniversalRenderingData universalRenderingData = frameData.Get<UniversalRenderingData>();
                UniversalCameraData cameraData = frameData.Get<UniversalCameraData>();
                UniversalLightData lightData = frameData.Get<UniversalLightData>();

                //var depthDesc = cameraData.cameraTargetDescriptor;
                //depthDesc.msaaSamples = 1;
                //depthDesc.bindMS = false;
                //depthDesc.graphicsFormat = GraphicsFormat.None;

                TextureDesc depthDesc;
                if (!resourceData.isActiveTargetBackBuffer)
                {
                    depthDesc = resourceData.activeDepthTexture.GetDescriptor(renderGraph);
                }
                else
                {
                    depthDesc = resourceData.cameraDepthTexture.GetDescriptor(renderGraph);
                    var backBufferInfo = renderGraph.GetRenderTargetInfo(resourceData.backBufferDepth);
                    depthDesc.colorFormat = backBufferInfo.format;
                }
                depthDesc.name = _CameraBackDepthTexture;
                depthDesc.useMipMap = false;
                depthDesc.clearBuffer = true;
                depthDesc.msaaSamples = MSAASamples.None;
                depthDesc.bindTextureMS = false;
                depthDesc.filterMode = FilterMode.Point;
                depthDesc.wrapMode = TextureWrapMode.Clamp;


                //if (resourceData.activeDepthTexture.IsValid())
                //    depthDesc.depthBufferBits = (int)resourceData.activeDepthTexture.GetDescriptor(renderGraph).depthBufferBits;

                // Render backface depth
                if (!backfaceLighting)
                {
                    //TextureHandle backDepthHandle = UniversalRenderer.CreateRenderGraphTexture(renderGraph, depthDesc, name: _CameraBackDepthTexture, true, FilterMode.Point, TextureWrapMode.Clamp);
                    TextureHandle backDepthHandle = renderGraph.CreateTexture(depthDesc);

                    RendererListDesc rendererListDesc = new RendererListDesc(new ShaderTagId(k_DepthOnly), universalRenderingData.cullResults, cameraData.camera);
                    m_DepthRenderStateBlock.depthState = new DepthState(true, CompareFunction.LessEqual);
                    m_DepthRenderStateBlock.mask |= RenderStateMask.Depth;
                    m_DepthRenderStateBlock.rasterState = new RasterState(CullMode.Front);
                    m_DepthRenderStateBlock.mask |= RenderStateMask.Raster;
                    rendererListDesc.stateBlock = m_DepthRenderStateBlock;
                    rendererListDesc.sortingCriteria = cameraData.defaultOpaqueSortFlags;
                    rendererListDesc.renderQueueRange = RenderQueueRange.opaque;

                    passData.rendererListHandle = renderGraph.CreateRendererList(rendererListDesc);

                    // We declare the RendererList we just created as an input dependency to this pass, via UseRendererList()
                    builder.UseRendererList(passData.rendererListHandle);

                    // Set to read & write to avoid texture reusing, since this texture will be used by other passes later.
                    builder.SetRenderAttachmentDepth(backDepthHandle, AccessFlags.ReadWrite);

                    builder.SetGlobalTextureAfterPass(backDepthHandle, cameraBackDepthTexture);

                    // Assign the ExecutePass function to the render pass delegate, which will be called by the render graph when executing the pass
                    builder.SetRenderFunc((PassData data, RasterGraphContext context) => ExecutePass(data, context));
                }
                // Render backface depth + color
                else
                {
                    //TextureHandle backDepthHandle = UniversalRenderer.CreateRenderGraphTexture(renderGraph, depthDesc, name: _CameraBackDepthTexture, true, FilterMode.Point, TextureWrapMode.Clamp);
                    TextureHandle backDepthHandle = renderGraph.CreateTexture(depthDesc);

                    //var colorDesc = cameraData.cameraTargetDescriptor;
                    //colorDesc.msaaSamples = 1;
                    //colorDesc.bindMS = false;
                    //colorDesc.depthStencilFormat = GraphicsFormat.None;
                    //colorDesc.graphicsFormat = GraphicsFormat.B10G11R11_UFloatPack32;

                    var colorDesc = resourceData.cameraColor.GetDescriptor(renderGraph);
                    colorDesc.name = _CameraBackOpaqueTexture;
                    colorDesc.useMipMap = false;
                    colorDesc.clearBuffer = true;
                    colorDesc.msaaSamples = MSAASamples.None;
                    colorDesc.bindTextureMS = false;
                    colorDesc.filterMode = FilterMode.Point;
                    colorDesc.wrapMode = TextureWrapMode.Clamp;
                    colorDesc.colorFormat = GraphicsFormat.B10G11R11_UFloatPack32;

                    //TextureHandle backColorHandle = UniversalRenderer.CreateRenderGraphTexture(renderGraph, colorDesc, name: _CameraBackOpaqueTexture, true, FilterMode.Point, TextureWrapMode.Clamp);
                    TextureHandle backColorHandle = renderGraph.CreateTexture(colorDesc);

                    m_LitTags[0] = new ShaderTagId(k_UniversalForward);
                    m_LitTags[1] = new ShaderTagId(k_UniversalForwardOnly);

                    RendererListDesc rendererListDesc = new RendererListDesc(m_LitTags, universalRenderingData.cullResults, cameraData.camera);
                    m_DepthRenderStateBlock.depthState = new DepthState(true, CompareFunction.LessEqual);
                    m_DepthRenderStateBlock.mask |= RenderStateMask.Depth;
                    m_DepthRenderStateBlock.rasterState = new RasterState(CullMode.Front);
                    m_DepthRenderStateBlock.mask |= RenderStateMask.Raster;
                    rendererListDesc.stateBlock = m_DepthRenderStateBlock;
                    rendererListDesc.sortingCriteria = cameraData.defaultOpaqueSortFlags;
                    rendererListDesc.renderQueueRange = RenderQueueRange.opaque;

                    passData.rendererListHandle = renderGraph.CreateRendererList(rendererListDesc);

                    // We declare the RendererList we just created as an input dependency to this pass, via UseRendererList()
                    builder.UseRendererList(passData.rendererListHandle);

                    builder.SetRenderAttachment(backColorHandle, 0);
                    builder.SetRenderAttachmentDepth(backDepthHandle);

                    builder.SetGlobalTextureAfterPass(backColorHandle, cameraBackOpaqueTexture);
                    builder.SetGlobalTextureAfterPass(backDepthHandle, cameraBackDepthTexture);

                    // Assign the ExecutePass function to the render pass delegate, which will be called by the render graph when executing the pass
                    builder.SetRenderFunc((PassData data, RasterGraphContext context) => ExecutePass(data, context));
                }
            }
        }
        #endregion
    #endif

        #region Shared
        public void Dispose()
        {
            m_BackDepthHandle?.Release();
            if (backfaceLighting)
                m_BackColorHandle?.Release();
        }
        #endregion
    }

    public class ForwardGBufferPass : ScriptableRenderPass
    {
        private const string m_ProfilerTag = "Render Forward GBuffer";
        private readonly ProfilingSampler m_ProfilingSampler = new ProfilingSampler(m_ProfilerTag);

        private List<ShaderTagId> m_ShaderTagIdList = new List<ShaderTagId>();
        private FilteringSettings m_filter;

        // Depth Priming.
        private RenderStateBlock m_RenderStateBlock = new RenderStateBlock(RenderStateMask.Nothing);

        public RTHandle m_GBuffer0;
        public RTHandle m_GBuffer1;
        public RTHandle m_GBuffer2;
        public RTHandle m_GBufferDepth;
        private RTHandle[] m_GBuffers;

        public ForwardGBufferPass(string[] PassNames)
        {
            RenderQueueRange queue = RenderQueueRange.opaque;
            m_filter = new FilteringSettings(queue);
            if (PassNames != null && PassNames.Length > 0)
            {
                foreach (var passName in PassNames)
                    m_ShaderTagIdList.Add(new ShaderTagId(passName));
            }
        }

        // From "URP-Package/Runtime/DeferredLights.cs".
        public GraphicsFormat GetGBufferFormat(int index)
        {
            if (index == 0) // sRGB albedo, materialFlags
                return QualitySettings.activeColorSpace == ColorSpace.Linear ? GraphicsFormat.R8G8B8A8_SRGB : GraphicsFormat.R8G8B8A8_UNorm;
            else if (index == 1) // sRGB specular, occlusion
                return GraphicsFormat.R8G8B8A8_UNorm;
            else if (index == 2) // normal normal normal packedSmoothness
                                 // NormalWS range is -1.0 to 1.0, so we need a signed render texture.
            #if UNITY_2023_2_OR_NEWER
                if (SystemInfo.IsFormatSupported(GraphicsFormat.R8G8B8A8_SNorm, GraphicsFormatUsage.Render))
            #else
                if (SystemInfo.IsFormatSupported(GraphicsFormat.R8G8B8A8_SNorm, FormatUsage.Render))
            #endif
                    return GraphicsFormat.R8G8B8A8_SNorm;
                else
                    return GraphicsFormat.R16G16B16A16_SFloat;
            else
                return GraphicsFormat.None;
        }

        #region Non Render Graph Pass
    #if UNITY_6000_0_OR_NEWER
        [Obsolete]
    #endif
        public override void Execute(ScriptableRenderContext context, ref RenderingData renderingData)
        {
            // GBuffer cannot store surface data from transparent objects.
            SortingCriteria sortingCriteria = renderingData.cameraData.defaultOpaqueSortFlags;

            CommandBuffer cmd = CommandBufferPool.Get();
            using (new ProfilingScope(cmd, m_ProfilingSampler))
            {
                RendererListDesc rendererListDesc = new RendererListDesc(m_ShaderTagIdList[0], renderingData.cullResults, renderingData.cameraData.camera);
                rendererListDesc.stateBlock = m_RenderStateBlock;
                rendererListDesc.sortingCriteria = sortingCriteria;
                rendererListDesc.renderQueueRange = m_filter.renderQueueRange;
                RendererList rendererList = context.CreateRendererList(rendererListDesc);

                cmd.DrawRendererList(rendererList);
            }

            context.ExecuteCommandBuffer(cmd);
            cmd.Clear();
            CommandBufferPool.Release(cmd);
        }

    #if UNITY_6000_0_OR_NEWER
        [Obsolete]
    #endif
        public override void OnCameraSetup(CommandBuffer cmd, ref RenderingData renderingData)
        {
            RenderTextureDescriptor desc = renderingData.cameraData.cameraTargetDescriptor;
            desc.depthBufferBits = 0; // Color and depth cannot be combined in RTHandles
            desc.stencilFormat = GraphicsFormat.None;
            desc.msaaSamples = 1; // Do not enable MSAA for GBuffers.
            desc.bindMS = false;

            // Albedo.rgb + MaterialFlags.a
            desc.graphicsFormat = GetGBufferFormat(0);
        #if UNITY_6000_0_OR_NEWER
            RenderingUtils.ReAllocateHandleIfNeeded(ref m_GBuffer0, desc, FilterMode.Point, TextureWrapMode.Clamp, name: _GBuffer0);
        #else
            RenderingUtils.ReAllocateIfNeeded(ref m_GBuffer0, desc, FilterMode.Point, TextureWrapMode.Clamp, name: _GBuffer0);
        #endif
            cmd.SetGlobalTexture(gBuffer0, m_GBuffer0);

            // Specular.rgb + Occlusion.a
            desc.graphicsFormat = GetGBufferFormat(1);
        #if UNITY_6000_0_OR_NEWER
            RenderingUtils.ReAllocateHandleIfNeeded(ref m_GBuffer1, desc, FilterMode.Point, TextureWrapMode.Clamp, name: _GBuffer1);
        #else
            RenderingUtils.ReAllocateIfNeeded(ref m_GBuffer1, desc, FilterMode.Point, TextureWrapMode.Clamp, name: _GBuffer1);
        #endif
            cmd.SetGlobalTexture(gBuffer1, m_GBuffer1);

            // [Resolve Later] The "_CameraNormalsTexture" still exists after disabling DepthNormals Prepass, which may cause issue during rendering.
            // So instead of checking the RTHandle, we need to check if DepthNormals Prepass is enqueued.

            /*
            // If "_CameraNormalsTexture" exists (lacking smoothness info), set the target to it instead of creating a new RT.
            if (normalsTextureFieldInfo.GetValue(renderingData.cameraData.renderer) is not RTHandle normalsTextureHandle)
            {
                // NormalWS.rgb + Smoothness.a
                desc.graphicsFormat = GetGBufferFormat(2);
            #if UNITY_6000_0_OR_NEWER
                RenderingUtils.ReAllocateHandleIfNeeded(ref m_GBuffer2, desc, FilterMode.Point, TextureWrapMode.Clamp, name: _GBuffer2);
            #else
                RenderingUtils.ReAllocateIfNeeded(ref m_GBuffer2, desc, FilterMode.Point, TextureWrapMode.Clamp, name: _GBuffer2);
            #endif
                cmd.SetGlobalTexture(gBuffer2, m_GBuffer2);
                m_GBuffers = new RTHandle[] { m_GBuffer0, m_GBuffer1, m_GBuffer2 };
            }
            else
            {
                cmd.SetGlobalTexture(gBuffer2, normalsTextureHandle);
                m_GBuffers = new RTHandle[] { m_GBuffer0, m_GBuffer1, normalsTextureHandle };
            }
            */

            // NormalWS.rgb + Smoothness.a
            desc.graphicsFormat = GetGBufferFormat(2);
        #if UNITY_6000_0_OR_NEWER
            RenderingUtils.ReAllocateHandleIfNeeded(ref m_GBuffer2, desc, FilterMode.Point, TextureWrapMode.Clamp, name: _GBuffer2);
        #else
            RenderingUtils.ReAllocateIfNeeded(ref m_GBuffer2, desc, FilterMode.Point, TextureWrapMode.Clamp, name: _GBuffer2);
        #endif
            cmd.SetGlobalTexture(gBuffer2, m_GBuffer2);
            m_GBuffers = new RTHandle[] { m_GBuffer0, m_GBuffer1, m_GBuffer2 };

            bool isOpenGL = (SystemInfo.graphicsDeviceType == GraphicsDeviceType.OpenGLES3) || (SystemInfo.graphicsDeviceType == GraphicsDeviceType.OpenGLCore); // GLES 2 is deprecated.

            // Disable depth priming if camera uses MSAA
            bool canDepthPriming = !isOpenGL && (renderingData.cameraData.renderType == CameraRenderType.Base || renderingData.cameraData.clearDepth) && renderingData.cameraData.cameraTargetDescriptor.msaaSamples == desc.msaaSamples;

            RenderTextureDescriptor depthDesc = renderingData.cameraData.cameraTargetDescriptor;
            depthDesc.msaaSamples = 1;
            depthDesc.bindMS = false;
            depthDesc.graphicsFormat = GraphicsFormat.None;

        #if UNITY_6000_0_OR_NEWER
            RenderingUtils.ReAllocateHandleIfNeeded(ref m_GBufferDepth, depthDesc, FilterMode.Point, TextureWrapMode.Clamp, name: _GBufferDepth);
        #else
            RenderingUtils.ReAllocateIfNeeded(ref m_GBufferDepth, depthDesc, FilterMode.Point, TextureWrapMode.Clamp, name: _GBufferDepth);
        #endif

            if (canDepthPriming)
                ConfigureTarget(m_GBuffers, renderingData.cameraData.renderer.cameraDepthTargetHandle);
            else
                ConfigureTarget(m_GBuffers, m_GBufferDepth);

            // Require Depth Texture in Forward pipeline.
            ConfigureInput(ScriptableRenderPassInput.Depth);

            // [OpenGL] Reusing the depth buffer seems to cause black glitching artifacts, so clear the existing depth.
            if (isOpenGL)
                ConfigureClear(ClearFlag.Color | ClearFlag.Depth, Color.black);
            else
                // We have to also clear previous color so that the "background" will remain empty (black) when moving the camera.
                ConfigureClear(ClearFlag.Color, Color.clear);

            // Reduce GBuffer overdraw using the depth from opaque pass. (excluding OpenGL platforms)
            if (canDepthPriming)
            {
                m_RenderStateBlock.depthState = new DepthState(false, CompareFunction.Equal);
                m_RenderStateBlock.mask |= RenderStateMask.Depth;
            }
            else if (m_RenderStateBlock.depthState.compareFunction == CompareFunction.Equal)
            {
                m_RenderStateBlock.depthState = new DepthState(true, CompareFunction.LessEqual);
                m_RenderStateBlock.mask |= RenderStateMask.Depth;
            }
        }
        #endregion

    #if UNITY_6000_0_OR_NEWER
        #region Render Graph Pass

        // This class stores the data needed by the pass, passed as parameter to the delegate function that executes the pass
        private class PassData
        {
            internal bool isOpenGL;

            internal RendererListHandle rendererListHandle;
        }

        // This static method is used to execute the pass and passed as the RenderFunc delegate to the RenderGraph render pass
        static void ExecutePass(PassData data, RasterGraphContext context)
        {
            if (data.isOpenGL)
                context.cmd.ClearRenderTarget(true, true, Color.black);
            //else
                // We have to also clear previous color so that the "background" will remain empty (black) when moving the camera.
                //context.cmd.ClearRenderTarget(false, true, Color.clear);

            context.cmd.DrawRendererList(data.rendererListHandle);
        }

        // This is where the renderGraph handle can be accessed.
        // Each ScriptableRenderPass can use the RenderGraph handle to add multiple render passes to the render graph
        public override void RecordRenderGraph(RenderGraph renderGraph, ContextContainer frameData)
        {
            // add a raster render pass to the render graph, specifying the name and the data type that will be passed to the ExecutePass function
            using (var builder = renderGraph.AddRasterRenderPass<PassData>(m_ProfilerTag, out var passData))
            {
                // UniversalResourceData contains all the texture handles used by the renderer, including the active color and depth textures
                // The active color and depth textures are the main color and depth buffers that the camera renders into
                UniversalResourceData resourceData = frameData.Get<UniversalResourceData>();
                UniversalRenderingData universalRenderingData = frameData.Get<UniversalRenderingData>();
                UniversalCameraData cameraData = frameData.Get<UniversalCameraData>();
                UniversalLightData lightData = frameData.Get<UniversalLightData>();

                RenderTextureDescriptor desc = cameraData.cameraTargetDescriptor;
                desc.msaaSamples = 1;
                desc.bindMS = false;
                desc.depthBufferBits = 0;

                // Albedo.rgb + MaterialFlags.a
                desc.graphicsFormat = GetGBufferFormat(0);
                TextureHandle gBuffer0Handle = UniversalRenderer.CreateRenderGraphTexture(renderGraph, desc, name: _GBuffer0, false, FilterMode.Point, TextureWrapMode.Clamp);

                // Specular.rgb + Occlusion.a
                desc.graphicsFormat = GetGBufferFormat(1);
                TextureHandle gBuffer1Handle = UniversalRenderer.CreateRenderGraphTexture(renderGraph, desc, name: _GBuffer1, false, FilterMode.Point, TextureWrapMode.Clamp);

                // [Resolve Later] The "_CameraNormalsTexture" still exists after disabling DepthNormals Prepass, which may cause issue during rendering.
                // So instead of checking the RTHandle, we need to check if DepthNormals Prepass is enqueued.

                /*
                TextureHandle gBuffer2Handle;
                // If "_CameraNormalsTexture" exists (lacking smoothness info), set the target to it instead of creating a new RT.
                if (normalsTextureFieldInfo.GetValue(cameraData.renderer) is not RTHandle normalsTextureHandle)
                {
                    // NormalWS.rgb + Smoothness.a
                    desc.graphicsFormat = GetGBufferFormat(2);
                    gBuffer2Handle = UniversalRenderer.CreateRenderGraphTexture(renderGraph, desc, name: _GBuffer2, false, FilterMode.Point, TextureWrapMode.Clamp);
                }
                else
                {
                    gBuffer2Handle = resourceData.cameraNormalsTexture;
                }
                */

                // NormalWS.rgb + Smoothness.a
                desc.graphicsFormat = GetGBufferFormat(2);
                TextureHandle gBuffer2Handle = UniversalRenderer.CreateRenderGraphTexture(renderGraph, desc, name: _GBuffer2, false, FilterMode.Point, TextureWrapMode.Clamp);

                // [OpenGL] Reusing the depth buffer seems to cause black glitching artifacts, so clear the existing depth.
                bool isOpenGL = (SystemInfo.graphicsDeviceType == GraphicsDeviceType.OpenGLES3) || (SystemInfo.graphicsDeviceType == GraphicsDeviceType.OpenGLCore); // GLES 2 is deprecated.

                // Disable depth priming if camera uses MSAA
                bool canDepthPriming = !isOpenGL && (cameraData.renderType == CameraRenderType.Base || cameraData.clearDepth) && cameraData.cameraTargetDescriptor.msaaSamples == desc.msaaSamples;

                //RenderTextureDescriptor depthDesc = cameraData.cameraTargetDescriptor;
                //depthDesc.msaaSamples = 1;
                //depthDesc.bindMS = false;
                //depthDesc.graphicsFormat = GraphicsFormat.None;
                //if (resourceData.activeDepthTexture.IsValid())
                //    depthDesc.depthBufferBits = (int)resourceData.activeDepthTexture.GetDescriptor(renderGraph).depthBufferBits;

                TextureDesc depthDesc;
                if (!resourceData.isActiveTargetBackBuffer)
                {
                    depthDesc = resourceData.activeDepthTexture.GetDescriptor(renderGraph);
                }
                else
                {
                    depthDesc = resourceData.cameraDepthTexture.GetDescriptor(renderGraph);
                    var backBufferInfo = renderGraph.GetRenderTargetInfo(resourceData.backBufferDepth);
                    depthDesc.colorFormat = backBufferInfo.format;
                }
                depthDesc.name = _GBufferDepth;
                depthDesc.useMipMap = false;
                depthDesc.clearBuffer = false;
                depthDesc.msaaSamples = MSAASamples.None;
                depthDesc.bindTextureMS = false;
                depthDesc.filterMode = FilterMode.Point;
                depthDesc.wrapMode = TextureWrapMode.Clamp;

                TextureHandle depthHandle;
                if (canDepthPriming)
                    depthHandle = resourceData.activeDepthTexture; // Note: there was a problem that the RT format was R32 (not the depth buffer) instead of D32, but I cannot reproduce it again
                else
                    depthHandle = renderGraph.CreateTexture(depthDesc);
                    //depthHandle = UniversalRenderer.CreateRenderGraphTexture(renderGraph, depthDesc, name: _GBufferDepth, false, FilterMode.Point, TextureWrapMode.Clamp);

                // Reduce GBuffer overdraw using the depth from opaque pass. (excluding OpenGL platforms)
                if ( canDepthPriming)
                {
                    m_RenderStateBlock.depthState = new DepthState(false, CompareFunction.Equal);
                    m_RenderStateBlock.mask |= RenderStateMask.Depth;
                }
                else if (m_RenderStateBlock.depthState.compareFunction == CompareFunction.Equal)
                {
                    m_RenderStateBlock.depthState = new DepthState(true, CompareFunction.LessEqual);
                    m_RenderStateBlock.mask |= RenderStateMask.Depth;
                }

                // GBuffer cannot store surface data from transparent objects.
                SortingCriteria sortingCriteria = cameraData.defaultOpaqueSortFlags;
                RendererListDesc rendererListDesc = new RendererListDesc(m_ShaderTagIdList[0], universalRenderingData.cullResults, cameraData.camera);
                DrawingSettings drawSettings = RenderingUtils.CreateDrawingSettings(m_ShaderTagIdList[0], universalRenderingData, cameraData, lightData, sortingCriteria);
                var param = new RendererListParams(universalRenderingData.cullResults, drawSettings, m_filter);
                rendererListDesc.stateBlock = m_RenderStateBlock;
                rendererListDesc.sortingCriteria = sortingCriteria;
                rendererListDesc.renderQueueRange = m_filter.renderQueueRange;

                // Set pass data
                passData.isOpenGL = isOpenGL;
                passData.rendererListHandle = renderGraph.CreateRendererList(rendererListDesc);

                // We declare the RendererList we just created as an input dependency to this pass, via UseRendererList()
                builder.UseRendererList(passData.rendererListHandle);

                // Set render targets
                builder.SetRenderAttachment(gBuffer0Handle, 0);
                builder.SetRenderAttachment(gBuffer1Handle, 1);
                builder.SetRenderAttachment(gBuffer2Handle, 2);
                builder.SetRenderAttachmentDepth(depthHandle, AccessFlags.Write);

                // Set global textures after this pass
                builder.SetGlobalTextureAfterPass(gBuffer0Handle, gBuffer0);
                builder.SetGlobalTextureAfterPass(gBuffer1Handle, gBuffer1);
                builder.SetGlobalTextureAfterPass(gBuffer2Handle, gBuffer2);

                // We disable culling for this pass for the demonstrative purpose of this sample, as normally this pass would be culled,
                // since the destination texture is not used anywhere else
                //builder.AllowGlobalStateModification(true);
                //builder.AllowPassCulling(false);

                // Assign the ExecutePass function to the render pass delegate, which will be called by the render graph when executing the pass
                builder.SetRenderFunc((PassData data, RasterGraphContext context) => ExecutePass(data, context));
            }
        }

        #endregion
    #endif

        #region Shared
        public void Dispose()
        {
            m_GBuffer0?.Release();
            m_GBuffer1?.Release();
            m_GBuffer2?.Release();
            m_GBufferDepth?.Release();
        }
        #endregion
    }
}
