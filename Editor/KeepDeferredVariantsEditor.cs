using System.Reflection;
using System.Threading.Tasks;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.Universal;
using UnityEditor;
using UnityEditor.Build;
using UnityEditor.Build.Reporting;

class KeepDeferredVariantsEditor : IPreprocessBuildWithReport, IPostprocessBuildWithReport
{
    // TODO: Only keeping variants if screen space global illumination
    public KeepDeferredVariantsEditor() { }

    // Use callbackOrder to set when Unity calls this shader preprocessor. Unity starts with the preprocessor that has the lowest callbackOrder value.
    public int callbackOrder { get { return 99; } }

    const string k_RendererDataList = "m_RendererDataList";
    const string k_SsgiRendererFeature = "ScreenSpaceGlobalIlluminationURP";

    bool isTemporaryRendererAdded = false;

    public void OnPreprocessBuild(BuildReport report)
    {
        var ssgi = GetRendererFeature(k_SsgiRendererFeature) as ScreenSpaceGlobalIlluminationURP;
        if (ssgi != null)
        {
            // Create a new temporary Deferred Renderer to keep the deferred GBuffer passes in Forward rendering path
            var rendererData = ScriptableObject.CreateInstance<UniversalRendererData>();
            rendererData.renderingMode = RenderingMode.Deferred;

            var urpAsset = GraphicsSettings.currentRenderPipeline as UniversalRenderPipelineAsset;
            if (urpAsset == null)
                return;

            FieldInfo fieldInfo = urpAsset.GetType().GetField(k_RendererDataList, BindingFlags.NonPublic | BindingFlags.Instance);

            // Get the current renderer list
            var oldRendererList = (ScriptableRendererData[])fieldInfo.GetValue(urpAsset);

            var newRendererList = new ScriptableRendererData[oldRendererList.Length + 1];
            for (int i = 0; i < oldRendererList.Length; i++)
            {
                newRendererList[i] = oldRendererList[i];
            }
            newRendererList[oldRendererList.Length] = rendererData;

            // Assign the new renderer list to the URP Asset
            fieldInfo.SetValue(urpAsset, newRendererList);

            isTemporaryRendererAdded = true;
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
