using System;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.Universal;

// Remove later:

//////////////////////////////////////////////////////
// This defines a custom VolumeComponent to be used with the Core VolumeFramework
// (see core API docs https://docs.unity3d.com/Packages/com.unity.render-pipelines.core@latest/index.html?subfolder=/api/UnityEngine.Rendering.VolumeComponent.html)
// (see URP docs https://docs.unity3d.com/Packages/com.unity.render-pipelines.universal@latest/index.html?subfolder=/manual/Volumes.html)
//
// After implementing this class you can:
// * Tweak the default values for this VolumeComponent in the URP GlobalSettings
// * Add overrides for this VolumeComponent to any local or global scene volume profiles
//   (see URP docs https://docs.unity3d.com/Packages/com.unity.render-pipelines.universal@latest/index.html?subfolder=/manual/Volume-Profile.html)
//   (see URP docs https://docs.unity3d.com/Packages/com.unity.render-pipelines.universal@latest/index.html?subfolder=/manual/VolumeOverrides.html)
// * Access the blended values of this VolumeComponent from your ScriptableRenderPasses or scripts using the VolumeManager API
//   (see core API docs https://docs.unity3d.com/Packages/com.unity.render-pipelines.core@latest/index.html?subfolder=/api/UnityEngine.Rendering.VolumeManager.html)
// * Override the values for this volume per-camera by placing a VolumeProfile in a dedicated layer and setting the camera's "Volume Mask" to that layer
//
// Things to keep in mind:
// * Be careful when renaming, changing types or removing public fields to not break existing instances of this class (note that this class inherits from ScriptableObject so the same serialization rules apply)
// * The 'IPostProcessComponent' interface adds the 'IsActive()' method, which is currently not strictly necessary and is for your own convenience
// * It is recommended to only expose fields that are expected to change. Fields which are constant such as shaders, materials or LUT textures
//   should likely be in AssetBundles or referenced by serialized fields of your custom ScriptableRendererFeatures on used renderers so they would not get stripped during builds
//////////////////////////////////////////////////////

#if UNITY_2023_1_OR_NEWER
[VolumeComponentMenu("Lighting/Screen Space Global Illumination (URP)"), SupportedOnRenderPipeline(typeof(UniversalRenderPipelineAsset))]
#else
[VolumeComponentMenuForRenderPipeline("Lighting/Screen Space Global Illumination (URP)", typeof(UniversalRenderPipeline))]
#endif
#if UNITY_2023_3_OR_NEWER
[VolumeRequiresRendererFeatures(typeof(ScreenSpaceGlobalIlluminationURP))]
#endif
[HelpURL("https://github.com/jiaozi158/UnitySSGIURP/blob/main/Documentation~/Documentation.md")]
public sealed class ScreenSpaceGlobalIlluminationVolume : VolumeComponent, IPostProcessComponent
{
    public ScreenSpaceGlobalIlluminationVolume()
    {
        displayName = "Screen Space Global Illumination";
    }

#if !UNITY_2023_2_OR_NEWER
    // This is unused since 2023.1
    public bool IsTileCompatible() => false;
#endif

    /// <summary>
    /// Enable screen space global illumination.
    /// </summary>
    [Tooltip("Enable screen space global illumination.")]
    public BoolParameter enable = new BoolParameter(false, BoolParameter.DisplayType.EnumPopup);

    /// <summary>
    /// Controls the thickness mode of screen space global illumination.
    /// </summary>
    [Tooltip("The thickness mode of screen space global illumination.")]
    public ThicknessParameter thicknessMode = new(value: ThicknessMode.Constant, overrideState: false);

    /// <summary>
    /// The thickness (or fallback thickness) of the depth buffer value used for the ray marching.
    /// </summary>
    [Tooltip("Controls the thickness (or fallback thickness) of the depth buffer used for ray marching.")]
    public ClampedFloatParameter depthBufferThickness = new ClampedFloatParameter(0.1f, 0.0f, 0.5f);

    /// <summary>
    /// Gets or sets the quality of screen space global illumination.
    /// </summary>
    public RayMarchingModeParameter qualityMode
    {
        get { return quality; }
        set { quality = value; ApplyCurrentQualityMode(); }
    }

    /// <summary>
    /// Gets the current screen space global illumination quality.
    /// </summary>
    [Tooltip("Specifies the quality of screen space global illumination.")]
    public RayMarchingModeParameter quality = new(QualityMode.Low, overrideState: false);

    /// <summary>
    /// Defines if the screen space global illumination should be evaluated at full resolution.
    /// </summary>
    [InspectorName("Full Resolution"), Tooltip("Controls if the screen space global illumination should be evaluated at full resolution.")]
    public BoolParameter fullResolutionSS = new BoolParameter(false);

    /// <summary>
    /// Defines the resolution used to evaluate screen space global illumination.
    /// This should not be changed frequently.
    /// </summary>
    [InspectorName("Resolution Scale"), Tooltip("Controls the resolution used to evaluate screen space global illumination.")]
    public NoInterpClampedFloatParameter resolutionScaleSS = new NoInterpClampedFloatParameter(0.5f, 0.25f, 0.75f);

    /// <summary>
    /// The number of samples for global illumination.
    /// </summary>
    [Tooltip("Controls the number of samples for global illumination.")]
    public ClampedIntParameter sampleCount = new ClampedIntParameter(2, 1, 16);

    /// <summary>
    /// The number of steps that should be used during ray marching.
    /// </summary>
    [Tooltip("Controls the number of steps used for ray marching.")]
    public MinIntParameter maxRaySteps = new MinIntParameter(32, 16);

    /// <summary>
    /// Defines if the screen space global illumination should be denoised.
    /// </summary>
    [InspectorName("Denoise"), Tooltip("Controls if the screen space global illumination should be denoised.")]
    public BoolParameter denoiseSS = new BoolParameter(true);

    /// <summary>
    /// Defines the denoising mode for screen space global illumination.
    /// </summary>
    [InspectorName("Algorithm"), Tooltip("Controls the denoising mode for screen space global illumination.")]
    public DenoiserAlgorithmParameter denoiserAlgorithmSS = new DenoiserAlgorithmParameter(DenoiserAlgorithm.Aggressive, false);

    /// <summary>
    /// Defines the intensity of temporal denoising pass.
    /// </summary>
    [InspectorName("Intensity"), Tooltip("Controls the intensity of temporal denoising pass.")]
    public ClampedFloatParameter denoiseIntensitySS = new ClampedFloatParameter(0.95f, 0.5f, 0.95f, false);

    /// <summary>
    /// Defines the radius of the GI denoiser (First Pass).
    /// </summary>
    [InspectorName("Denoiser Radius"), Tooltip("Controls the radius of the GI denoiser (First Pass).")]
    public ClampedFloatParameter denoiserRadiusSS = new ClampedFloatParameter(0.6f, 0.001f, 1.0f, false);

    /// <summary>
    /// Defines if the second denoising pass should be enabled.
    /// </summary>
    [InspectorName("Second Denoiser Pass"), Tooltip("Enable second denoising pass.")]
    public BoolParameter secondDenoiserPassSS = new BoolParameter(true);

    /// <summary>
    /// Controls the fallback hierarchy for indirect diffuse in case the ray misses.
    /// </summary>
    [AdditionalProperty, Tooltip("Controls the fallback hierarchy for indirect diffuse in case the ray misses.")]
    public RayMarchingFallbackHierarchyParameter rayMiss = new RayMarchingFallbackHierarchyParameter(RayMarchingFallbackHierarchy.ReflectionProbes);

    /// <summary>
    /// Controls the indirect diffuse lighting from screen space global illumination.
    /// </summary>
    [Header("Artistic Overrides"), InspectorName("Indirect Diffuse Lighting Multiplier"), Tooltip("Controls the indirect diffuse lighting from screen space global illumination.")]
    public MinFloatParameter indirectDiffuseLightingMultiplier = new MinFloatParameter(1.0f, 0.0f);

#if UNITY_2023_1_OR_NEWER
    /// <summary>
    /// Controls which rendering layer will be affected by screen space global illumination.
    /// </summary>
    [Header("Experimental"), AdditionalProperty, InspectorName("Indirect Diffuse Rendering Layers"), Tooltip("Controls which rendering layer will be affected by screen space global illumination.")]
    public RenderingLayerEnumParameter indirectDiffuseRenderingLayers = new RenderingLayerEnumParameter(0xFFFF);
#endif

    public bool IsActive()
    {
    #if UNITY_2023_1_OR_NEWER
        return enable.value && indirectDiffuseRenderingLayers.value.value != 0; // 0 -> RenderingLayerMask.Nothing
    #else
        return enable.value;
    #endif
    }

    public enum DenoiserAlgorithm
    {
        [Tooltip("Produces results with more detail, but may retain some noise.")]
        Conservative = 0,

        [Tooltip("Produces cleaner results.")]
        Aggressive = 1
    }

    /// <summary>
    /// A <see cref="VolumeParameter"/> that holds a <see cref="DenoiserAlgorithm"/> value.
    /// </summary>
    [Serializable]
    public sealed class DenoiserAlgorithmParameter : VolumeParameter<DenoiserAlgorithm>
    {
        /// <summary>
        /// Creates a new <see cref="DenoiserAlgorithmParameter"/> instance.
        /// </summary>
        /// <param name="value">The initial value to store in the parameter.</param>
        /// <param name="overrideState">The initial override state for the parameter.</param>
        public DenoiserAlgorithmParameter(DenoiserAlgorithm value, bool overrideState = false) : base(value, overrideState) { }
    }

    public enum ThicknessMode
    {
        [InspectorName("Constant"), Tooltip("Apply constant thickness to every scene object.")]
        Constant = 0,

        [InspectorName("Automatic"), Tooltip("Render the back-faces of scene objects to compute thickness.")]
        ComputeBackface = 1
    }

    /// <summary>
    /// A <see cref="VolumeParameter"/> that holds a <see cref="ThicknessMode"/> value.
    /// </summary>
    [Serializable]
    public sealed class ThicknessParameter : VolumeParameter<ThicknessMode>
    {
        /// <summary>
        /// Creates a new <see cref="SSGIThicknessParameter"/> instance.
        /// </summary>
        /// <param name="value">The initial value to store in the parameter.</param>
        /// <param name="overrideState">The initial override state for the parameter.</param>
        public ThicknessParameter(ThicknessMode value, bool overrideState = false) : base(value, overrideState) { }
    }

    public enum QualityMode
    {
        /// <summary>
        /// When selected, choices are made to reduce execution time of the effect.
        /// </summary>
        [Tooltip("When selected, choices are made to reduce execution time of the effect.")]
        Low = 0,

        /// <summary>
        /// When selected, choices are made to increase the visual quality of the effect.
        /// </summary>
        [Tooltip("When selected, choices are made to increase the visual quality of the effect.")]
        Medium = 1,

        /// <summary>
        /// When selected, choices are made to increase the visual quality of the effect.
        /// </summary>
        [Tooltip("When selected, choices are made to increase the visual quality of the effect.")]
        High = 2,

        /// <summary>
        /// When selected, choices are made to increase the visual quality of the effect.
        /// </summary>
        [Tooltip("When selected, choices are made to increase the visual quality of the effect.")]
        Custom = 3
    }

    /// <summary>
    /// A <see cref="VolumeParameter"/> that holds a <see cref="QualityMode"/> value.
    /// </summary>
    [Serializable]
    public sealed class RayMarchingModeParameter : VolumeParameter<QualityMode>
    {
        /// <summary>
        /// Creates a new <see cref="RayMarchingModeParameter"/> instance.
        /// </summary>
        /// <param name="value">The initial value to store in the parameter.</param>
        /// <param name="overrideState">The initial override state for the parameter.</param>
        public RayMarchingModeParameter(QualityMode value, bool overrideState = false) : base(value, overrideState) { }
    }

    /// <summary>
    /// This defines the order in which the fall backs are used if a screen space global illumination ray misses.
    /// </summary>
    public enum RayMarchingFallbackHierarchy
    {
        /// <summary>
        /// When selected, ray marching will return a black color.
        /// </summary>
        [InspectorName("Nothing"), Tooltip("When selected, ray marching will return a black color.")]
        None = 0,

        /// <summary>
        /// When selected, ray marching will fall back on reflection probes (if any).
        /// </summary>
        [InspectorName("Reflection Probes"), Tooltip("When selected, ray marching will fall back on reflection probes (if any).")]
        ReflectionProbes = 1
    }

    /// <summary>
    /// A <see cref="VolumeParameter"/> that holds
    /// <see cref="RayMarchingFallbackHierarchy"/> value.
    /// </summary>
    [Serializable]
    public sealed class RayMarchingFallbackHierarchyParameter : VolumeParameter<RayMarchingFallbackHierarchy>
    {
        /// <summary>
        /// Creates a new <see cref="RayMarchingFallbackHierarchyParameter"/> instance.
        /// </summary>
        /// <param name="value">The initial value to store in the parameter.</param>
        /// <param name="overrideState">The initial override state for the parameter.</param>
        public RayMarchingFallbackHierarchyParameter(RayMarchingFallbackHierarchy value, bool overrideState = false) : base(value, overrideState) { }
    }

#if UNITY_2023_1_OR_NEWER
    /// <summary>
    /// A <see cref="VolumeParameter"/> that holds
    /// <see cref="RenderingLayerMask"/> value.
    /// </summary>
    [Serializable]
    public sealed class RenderingLayerEnumParameter : VolumeParameter<RenderingLayerMask>
    {
        /// <summary>
        /// Creates a new <see cref="RenderingLayerEnumParameter"/> instance.
        /// </summary>
        /// <param name="value">The initial value to store in the parameter.</param>
        /// <param name="overrideState">The initial override state for the parameter.</param>
        public RenderingLayerEnumParameter(RenderingLayerMask value, bool overrideState = false) : base(value, overrideState) { }
    }
#endif

    private void ApplyCurrentQualityMode()
    {
        // Apply the currently set preset
        switch (quality.value)
        {
            case QualityMode.Low:
            {
                sampleCount.value = 1;
                maxRaySteps.value = 24;
            }
            break;
            case QualityMode.Medium:
            {
                sampleCount.value = 2;
                maxRaySteps.value = 32;
            }
            break;
            case QualityMode.High:
            {
                sampleCount.value = 4;
                maxRaySteps.value = 64;
            }
            break;
            default:
                break;
        }
    }
}
