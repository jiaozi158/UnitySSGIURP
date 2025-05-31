using System.Reflection;
using System.Threading.Tasks;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.Universal;
using UnityEditor;
using UnityEditor.Build;
using UnityEditor.Build.Reporting;

/// <summary>
/// [Editor Only] Preserve GBuffer shader variants when building Universal RP projects.
/// </summary>
class KeepDeferredVariantsEditor : IPreprocessBuildWithReport, IPostprocessBuildWithReport, IProcessSceneWithReport
{
    public KeepDeferredVariantsEditor() { }

    // Use callbackOrder to set when Unity calls this shader preprocessor. Unity starts with the preprocessor that has the lowest callbackOrder value.

    // TODO: Test on Unity 2023
#if UNITY_6000_0_OR_NEWER
    public int callbackOrder { get { return 0; } }
#else
    public int callbackOrder { get { return 1; } } // Unity 2022 LTS
#endif

    const string k_RendererDataList = "m_RendererDataList";
    const string k_SsgiRendererFeature = "ScreenSpaceGlobalIlluminationURP";
    const string k_TemporaryRendererName = "SSGI-EmptyDeferredRenderer";

    bool isTemporaryRendererAdded = false;
    UniversalRendererData tempRendererData;

    public void OnPreprocessBuild(BuildReport report)
    {
        var urpAsset = GraphicsSettings.currentRenderPipeline as UniversalRenderPipelineAsset;
        if (urpAsset == null)
            return;

        var ssgi = GetRendererFeature(k_SsgiRendererFeature) as ScreenSpaceGlobalIlluminationURP;
        if (ssgi != null)
        {
            FieldInfo fieldInfo = urpAsset.GetType().GetField(k_RendererDataList, BindingFlags.NonPublic | BindingFlags.Instance);

            // Get the current renderer list
            var oldRendererList = (ScriptableRendererData[])fieldInfo.GetValue(urpAsset);

            bool hasDeferredRenderer = false;
            for (int i = 0; i < oldRendererList.Length; i++)
            {
                var renderingMode = ((UniversalRendererData)oldRendererList[i]).renderingMode;
                hasDeferredRenderer |= renderingMode != RenderingMode.Forward && renderingMode != RenderingMode.ForwardPlus; // Deferred or Deferred+
            }

            // If there's no Deferred Renderer in the renderer list
            if (!hasDeferredRenderer)
            {
                // Create a new temporary Deferred Renderer to keep the deferred GBuffer passes in Forward rendering path
                tempRendererData = ScriptableObject.CreateInstance<UniversalRendererData>();
                tempRendererData.renderingMode = RenderingMode.Deferred;
                tempRendererData.name = k_TemporaryRendererName;

                var newRendererList = new ScriptableRendererData[oldRendererList.Length + 1];
                for (int i = 0; i < oldRendererList.Length; i++)
                {
                    newRendererList[i] = oldRendererList[i];
                }
                newRendererList[oldRendererList.Length] = tempRendererData;

                // Assign the new renderer list to the URP Asset
                fieldInfo.SetValue(urpAsset, newRendererList);
            }

            isTemporaryRendererAdded = !hasDeferredRenderer;
        }
        else
            isTemporaryRendererAdded = false;

        WaitForBuildCompletion(report);
    }

    // The post method is not called if a build failed, but we must remove the temporary Deferred renderer after the build
    // A temporary workaround: "https://forum.unity.com/threads/ipostprocessbuildwithreport-and-qa-embarrasing-answer-about-a-serious-bug.891055/#post-9377765"
    async void WaitForBuildCompletion(BuildReport report)
    {
        while (BuildPipeline.isBuildingPlayer || report.summary.result == BuildResult.Unknown)
        {
            await Task.Delay(1000);
        }

        OnPostprocessBuild(report);
    }

    // After shader compilation
    // Remove our temp deferred renderer before build packaging
    public void OnProcessScene(UnityEngine.SceneManagement.Scene scene, BuildReport report)
    {
        if (isTemporaryRendererAdded)
        {
            var urpAsset = GraphicsSettings.currentRenderPipeline as UniversalRenderPipelineAsset;
            if (urpAsset == null)
                return;

            FieldInfo fieldInfo = urpAsset.GetType().GetField(k_RendererDataList, BindingFlags.NonPublic | BindingFlags.Instance);

            if (fieldInfo != null)
            {
                // Get the current renderer list
                var oldRendererList = (ScriptableRendererData[])fieldInfo.GetValue(urpAsset);

                var newRendererList = new ScriptableRendererData[oldRendererList.Length - 1];
                int index = 0;
                for (int i = 0; i < oldRendererList.Length - 1; i++)
                {
                    if (oldRendererList[i] != null)
                    {
                        newRendererList[index++] = oldRendererList[i];
                    }
                }

                // Set the renderer list back to the URP Asset
                fieldInfo.SetValue(urpAsset, newRendererList);
            }

            Object.DestroyImmediate(tempRendererData);
            isTemporaryRendererAdded = false;
        }
    }

    // Ensure the temp renderer is removed from build
    public void OnPostprocessBuild(BuildReport report)
    {
        if (isTemporaryRendererAdded)
        {
            var urpAsset = GraphicsSettings.currentRenderPipeline as UniversalRenderPipelineAsset;
            if (urpAsset == null)
                return;

            FieldInfo fieldInfo = urpAsset.GetType().GetField(k_RendererDataList, BindingFlags.NonPublic | BindingFlags.Instance);

            if (fieldInfo != null)
            {
                // Get the current renderer list
                var oldRendererList = (ScriptableRendererData[])fieldInfo.GetValue(urpAsset);
                
                var newRendererList = new ScriptableRendererData[oldRendererList.Length - 1];
                int index = 0;
                for (int i = 0; i < oldRendererList.Length - 1; i++)
                {
                    if (oldRendererList[i] != null)
                    {
                        newRendererList[index++] = oldRendererList[i];
                    }
                }

                // Set the renderer list back to the URP Asset
                fieldInfo.SetValue(urpAsset, newRendererList);
            }

            Object.DestroyImmediate(tempRendererData);
            isTemporaryRendererAdded = false;
        }
    }

    /// <summary>
    /// Check if the SSGI renderer feature has been added.
    /// From "https://forum.unity.com/threads/enable-or-disable-render-features-at-runtime.932571/"
    /// </summary>
    private static readonly FieldInfo RenderDataListFieldInfo = typeof(UniversalRenderPipelineAsset).GetField(k_RendererDataList, BindingFlags.Instance | BindingFlags.NonPublic);

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
}
