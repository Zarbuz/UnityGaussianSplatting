# CLAUDE.md

This file provides guidance to Claude Code (claude.ai/code) when working with code in this repository.

## Unity Package Architecture

This is a Unity package for 3D Gaussian Splatting rendering and tools. The package follows Unity's package structure:

- **Runtime/**: Core rendering components that work in builds
  - `GaussianSplatRenderer.cs`: Main component for rendering Gaussian splats
  - `GaussianSplatAsset.cs`: Asset type that stores splat data
  - `GaussianSplatRenderSystem.cs`: Singleton that manages splat rendering across cameras
  - `GpuSorting.cs`: GPU-based sorting for splats
  - `GaussianSplatURPFeature.cs`: Universal Render Pipeline integration
  - `GaussianSplatHDRPPass.cs`: High Definition Render Pipeline integration

- **Editor/**: Unity Editor tools and utilities
  - Asset importing from PLY, SPZ, and other formats
  - Visual editing tools for moving, rotating, and scaling splats
  - Custom inspectors and validation

- **Shaders/**: HLSL shaders and compute shaders
  - `RenderGaussianSplats.shader`: Main rendering shader
  - `GaussianSplatting.hlsl`: Core splatting math
  - `DeviceRadixSort.hlsl`: GPU sorting implementation

## Development Commands

This is a Unity package, so development happens within Unity Editor:

1. **Testing**: Open one of the example projects:
   - `../projects/GaussianExample/` (Built-in RP)
   - `../projects/GaussianExample-URP/` (Universal RP)
   - `../projects/GaussianExample-HDRP/` (High Definition RP)

2. **Package Installation**: The package can be installed via:
   - Package Manager using git URL
   - Local package reference in manifest.json

## Key Architecture Concepts

### Render Pipeline Integration
The package supports all three Unity render pipelines through conditional compilation:
- Built-in RP: Uses Camera callbacks and CommandBuffers
- URP: Custom renderer feature (`GaussianSplatURPFeature`)
- HDRP: Custom render pass (`GaussianSplatHDRPPass`)

### GPU Data Management
Splat data is stored in multiple GraphicsBuffers:
- Position data (`m_GpuPosData`)
- Rotation/scale data (`m_GpuOtherData`)
- Spherical harmonics data (`m_GpuSHData`)
- Color data (Texture2D)

### Sorting System
Critical for proper blending - splats must be sorted back-to-front:
- Two sorting modes: Radix (DX12/Vulkan) and FFX (fallback)
- Sorting happens every N frames (configurable)
- Uses GPU compute shaders for performance

### Editing System
Runtime editing capabilities using GPU compute:
- Selection system with bit buffers
- Transform operations (translate, rotate, scale)
- Copy/paste between renderers
- Export functionality

## File Format Support

The package imports various splat file formats:
- PLY files (standard format)
- SPZ files (compressed format)
- Gaussian splatting training outputs

## Assembly Definitions

- `GaussianSplatting.asmdef`: Runtime assembly with conditional defines for render pipelines
- `GaussianSplattingEditor.asmdef`: Editor-only assembly for tools

## Dependencies

Key Unity packages required:
- Unity Burst (performance)
- Unity Collections (native containers)
- Unity Mathematics (math functions)
- Render pipeline packages (URP/HDRP) for respective features