using System.Reflection;
using UnityEditor;
using UnityEditor.Rendering;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.Universal;

[CanEditMultipleObjects]
#if UNITY_2022_2_OR_NEWER
[CustomEditor(typeof(ScreenSpaceGlobalIlluminationVolume))]
#else
[VolumeComponentEditor(typeof(ScreenSpaceGlobalIlluminationVolume))]
#endif
class ScreenSpaceGlobalIlluminationVolumeEditor : VolumeComponentEditor
{
    SerializedDataParameter m_Enable;

    // Screen space global illumination parameters
    SerializedDataParameter m_FullResolutionSS;
    SerializedDataParameter m_ResolutionScaleSS;
    SerializedDataParameter m_ThicknessMode;
    SerializedDataParameter m_DepthBufferThickness;
    SerializedDataParameter m_Quality;
    SerializedDataParameter m_SampleCount;
    SerializedDataParameter m_MaxRaySteps;

    // Filtering SS
    SerializedDataParameter m_DenoiseSS;
    SerializedDataParameter m_DenoiserAlgorithm;
    SerializedDataParameter m_DenoiseIntensitySS;
    SerializedDataParameter m_DenoiserRadiusSS;
    SerializedDataParameter m_SecondDenoiserPassSS;

    // Ray miss hierarchy
    SerializedDataParameter m_RayMiss;

    // Indirect Lighting Controller (SSGI only)
    SerializedDataParameter m_IndirectDiffuseLightingMultiplier;
#if UNITY_2023_3_OR_NEWER
    SerializedDataParameter m_IndirectDiffuseRenderingLayers;
#endif

    const string k_PROBE_VOLUMES_L1 = "PROBE_VOLUMES_L1";
    const string k_PROBE_VOLUMES_L2 = "PROBE_VOLUMES_L2";
    const string k_EVALUATE_SH_VERTEX = "EVALUATE_SH_VERTEX";
    const string k_EVALUATE_SH_MIXED = "EVALUATE_SH_MIXED";
    const string k_LIGHT_LAYERS = "_LIGHT_LAYERS";
    const string k_WRITE_RENDERING_LAYERS = "_WRITE_RENDERING_LAYERS";
#if UNITY_6000_1_OR_NEWER
    const string k_CLUSTER_LIGHT_LOOP = "_CLUSTER_LIGHT_LOOP";
    const string k_REFLECTION_PROBE_ATLAS = "_REFLECTION_PROBE_ATLAS";
#else
    const string k_FORWARD_PLUS = "_FORWARD_PLUS";
#endif

    const string k_RendererDataList = "m_RendererDataList";

#if UNITY_2023_3_OR_NEWER
    const string k_GetHDRCubemapEncodingQualityForPlatform = "GetHDRCubemapEncodingQualityForPlatform";
#else
    const string k_GetHDRCubemapEncodingQualityForPlatform = "GetHDRCubemapEncodingQualityForPlatformGroup";
#endif

    const string k_SsgiRendererFeature = "ScreenSpaceGlobalIlluminationURP";
    const string k_NoRendererFeatureMessage = "Screen Space Global Illumination renderer feature is missing in the active URP renderer.";
    const string k_RendererFeatureOffMessage = "Screen Space Global Illumination is disabled in the active URP renderer.";
    const string k_HDRCubemapEncodingMessage = "HDR Cubemap Encoding Quality is not set to High in the active platform's player settings.";
    const string k_PerVertexAPVMessage = "The \"SH Evaluation Mode\" in the current URP asset is set to \"Per Vertex\". This may result in inaccurate lighting when combined with Adaptive Probe Volumes.";
    const string k_MixedAPVMessage = "The \"SH Evaluation Mode\" in the current URP asset is set to \"Mixed\". This may result in inaccurate lighting when combined with Adaptive Probe Volumes.";
#if UNITY_6000_1_OR_NEWER
    const string k_ClusterLightingUnavailableMessage = "The current rendering path is not \"Forward+\" or \"Deferred+\", which may affect the accuracy of \"Ray Miss\" in large complex scenes.";
#else
    const string k_ClusterLightingUnavailableMessage = "The current rendering path is not \"Forward+\", which may affect the accuracy of \"Ray Miss\" in large complex scenes.";
#endif
    const string k_ProbeAtlasUnavailableMessage = "The \"Probe Atlas Blending\" is disabled in the active URP asset, which may affect the accuracy of \"Ray Miss\" in large complex scenes.";
    const string k_RenderingLayerDisabledMessage = "The \"Use Rendering Layers\" is disabled in the current URP asset.";
    const string k_RenderingLayerHelpMessage = "To enable \"Rendering Layers\", make sure the \"Use Rendering Layers\" is checked in the \"Decal\" renderer feature.";
    const string k_RenderingLayerNotSupportedMessage = "Note: Rendering Layers are not supported on OpenGL backends.";
    const string k_RenderingDebuggerMessage = "Screen Space Global Illumination is disabled to avoid affecting rendering debugging.";

    const string k_PlayerSettingsPath = "Project/Player";
    const string k_FixButtonName = "Fix";
    const string k_OpenButtonName = "Open";
    const string k_EnableButtonName = "Enable";

#if UNITY_2023_3_OR_NEWER
    bool isOpenGL;
#endif

    public override void OnEnable()
    {
        var o = new PropertyFetcher<ScreenSpaceGlobalIlluminationVolume>(serializedObject);

#if UNITY_2023_3_OR_NEWER
        isOpenGL = (SystemInfo.graphicsDeviceType == GraphicsDeviceType.OpenGLES3) || (SystemInfo.graphicsDeviceType == GraphicsDeviceType.OpenGLCore); // GLES 2 is deprecated.

        GraphicsDeviceType[] graphicsAPIs = PlayerSettings.GetGraphicsAPIs(EditorUserBuildSettings.activeBuildTarget);
        foreach (var graphicsAPI in graphicsAPIs)
        {
            if (graphicsAPI == GraphicsDeviceType.OpenGLES3 || graphicsAPI == GraphicsDeviceType.OpenGLCore)
            {
                isOpenGL = true;
                break;
            }
        }
#endif

        RenderDataListFieldInfo = typeof(UniversalRenderPipelineAsset).GetField(k_RendererDataList, BindingFlags.Instance | BindingFlags.NonPublic);
        GetHDRCubemapEncodingQualityMethodInfo = typeof(PlayerSettings).GetMethod(k_GetHDRCubemapEncodingQualityForPlatform, BindingFlags.NonPublic | BindingFlags.Static);

        m_Enable = Unpack(o.Find(x => x.enable));

        m_FullResolutionSS = Unpack(o.Find(x => x.fullResolutionSS));
        m_ResolutionScaleSS = Unpack(o.Find(x => x.resolutionScaleSS));
        m_ThicknessMode = Unpack(o.Find(x => x.thicknessMode));
        m_DepthBufferThickness = Unpack(o.Find(x => x.depthBufferThickness));
        m_Quality = Unpack(o.Find(x => x.quality));
        m_SampleCount = Unpack(o.Find(x => x.sampleCount));
        m_MaxRaySteps = Unpack(o.Find(x => x.maxRaySteps));

        m_DenoiseSS = Unpack(o.Find(x => x.denoiseSS));
        m_DenoiserAlgorithm = Unpack(o.Find(x => x.denoiserAlgorithmSS));
        m_DenoiseIntensitySS = Unpack(o.Find(x => x.denoiseIntensitySS));
        m_DenoiserRadiusSS = Unpack(o.Find(x => x.denoiserRadiusSS));
        m_SecondDenoiserPassSS = Unpack(o.Find(x => x.secondDenoiserPassSS));

        m_RayMiss = Unpack(o.Find(x => x.rayMiss));

        m_IndirectDiffuseLightingMultiplier = Unpack(o.Find(x => x.indirectDiffuseLightingMultiplier));
#if UNITY_2023_3_OR_NEWER
        m_IndirectDiffuseRenderingLayers = Unpack(o.Find(x => x.indirectDiffuseRenderingLayers));
#endif
        base.OnEnable();
    }

    public override void OnInspectorGUI()
    {
        var ssgi = GetRendererFeature(k_SsgiRendererFeature) as ScreenSpaceGlobalIlluminationURP;
        if (ssgi == null)
        {
            EditorGUILayout.Space();
            EditorGUILayout.HelpBox(k_NoRendererFeatureMessage, MessageType.Error, wide: true);
            return;
        }
        else if (!ssgi.isActive)
        {
            EditorGUILayout.Space();
            CoreEditorUtils.DrawFixMeBox(k_RendererFeatureOffMessage, MessageType.Warning, k_FixButtonName, () =>
            {
                ssgi.SetActive(true);
                GUIUtility.ExitGUI();
            });
            EditorGUILayout.Space();
        }

        bool enableSSGI = m_Enable.value.boolValue && m_Enable.overrideState.boolValue;
        bool useAPV = Shader.IsKeywordEnabled(k_PROBE_VOLUMES_L1) || Shader.IsKeywordEnabled(k_PROBE_VOLUMES_L2);
        bool isVertexSH = Shader.IsKeywordEnabled(k_EVALUATE_SH_VERTEX);
        bool isMixedSH = Shader.IsKeywordEnabled(k_EVALUATE_SH_MIXED);
        bool showDebuggerMessage = DebugManager.instance.isAnyDebugUIActive && !ssgi.RenderingDebugger;

        if (ssgi.isActive && enableSSGI && showDebuggerMessage)
        {
            EditorGUILayout.Space();
            CoreEditorUtils.DrawFixMeBox(k_RenderingDebuggerMessage, MessageType.Warning, k_EnableButtonName, () =>
            {
                ssgi.RenderingDebugger = true;
                GUIUtility.ExitGUI();
            });
            EditorGUILayout.Space();
        }

        if (enableSSGI && useAPV && (isVertexSH || isMixedSH))
        {
            EditorGUILayout.Space();
            if (isVertexSH) { EditorGUILayout.HelpBox(k_PerVertexAPVMessage, MessageType.Info, wide: true); }
            else { EditorGUILayout.HelpBox(k_MixedAPVMessage, MessageType.Info, wide: true); }
            EditorGUILayout.Space();
        }

        if (GetHDRCubemapEncodingQuality() != HDRCubemapEncodingQuality.High)
        {
            EditorGUILayout.Space();
            CoreEditorUtils.DrawFixMeBox(k_HDRCubemapEncodingMessage, MessageType.Warning, k_OpenButtonName, () =>
            {
                SettingsService.OpenProjectSettings(k_PlayerSettingsPath);
                GUIUtility.ExitGUI();
            });
            EditorGUILayout.Space();
        }

        if (enableSSGI)
        {
#if UNITY_6000_1_OR_NEWER
            bool isClusterLighting = Shader.IsKeywordEnabled(k_CLUSTER_LIGHT_LOOP); // Forward+ or Deferred+
            bool supportProbeAtlas = Shader.IsKeywordEnabled(k_REFLECTION_PROBE_ATLAS);
#else
            bool supportProbeAtlas = Shader.IsKeywordEnabled(k_FORWARD_PLUS);
            bool isClusterLighting = supportProbeAtlas;
#endif

            if (!isClusterLighting)
            {
                EditorGUILayout.Space();
                EditorGUILayout.HelpBox(k_ClusterLightingUnavailableMessage, MessageType.Info, wide: true);
                EditorGUILayout.Space();
            }
            else if (!supportProbeAtlas) // "Probe Atlas Blending" is off
            {
                EditorGUILayout.Space();
                EditorGUILayout.HelpBox(k_ProbeAtlasUnavailableMessage, MessageType.Info, wide: true);
                EditorGUILayout.Space();
            }
        }

        PropertyField(m_Enable);
        PropertyField(m_FullResolutionSS);
        if (!m_FullResolutionSS.value.boolValue)
        {
            using (new IndentLevelScope())
            {
                PropertyField(m_ResolutionScaleSS);
            }
        }

        ScreenSpaceGlobalIlluminationVolume.QualityMode previousMode = (ScreenSpaceGlobalIlluminationVolume.QualityMode)m_Quality.value.enumValueIndex;

        // Start checking for changes
        EditorGUI.BeginChangeCheck();


        PropertyField(m_Quality);
        using (new IndentLevelScope())
        {
            ScreenSpaceGlobalIlluminationVolume.QualityMode currentMode = (ScreenSpaceGlobalIlluminationVolume.QualityMode)m_Quality.value.enumValueIndex;
            bool customQualityMode = currentMode == ScreenSpaceGlobalIlluminationVolume.QualityMode.Custom;
            if (EditorGUI.EndChangeCheck() || previousMode != currentMode)
                LoadCurrentQualityMode(currentMode);

            if (!customQualityMode)
            {
                m_SampleCount.overrideState.boolValue = m_Quality.overrideState.boolValue;
                m_MaxRaySteps.overrideState.boolValue = m_Quality.overrideState.boolValue;
            }

            EditorGUI.BeginChangeCheck();

            PropertyField(m_SampleCount);
            PropertyField(m_MaxRaySteps);

            if (EditorGUI.EndChangeCheck())
            {
                // Has the any of the properties have changed and we were not in the custom mode, it means we need to switch to the custom mode
                if (!customQualityMode)
                {
                    m_Quality.value.enumValueIndex = (int)ScreenSpaceGlobalIlluminationVolume.QualityMode.Custom;
                }
            }
        }

        PropertyField(m_ThicknessMode);
        using (new IndentLevelScope())
        {
            PropertyField(m_DepthBufferThickness);
        }

        PropertyField(m_DenoiseSS);

        if (m_DenoiseSS.value.boolValue)
        {
            using (new IndentLevelScope())
            {
                PropertyField(m_DenoiserAlgorithm);
                PropertyField(m_DenoiseIntensitySS);
                if (m_DenoiserAlgorithm.value.enumValueIndex == (int)ScreenSpaceGlobalIlluminationVolume.DenoiserAlgorithm.Aggressive) { PropertyField(m_DenoiserRadiusSS); }
                PropertyField(m_SecondDenoiserPassSS);
            }
        }

        PropertyField(m_RayMiss);
        PropertyField(m_IndirectDiffuseLightingMultiplier);

#if UNITY_2023_3_OR_NEWER
        if (m_IndirectDiffuseRenderingLayers.overrideState.boolValue && m_IndirectDiffuseRenderingLayers.value.intValue != -1)
        {
            bool enableRenderingLayers = Shader.IsKeywordEnabled(k_LIGHT_LAYERS);
            bool hasRenderingLayersTexture = Shader.IsKeywordEnabled(k_WRITE_RENDERING_LAYERS);
            if (!enableRenderingLayers)
            {
                EditorGUILayout.Space();
                EditorGUILayout.HelpBox(k_RenderingLayerDisabledMessage, MessageType.Warning, wide: true);
                EditorGUILayout.Space();
            }
            else if (!hasRenderingLayersTexture && !isOpenGL)
            {
                EditorGUILayout.Space();
                EditorGUILayout.HelpBox(k_RenderingLayerHelpMessage, MessageType.Warning, wide: true);
                EditorGUILayout.Space();
            }

            if (isOpenGL)
            {
                EditorGUILayout.HelpBox(k_RenderingLayerNotSupportedMessage, MessageType.Info, wide: true);
                EditorGUILayout.Space();
            }
        }
        PropertyField(m_IndirectDiffuseRenderingLayers);
#endif
    }
    void LoadCurrentQualityMode(ScreenSpaceGlobalIlluminationVolume.QualityMode mode)
    {
        // Apply the currently set preset
        switch (mode)
        {
            case ScreenSpaceGlobalIlluminationVolume.QualityMode.Low:
            {
                m_SampleCount.value.intValue = 1;
                m_MaxRaySteps.value.intValue = 24;
            }
            break;
            case ScreenSpaceGlobalIlluminationVolume.QualityMode.Medium:
            {
                m_SampleCount.value.intValue = 2;
                m_MaxRaySteps.value.intValue = 32;
            }
            break;
            case ScreenSpaceGlobalIlluminationVolume.QualityMode.High:
            {
                m_SampleCount.value.intValue = 4;
                m_MaxRaySteps.value.intValue = 64;
            }
            break;
            default:
                break;
        }
    }

    /// <summary>
    /// Check if the SSGI renderer feature has been added.
    /// From "https://forum.unity.com/threads/enable-or-disable-render-features-at-runtime.932571/"
    /// </summary>
#region Reflection
    private static FieldInfo RenderDataListFieldInfo;
    private static MethodInfo GetHDRCubemapEncodingQualityMethodInfo;

    private static ScriptableRendererData[] GetRendererDataList(UniversalRenderPipelineAsset asset = null)
    {
        try
        {
            if (asset == null)
                asset = (UniversalRenderPipelineAsset)GraphicsSettings.currentRenderPipeline;

            if (asset == null)
                return null;
 
            if (RenderDataListFieldInfo == null)
                return null;
 
            var renderDataList = (ScriptableRendererData[])RenderDataListFieldInfo.GetValue(asset);
            return renderDataList;
        }
        catch
        {
            // Fail silently if reflection failed.
            return null;
        }
    }

    private static ScriptableRendererFeature GetRendererFeature(string typeName)
    {
        var renderDataList = GetRendererDataList();
        if (renderDataList == null || renderDataList.Length == 0)
            return null;

        foreach (var renderData in renderDataList)
        {
            foreach (var rendererFeature in renderData.rendererFeatures)
            {
                if (rendererFeature == null)
                    continue;

                if (rendererFeature.GetType().Name.Contains(typeName))
                {
                    return rendererFeature;
                }
            }
        }

        return null;
    }

    // From editor script: "PlayerSettings.bindings.cs"
    private enum HDRCubemapEncodingQuality
    {
        Low = 0,
        Normal = 1,
        High = 2
    }

    private HDRCubemapEncodingQuality GetHDRCubemapEncodingQuality()
    {
    #if UNITY_2023_3_OR_NEWER
        BuildTarget buildTarget = EditorUserBuildSettings.activeBuildTarget;
    #else
        BuildTargetGroup buildTarget = BuildPipeline.GetBuildTargetGroup(EditorUserBuildSettings.activeBuildTarget);
    #endif

        if (GetHDRCubemapEncodingQualityMethodInfo != null)
        {
            var encodingQuality = GetHDRCubemapEncodingQualityMethodInfo.Invoke(null, new object[] { buildTarget });
            
            // Do not show warning if we don't know the current encoding quality.
            if (encodingQuality == null)
                return HDRCubemapEncodingQuality.High;
            else
                return (HDRCubemapEncodingQuality)encodingQuality;
        }
        else
        {
            // Do not show warning if we don't know the current encoding quality.
            return HDRCubemapEncodingQuality.High;
        }
    }
#endregion
}
