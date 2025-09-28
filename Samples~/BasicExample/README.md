# Basic Gaussian Splatting Example

This sample demonstrates how to use the Gaussian Splatting package with the new performance optimizations.

## Setup

1. Import this sample via Package Manager
2. Open the included scene
3. Follow the main package documentation to create GaussianSplat assets from PLY files
4. Assign your asset to the GaussianSplatRenderer component
5. Experiment with the performance settings:
   - Enable Adaptive Sorting for movement-based optimization
   - Adjust thresholds to fit your scene and camera behavior
   - Use Frustum Culling for large scenes
   - Enable caching for static or slow-moving cameras

## Performance Settings Guide

### Adaptive Sorting
- **Camera Movement Threshold**: Lower values = more sensitive to small movements
- **Camera Rotation Threshold**: Lower values = more sensitive to rotations
- **Fast Sort Frequency**: How often to sort when moving (1 = every frame)

### Caching
- **Cache Distance Threshold**: Distance camera must move to invalidate cache
- Enable for scenes where camera pauses frequently

### Distance-Based Optimization
- **Distant Chunk Threshold**: Objects beyond this distance sort less frequently
- Useful for large outdoor scenes with distant objects

For complete documentation, see the main package README.