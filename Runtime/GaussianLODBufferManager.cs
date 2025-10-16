// SPDX-License-Identifier: MIT

using System;
using System.Collections.Generic;
using Unity.Collections.LowLevel.Unsafe;
using UnityEngine;
using UnityEngine.Experimental.Rendering;

namespace GaussianSplatting.Runtime
{
	/// <summary>
	/// Manages multiple LOD level GPU buffers for dynamic streaming
	/// Handles loading, unloading, and swapping between LOD data
	/// </summary>
	public class GaussianLODBufferManager : IDisposable
	{
		public struct LODBufferSet
		{
			public int lodLevel;
			public int splatCount;
			public GraphicsBuffer posData;
			public GraphicsBuffer otherData;
			public GraphicsBuffer shData;
			public Texture colorData;
			public GraphicsBuffer chunks;
			public bool chunksValid;
			public bool isLoaded;
			public long memorySize; // in bytes
		}

		readonly GaussianSplatAsset m_Asset;
		readonly Dictionary<int, LODBufferSet> m_LoadedLODs = new Dictionary<int, LODBufferSet>();

		int m_CurrentLODLevel = -1;
		long m_MemoryBudget = 512 * 1024 * 1024; // 512MB default
		long m_CurrentMemoryUsage = 0;

		public int currentLODLevel => m_CurrentLODLevel;
		public long memoryBudget
		{
			get => m_MemoryBudget;
			set => m_MemoryBudget = value;
		}
		public long currentMemoryUsage => m_CurrentMemoryUsage;
		public int loadedLODCount => m_LoadedLODs.Count;

		public GaussianLODBufferManager(GaussianSplatAsset asset)
		{
			m_Asset = asset;
		}

		/// <summary>
		/// Load a specific LOD level into GPU memory
		/// </summary>
		public bool LoadLODLevel(int lodLevel)
		{
			if (lodLevel < 0 || lodLevel >= m_Asset.lodLevelCount)
			{
				Debug.LogError($"[LODBufferManager] Invalid LOD level: {lodLevel}");
				return false;
			}

			// Already loaded?
			if (m_LoadedLODs.ContainsKey(lodLevel) && m_LoadedLODs[lodLevel].isLoaded)
			{
				Debug.Log($"[LODBufferManager] LOD level {lodLevel} already loaded");
				return true;
			}

			try
			{
				// Get data for this LOD level
				m_Asset.GetDataForLODLevel(lodLevel, out var posData, out var otherData, out var colorData, out var shData, out var chunkData, out int splatCount);

				if (posData == null || otherData == null || colorData == null || shData == null)
				{
					Debug.LogError($"[LODBufferManager] Missing data files for LOD level {lodLevel}");
					return false;
				}

				// Calculate memory requirements
				long memoryRequired = CalculateMemorySize(splatCount);

				// Check if we need to free memory
				if (m_CurrentMemoryUsage + memoryRequired > m_MemoryBudget)
				{
					FreeMemoryForBudget(memoryRequired);
				}

				// Create GPU buffers
				var bufferSet = new LODBufferSet
				{
					lodLevel = lodLevel,
					splatCount = splatCount
				};

				// Position data
				int posDataSize = (int)posData.dataSize;
				int posBufferCount = (posDataSize + 3) / 4; // Round up to nearest uint
				bufferSet.posData = new GraphicsBuffer(GraphicsBuffer.Target.Raw | GraphicsBuffer.Target.CopySource, posBufferCount, 4)
				{
					name = $"GaussianPosData_LOD{lodLevel}"
				};
				// Use byte array if size not multiple of 4
				if (posDataSize % 4 == 0)
				{
					bufferSet.posData.SetData(posData.GetData<uint>());
				}
				else
				{
					// Pad to multiple of 4 bytes
					var bytes = posData.GetData<byte>();
					int paddedSize = posBufferCount * 4;
					var paddedBytes = new byte[paddedSize];
					bytes.CopyTo(paddedBytes);
					bufferSet.posData.SetData(paddedBytes);
				}

				// Other data (rotation/scale)
				int otherDataSize = (int)otherData.dataSize;
				int otherBufferCount = (otherDataSize + 3) / 4;
				bufferSet.otherData = new GraphicsBuffer(GraphicsBuffer.Target.Raw | GraphicsBuffer.Target.CopySource, otherBufferCount, 4)
				{
					name = $"GaussianOtherData_LOD{lodLevel}"
				};
				if (otherDataSize % 4 == 0)
				{
					bufferSet.otherData.SetData(otherData.GetData<uint>());
				}
				else
				{
					// Pad to multiple of 4 bytes
					var bytes = otherData.GetData<byte>();
					int paddedSize = otherBufferCount * 4;
					var paddedBytes = new byte[paddedSize];
					bytes.CopyTo(paddedBytes);
					bufferSet.otherData.SetData(paddedBytes);
				}

				// SH data
				int shDataSize = (int)shData.dataSize;
				int shBufferCount = (shDataSize + 3) / 4;
				bufferSet.shData = new GraphicsBuffer(GraphicsBuffer.Target.Raw, shBufferCount, 4)
				{
					name = $"GaussianSHData_LOD{lodLevel}"
				};
				if (shDataSize % 4 == 0)
				{
					bufferSet.shData.SetData(shData.GetData<uint>());
				}
				else
				{
					// Pad to multiple of 4 bytes
					var bytes = shData.GetData<byte>();
					int paddedSize = shBufferCount * 4;
					var paddedBytes = new byte[paddedSize];
					bytes.CopyTo(paddedBytes);
					bufferSet.shData.SetData(paddedBytes);
				}

				// Color data
				var (texWidth, texHeight) = GaussianSplatAsset.CalcTextureSize(splatCount);
				var texFormat = GaussianSplatAsset.ColorFormatToGraphics(m_Asset.colorFormat);
				var tex = new Texture2D(texWidth, texHeight, texFormat, TextureCreationFlags.DontInitializePixels | TextureCreationFlags.IgnoreMipmapLimit | TextureCreationFlags.DontUploadUponCreate)
				{
					name = $"GaussianColorData_LOD{lodLevel}"
				};
				tex.SetPixelData(colorData.GetData<byte>(), 0);
				tex.Apply(false, true);
				bufferSet.colorData = tex;

				// Chunk data (if available)
				if (chunkData != null && chunkData.dataSize != 0)
				{
					bufferSet.chunks = new GraphicsBuffer(GraphicsBuffer.Target.Structured,
						(int)(chunkData.dataSize / UnsafeUtility.SizeOf<GaussianSplatAsset.ChunkInfo>()),
						UnsafeUtility.SizeOf<GaussianSplatAsset.ChunkInfo>())
					{
						name = $"GaussianChunkData_LOD{lodLevel}"
					};
					bufferSet.chunks.SetData(chunkData.GetData<GaussianSplatAsset.ChunkInfo>());
					bufferSet.chunksValid = true;
				}
				else
				{
					// Dummy chunk buffer
					bufferSet.chunks = new GraphicsBuffer(GraphicsBuffer.Target.Structured, 1,
						UnsafeUtility.SizeOf<GaussianSplatAsset.ChunkInfo>())
					{
						name = $"GaussianChunkData_LOD{lodLevel}"
					};
					bufferSet.chunksValid = false;
				}

				bufferSet.isLoaded = true;
				bufferSet.memorySize = memoryRequired;

				m_LoadedLODs[lodLevel] = bufferSet;
				m_CurrentMemoryUsage += memoryRequired;

				Debug.Log($"[LODBufferManager] Loaded LOD level {lodLevel}: {splatCount:N0} splats, {FormatBytes(memoryRequired)} " +
				          $"(Total: {FormatBytes(m_CurrentMemoryUsage)} / {FormatBytes(m_MemoryBudget)})");

				return true;
			}
			catch (Exception e)
			{
				Debug.LogError($"[LODBufferManager] Failed to load LOD level {lodLevel}: {e.Message}");
				return false;
			}
		}

		/// <summary>
		/// Unload a specific LOD level from GPU memory
		/// </summary>
		public void UnloadLODLevel(int lodLevel)
		{
			if (!m_LoadedLODs.ContainsKey(lodLevel))
				return;

			var bufferSet = m_LoadedLODs[lodLevel];
			if (!bufferSet.isLoaded)
				return;

			// Dispose GPU resources
			bufferSet.posData?.Dispose();
			bufferSet.otherData?.Dispose();
			bufferSet.shData?.Dispose();
			bufferSet.chunks?.Dispose();
			if (bufferSet.colorData != null)
				UnityEngine.Object.DestroyImmediate(bufferSet.colorData);

			m_CurrentMemoryUsage -= bufferSet.memorySize;
			m_LoadedLODs.Remove(lodLevel);

			Debug.Log($"[LODBufferManager] Unloaded LOD level {lodLevel}: {FormatBytes(bufferSet.memorySize)} freed " +
			          $"(Total: {FormatBytes(m_CurrentMemoryUsage)} / {FormatBytes(m_MemoryBudget)})");
		}

		/// <summary>
		/// Switch to a different LOD level (loading if necessary)
		/// </summary>
		public bool SwitchToLODLevel(int targetLevel, out LODBufferSet bufferSet)
		{
			bufferSet = default;

			if (targetLevel < 0 || targetLevel >= m_Asset.lodLevelCount)
				return false;

			// Already at this level?
			if (m_CurrentLODLevel == targetLevel && m_LoadedLODs.ContainsKey(targetLevel))
			{
				bufferSet = m_LoadedLODs[targetLevel];
				return true;
			}

			// Load if not already loaded
			if (!m_LoadedLODs.ContainsKey(targetLevel) || !m_LoadedLODs[targetLevel].isLoaded)
			{
				if (!LoadLODLevel(targetLevel))
					return false;
			}

			m_CurrentLODLevel = targetLevel;
			bufferSet = m_LoadedLODs[targetLevel];

			Debug.Log($"[LODBufferManager] Switched to LOD level {targetLevel} ({bufferSet.splatCount:N0} splats)");
			return true;
		}

		/// <summary>
		/// Get buffers for the current LOD level
		/// </summary>
		public bool GetCurrentBuffers(out LODBufferSet bufferSet)
		{
			if (m_CurrentLODLevel >= 0 && m_LoadedLODs.ContainsKey(m_CurrentLODLevel))
			{
				bufferSet = m_LoadedLODs[m_CurrentLODLevel];
				return bufferSet.isLoaded;
			}

			bufferSet = default;
			return false;
		}

		/// <summary>
		/// Preload adjacent LOD levels for faster transitions
		/// </summary>
		public void PreloadAdjacentLODs()
		{
			if (m_CurrentLODLevel < 0)
				return;

			// Try to load LOD level below (higher detail)
			if (m_CurrentLODLevel > 0 && !m_LoadedLODs.ContainsKey(m_CurrentLODLevel - 1))
			{
				LoadLODLevel(m_CurrentLODLevel - 1);
			}

			// Try to load LOD level above (lower detail)
			if (m_CurrentLODLevel < m_Asset.lodLevelCount - 1 && !m_LoadedLODs.ContainsKey(m_CurrentLODLevel + 1))
			{
				LoadLODLevel(m_CurrentLODLevel + 1);
			}
		}

		/// <summary>
		/// Free memory to fit within budget
		/// </summary>
		void FreeMemoryForBudget(long requiredMemory)
		{
			if (m_CurrentMemoryUsage + requiredMemory <= m_MemoryBudget)
				return;

			// Strategy: Unload LOD levels furthest from current level
			var levelsToUnload = new List<int>();

			foreach (var kvp in m_LoadedLODs)
			{
				if (kvp.Key != m_CurrentLODLevel) // Don't unload current LOD
				{
					levelsToUnload.Add(kvp.Key);
				}
			}

			// Sort by distance from current level (furthest first)
			if (m_CurrentLODLevel >= 0)
			{
				levelsToUnload.Sort((a, b) =>
				{
					int distA = Math.Abs(a - m_CurrentLODLevel);
					int distB = Math.Abs(b - m_CurrentLODLevel);
					return distB.CompareTo(distA);
				});
			}

			// Unload until we have enough space
			foreach (var level in levelsToUnload)
			{
				UnloadLODLevel(level);
				if (m_CurrentMemoryUsage + requiredMemory <= m_MemoryBudget)
					break;
			}
		}

		long CalculateMemorySize(int splatCount)
		{
			long size = 0;

			// Position data
			size += GaussianSplatAsset.CalcPosDataSize(splatCount, m_Asset.posFormat);

			// Other data
			size += GaussianSplatAsset.CalcOtherDataSize(splatCount, m_Asset.scaleFormat);

			// Color data
			size += GaussianSplatAsset.CalcColorDataSize(splatCount, m_Asset.colorFormat);

			// SH data
			size += GaussianSplatAsset.CalcSHDataSize(splatCount, m_Asset.shFormat);

			// Chunk data
			size += GaussianSplatAsset.CalcChunkDataSize(splatCount);

			return size;
		}

		string FormatBytes(long bytes)
		{
			if (bytes >= 1024 * 1024 * 1024)
				return $"{bytes / (1024.0 * 1024.0 * 1024.0):F2} GB";
			if (bytes >= 1024 * 1024)
				return $"{bytes / (1024.0 * 1024.0):F2} MB";
			if (bytes >= 1024)
				return $"{bytes / 1024.0:F2} KB";
			return $"{bytes} B";
		}

		public void Dispose()
		{
			// Unload all LOD levels
			var levels = new List<int>(m_LoadedLODs.Keys);
			foreach (var level in levels)
			{
				UnloadLODLevel(level);
			}

			m_LoadedLODs.Clear();
			m_CurrentMemoryUsage = 0;
		}
	}
}
