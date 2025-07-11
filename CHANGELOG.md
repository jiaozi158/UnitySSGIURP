# Changelog

All notable changes to this package will be documented in this file.

The format is based on [Keep a Changelog](http://keepachangelog.com/en/1.0.0/)
and this project adheres to [Semantic Versioning](http://semver.org/spec/v2.0.0.html).

## [1.1.5] - 2025-06-01

### Fixed

- Removed an unnecessary duplicate Blitter pass introduced in **v1.1.4**. (no visual impact on SSGI)


## [1.1.4] - 2025-05-31

### Added

- Added support for Deferred+ rendering path in Unity 6.1.
- Added support for orthographic cameras.

### Fixed

- Fixed a flickering issue when using multiple cameras with SSGI enabled.
- Fixed a rendering issue when MSAA is enabled.
- Fixed a potential green artifacts issue when SSAO is enabled.
- Fixed an issue with SSGI adding an empty deferred renderer to builds.


## [1.1.3] - 2024-11-23

### Fixed

- Fixed a shader compilation issue in Unity 2023.


## [1.1.2] - 2024-11-12

### Fixed

- Fixed a compilation issue by disabling rendering layers in Unity 2023.1. The `RenderingLayerMask` was introduced in Unity 2023.3.


## [1.1.1] - 2024-11-07

### Added

- Added a message to the volume override when the rendering debugger window is open.

### Changed

- Improved performance when SSGI falls back to APV.
- Adjusted the rules for keeping deferred shader variants when building the project.

### Fixed

- Fixed an issue with the Deferred rendering path in Unity 6 LTS.
- Fixed an issue with the rendering layers in volume override.
- Fixed an issue where APV fallback is not available in Forward and Deferred rendering paths.
- Fixed a performance issue with a shader graph skybox.
- Fixed a rendering order issue with volumetric clouds.


## [1.1.0] - 2024-09-16

### Added

- Added new fallback options and support for Adaptive Probe Volumes (APV):
  - **Sky**: Falls back to the sky ambient probe or APV.
  - **Reflection Probes and Sky**: Falls back to reflection probes (if any), then to the sky ambient probe or APV.

### Changed

- Changed the default value of ray miss property from **Reflection Probes** to **Reflection Probes and Sky**.
- Improved the quality of SSGI across different resolution scales.


## [1.0.10] - 2024-09-05

### Fixed

- Fixed an issue where the SSGI quality did not match the volume override when denoising was disabled.


## [1.0.9] - 2024-09-02

### Added

- Added **High Quality Upscaling** option to re-enable the previous upscaling method.
- Added **Artistic Overrides** header to the **Indirect Diffuse Lighting Multiplier** property.

### Changed

- Improved SSGI performance by reducing arithmetic and texture sampling operations.

### Fixed

- Fixed an issue with incorrect depth precision on mobile platforms.
- Fixed an issue where a temporary camera texture did not follow URP render scale.


## [1.0.8] - 2024-08-30

### Added

- Added three global shader keywords to indicate the state of resources created by SSGI:
  - `SSGI_RENDER_GBUFFER`
  - `SSGI_RENDER_BACKFACE_DEPTH`
  - `SSGI_RENDER_BACKFACE_COLOR`

### Fixed

- Fixed an issue where the history texture was set before allocation.
- Fixed an issue with **Backface Lighting** when using the Deferred rendering path.


## [1.0.7] - 2024-08-30

### Fixed

- Fixed an issue where the color format of direct lighting texture was incorrect in some cases.
- Fixed a regression with the deferred shader variants in URP 17.


## [1.0.6] - 2024-08-29

### Changed

- Changed the **Indirect Diffuse Lighting Multiplier** property type to always visible.

### Fixed

- Fixed an issue where deferred shader variants were stripped by URP 14 during the build.
- Fixed an issue with SSGI on mobile devices.


## [1.0.5] - 2024-08-01

### Changed

- Changed shader includes from absolute paths to relative paths to avoid potential issues in certain situations.


## [1.0.4] - 2024-07-17

### Fixed

- Fixed an issue where previous camera color texture was not released.


## [1.0.3] - 2024-07-17

### Added

- Implemented infinite bounce indirect lighting using the previous camera color texture.
- Enhanced aggressive denoising mode by adding **Poisson Disk Recurrent Denoising**.

### Changed

- Improved the pre-denoising logic to achieve more stable results.


## [1.0.2] - 2024-07-14

### Fixed

- Fixed an issue from URP where motion vectors in scene view were incorrectly rendered in Unity 2022 & 2023.
- Fixed an issue where previous depth texture was incorrectly set as the current one, causing severe ghosting.
- Fixed an issue where textures may not be set when Render Graph is enabled.
- Fixed an issue with Automatic Thickness Mode.
- Fixed an issue with Indirect Diffuse Rendering Layers.
- Fixed an issue with incorrect surface normals when Accurate G-buffer Normals was enabled.
- Fixed an issue with random sampling direction being broken on platforms using half-precision floats.


## [1.0.1] - 2024-07-12

### Changed

- Increased minimum supported Unity version to 2022.3.35f1 (from 2022.3.0f1).

### Fixed

- Resolved compiling errors in Unity Editor 2022 & 2023.


## [1.0.0] - 2024-07-11

### Added

- Initial release of this package.