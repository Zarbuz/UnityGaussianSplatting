// SPDX-License-Identifier: MIT

using System;
using System.Collections.Generic;
using Unity.Collections.LowLevel.Unsafe;
using Unity.Profiling;
using Unity.Profiling.LowLevel;
using UnityEngine;
using UnityEngine.Experimental.Rendering;
using UnityEngine.Rendering;
#if GS_ENABLE_VR
using UnityEngine.XR;
#endif

namespace GaussianSplatting.Runtime
{
	class GaussianSplatRenderSystem
	{
		// ReSharper disable MemberCanBePrivate.Global - used by HDRP/URP features that are not always compiled
		internal static readonly ProfilerMarker s_ProfDraw = new(ProfilerCategory.Render, "GaussianSplat.Draw", MarkerFlags.SampleGPU);
		internal static readonly ProfilerMarker s_ProfCompose = new(ProfilerCategory.Render, "GaussianSplat.Compose", MarkerFlags.SampleGPU);
		internal static readonly ProfilerMarker s_ProfCalcView = new(ProfilerCategory.Render, "GaussianSplat.CalcView", MarkerFlags.SampleGPU);
		// ReSharper restore MemberCanBePrivate.Global

		public static GaussianSplatRenderSystem instance => ms_Instance ??= new GaussianSplatRenderSystem();
		static GaussianSplatRenderSystem ms_Instance;

		readonly Dictionary<GaussianSplatRenderer, MaterialPropertyBlock> m_Splats = new();
		readonly HashSet<Camera> m_CameraCommandBuffersDone = new();
		readonly List<(GaussianSplatRenderer, MaterialPropertyBlock)> m_ActiveSplats = new();

		CommandBuffer m_CommandBuffer;

		public void RegisterSplat(GaussianSplatRenderer r)
		{
			if (m_Splats.Count == 0)
			{
				if (GraphicsSettings.currentRenderPipeline == null)
					Camera.onPreCull += OnPreCullCamera;
			}

			m_Splats.Add(r, new MaterialPropertyBlock());
		}

		public void UnregisterSplat(GaussianSplatRenderer r)
		{
			if (!m_Splats.ContainsKey(r))
				return;
			m_Splats.Remove(r);
			if (m_Splats.Count == 0)
			{
				if (m_CameraCommandBuffersDone != null)
				{
					if (m_CommandBuffer != null)
					{
						foreach (var cam in m_CameraCommandBuffersDone)
						{
							if (cam)
								cam.RemoveCommandBuffer(CameraEvent.BeforeForwardAlpha, m_CommandBuffer);
						}
					}
					m_CameraCommandBuffersDone.Clear();
				}

				m_ActiveSplats.Clear();
				m_CommandBuffer?.Dispose();
				m_CommandBuffer = null;
				Camera.onPreCull -= OnPreCullCamera;
			}
		}

		// ReSharper disable once MemberCanBePrivate.Global - used by HDRP/URP features that are not always compiled
		public bool GatherSplatsForCamera(Camera cam)
		{
			if (cam.cameraType == CameraType.Preview)
				return false;
			// gather all active & valid splat objects
			m_ActiveSplats.Clear();
			foreach (var kvp in m_Splats)
			{
				var gs = kvp.Key;
				if (gs == null || !gs.isActiveAndEnabled || !gs.HasValidAsset || !gs.HasValidRenderSetup)
					continue;
				m_ActiveSplats.Add((kvp.Key, kvp.Value));
			}
			if (m_ActiveSplats.Count == 0)
				return false;

			// sort them by order and depth from camera
			var camTr = cam.transform;
			m_ActiveSplats.Sort((a, b) =>
			{
				var orderA = a.Item1.m_RenderOrder;
				var orderB = b.Item1.m_RenderOrder;
				if (orderA != orderB)
					return orderB.CompareTo(orderA);
				var trA = a.Item1.transform;
				var trB = b.Item1.transform;
				var posA = camTr.InverseTransformPoint(trA.position);
				var posB = camTr.InverseTransformPoint(trB.position);
				return posA.z.CompareTo(posB.z);
			});

			return true;
		}

		// ReSharper disable once MemberCanBePrivate.Global - used by HDRP/URP features that are not always compiled
		public Material SortAndRenderSplats(Camera cam, CommandBuffer cmb)
		{
			Material matComposite = null;
			foreach (var kvp in m_ActiveSplats)
			{
				var gs = kvp.Item1;
				gs.EnsureMaterials();
				matComposite = gs.m_MatComposite;
				var mpb = kvp.Item2;

				// Sort runs based on adaptive sorting logic
				var matrix = gs.transform.localToWorldMatrix;
				bool shouldSort = gs.ShouldSort(cam);
				if (shouldSort)
				{
					if (gs.m_FrustumCullingEnabled)
					{
						gs.UpdateFrustumCulling(cmb, cam, matrix);
					}
					else
					{
						// Initialize buffers when culling is disabled (only when sorting)
						gs.InitializeBuffersForNoCulling(cmb);
					}

					gs.SortPoints(cmb, cam, matrix);
				}
				else if (gs.m_FrustumCullingEnabled)
				{
					// Update frustum culling even when not sorting, to keep visibility data fresh
					gs.UpdateFrustumCulling(cmb, cam, matrix);
				}
				++gs.m_FrameCounter;
				++gs.m_AdaptiveSortFrameCounter;

				// cache view
				kvp.Item2.Clear();
				Material displayMat = gs.m_RenderMode switch
				{
					GaussianSplatRenderer.RenderMode.DebugPoints => gs.m_MatDebugPoints,
					GaussianSplatRenderer.RenderMode.DebugPointIndices => gs.m_MatDebugPoints,
					GaussianSplatRenderer.RenderMode.DebugBoxes => gs.m_MatDebugBoxes,
					GaussianSplatRenderer.RenderMode.DebugChunkBounds => gs.m_MatDebugBoxes,
					_ => gs.m_MatSplats
				};
				if (displayMat == null)
					continue;

				gs.SetAssetDataOnMaterial(mpb);
				mpb.SetBuffer(GaussianSplatRenderer.Props.SplatChunks, gs.m_GpuChunks);

				mpb.SetBuffer(GaussianSplatRenderer.Props.SplatViewData, gs.m_GpuView);

				mpb.SetBuffer(GaussianSplatRenderer.Props.OrderBuffer, gs.m_GpuSortKeys);
				mpb.SetFloat(GaussianSplatRenderer.Props.SplatScale, gs.m_SplatScale);
				mpb.SetFloat(GaussianSplatRenderer.Props.SplatOpacityScale, gs.m_OpacityScale);
				mpb.SetFloat(GaussianSplatRenderer.Props.SplatSize, gs.m_PointDisplaySize);
				mpb.SetInteger(GaussianSplatRenderer.Props.SHOrder, gs.m_SHOrder);
				mpb.SetInteger(GaussianSplatRenderer.Props.SHOnly, gs.m_SHOnly ? 1 : 0);
				mpb.SetInteger(GaussianSplatRenderer.Props.DisplayIndex, gs.m_RenderMode == GaussianSplatRenderer.RenderMode.DebugPointIndices ? 1 : 0);
				mpb.SetInteger(GaussianSplatRenderer.Props.DisplayChunks, gs.m_RenderMode == GaussianSplatRenderer.RenderMode.DebugChunkBounds ? 1 : 0);

				cmb.BeginSample(s_ProfCalcView);
				gs.CalcViewData(cmb, cam);
				cmb.EndSample(s_ProfCalcView);

				// draw
				int indexCount = 6;
				int instanceCount = gs.splatCount;
				MeshTopology topology = MeshTopology.Triangles;
				if (gs.m_RenderMode is GaussianSplatRenderer.RenderMode.DebugBoxes or GaussianSplatRenderer.RenderMode.DebugChunkBounds)
					indexCount = 36;
				if (gs.m_RenderMode == GaussianSplatRenderer.RenderMode.DebugChunkBounds)
					instanceCount = gs.m_GpuChunksValid ? gs.m_GpuChunks.count : 0;

				cmb.BeginSample(s_ProfDraw);

				// Use indirect draw if frustum culling is enabled, otherwise use direct draw
				if (gs.m_FrustumCullingEnabled && gs.m_RenderMode == GaussianSplatRenderer.RenderMode.Splats)
				{
					// Update indirect arguments with visible splat count
					gs.UpdateIndirectArgs(cmb, indexCount, topology);

					// Use indirect draw for optimized rendering
					cmb.DrawProceduralIndirect(gs.m_GpuIndexBuffer, matrix, displayMat, 0, topology, gs.m_GpuIndirectArgs, 0, mpb);
				}
				else
				{
					// Use direct draw for debug modes or when culling is disabled
					cmb.DrawProcedural(gs.m_GpuIndexBuffer, matrix, displayMat, 0, topology, indexCount, instanceCount, mpb);
				}

				cmb.EndSample(s_ProfDraw);
			}
			return matComposite;
		}

		// ReSharper disable once MemberCanBePrivate.Global - used by HDRP/URP features that are not always compiled
		// ReSharper disable once UnusedMethodReturnValue.Global - used by HDRP/URP features that are not always compiled
		public CommandBuffer InitialClearCmdBuffer(Camera cam)
		{
			m_CommandBuffer ??= new CommandBuffer { name = "RenderGaussianSplats" };
			if (GraphicsSettings.currentRenderPipeline == null && cam != null && !m_CameraCommandBuffersDone.Contains(cam))
			{
				cam.AddCommandBuffer(CameraEvent.BeforeForwardAlpha, m_CommandBuffer);
				m_CameraCommandBuffersDone.Add(cam);
			}

			// get render target for all splats
			m_CommandBuffer.Clear();
			return m_CommandBuffer;
		}

		void OnPreCullCamera(Camera cam)
		{
			if (!GatherSplatsForCamera(cam))
				return;

			InitialClearCmdBuffer(cam);

			m_CommandBuffer.GetTemporaryRT(GaussianSplatRenderer.Props.GaussianSplatRT, -1, -1, 0, FilterMode.Point, GraphicsFormat.R16G16B16A16_SFloat);
			m_CommandBuffer.SetRenderTarget(GaussianSplatRenderer.Props.GaussianSplatRT, BuiltinRenderTextureType.CurrentActive);
			m_CommandBuffer.ClearRenderTarget(RTClearFlags.Color, new Color(0, 0, 0, 0), 0, 0);

			// We only need this to determine whether we're rendering into backbuffer or not. However, detection this
			// way only works in BiRP so only do it here.
			m_CommandBuffer.SetGlobalTexture(GaussianSplatRenderer.Props.CameraTargetTexture, BuiltinRenderTextureType.CameraTarget);

			// add sorting, view calc and drawing commands for each splat object
			Material matComposite = SortAndRenderSplats(cam, m_CommandBuffer);

			// compose
			m_CommandBuffer.BeginSample(s_ProfCompose);
			m_CommandBuffer.SetRenderTarget(BuiltinRenderTextureType.CameraTarget);
			m_CommandBuffer.DrawProcedural(Matrix4x4.identity, matComposite, 0, MeshTopology.Triangles, 3, 1);
			m_CommandBuffer.EndSample(s_ProfCompose);
			m_CommandBuffer.ReleaseTemporaryRT(GaussianSplatRenderer.Props.GaussianSplatRT);
		}
	}

	[ExecuteInEditMode]
	public class GaussianSplatRenderer : MonoBehaviour
	{
		public enum RenderMode
		{
			Splats,
			DebugPoints,
			DebugPointIndices,
			DebugBoxes,
			DebugChunkBounds,
		}
		public GaussianSplatAsset m_Asset;

		[Tooltip("Rendering order compared to other splats. Within same order splats are sorted by distance. Higher order splats render 'on top of' lower order splats.")]
		public int m_RenderOrder;
		[Range(0.1f, 2.0f)]
		[Tooltip("Additional scaling factor for the splats")]
		public float m_SplatScale = 1.0f;
		[Range(0.05f, 20.0f)]
		[Tooltip("Additional scaling factor for opacity")]
		public float m_OpacityScale = 1.0f;
		[Range(0, 3)]
		[Tooltip("Spherical Harmonics order to use")]
		public int m_SHOrder = 3;
		[Tooltip("Show only Spherical Harmonics contribution, using gray color")]
		public bool m_SHOnly;
		[Range(1, 30)]
		[Tooltip("Base sort frequency - sort splats only every N frames when camera is static")]
		public int m_SortNthFrame = 1;
		[Tooltip("Enable adaptive sorting based on camera movement")]
		public bool m_AdaptiveSortingEnabled = true;
		[Range(0.01f, 1.0f)]
		[Tooltip("Camera movement threshold to trigger frequent sorting (units/frame)")]
		public float m_CameraMovementThreshold = 0.1f;
		[Range(0.5f, 10.0f)]
		[Tooltip("Camera rotation threshold to trigger frequent sorting (degrees/frame)")]
		public float m_CameraRotationThreshold = 1.0f;
		[Range(1, 10)]
		[Tooltip("Multiplier for sort frequency when camera is moving fast")]
		public int m_FastSortFrequency = 1;
		[Tooltip("Enable chunk-based sort caching to avoid re-sorting static chunks")]
		public bool m_ChunkSortCacheEnabled = true;
		[Range(0.1f, 2.0f)]
		[Tooltip("Distance threshold for chunk sort cache invalidation")]
		public float m_ChunkCacheDistanceThreshold = 0.5f;
		[Tooltip("Enable distance-based sort frequency optimization")]
		public bool m_DistanceBasedSortEnabled = true;
		[Range(5.0f, 100.0f)]
		[Tooltip("Distance threshold for reducing sort frequency on distant chunks")]
		public float m_DistantChunkThreshold = 20.0f;
		[Tooltip("Enable frustum culling to improve performance by not processing splats outside camera view")]
		public bool m_FrustumCullingEnabled = true;
		[Range(0.5f, 10.0f)]
		[Tooltip("Frustum culling tolerance to avoid cutting splats at screen edges (higher values = more stable, less culling)")]
		public float m_FrustumCullingTolerance = 2.0f;

		public RenderMode m_RenderMode = RenderMode.Splats;
		[Range(1.0f, 15.0f)] public float m_PointDisplaySize = 3.0f;

		public Shader m_ShaderSplats;
		public Shader m_ShaderComposite;
		public Shader m_ShaderDebugPoints;
		public Shader m_ShaderDebugBoxes;

		[Tooltip("Gaussian splatting compute shader")]
		public ComputeShader m_CSSplatUtilitiesFFX;
		public ComputeShader m_CSStreamCompact;

		int m_SplatCount; // initially same as asset splat count, but editing can change this
		GraphicsBuffer m_GpuSortDistances;
		internal GraphicsBuffer m_GpuSortKeys;
		GraphicsBuffer m_GpuPosData;
		GraphicsBuffer m_GpuPosDataTemp;
		GraphicsBuffer m_GpuOtherData;
		GraphicsBuffer m_GpuSHData;
		Texture m_GpuColorData;
		internal GraphicsBuffer m_GpuChunks;
		internal bool m_GpuChunksValid;
		internal GraphicsBuffer m_GpuView;
		internal GraphicsBuffer m_GpuIndexBuffer;

		// these buffers are only for splat editing, and are lazily created
		GraphicsBuffer m_GpuEditCutouts;
		GraphicsBuffer m_GpuEditCountsBounds;
		GraphicsBuffer m_GpuEditSelected;
		GraphicsBuffer m_GpuEditDeleted;
		GraphicsBuffer m_GpuEditSelectedMouseDown; // selection state at start of operation
		GraphicsBuffer m_GpuEditPosMouseDown; // position state at start of operation
		GraphicsBuffer m_GpuEditOtherMouseDown; // rotation/scale state at start of operation

		// Frustum culling buffers
		GraphicsBuffer m_GpuChunkVisibility;    // per chunk: 0=hidden, 1=visible, 2=partial
		GraphicsBuffer m_GpuSplatVisibility;    // per splat: 0=hidden, 1=visible
		GraphicsBuffer m_GpuVisibleIndices;     // compacted visible splat indices
		GraphicsBuffer m_GpuStreamCompactTemp;  // temp buffer for scan operations
		GraphicsBuffer m_GpuVisibleCount;       // [0] = number of visible splats
		internal GraphicsBuffer m_GpuIndirectArgs;       // arguments for indirect draw
		uint m_VisibleSplatCount;               // cached visible count from GPU

		GpuSorting m_Sorter;
		GpuSorting.Args m_SorterArgs;

		internal Material m_MatSplats;
		internal Material m_MatComposite;
		internal Material m_MatDebugPoints;
		internal Material m_MatDebugBoxes;

		internal int m_FrameCounter;
		GaussianSplatAsset m_PrevAsset;
		Hash128 m_PrevHash;
		bool m_Registered;

		// Adaptive sorting tracking
		Vector3 m_LastCameraPosition;
		Quaternion m_LastCameraRotation;
		bool m_CameraPositionInitialized;
		bool m_ForceInitialSort = true;
		int m_InitialFrameCounter = 0;
		internal int m_AdaptiveSortFrameCounter;

		// Chunk sort cache
		Vector3 m_LastSortCameraPosition;
		Quaternion m_LastSortCameraRotation;
		Matrix4x4 m_LastSortMatrix;
		bool m_SortCacheValid;
		float m_LastSortDistance;

		// Distance-based optimization
		float m_AverageChunkDistance;
		int m_DistanceFrameSkipCounter;

		// Parameter tracking for cache invalidation
		float m_LastFrustumCullingTolerance;
		float m_LastChunkCacheDistanceThreshold;
		float m_LastDistantChunkThreshold;
		bool m_LastFrustumCullingEnabled;
		bool m_LastChunkSortCacheEnabled;
		bool m_LastDistanceBasedSortEnabled;
		bool m_ParametersInitialized;

		// Smoothing for movement detection to reduce flickering
		float m_SmoothedMovementSpeed;
		float m_SmoothedRotationSpeed;
		int m_ConsecutiveMovementFrames;
		const float kMovementSmoothingFactor = 0.8f;

		// Runtime GUI for parameter tweaking
		bool m_ShowRuntimeGUI = false;
		Rect m_GUIWindowRect = new Rect(20, 20, 350, 600);
		Vector2 m_ScrollPosition = Vector2.zero;
		uint m_LastVisibleSplatCount = 0;

		static readonly ProfilerMarker s_ProfSort = new(ProfilerCategory.Render, "GaussianSplat.Sort", MarkerFlags.SampleGPU);

		internal static class Props
		{
			public static readonly int SplatPos = Shader.PropertyToID("_SplatPos");
			public static readonly int SplatOther = Shader.PropertyToID("_SplatOther");
			public static readonly int SplatSH = Shader.PropertyToID("_SplatSH");
			public static readonly int SplatColor = Shader.PropertyToID("_SplatColor");
			public static readonly int SplatSelectedBits = Shader.PropertyToID("_SplatSelectedBits");
			public static readonly int SplatDeletedBits = Shader.PropertyToID("_SplatDeletedBits");
			public static readonly int SplatBitsValid = Shader.PropertyToID("_SplatBitsValid");
			public static readonly int SplatFormat = Shader.PropertyToID("_SplatFormat");
			public static readonly int SplatChunks = Shader.PropertyToID("_SplatChunks");
			public static readonly int SplatChunkCount = Shader.PropertyToID("_SplatChunkCount");
			public static readonly int SplatViewData = Shader.PropertyToID("_SplatViewData");
			public static readonly int OrderBuffer = Shader.PropertyToID("_OrderBuffer");
			public static readonly int SplatScale = Shader.PropertyToID("_SplatScale");
			public static readonly int FrustumCullingTolerance = Shader.PropertyToID("_FrustumCullingTolerance");
			public static readonly int SplatOpacityScale = Shader.PropertyToID("_SplatOpacityScale");
			public static readonly int SplatSize = Shader.PropertyToID("_SplatSize");
			public static readonly int SplatCount = Shader.PropertyToID("_SplatCount");
			public static readonly int SHOrder = Shader.PropertyToID("_SHOrder");
			public static readonly int SHOnly = Shader.PropertyToID("_SHOnly");
			public static readonly int DisplayIndex = Shader.PropertyToID("_DisplayIndex");
			public static readonly int DisplayChunks = Shader.PropertyToID("_DisplayChunks");
			public static readonly int GaussianSplatRT = Shader.PropertyToID("_GaussianSplatRT");
			public static readonly int SplatSortKeys = Shader.PropertyToID("_SplatSortKeys");
			public static readonly int SplatSortDistances = Shader.PropertyToID("_SplatSortDistances");
			public static readonly int SrcBuffer = Shader.PropertyToID("_SrcBuffer");
			public static readonly int DstBuffer = Shader.PropertyToID("_DstBuffer");
			public static readonly int BufferSize = Shader.PropertyToID("_BufferSize");
			public static readonly int MatrixMV = Shader.PropertyToID("_MatrixMV");
			public static readonly int MatrixP = Shader.PropertyToID("_MatrixP");
			public static readonly int MatrixVP = Shader.PropertyToID("_MatrixVP");
			public static readonly int MatrixObjectToWorld = Shader.PropertyToID("_MatrixObjectToWorld");
			public static readonly int MatrixWorldToObject = Shader.PropertyToID("_MatrixWorldToObject");
			public static readonly int VecScreenParams = Shader.PropertyToID("_VecScreenParams");
			public static readonly int VecWorldSpaceCameraPos = Shader.PropertyToID("_VecWorldSpaceCameraPos");
			public static readonly int CameraTargetTexture = Shader.PropertyToID("_CameraTargetTexture");
			public static readonly int SelectionCenter = Shader.PropertyToID("_SelectionCenter");
			public static readonly int SelectionDelta = Shader.PropertyToID("_SelectionDelta");
			public static readonly int SelectionDeltaRot = Shader.PropertyToID("_SelectionDeltaRot");
			public static readonly int SplatCutoutsCount = Shader.PropertyToID("_SplatCutoutsCount");
			public static readonly int SplatCutouts = Shader.PropertyToID("_SplatCutouts");
			public static readonly int SelectionMode = Shader.PropertyToID("_SelectionMode");
			public static readonly int SplatPosMouseDown = Shader.PropertyToID("_SplatPosMouseDown");
			public static readonly int SplatOtherMouseDown = Shader.PropertyToID("_SplatOtherMouseDown");
			public static readonly int FrustumCullingEnabled = Shader.PropertyToID("_FrustumCullingEnabled");
			public static readonly int FrustumPlanes = Shader.PropertyToID("_FrustumPlanes");
			public static readonly int ChunkVisibility = Shader.PropertyToID("_ChunkVisibility");
			public static readonly int SplatVisibility = Shader.PropertyToID("_SplatVisibility");
			public static readonly int VisibleIndices = Shader.PropertyToID("_VisibleIndices");
			public static readonly int StreamCompactTemp = Shader.PropertyToID("_StreamCompactTemp");
			public static readonly int VisibleCount = Shader.PropertyToID("_VisibleCount");
		}

		[field: NonSerialized] public bool editModified { get; private set; }
		[field: NonSerialized] public uint editSelectedSplats { get; private set; }
		[field: NonSerialized] public uint editDeletedSplats { get; private set; }
		[field: NonSerialized] public uint editCutSplats { get; private set; }
		[field: NonSerialized] public Bounds editSelectedBounds { get; private set; }

		public GaussianSplatAsset asset => m_Asset;
		public int splatCount => m_SplatCount;

		enum KernelIndices
		{
			SetIndices = 0,
			CalcDistances = 1,
			CalcViewData = 2,
			CullChunks = 3,
			CullSplatsInPartialChunks = 4,
			StreamCompactScan = 5,
			StreamCompactWrite = 6,
		}

		public bool HasValidAsset =>
			m_Asset != null &&
			m_Asset.splatCount > 0 &&
			m_Asset.formatVersion == GaussianSplatAsset.kCurrentVersion &&
			m_Asset.posData != null &&
			m_Asset.otherData != null &&
			m_Asset.shData != null &&
			m_Asset.colorData != null;
		public bool HasValidRenderSetup => m_GpuPosData != null && m_GpuOtherData != null && m_GpuChunks != null &&
			m_GpuSplatVisibility != null && m_GpuVisibleIndices != null && m_GpuVisibleCount != null && m_CSStreamCompact != null;

		const int kGpuViewDataSize = 40;

		void CreateResourcesForAsset()
		{
			if (!HasValidAsset)
				return;

			m_SplatCount = asset.splatCount;
			m_GpuPosData = new GraphicsBuffer(GraphicsBuffer.Target.Raw | GraphicsBuffer.Target.CopySource, (int)(asset.posData.dataSize / 4), 4) { name = "GaussianPosData" };
			m_GpuPosData.SetData(asset.posData.GetData<uint>());
			m_GpuOtherData = new GraphicsBuffer(GraphicsBuffer.Target.Raw | GraphicsBuffer.Target.CopySource, (int)(asset.otherData.dataSize / 4), 4) { name = "GaussianOtherData" };
			m_GpuOtherData.SetData(asset.otherData.GetData<uint>());
			m_GpuSHData = new GraphicsBuffer(GraphicsBuffer.Target.Raw, (int)(asset.shData.dataSize / 4), 4) { name = "GaussianSHData" };
			m_GpuSHData.SetData(asset.shData.GetData<uint>());
			var (texWidth, texHeight) = GaussianSplatAsset.CalcTextureSize(asset.splatCount);
			var texFormat = GaussianSplatAsset.ColorFormatToGraphics(asset.colorFormat);
			var tex = new Texture2D(texWidth, texHeight, texFormat, TextureCreationFlags.DontInitializePixels | TextureCreationFlags.IgnoreMipmapLimit | TextureCreationFlags.DontUploadUponCreate) { name = "GaussianColorData" };
			tex.SetPixelData(asset.colorData.GetData<byte>(), 0);
			tex.Apply(false, true);
			m_GpuColorData = tex;
			if (asset.chunkData != null && asset.chunkData.dataSize != 0)
			{
				m_GpuChunks = new GraphicsBuffer(GraphicsBuffer.Target.Structured,
					(int)(asset.chunkData.dataSize / UnsafeUtility.SizeOf<GaussianSplatAsset.ChunkInfo>()),
					UnsafeUtility.SizeOf<GaussianSplatAsset.ChunkInfo>())
				{ name = "GaussianChunkData" };
				m_GpuChunks.SetData(asset.chunkData.GetData<GaussianSplatAsset.ChunkInfo>());
				m_GpuChunksValid = true;
			}
			else
			{
				// just a dummy chunk buffer
				m_GpuChunks = new GraphicsBuffer(GraphicsBuffer.Target.Structured, 1,
					UnsafeUtility.SizeOf<GaussianSplatAsset.ChunkInfo>())
				{ name = "GaussianChunkData" };
				m_GpuChunksValid = false;
			}

			m_GpuView = new GraphicsBuffer(GraphicsBuffer.Target.Structured, m_Asset.splatCount, kGpuViewDataSize);
			m_GpuIndexBuffer = new GraphicsBuffer(GraphicsBuffer.Target.Index, 36, 2);
			// cube indices, most often we use only the first quad
			m_GpuIndexBuffer.SetData(new ushort[]
			{
				0, 1, 2, 1, 3, 2,
				4, 6, 5, 5, 6, 7,
				0, 2, 4, 4, 2, 6,
				1, 5, 3, 5, 7, 3,
				0, 4, 1, 4, 5, 1,
				2, 3, 6, 3, 7, 6
			});

			InitSortBuffers(splatCount);
			InitFrustumCullingBuffers(splatCount);
		}

		ComputeShader m_CSSplatUtilities => m_CSSplatUtilitiesFFX;

		void InitSortBuffers(int count)
		{
			m_GpuSortDistances?.Dispose();
			m_GpuSortKeys?.Dispose();
			m_SorterArgs.resources?.Dispose();

			EnsureSorterAndRegister();

			m_GpuSortDistances = new GraphicsBuffer(GraphicsBuffer.Target.Structured, count, 4) { name = "GaussianSplatSortDistances" };
			m_GpuSortKeys = new GraphicsBuffer(GraphicsBuffer.Target.Structured, count, 4) { name = "GaussianSplatSortIndices" };

			// init keys buffer to splat indices
			m_CSSplatUtilities.SetBuffer((int)KernelIndices.SetIndices, Props.SplatSortKeys, m_GpuSortKeys);
			m_CSSplatUtilities.SetInt(Props.SplatCount, m_GpuSortDistances.count);
			m_CSSplatUtilities.GetKernelThreadGroupSizes((int)KernelIndices.SetIndices, out uint gsX, out _, out _);
			m_CSSplatUtilities.Dispatch((int)KernelIndices.SetIndices, (m_GpuSortDistances.count + (int)gsX - 1) / (int)gsX, 1, 1);

			m_SorterArgs.inputKeys = m_GpuSortDistances;
			m_SorterArgs.inputValues = m_GpuSortKeys;
			m_SorterArgs.count = (uint)count;
			if (m_Sorter.Valid)
			{
				m_SorterArgs.resources = GpuSortingFFX.SupportResourcesFFX.Load((uint)count);
			}
		}

		void InitFrustumCullingBuffers(int splatCount)
		{
			// Calculate number of chunks
			int chunkCount = m_GpuChunksValid ? m_GpuChunks.count : (splatCount + GaussianSplatAsset.kChunkSize - 1) / GaussianSplatAsset.kChunkSize;

			// Dispose existing buffers
			DisposeBuffer(ref m_GpuChunkVisibility);
			DisposeBuffer(ref m_GpuSplatVisibility);
			DisposeBuffer(ref m_GpuVisibleIndices);
			DisposeBuffer(ref m_GpuStreamCompactTemp);
			DisposeBuffer(ref m_GpuVisibleCount);
			DisposeBuffer(ref m_GpuIndirectArgs);

			// Create frustum culling buffers
			m_GpuChunkVisibility = new GraphicsBuffer(GraphicsBuffer.Target.Structured, chunkCount, 4) { name = "GaussianChunkVisibility" };
			m_GpuSplatVisibility = new GraphicsBuffer(GraphicsBuffer.Target.Structured, splatCount, 4) { name = "GaussianSplatVisibility" };
			m_GpuVisibleIndices = new GraphicsBuffer(GraphicsBuffer.Target.Structured, splatCount, 4) { name = "GaussianVisibleIndices" };
			m_GpuStreamCompactTemp = new GraphicsBuffer(GraphicsBuffer.Target.Structured, splatCount, 4) { name = "GaussianStreamCompactTemp" };
			m_GpuVisibleCount = new GraphicsBuffer(GraphicsBuffer.Target.Structured, 1, 4) { name = "GaussianVisibleCount" };

			// Indirect args buffer: [indexCountPerInstance, instanceCount, startIndexLocation, baseVertexLocation, startInstanceLocation]
			m_GpuIndirectArgs = new GraphicsBuffer(GraphicsBuffer.Target.IndirectArguments, 5, 4) { name = "GaussianIndirectArgs" };

			// Initialize visibility to all visible (fallback if culling is disabled)
			var chunkVisData = new uint[chunkCount];
			var splatVisData = new uint[splatCount];
			for (int i = 0; i < chunkCount; i++) chunkVisData[i] = 1; // CHUNK_VISIBLE
			for (int i = 0; i < splatCount; i++) splatVisData[i] = 1;
			m_GpuChunkVisibility.SetData(chunkVisData);
			m_GpuSplatVisibility.SetData(splatVisData);

			// Initialize visible count to full count
			m_GpuVisibleCount.SetData(new uint[] { (uint)splatCount });
			m_VisibleSplatCount = (uint)splatCount;
		}

		bool resourcesAreSetUp => m_ShaderSplats != null && m_ShaderComposite != null && m_ShaderDebugPoints != null &&
								  m_ShaderDebugBoxes != null && m_CSSplatUtilities != null && SystemInfo.supportsComputeShaders;

		public void EnsureMaterials()
		{
			if (m_MatSplats == null && resourcesAreSetUp)
			{
				m_MatSplats = new Material(m_ShaderSplats) { name = "GaussianSplats" };
				m_MatComposite = new Material(m_ShaderComposite) { name = "GaussianClearDstAlpha" };
				m_MatDebugPoints = new Material(m_ShaderDebugPoints) { name = "GaussianDebugPoints" };
				m_MatDebugBoxes = new Material(m_ShaderDebugBoxes) { name = "GaussianDebugBoxes" };
			}
		}

		public void EnsureSorterAndRegister()
		{
			if (m_Sorter == null && resourcesAreSetUp)
			{
				m_Sorter = new GpuSortingFFX(m_CSSplatUtilitiesFFX);
			}

			if (!m_Registered && resourcesAreSetUp)
			{
				GaussianSplatRenderSystem.instance.RegisterSplat(this);
				m_Registered = true;
			}
		}

		public void OnEnable()
		{
			m_FrameCounter = 0;

			// Reset movement tracking variables to ensure proper initialization
			m_SmoothedMovementSpeed = 0f;
			m_SmoothedRotationSpeed = 0f;
			m_ConsecutiveMovementFrames = 0;
			m_CameraPositionInitialized = false;
			m_ForceInitialSort = true;
			m_InitialFrameCounter = 0;
			m_ParametersInitialized = false;

			if (!resourcesAreSetUp)
				return;

			EnsureMaterials();
			EnsureSorterAndRegister();

			CreateResourcesForAsset();
		}

#if UNITY_EDITOR
		void OnValidate()
		{
			// Invalidate cache when parameters change in editor
			// Only do this during play mode when everything is properly initialized
			if (Application.isPlaying && m_ParametersInitialized && HasValidAsset)
			{
				CheckParameterChanges();
			}

			// Warning: Chunk sort cache is incompatible with frustum culling
			if (m_ChunkSortCacheEnabled && m_FrustumCullingEnabled)
			{
				Debug.LogWarning($"[{name}] Chunk Sort Cache is incompatible with Frustum Culling. " +
				                 "The cache will be automatically disabled when frustum culling is active. " +
				                 "Consider disabling Chunk Sort Cache to avoid confusion.", this);
			}
		}
#endif


		void OnGUI()
		{
			if (!m_ShowRuntimeGUI)
				return;

			m_GUIWindowRect = GUI.Window(0, m_GUIWindowRect, DrawRuntimeGUI, "Gaussian Splat Runtime Settings");
		}

		void DrawRuntimeGUI(int windowID)
		{
			GUILayout.BeginVertical();

			m_ScrollPosition = GUILayout.BeginScrollView(m_ScrollPosition, GUILayout.Height(550));

			// Asset Info
			GUILayout.Label("Asset Info", GUI.skin.box);
			GUILayout.Label($"Total Splat Count: {m_SplatCount:N0}");

			if (m_FrustumCullingEnabled && m_LastVisibleSplatCount > 0)
			{
				float percentage = (m_LastVisibleSplatCount / (float)m_SplatCount) * 100f;
				GUILayout.Label($"Visible Splats: {m_LastVisibleSplatCount:N0} ({percentage:F1}%)");
				uint culledCount = (uint)m_SplatCount - m_LastVisibleSplatCount;
				float culledPercentage = (culledCount / (float)m_SplatCount) * 100f;
				GUILayout.Label($"Culled Splats: {culledCount:N0} ({culledPercentage:F1}%)");
			}
			else if (m_FrustumCullingEnabled)
			{
				GUILayout.Label($"Visible Splats: Calculating...");
			}
			else
			{
				GUILayout.Label($"Visible Splats: {m_SplatCount:N0} (100.0%) - Culling disabled");
			}

			GUILayout.Label($"Has Valid Asset: {HasValidAsset}");
			GUILayout.Label($"Format Version: {(m_Asset ? m_Asset.formatVersion.ToString() : "N/A")}");

			GUILayout.Space(10);

			// Rendering Parameters
			GUILayout.Label("Rendering", GUI.skin.box);
			m_SplatScale = GUILayout.HorizontalSlider(m_SplatScale, 0.1f, 2.0f);
			GUILayout.Label($"Splat Scale: {m_SplatScale:F2}");

			m_OpacityScale = GUILayout.HorizontalSlider(m_OpacityScale, 0.1f, 2.0f);
			GUILayout.Label($"Opacity Scale: {m_OpacityScale:F2}");

			GUILayout.Space(10);

			// Sorting Parameters
			GUILayout.Label("Sorting", GUI.skin.box);
			m_AdaptiveSortingEnabled = GUILayout.Toggle(m_AdaptiveSortingEnabled, "Adaptive Sorting");

			m_SortNthFrame = (int)GUILayout.HorizontalSlider(m_SortNthFrame, 1, 30);
			GUILayout.Label($"Sort Every N Frames: {m_SortNthFrame}");

			if (m_AdaptiveSortingEnabled)
			{
				m_CameraMovementThreshold = GUILayout.HorizontalSlider(m_CameraMovementThreshold, 0.01f, 1.0f);
				GUILayout.Label($"Movement Threshold: {m_CameraMovementThreshold:F3}");

				m_CameraRotationThreshold = GUILayout.HorizontalSlider(m_CameraRotationThreshold, 0.5f, 10.0f);
				GUILayout.Label($"Rotation Threshold: {m_CameraRotationThreshold:F1}°");

				m_FastSortFrequency = (int)GUILayout.HorizontalSlider(m_FastSortFrequency, 1, 10);
				GUILayout.Label($"Fast Sort Frequency: {m_FastSortFrequency}");
			}

			GUILayout.Space(10);

			// Cache Settings
			GUILayout.Label("Cache System", GUI.skin.box);
			m_ChunkSortCacheEnabled = GUILayout.Toggle(m_ChunkSortCacheEnabled, "Chunk Sort Cache");

			if (m_ChunkSortCacheEnabled)
			{
				// Warning if both cache and culling are enabled
				if (m_FrustumCullingEnabled)
				{
					GUILayout.Label("WARNING: Cache disabled (incompatible with frustum culling)", GUI.skin.box);
				}

				m_ChunkCacheDistanceThreshold = GUILayout.HorizontalSlider(m_ChunkCacheDistanceThreshold, 0.1f, 2.0f);
				GUILayout.Label($"Cache Distance Threshold: {m_ChunkCacheDistanceThreshold:F2}");
			}

			GUILayout.Space(10);

			// Distance-based Optimization
			GUILayout.Label("Distance Optimization", GUI.skin.box);
			m_DistanceBasedSortEnabled = GUILayout.Toggle(m_DistanceBasedSortEnabled, "Distance-based Sort");

			if (m_DistanceBasedSortEnabled)
			{
				m_DistantChunkThreshold = GUILayout.HorizontalSlider(m_DistantChunkThreshold, 5.0f, 100.0f);
				GUILayout.Label($"Distant Chunk Threshold: {m_DistantChunkThreshold:F1}");
				GUILayout.Label($"Average Chunk Distance: {m_AverageChunkDistance:F1}");
			}

			GUILayout.Space(10);

			// Frustum Culling
			GUILayout.Label("Frustum Culling", GUI.skin.box);
			m_FrustumCullingEnabled = GUILayout.Toggle(m_FrustumCullingEnabled, "Enable Frustum Culling");

			if (m_FrustumCullingEnabled)
			{
				m_FrustumCullingTolerance = GUILayout.HorizontalSlider(m_FrustumCullingTolerance, 0.5f, 10.0f);
				GUILayout.Label($"Culling Tolerance: {m_FrustumCullingTolerance:F1}");
			}

			GUILayout.Space(10);

			// Debug Info
			GUILayout.Label("Debug Info", GUI.skin.box);
			GUILayout.Label($"Frame Counter: {m_FrameCounter}");
			GUILayout.Label($"Adaptive Sort Counter: {m_AdaptiveSortFrameCounter}");
			GUILayout.Label($"Initial Frame Counter: {m_InitialFrameCounter}");
			GUILayout.Label($"Consecutive Movement Frames: {m_ConsecutiveMovementFrames}");
			GUILayout.Label($"Smoothed Movement Speed: {m_SmoothedMovementSpeed:F4}");
			GUILayout.Label($"Smoothed Rotation Speed: {m_SmoothedRotationSpeed:F2}°");
			GUILayout.Label($"Cache Valid: {m_SortCacheValid}");

			GUILayout.Space(10);

			// Actions
			if (GUILayout.Button("Force Sort"))
			{
				InvalidateSortCache();
				m_ForceInitialSort = true;
			}

			if (GUILayout.Button("Reset to Defaults"))
			{
				ResetToDefaults();
			}

			GUILayout.EndScrollView();

			// Close button
			if (GUILayout.Button("Close (D)"))
			{
				m_ShowRuntimeGUI = false;
			}

			GUILayout.EndVertical();

			// Make window draggable
			GUI.DragWindow();
		}

		void ResetToDefaults()
		{
			m_SplatScale = 1.0f;
			m_OpacityScale = 1.0f;
			m_SortNthFrame = 4;
			m_AdaptiveSortingEnabled = true;
			m_CameraMovementThreshold = 0.1f;
			m_CameraRotationThreshold = 1.0f;
			m_FastSortFrequency = 1;
			m_ChunkSortCacheEnabled = true;
			m_ChunkCacheDistanceThreshold = 0.5f;
			m_DistanceBasedSortEnabled = true;
			m_DistantChunkThreshold = 20.0f;
			m_FrustumCullingEnabled = true;
			m_FrustumCullingTolerance = 2.0f;

			// Force cache invalidation to apply changes
			InvalidateSortCache();
			m_ForceInitialSort = true;
		}

		void SetAssetDataOnCS(CommandBuffer cmb, KernelIndices kernel)
		{
			ComputeShader cs = m_CSSplatUtilities;
			int kernelIndex = (int)kernel;
			cmb.SetComputeBufferParam(cs, kernelIndex, Props.SplatPos, m_GpuPosData);
			cmb.SetComputeBufferParam(cs, kernelIndex, Props.SplatChunks, m_GpuChunks);
			cmb.SetComputeBufferParam(cs, kernelIndex, Props.SplatOther, m_GpuOtherData);
			cmb.SetComputeBufferParam(cs, kernelIndex, Props.SplatSH, m_GpuSHData);
			cmb.SetComputeTextureParam(cs, kernelIndex, Props.SplatColor, m_GpuColorData);

			// WebGPU does not allow the same buffer to be bound twice, for both read and write access
			var copyReadBuffers = SystemInfo.graphicsDeviceType == GraphicsDeviceType.WebGPU;
			bool tempBufferCopied = false;

			if (copyReadBuffers && m_GpuEditSelected == null)
			{
				if (m_GpuPosDataTemp == null || m_GpuPosDataTemp.count != m_GpuPosData.count)
				{
					DisposeBuffer(ref m_GpuPosDataTemp);
					var src = m_GpuPosData;
					m_GpuPosDataTemp = new GraphicsBuffer(
						src.target | GraphicsBuffer.Target.CopyDestination,
						src.count, src.stride);
				}
				tempBufferCopied = true;
				cmb.CopyBuffer(m_GpuPosData, m_GpuPosDataTemp);
				cmb.SetComputeBufferParam(cs, kernelIndex, Props.SplatSelectedBits, m_GpuPosDataTemp);
			}
			else
			{
				cmb.SetComputeBufferParam(cs, kernelIndex, Props.SplatSelectedBits, m_GpuEditSelected ?? m_GpuPosData);
			}

			if (copyReadBuffers && m_GpuEditDeleted == null)
			{
				if (m_GpuPosDataTemp == null || m_GpuPosDataTemp.count != m_GpuPosData.count)
				{
					DisposeBuffer(ref m_GpuPosDataTemp);
					var src = m_GpuPosData;
					m_GpuPosDataTemp = new GraphicsBuffer(
						src.target | GraphicsBuffer.Target.CopyDestination,
						src.count, src.stride);
				}
				if (!tempBufferCopied)
				{
					cmb.CopyBuffer(m_GpuPosData, m_GpuPosDataTemp);
				}

				cmb.SetComputeBufferParam(cs, kernelIndex, Props.SplatDeletedBits, m_GpuPosDataTemp);
			}
			else
			{
				cmb.SetComputeBufferParam(cs, kernelIndex, Props.SplatDeletedBits, m_GpuEditDeleted ?? m_GpuPosData);
			}
			cmb.SetComputeBufferParam(cs, kernelIndex, Props.SplatViewData, m_GpuView);
			cmb.SetComputeBufferParam(cs, kernelIndex, Props.OrderBuffer, m_GpuSortKeys);

			cmb.SetComputeIntParam(cs, Props.SplatBitsValid, m_GpuEditSelected != null && m_GpuEditDeleted != null ? 1 : 0);
			uint format = (uint)m_Asset.posFormat | ((uint)m_Asset.scaleFormat << 8) | ((uint)m_Asset.shFormat << 16);
			cmb.SetComputeIntParam(cs, Props.SplatFormat, (int)format);
			cmb.SetComputeIntParam(cs, Props.SplatCount, m_SplatCount);
			cmb.SetComputeIntParam(cs, Props.SplatChunkCount, m_GpuChunksValid ? m_GpuChunks.count : 0);
		}

		internal void SetAssetDataOnMaterial(MaterialPropertyBlock mat)
		{
			mat.SetBuffer(Props.SplatPos, m_GpuPosData);
			mat.SetBuffer(Props.SplatOther, m_GpuOtherData);
			mat.SetBuffer(Props.SplatSH, m_GpuSHData);
			mat.SetTexture(Props.SplatColor, m_GpuColorData);
			mat.SetBuffer(Props.SplatSelectedBits, m_GpuEditSelected ?? m_GpuPosData);
			mat.SetBuffer(Props.SplatDeletedBits, m_GpuEditDeleted ?? m_GpuPosData);
			mat.SetInt(Props.SplatBitsValid, m_GpuEditSelected != null && m_GpuEditDeleted != null ? 1 : 0);
			uint format = (uint)m_Asset.posFormat | ((uint)m_Asset.scaleFormat << 8) | ((uint)m_Asset.shFormat << 16);
			mat.SetInteger(Props.SplatFormat, (int)format);
			mat.SetInteger(Props.SplatCount, m_SplatCount);
			mat.SetInteger(Props.SplatChunkCount, m_GpuChunksValid ? m_GpuChunks.count : 0);
		}

		static void DisposeBuffer(ref GraphicsBuffer buf)
		{
			buf?.Dispose();
			buf = null;
		}

		void DisposeResourcesForAsset()
		{
			DestroyImmediate(m_GpuColorData);

			DisposeBuffer(ref m_GpuPosData);
			if (m_GpuPosDataTemp != null)
				DisposeBuffer(ref m_GpuPosDataTemp);
			DisposeBuffer(ref m_GpuOtherData);
			DisposeBuffer(ref m_GpuSHData);
			DisposeBuffer(ref m_GpuChunks);

			DisposeBuffer(ref m_GpuView);
			DisposeBuffer(ref m_GpuIndexBuffer);
			DisposeBuffer(ref m_GpuSortDistances);
			DisposeBuffer(ref m_GpuSortKeys);

			DisposeBuffer(ref m_GpuEditSelectedMouseDown);
			DisposeBuffer(ref m_GpuEditPosMouseDown);
			DisposeBuffer(ref m_GpuEditOtherMouseDown);
			DisposeBuffer(ref m_GpuEditSelected);
			DisposeBuffer(ref m_GpuEditDeleted);
			DisposeBuffer(ref m_GpuEditCountsBounds);
			DisposeBuffer(ref m_GpuEditCutouts);

			// Dispose frustum culling buffers
			DisposeBuffer(ref m_GpuChunkVisibility);
			DisposeBuffer(ref m_GpuSplatVisibility);
			DisposeBuffer(ref m_GpuVisibleIndices);
			DisposeBuffer(ref m_GpuStreamCompactTemp);
			DisposeBuffer(ref m_GpuVisibleCount);
			DisposeBuffer(ref m_GpuIndirectArgs);

			m_SorterArgs.resources?.Dispose();

			m_Sorter = null;
			m_SplatCount = 0;
			m_GpuChunksValid = false;

			editSelectedSplats = 0;
			editDeletedSplats = 0;
			editCutSplats = 0;
			editModified = false;
			editSelectedBounds = default;
		}

		public void OnDisable()
		{
			DisposeResourcesForAsset();
			GaussianSplatRenderSystem.instance.UnregisterSplat(this);
			m_Registered = false;

			DestroyImmediate(m_MatSplats);
			DestroyImmediate(m_MatComposite);
			DestroyImmediate(m_MatDebugPoints);
			DestroyImmediate(m_MatDebugBoxes);
		}

		internal void CalcViewData(CommandBuffer cmb, Camera cam)
		{
			if (cam.cameraType == CameraType.Preview)
				return;

			var tr = transform;

			Matrix4x4 matView = cam.worldToCameraMatrix;
			Matrix4x4 matO2W = tr.localToWorldMatrix;
			Matrix4x4 matW2O = tr.worldToLocalMatrix;
			int screenW = cam.pixelWidth, screenH = cam.pixelHeight;

			// Get XR eye texture dimensions if VR/XR is available, otherwise fallback to camera dimensions
			// This ensures compatibility when XR packages are not installed or VR is disabled
			int eyeW = 0, eyeH = 0;
#if GS_ENABLE_VR
			eyeW = XRSettings.eyeTextureWidth;
			eyeH = XRSettings.eyeTextureHeight;
#endif
			Vector4 screenPar = new Vector4(eyeW != 0 ? eyeW : screenW, eyeH != 0 ? eyeH : screenH, 0, 0);
			Vector4 camPos = cam.transform.position;

			// calculate view dependent data for each splat
			SetAssetDataOnCS(cmb, KernelIndices.CalcViewData);

			cmb.SetComputeMatrixParam(m_CSSplatUtilities, Props.MatrixMV, matView * matO2W);
			cmb.SetComputeMatrixParam(m_CSSplatUtilities, Props.MatrixObjectToWorld, matO2W);
			cmb.SetComputeMatrixParam(m_CSSplatUtilities, Props.MatrixWorldToObject, matW2O);

			cmb.SetComputeVectorParam(m_CSSplatUtilities, Props.VecScreenParams, screenPar);
			cmb.SetComputeVectorParam(m_CSSplatUtilities, Props.VecWorldSpaceCameraPos, camPos);
			cmb.SetComputeFloatParam(m_CSSplatUtilities, Props.SplatScale, m_SplatScale);
			cmb.SetComputeFloatParam(m_CSSplatUtilities, Props.SplatOpacityScale, m_OpacityScale);
			cmb.SetComputeIntParam(m_CSSplatUtilities, Props.SHOrder, m_SHOrder);
			cmb.SetComputeIntParam(m_CSSplatUtilities, Props.SHOnly, m_SHOnly ? 1 : 0);

			m_CSSplatUtilities.GetKernelThreadGroupSizes((int)KernelIndices.CalcViewData, out uint gsX, out _, out _);
			cmb.DispatchCompute(m_CSSplatUtilities, (int)KernelIndices.CalcViewData, (m_GpuView.count + (int)gsX - 1) / (int)gsX, 1, 1);
		}

		internal bool ShouldSort(Camera cam)
		{
			// Check for parameter changes that require cache invalidation
			// Only do this if we have a valid asset to avoid initialization issues
			if (HasValidAsset)
			{
				CheckParameterChanges();
			}

			// Force initial sort to ensure proper rendering on startup
			// Sort multiple times during the first few frames to ensure correct initialization
			m_InitialFrameCounter++;
			if (m_ForceInitialSort || m_InitialFrameCounter <= 3)
			{
				if (m_ForceInitialSort)
				{
					m_ForceInitialSort = false;
					m_LastCameraPosition = cam.transform.position;
					m_LastCameraRotation = cam.transform.rotation;
					m_CameraPositionInitialized = true;
					InvalidateSortCache(); // Invalidate cache on init
				}
				return true; // Force sort during first few frames
			}

			// Fallback to basic frame counter if adaptive sorting is disabled
			if (!m_AdaptiveSortingEnabled)
			{
				return m_FrameCounter % m_SortNthFrame == 0;
			}

			// Initialize camera position tracking
			if (!m_CameraPositionInitialized)
			{
				m_LastCameraPosition = cam.transform.position;
				m_LastCameraRotation = cam.transform.rotation;
				m_CameraPositionInitialized = true;
				InvalidateSortCache(); // Invalidate cache on init
				return true; // Sort on first frame
			}

			// Calculate camera movement
			Vector3 currentPosition = cam.transform.position;
			Quaternion currentRotation = cam.transform.rotation;

			float positionDelta = Vector3.Distance(currentPosition, m_LastCameraPosition);
			float rotationDelta = Quaternion.Angle(currentRotation, m_LastCameraRotation);

			// Smooth movement detection to reduce flickering
			m_SmoothedMovementSpeed = Mathf.Lerp(m_SmoothedMovementSpeed, positionDelta, 1.0f - kMovementSmoothingFactor);
			m_SmoothedRotationSpeed = Mathf.Lerp(m_SmoothedRotationSpeed, rotationDelta, 1.0f - kMovementSmoothingFactor);

			// Update last known position/rotation
			m_LastCameraPosition = currentPosition;
			m_LastCameraRotation = currentRotation;

			// Check if camera is moving significantly (using smoothed values and hysteresis)
			bool currentlyMoving = m_SmoothedMovementSpeed > m_CameraMovementThreshold || m_SmoothedRotationSpeed > m_CameraRotationThreshold;

			// Use hysteresis to prevent flickering: require several frames of movement to consider "moving"
			if (currentlyMoving)
			{
				m_ConsecutiveMovementFrames++;
			}
			else
			{
				m_ConsecutiveMovementFrames = Mathf.Max(0, m_ConsecutiveMovementFrames - 1);
			}

			bool cameraMoving = m_ConsecutiveMovementFrames >= 2; // Require at least 2 frames of movement

			// Check sort cache validity if enabled
			// NOTE: Sort cache is incompatible with frustum culling because visible indices change every frame
			// even when camera movement is minimal, causing sort key/distance mismatch
			if (m_ChunkSortCacheEnabled && m_SortCacheValid && !m_FrustumCullingEnabled)
			{
				float sortDistanceDelta = Vector3.Distance(currentPosition, m_LastSortCameraPosition);
				float sortRotationDelta = Quaternion.Angle(currentRotation, m_LastSortCameraRotation);

				// During rapid movement, disable cache to ensure frequent sorting
				// Cache is valid only if both position AND rotation haven't changed significantly AND not moving rapidly
				if (sortDistanceDelta < m_ChunkCacheDistanceThreshold &&
					sortRotationDelta < 2.0f && // 2 degrees rotation threshold for cache
					m_ConsecutiveMovementFrames < 5) // Disable cache during sustained rapid movement
				{
					// Cache is still valid, skip sorting
					return false;
				}
				else if (cameraMoving)
				{
					// Force cache invalidation during movement to prevent flickering
					InvalidateSortCache();
				}
			}

			// Distance-based frequency optimization
			if (m_DistanceBasedSortEnabled && m_AverageChunkDistance > m_DistantChunkThreshold)
			{
				// For distant chunks, reduce sort frequency significantly
				int distantSortFrequency = Mathf.Max(m_SortNthFrame * 2, 4);
				if (cameraMoving)
				{
					return m_AdaptiveSortFrameCounter % (m_FastSortFrequency * 2) == 0;
				}
				else
				{
					return m_AdaptiveSortFrameCounter % distantSortFrequency == 0;
				}
			}

			if (cameraMoving)
			{
				// When moving, sort more frequently (every m_FastSortFrequency frames)
				return m_AdaptiveSortFrameCounter % m_FastSortFrequency == 0;
			}
			else
			{
				// When static, use the base frequency
				return m_AdaptiveSortFrameCounter % m_SortNthFrame == 0;
			}
		}

		void InvalidateSortCache()
		{
			m_SortCacheValid = false;
		}

		void CheckParameterChanges()
		{
			// Initialize parameters tracking on first call
			if (!m_ParametersInitialized)
			{
				m_LastFrustumCullingTolerance = m_FrustumCullingTolerance;
				m_LastChunkCacheDistanceThreshold = m_ChunkCacheDistanceThreshold;
				m_LastDistantChunkThreshold = m_DistantChunkThreshold;
				m_LastFrustumCullingEnabled = m_FrustumCullingEnabled;
				m_LastChunkSortCacheEnabled = m_ChunkSortCacheEnabled;
				m_LastDistanceBasedSortEnabled = m_DistanceBasedSortEnabled;
				m_ParametersInitialized = true;
				return;
			}

			// Check if any critical parameters have changed
			bool parametersChanged =
				!Mathf.Approximately(m_LastFrustumCullingTolerance, m_FrustumCullingTolerance) ||
				!Mathf.Approximately(m_LastChunkCacheDistanceThreshold, m_ChunkCacheDistanceThreshold) ||
				!Mathf.Approximately(m_LastDistantChunkThreshold, m_DistantChunkThreshold) ||
				m_LastFrustumCullingEnabled != m_FrustumCullingEnabled ||
				m_LastChunkSortCacheEnabled != m_ChunkSortCacheEnabled ||
				m_LastDistanceBasedSortEnabled != m_DistanceBasedSortEnabled;

			if (parametersChanged)
			{
				// Invalidate cache when rendering parameters change
				InvalidateSortCache();

				// Update stored values
				m_LastFrustumCullingTolerance = m_FrustumCullingTolerance;
				m_LastChunkCacheDistanceThreshold = m_ChunkCacheDistanceThreshold;
				m_LastDistantChunkThreshold = m_DistantChunkThreshold;
				m_LastFrustumCullingEnabled = m_FrustumCullingEnabled;
				m_LastChunkSortCacheEnabled = m_ChunkSortCacheEnabled;
				m_LastDistanceBasedSortEnabled = m_DistanceBasedSortEnabled;
			}
		}

		void UpdateSortCache(Camera cam, Matrix4x4 matrix)
		{
			m_LastSortCameraPosition = cam.transform.position;
			m_LastSortCameraRotation = cam.transform.rotation;
			m_LastSortMatrix = matrix;
			m_SortCacheValid = true;

			// Update average chunk distance for distance-based optimization
			if (m_DistanceBasedSortEnabled)
			{
				UpdateAverageChunkDistance(cam);
			}
		}

		void UpdateAverageChunkDistance(Camera cam)
		{
			// Calculate average distance to chunks (simplified estimation)
			if (HasValidAsset && m_SplatCount > 0)
			{
				// Estimate center of splat cloud
				Vector3 cameraPos = cam.transform.position;
				Vector3 splatCenter = transform.position; // Object center as approximation

				m_AverageChunkDistance = Vector3.Distance(cameraPos, splatCenter);
			}
		}

		internal void UpdateFrustumCulling(CommandBuffer cmd, Camera cam, Matrix4x4 matrix)
		{
			if (cam.cameraType == CameraType.Preview)
				return;

			// Ensure resources are created before culling
			if (!HasValidRenderSetup)
				return;

			// Perform frustum culling and stream compaction
			PerformFrustumCulling(cmd, cam, matrix);
			StreamCompactVisibleSplats(cmd);
		}

		internal void InitializeBuffersForNoCulling(CommandBuffer cmd)
		{
			// Initialize sort keys with original indices (0, 1, 2, ...)
			cmd.SetComputeBufferParam(m_CSSplatUtilities, (int)KernelIndices.SetIndices, Props.SplatSortKeys, m_GpuSortKeys);
			cmd.SetComputeIntParam(m_CSSplatUtilities, Props.SplatCount, m_SplatCount);
			m_CSSplatUtilities.GetKernelThreadGroupSizes((int)KernelIndices.SetIndices, out uint gsX, out _, out _);
			cmd.DispatchCompute(m_CSSplatUtilities, (int)KernelIndices.SetIndices, (m_SplatCount + (int)gsX - 1) / (int)gsX, 1, 1);

			// Initialize visible count buffer
			m_GpuVisibleCount.SetData(new uint[] { (uint)m_SplatCount });
		}

		internal void SortPoints(CommandBuffer cmd, Camera cam, Matrix4x4 matrix)
		{
			if (cam.cameraType == CameraType.Preview)
				return;

			// Ensure resources are created before sorting
			if (!HasValidRenderSetup)
				return;

			Matrix4x4 worldToCamMatrix = cam.worldToCameraMatrix;
			worldToCamMatrix.m20 *= -1;
			worldToCamMatrix.m21 *= -1;
			worldToCamMatrix.m22 *= -1;

			cmd.BeginSample(s_ProfSort);

			// Calculate distances and sort the splats
			// Note: This works for both culled and non-culled cases
			// - With culling: sort keys contain visible indices only (set by UpdateFrustumCulling)
			// - Without culling: sort keys contain all indices (set by InitializeBuffersForNoCulling)
			EnsureSorterAndRegister();
			CalcDistancesForVisibleSplats(cmd, cam, matrix, worldToCamMatrix);
			m_Sorter.Dispatch(cmd, m_SorterArgs);

			// Update sort cache when sorting is performed
			UpdateSortCache(cam, matrix);

			cmd.EndSample(s_ProfSort);
		}

		void PerformFrustumCulling(CommandBuffer cmd, Camera cam, Matrix4x4 matrix)
		{
			// Safety check: ensure required buffers exist for frustum culling
			if (m_GpuSplatVisibility == null || m_GpuPosData == null)
			{
				Debug.LogError("PerformFrustumCulling called before frustum culling buffers are created!");
				return;
			}

			// Calculate view-projection matrix with GPU adjustments
			Matrix4x4 projMatrix = GL.GetGPUProjectionMatrix(cam.projectionMatrix, false);
			Matrix4x4 viewMatrix = cam.worldToCameraMatrix;
			Matrix4x4 vpMatrix = projMatrix * viewMatrix;

			// Bind all asset data buffers (need _MatrixObjectToWorld which is set here)
			SetAssetDataOnCS(cmd, KernelIndices.CullChunks);

			// Set frustum culling parameters
			cmd.SetComputeIntParam(m_CSSplatUtilities, Props.FrustumCullingEnabled, 1);
			cmd.SetComputeMatrixParam(m_CSSplatUtilities, Props.MatrixVP, vpMatrix);
			cmd.SetComputeFloatParam(m_CSSplatUtilities, Props.FrustumCullingTolerance, m_FrustumCullingTolerance);

			// Bind visibility buffer
			cmd.SetComputeBufferParam(m_CSSplatUtilities, (int)KernelIndices.CullChunks, Props.SplatVisibility,
				m_GpuSplatVisibility);

			// Dispatch to test all splats against frustum
			m_CSSplatUtilities.GetKernelThreadGroupSizes((int)KernelIndices.CullChunks, out uint splatGroupSize, out _, out _);
			cmd.DispatchCompute(m_CSSplatUtilities, (int)KernelIndices.CullChunks,
				(m_SplatCount + (int)splatGroupSize - 1) / (int)splatGroupSize, 1, 1);
		}

		void StreamCompactVisibleSplats(CommandBuffer cmd)
		{
			// Safety check: ensure required buffers exist
			if (m_GpuSplatVisibility == null || m_GpuVisibleIndices == null || m_GpuVisibleCount == null)
			{
				Debug.LogError("StreamCompactVisibleSplats called before buffers are created!");
				return;
			}

			// Step 1: Initialize visible count to 0 using CSStreamCompactScan kernel (kernel 17)
			cmd.SetComputeBufferParam(m_CSSplatUtilities, (int)KernelIndices.StreamCompactScan, Props.VisibleCount,
	  m_GpuVisibleCount);
			cmd.DispatchCompute(m_CSSplatUtilities, (int)KernelIndices.StreamCompactScan, 1, 1, 1);

			// Step 2: Perform atomic stream compaction using CSCullSplatsInPartialChunks kernel (kernel 16)
			cmd.SetComputeBufferParam(m_CSSplatUtilities, (int)KernelIndices.CullSplatsInPartialChunks, Props.SplatVisibility,
	   m_GpuSplatVisibility);
			cmd.SetComputeBufferParam(m_CSSplatUtilities, (int)KernelIndices.CullSplatsInPartialChunks, Props.VisibleIndices,
	  m_GpuVisibleIndices);
			cmd.SetComputeBufferParam(m_CSSplatUtilities, (int)KernelIndices.CullSplatsInPartialChunks, Props.VisibleCount,
	  m_GpuVisibleCount);
			cmd.SetComputeIntParam(m_CSSplatUtilities, Props.SplatCount, m_SplatCount);

			m_CSSplatUtilities.GetKernelThreadGroupSizes((int)KernelIndices.CullSplatsInPartialChunks, out uint
	  compactGroupSize, out _, out _);
			cmd.DispatchCompute(m_CSSplatUtilities, (int)KernelIndices.CullSplatsInPartialChunks, (m_SplatCount +
	  (int)compactGroupSize - 1) / (int)compactGroupSize, 1, 1);

			// Step 3: Copy visible indices to sort keys using CSStreamCompactWrite kernel (kernel 18)
			cmd.SetComputeBufferParam(m_CSSplatUtilities, (int)KernelIndices.StreamCompactWrite, Props.VisibleIndices,
	  m_GpuVisibleIndices);
			cmd.SetComputeBufferParam(m_CSSplatUtilities, (int)KernelIndices.StreamCompactWrite, Props.VisibleCount,
	  m_GpuVisibleCount);
			cmd.SetComputeBufferParam(m_CSSplatUtilities, (int)KernelIndices.StreamCompactWrite, Props.SplatSortKeys,
	  m_GpuSortKeys);
			cmd.SetComputeIntParam(m_CSSplatUtilities, Props.SplatCount, m_SplatCount);

			m_CSSplatUtilities.GetKernelThreadGroupSizes((int)KernelIndices.StreamCompactWrite, out uint writeGroupSize, out
	  _, out _);
			cmd.DispatchCompute(m_CSSplatUtilities, (int)KernelIndices.StreamCompactWrite, (m_SplatCount + (int)writeGroupSize
	   - 1) / (int)writeGroupSize, 1, 1);

			// GPU-only approach: No CPU readback needed
			// The visible count stays on GPU and is used directly via indirect rendering
		}

		void CalcDistancesForVisibleSplats(CommandBuffer cmd, Camera cam, Matrix4x4 matrix, Matrix4x4 worldToCamMatrix)
		{
			// Calculate distances for the correct number of splats based on culling mode
			cmd.SetComputeBufferParam(m_CSSplatUtilities, (int)KernelIndices.CalcDistances, Props.SplatSortDistances, m_GpuSortDistances);
			cmd.SetComputeBufferParam(m_CSSplatUtilities, (int)KernelIndices.CalcDistances, Props.SplatSortKeys, m_GpuSortKeys);
			cmd.SetComputeBufferParam(m_CSSplatUtilities, (int)KernelIndices.CalcDistances, Props.SplatChunks, m_GpuChunks);
			cmd.SetComputeBufferParam(m_CSSplatUtilities, (int)KernelIndices.CalcDistances, Props.SplatPos, m_GpuPosData);
			cmd.SetComputeIntParam(m_CSSplatUtilities, Props.SplatFormat, (int)m_Asset.posFormat);
			cmd.SetComputeMatrixParam(m_CSSplatUtilities, Props.MatrixMV, worldToCamMatrix * matrix);
			cmd.SetComputeIntParam(m_CSSplatUtilities, Props.SplatChunkCount, m_GpuChunksValid ? m_GpuChunks.count : 0);

			// Always calculate distances for all splats - the sorting will handle visible/hidden distinction
			cmd.SetComputeIntParam(m_CSSplatUtilities, Props.SplatCount, m_SplatCount);

			m_CSSplatUtilities.GetKernelThreadGroupSizes((int)KernelIndices.CalcDistances, out uint gsX, out _, out _);
			cmd.DispatchCompute(m_CSSplatUtilities, (int)KernelIndices.CalcDistances, (m_SplatCount + (int)gsX - 1) / (int)gsX, 1, 1);
		}

		internal void UpdateIndirectArgs(CommandBuffer cmd, int indexCount, MeshTopology topology)
		{
			if (m_FrustumCullingEnabled)
			{
				// GPU-to-GPU copy of visible count to indirect args
				// First, set the static args (everything except instanceCount)
				var staticArgs = new uint[5];
				staticArgs[0] = (uint)indexCount;  // indexCountPerInstance
				staticArgs[1] = 0;                 // instanceCount - will be set by GPU
				staticArgs[2] = 0;                 // startIndexLocation
				staticArgs[3] = 0;                 // baseVertexLocation
				staticArgs[4] = 0;                 // startInstanceLocation
				m_GpuIndirectArgs.SetData(staticArgs);

				// Use the copy kernel to set instanceCount from visible count
				const int copyKernelIndex = 1; // CSCopyVisibleCountToIndirectArgs
				cmd.SetComputeBufferParam(m_CSStreamCompact, copyKernelIndex, "_VisibleCount", m_GpuVisibleCount);
				cmd.SetComputeBufferParam(m_CSStreamCompact, copyKernelIndex, "_IndirectArgs", m_GpuIndirectArgs);
				cmd.DispatchCompute(m_CSStreamCompact, copyKernelIndex, 1, 1, 1);

				// Debug: Display visible splats percentage occasionally
				if (Time.frameCount % 60 == 0) // Every second at 60 FPS
				{
					cmd.RequestAsyncReadback(m_GpuIndirectArgs, (readback) =>
					{
						if (!readback.hasError)
						{
							var data = readback.GetData<uint>();
							m_LastVisibleSplatCount = data[1]; // instanceCount

							// Log culling statistics for debugging
							float visiblePercentage = (m_LastVisibleSplatCount * 100f) / m_SplatCount;
							uint culledCount = (uint)m_SplatCount - m_LastVisibleSplatCount;
							float culledPercentage = (culledCount * 100f) / m_SplatCount;
							Debug.Log($"[{name}] Frustum Culling Stats - Frame {Time.frameCount}: " +
							          $"Visible={m_LastVisibleSplatCount:N0}/{m_SplatCount:N0} ({visiblePercentage:F1}%), " +
							          $"Culled={culledCount:N0} ({culledPercentage:F1}%)");
						}
					});
				}
			}
			else
			{
				// Fallback: use full splat count
				m_LastVisibleSplatCount = (uint)m_SplatCount; // Set to full count when culling is disabled
				var indirectArgs = new uint[5];
				indirectArgs[0] = (uint)indexCount;           // indexCountPerInstance
				indirectArgs[1] = (uint)m_SplatCount;         // instanceCount (all splats)
				indirectArgs[2] = 0;                          // startIndexLocation
				indirectArgs[3] = 0;                          // baseVertexLocation
				indirectArgs[4] = 0;                          // startInstanceLocation
				m_GpuIndirectArgs.SetData(indirectArgs);
			}
		}

		public void Update()
		{
			// Toggle runtime GUI with D
			if (Input.GetKeyDown(KeyCode.D))
			{
				m_ShowRuntimeGUI = !m_ShowRuntimeGUI;
			}

			var curHash = m_Asset ? m_Asset.dataHash : new Hash128();
			if (m_PrevAsset != m_Asset || m_PrevHash != curHash)
			{
				m_PrevAsset = m_Asset;
				m_PrevHash = curHash;
				if (resourcesAreSetUp)
				{
					DisposeResourcesForAsset();
					CreateResourcesForAsset();
					InvalidateSortCache(); // Invalidate cache when asset changes
					m_ForceInitialSort = true; // Force sort after asset change
				}
				else
				{
					Debug.LogError($"{nameof(GaussianSplatRenderer)} component is not set up correctly (Resource references are missing), or platform does not support compute shaders");
				}
			}
		}

		public void ActivateCamera(int index)
		{
			Camera mainCam = Camera.main;
			if (!mainCam)
				return;
			if (!m_Asset || m_Asset.cameras == null)
				return;

			var selfTr = transform;
			var camTr = mainCam.transform;
			var prevParent = camTr.parent;
			var cam = m_Asset.cameras[index];
			camTr.parent = selfTr;
			camTr.localPosition = cam.pos;
			camTr.localRotation = Quaternion.LookRotation(cam.axisZ, cam.axisY);
			camTr.parent = prevParent;
			camTr.localScale = Vector3.one;
#if UNITY_EDITOR
			UnityEditor.EditorUtility.SetDirty(camTr);
#endif
		}

	}
}