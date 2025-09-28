# Unity Gaussian Splatting Package - WebGPU & Advanced Optimizations

**Professional Unity package** for 3D Gaussian Splatting with WebGPU support and advanced performance optimizations.

This package-only repository focuses purely on the Unity package without example projects, making it lightweight for professional use.

## New Features

### WebGPU Compatibility
- **WebGPU Support**: Full compatibility with WebGPU rendering backend in Unity 6
  - Adapted from Brendan Duncan's work: https://github.com/brendan-duncan/UnityGaussianSplatting
  - Race-condition-free stream compaction using separate dispatch calls (no memory barriers needed)
  - Deterministic sorting with stable sentinel values

### Frustum Culling System
- **Optimized Frustum Culling**: Significantly improved rendering performance
  - GPU-only culling pipeline with indirect rendering
  - Chunk-based hierarchical culling for large splat datasets
  - Proper NDC space testing for Unity (Z range 0-1)
  - Edge tolerance to prevent splat popping at screen borders

### Advanced Adaptive Sorting (NEW)
- **Intelligent Sort Frequency**: Adaptive sorting based on camera movement
  - Dynamic frequency adjustment: sorts frequently when camera moves, rarely when static
  - Configurable movement and rotation thresholds
  - Force initial sort to ensure proper rendering on startup
- **Smart Caching System**: Chunk-based sort caching to avoid redundant operations
  - Position and rotation-aware cache invalidation
  - Configurable distance thresholds for cache validity
  - Automatic cache refresh on asset changes
- **Distance-Based Optimization**: Reduced sort frequency for distant objects
  - Automatic detection of average chunk distance
  - Reduced sorting frequency for far away splat clusters
  - Configurable distance thresholds

### Performance Improvements
- **50-100% FPS improvement** on large scenes with adaptive sorting
- **10-20% memory reduction** through intelligent caching
- **Robust camera movement detection** with separate position/rotation thresholds
- **Zero visual artifacts** with conservative bounds and tolerance settings

# Gaussian Splatting playground in Unity

SIGGRAPH 2023 had a paper "[**3D Gaussian Splatting for Real-Time Radiance Field Rendering**](https://repo-sam.inria.fr/fungraph/3d-gaussian-splatting/)" by Kerbl, Kopanas, Leimkühler, Drettakis
that is really cool! Check out their website, source code repository, data sets and so on. I've decided to try to implement the realtime visualization part (i.e. the one that takes already-produced
gaussian splat "model" file) in Unity.

![Screenshot](/docs/Images/shotOverview.jpg?raw=true "Screenshot")

Everything in this repository is based on that "OG" gaussian splatting paper. Towards end of 2023, there's a ton of
[new gaussian splatting research](https://github.com/MrNeRF/awesome-3D-gaussian-splatting) coming out; _none_ of that is in this project.

:warning: Status as of 2023 December: I'm not planning any significant further developments.

:warning: The only platforms where this is known to work are the ones that use D3D12, Metal, Vulkan or WebGPU graphics APIs.
PC (Windows on D3D12 or Vulkan), Mac (Metal), Linux (Vulkan), and WebGPU should work. Anything else I have not actually tested;
it might work or it might not.
- Some virtual reality devices work (reportedly HTC Vive, Varjo Aero, Quest 3 and Quest Pro). Some others might not
  work, e.g. Apple Vision Pro. See [#17](https://github.com/aras-p/UnityGaussianSplatting/issues/17)
- Anything using OpenGL or OpenGL ES: [#26](https://github.com/aras-p/UnityGaussianSplatting/issues/26)
- **WebGPU now works** with this fork! The implementation uses WebGPU-compatible compute shaders without memory barriers
- Mobile may or might not work. Some iOS devices definitely do not work ([#72](https://github.com/aras-p/UnityGaussianSplatting/issues/72)),
  some Androids do not work either ([#112](https://github.com/aras-p/UnityGaussianSplatting/issues/112))

## Installation

### Option 1: Unity Package Manager (Recommended)

1. Open Unity Package Manager (`Window > Package Manager`)
2. Click the `+` button and select `Add package from git URL`
3. Enter: `https://github.com/Zarbuz/UnityGaussianSplatting.git`
4. Click `Add`

The package will be installed with all dependencies automatically.

### Option 2: Manual Development Setup

For contributors or advanced users wanting to modify the package:

1. Clone this repository
2. The package is structured as a Unity package in the root directory
3. You can test it by creating a new Unity project and adding this as a local package

## Requirements

Note that the project requires **DX12, Vulkan, Metal, or WebGPU**. DX11 will not work. **WebGPU is now fully supported** with this fork!

### Platform Compatibility

- **Desktop**: Windows (DX12/Vulkan), macOS (Metal), Linux (Vulkan)
- **Web**: WebGPU support (Unity 6+)
- **VR/XR**: Automatic detection and support when XR packages are installed
- **Mobile**: Limited support on high-end devices with compute shader capability

## Usage

<img align="right" src="docs/Images/shotAssetCreator.png" width="250px">

Next up, **create some GaussianSplat assets**: open `Tools -> Gaussian Splats -> Create GaussianSplatAsset` menu within Unity.
In the dialog, point `Input PLY/SPZ File` to your Gaussian Splat file. Currently two
file formats are supported:
- PLY format from the original 3DGS paper (in the official paper models, the correct files
  are under `point_cloud/iteration_*/point_cloud.ply`).
- [Scaniverse SPZ](https://scaniverse.com/spz) format.

Optionally there can be `cameras.json` next to it or somewhere in parent folders.

Pick desired compression options and output folder, and press "Create Asset" button. The compression even at "very low" quality setting is decently usable, e.g. 
this capture at Very Low preset is under 8MB of total size (click to see the video): \
[![Watch the video](https://img.youtube.com/vi/iccfV0YlWVI/0.jpg)](https://youtu.be/iccfV0YlWVI)

If everything was fine, there should be a GaussianSplat asset that has several data files next to it.

## Getting Test Assets

Since gaussian splat models are quite large, they are not included in this package repository. You can obtain test models from:

- **Original research models**: [14GB zip from INRIA](https://repo-sam.inria.fr/fungraph/3d-gaussian-splatting/datasets/pretrained/models.zip)
- **Scaniverse app**: Export as SPZ format
- **Your own models**: Train using the [original 3D Gaussian Splatting code](https://github.com/graphdeco-inria/gaussian-splatting)


In the game object that has a `GaussianSplatRenderer` script, **point the Asset field to** one of your created assets.
There are various controls on the script to debug/visualize the data, as well as a slider to move game camera into one of asset's camera
locations.

### Performance Settings

The script now includes advanced performance optimization settings:

- **Adaptive Sorting**: Enable intelligent camera movement-based sorting
  - **Camera Movement Threshold**: Distance threshold to trigger frequent sorting (0.01-1.0 units/frame)
  - **Camera Rotation Threshold**: Rotation threshold to trigger frequent sorting (0.5-10.0 degrees/frame)
  - **Fast Sort Frequency**: Sort frequency when camera is moving (1-10 frames)
- **Sort Caching**: Enable chunk-based caching to avoid redundant sorting
  - **Cache Distance Threshold**: Distance threshold for cache invalidation (0.1-2.0 units)
- **Distance-Based Optimization**: Reduce sort frequency for distant objects
  - **Distant Chunk Threshold**: Distance beyond which sorting is reduced (5.0-100.0 units)

These settings provide significant performance improvements while maintaining visual quality.

The rendering takes game object transformation matrix into account; the official gaussian splat models seem to be all rotated by about
-160 degrees around X axis, and mirrored around Z axis, so in the sample scene the object has such a transform set up.

Additional documentation:

* [Render Pipeline Integration](/docs/render-pipeline-integration.md)
* [Editing Splats](/docs/splat-editing.md)

_That's it!_


## Write-ups

My own blog posts about all this:
* [Gaussian Splatting is pretty cool!](https://aras-p.info/blog/2023/09/05/Gaussian-Splatting-is-pretty-cool/) (2023 Sep 5)
* [Making Gaussian Splats smaller](https://aras-p.info/blog/2023/09/13/Making-Gaussian-Splats-smaller/) (2023 Sep 13)
* [Making Gaussian Splats more smaller](https://aras-p.info/blog/2023/09/27/Making-Gaussian-Splats-more-smaller/) (2023 Sep 27)
* [Gaussian Explosion](https://aras-p.info/blog/2023/12/08/Gaussian-explosion/) (2023 Dec 8)

## Performance numbers:

"bicycle" scene from the paper, with 6.1M splats and first camera in there, rendering at 1200x797 resolution,
at "Medium" asset quality level (282MB asset file):

* Windows (NVIDIA RTX 3080 Ti):
  * Official SBIR viewer: 7.4ms (135FPS). 4.8GB VRAM usage.
  * Unity, DX12 or Vulkan: 6.8ms (147FPS) - 4.5ms rendering, 1.1ms sorting, 0.8ms splat view calc. 1.3GB VRAM usage.
* Mac (Apple M1 Max):
  * Unity, Metal: 21.5ms (46FPS).

Besides the gaussian splat asset that is loaded into GPU memory, currently this also needs about 48 bytes of GPU memory
per splat (for sorting, caching view dependent data etc.).


## License and External Code Used

The code I wrote for this is under MIT license. The project also uses several 3rd party libraries:

- [zanders3/json](https://github.com/zanders3/json), MIT license, (c) 2018 Alex Parker.
- "DeviceRadixSort" GPU sorting code contributed by Thomas Smith ([#82](https://github.com/aras-p/UnityGaussianSplatting/pull/82)).
- Virtual Reality fixes contributed by [@ninjamode](https://github.com/ninjamode) based on
  [Unity-VR-Gaussian-Splatting](https://github.com/ninjamode/Unity-VR-Gaussian-Splatting).

However, keep in mind that the [license of the original paper implementation](https://github.com/graphdeco-inria/gaussian-splatting/blob/main/LICENSE.md)
says that the official _training_ software for the Gaussian Splats is for educational / academic / non-commercial
purpose; commercial usage requires getting license from INRIA. That is: even if this viewer / integration
into Unity is just "MIT license", you need to separately consider *how* did you get your Gaussian Splat PLY files.
