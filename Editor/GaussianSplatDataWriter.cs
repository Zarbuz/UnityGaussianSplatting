// SPDX-License-Identifier: MIT

using GaussianSplatting.Runtime;
using System;
using System.Collections.Generic;
using System.IO;
using Unity.Mathematics;
using UnityEditor;
using UnityEngine;

namespace GaussianSplatting.Editor
{
	/// <summary>
	/// Utility class for writing LOD splat data to asset files
	/// </summary>
	public class GaussianSplatDataWriter
	{
		readonly GaussianSplatAsset m_SourceAsset;
		readonly string m_AssetPath;

		public GaussianSplatDataWriter(GaussianSplatAsset sourceAsset)
		{
			m_SourceAsset = sourceAsset;
			m_AssetPath = AssetDatabase.GetAssetPath(sourceAsset);
		}

		/// <summary>
		/// Generate LOD data files from filtered splat data
		/// </summary>
		public GaussianSplatAsset.LODDataFiles GenerateLODDataFiles(
			GaussianSplatDataProcessor.SplatData[] splats,
			int lodLevel,
			Action<string, float> progressCallback = null)
		{
			try
			{
				string assetDir = Path.GetDirectoryName(m_AssetPath);
				string assetName = Path.GetFileNameWithoutExtension(m_AssetPath);

				progressCallback?.Invoke($"Generating LOD {lodLevel} data files...", 0f);

				// Create LOD data files
				var lodData = new GaussianSplatAsset.LODDataFiles();
				lodData.splatCount = splats.Length;

				// Generate position data
				progressCallback?.Invoke($"Encoding positions for LOD {lodLevel}...", 0.2f);
				lodData.posData = CreatePositionDataFile(splats, assetDir, assetName, lodLevel);

				// Generate rotation/scale data
				progressCallback?.Invoke($"Encoding rotations/scales for LOD {lodLevel}...", 0.4f);
				lodData.otherData = CreateOtherDataFile(splats, assetDir, assetName, lodLevel);

				// Generate color data
				progressCallback?.Invoke($"Encoding colors for LOD {lodLevel}...", 0.6f);
				lodData.colorData = CreateColorDataFile(splats, assetDir, assetName, lodLevel);

				// Copy SH data (simplified - use source SH data with remapping)
				progressCallback?.Invoke($"Copying SH data for LOD {lodLevel}...", 0.8f);
				lodData.shData = CreateSHDataFile(splats, assetDir, assetName, lodLevel);

				// Generate chunk data if source has it
				if (m_SourceAsset.chunkData != null)
				{
					progressCallback?.Invoke($"Generating chunk data for LOD {lodLevel}...", 0.9f);
					lodData.chunkData = CreateChunkDataFile(splats, assetDir, assetName, lodLevel);
				}

				progressCallback?.Invoke($"LOD {lodLevel} data files generated!", 1f);

				return lodData;
			}
			catch (Exception e)
			{
				Debug.LogError($"[SplatDataWriter] Failed to generate LOD {lodLevel} data files: {e.Message}\n{e.StackTrace}");
				throw;
			}
		}

		TextAsset CreatePositionDataFile(GaussianSplatDataProcessor.SplatData[] splats, string dir, string name, int lodLevel)
		{
			string fileName = $"{name}_LOD{lodLevel}_pos.bytes";
			string path = Path.Combine(dir, fileName);

			// Encode positions based on source format
			byte[] data = EncodePositions(splats, m_SourceAsset.posFormat);

			// Write to file
			File.WriteAllBytes(path, data);
			AssetDatabase.ImportAsset(path);

			return AssetDatabase.LoadAssetAtPath<TextAsset>(path);
		}

		byte[] EncodePositions(GaussianSplatDataProcessor.SplatData[] splats, GaussianSplatAsset.VectorFormat format)
		{
			var bounds = new Bounds();
			bounds.SetMinMax(m_SourceAsset.boundsMin, m_SourceAsset.boundsMax);

			switch (format)
			{
				case GaussianSplatAsset.VectorFormat.Float32:
					return EncodePositionsFloat32(splats);
				case GaussianSplatAsset.VectorFormat.Norm16:
					return EncodePositionsNorm16(splats, bounds);
				case GaussianSplatAsset.VectorFormat.Norm11:
					return EncodePositionsNorm11(splats, bounds);
				case GaussianSplatAsset.VectorFormat.Norm6:
					return EncodePositionsNorm6(splats, bounds);
				default:
					throw new ArgumentException($"Unsupported position format: {format}");
			}
		}

		byte[] EncodePositionsFloat32(GaussianSplatDataProcessor.SplatData[] splats)
		{
			var data = new List<byte>(splats.Length * 12);
			foreach (var splat in splats)
			{
				data.AddRange(BitConverter.GetBytes(splat.position.x));
				data.AddRange(BitConverter.GetBytes(splat.position.y));
				data.AddRange(BitConverter.GetBytes(splat.position.z));
			}
			return data.ToArray();
		}

		byte[] EncodePositionsNorm16(GaussianSplatDataProcessor.SplatData[] splats, Bounds bounds)
		{
			var data = new List<byte>(splats.Length * 6);
			foreach (var splat in splats)
			{
				float fx = Mathf.InverseLerp(bounds.min.x, bounds.max.x, splat.position.x);
				float fy = Mathf.InverseLerp(bounds.min.y, bounds.max.y, splat.position.y);
				float fz = Mathf.InverseLerp(bounds.min.z, bounds.max.z, splat.position.z);

				ushort x = (ushort)(fx * 65535f);
				ushort y = (ushort)(fy * 65535f);
				ushort z = (ushort)(fz * 65535f);

				data.AddRange(BitConverter.GetBytes(x));
				data.AddRange(BitConverter.GetBytes(y));
				data.AddRange(BitConverter.GetBytes(z));
			}
			return data.ToArray();
		}

		byte[] EncodePositionsNorm11(GaussianSplatDataProcessor.SplatData[] splats, Bounds bounds)
		{
			var data = new List<byte>(splats.Length * 4);
			foreach (var splat in splats)
			{
				float fx = Mathf.InverseLerp(bounds.min.x, bounds.max.x, splat.position.x);
				float fy = Mathf.InverseLerp(bounds.min.y, bounds.max.y, splat.position.y);
				float fz = Mathf.InverseLerp(bounds.min.z, bounds.max.z, splat.position.z);

				uint x = (uint)(fx * 2047f) & 0x7FF;
				uint y = (uint)(fy * 1023f) & 0x3FF;
				uint z = (uint)(fz * 2047f) & 0x7FF;

				uint packed = x | (y << 11) | (z << 21);
				data.AddRange(BitConverter.GetBytes(packed));
			}
			return data.ToArray();
		}

		byte[] EncodePositionsNorm6(GaussianSplatDataProcessor.SplatData[] splats, Bounds bounds)
		{
			var data = new List<byte>(splats.Length * 2);
			foreach (var splat in splats)
			{
				float fx = Mathf.InverseLerp(bounds.min.x, bounds.max.x, splat.position.x);
				float fy = Mathf.InverseLerp(bounds.min.y, bounds.max.y, splat.position.y);
				float fz = Mathf.InverseLerp(bounds.min.z, bounds.max.z, splat.position.z);

				uint x = (uint)(fx * 63f) & 0x3F;
				uint y = (uint)(fy * 31f) & 0x1F;
				uint z = (uint)(fz * 31f) & 0x1F;

				ushort packed = (ushort)(x | (y << 6) | (z << 11));
				data.AddRange(BitConverter.GetBytes(packed));
			}
			return data.ToArray();
		}

		TextAsset CreateOtherDataFile(GaussianSplatDataProcessor.SplatData[] splats, string dir, string name, int lodLevel)
		{
			string fileName = $"{name}_LOD{lodLevel}_other.bytes";
			string path = Path.Combine(dir, fileName);

			// Encode rotation/scale data
			byte[] data = EncodeOtherData(splats, m_SourceAsset.scaleFormat);

			File.WriteAllBytes(path, data);
			AssetDatabase.ImportAsset(path);

			return AssetDatabase.LoadAssetAtPath<TextAsset>(path);
		}

		byte[] EncodeOtherData(GaussianSplatDataProcessor.SplatData[] splats, GaussianSplatAsset.VectorFormat scaleFormat)
		{
			int stride = GaussianSplatAsset.GetOtherSizeNoSHIndex(scaleFormat);
			var data = new List<byte>(splats.Length * stride);

			foreach (var splat in splats)
			{
				// Encode quaternion (4 bytes)
				uint packedQuat = PackQuaternion(splat.rotation);
				data.AddRange(BitConverter.GetBytes(packedQuat));

				// Encode scale
				data.AddRange(EncodeScale(splat.scale, scaleFormat));
			}

			return data.ToArray();
		}

		uint PackQuaternion(float4 q)
		{
			// Find largest component
			int maxComp = 0;
			float maxVal = math.abs(q.x);
			for (int i = 1; i < 4; i++)
			{
				float val = math.abs(q[i]);
				if (val > maxVal)
				{
					maxVal = val;
					maxComp = i;
				}
			}

			// Reorder so largest component is last
			float4 reordered = new float4(
				q[(maxComp + 1) % 4],
				q[(maxComp + 2) % 4],
				q[(maxComp + 3) % 4],
				q[maxComp]
			);

			// Ensure w (now largest) is positive
			if (reordered.w < 0)
				reordered = -reordered;

			// Pack to 10-10-10-2 format
			uint a = (uint)Mathf.Clamp((reordered.x * 0.5f + 0.5f) * 1023f, 0, 1023);
			uint b = (uint)Mathf.Clamp((reordered.y * 0.5f + 0.5f) * 1023f, 0, 1023);
			uint c = (uint)Mathf.Clamp((reordered.z * 0.5f + 0.5f) * 1023f, 0, 1023);
			uint comp = (uint)maxComp;

			return (comp << 30) | (a << 20) | (b << 10) | c;
		}

		byte[] EncodeScale(float3 scale, GaussianSplatAsset.VectorFormat format)
		{
			switch (format)
			{
				case GaussianSplatAsset.VectorFormat.Float32:
					{
						var data = new List<byte>(12);
						data.AddRange(BitConverter.GetBytes(scale.x));
						data.AddRange(BitConverter.GetBytes(scale.y));
						data.AddRange(BitConverter.GetBytes(scale.z));
						return data.ToArray();
					}
				case GaussianSplatAsset.VectorFormat.Norm16:
					{
						var data = new List<byte>(6);
						ushort x = (ushort)Mathf.Clamp(scale.x / 2f * 65535f, 0, 65535);
						ushort y = (ushort)Mathf.Clamp(scale.y / 2f * 65535f, 0, 65535);
						ushort z = (ushort)Mathf.Clamp(scale.z / 2f * 65535f, 0, 65535);
						data.AddRange(BitConverter.GetBytes(x));
						data.AddRange(BitConverter.GetBytes(y));
						data.AddRange(BitConverter.GetBytes(z));
						return data.ToArray();
					}
				case GaussianSplatAsset.VectorFormat.Norm11:
					{
						uint x = (uint)Mathf.Clamp(scale.x / 2f * 2047f, 0, 2047) & 0x7FF;
						uint y = (uint)Mathf.Clamp(scale.y / 2f * 1023f, 0, 1023) & 0x3FF;
						uint z = (uint)Mathf.Clamp(scale.z / 2f * 2047f, 0, 2047) & 0x7FF;
						uint packed = x | (y << 11) | (z << 21);
						return BitConverter.GetBytes(packed);
					}
				default:
					throw new ArgumentException($"Unsupported scale format: {format}");
			}
		}

		TextAsset CreateColorDataFile(GaussianSplatDataProcessor.SplatData[] splats, string dir, string name, int lodLevel)
		{
			string fileName = $"{name}_LOD{lodLevel}_color.bytes";
			string path = Path.Combine(dir, fileName);

			// Create texture with color data
			var (width, height) = GaussianSplatAsset.CalcTextureSize(splats.Length);
			var format = GaussianSplatAsset.ColorFormatToGraphics(m_SourceAsset.colorFormat);

			var tex = new Texture2D(width, height, format, UnityEngine.Experimental.Rendering.TextureCreationFlags.DontInitializePixels);

			// Set pixel colors
			for (int i = 0; i < splats.Length; i++)
			{
				int x = i % width;
				int y = i / width;
				Color col = new Color(
					splats[i].color.r / 255f,
					splats[i].color.g / 255f,
					splats[i].color.b / 255f,
					splats[i].opacity
				);
				tex.SetPixel(x, y, col);
			}

			// Fill remaining pixels with transparent
			for (int i = splats.Length; i < width * height; i++)
			{
				int x = i % width;
				int y = i / width;
				tex.SetPixel(x, y, Color.clear);
			}

			tex.Apply();

			// Get raw texture data
			byte[] data = tex.GetRawTextureData();
			File.WriteAllBytes(path, data);

			UnityEngine.Object.DestroyImmediate(tex);

			AssetDatabase.ImportAsset(path);
			return AssetDatabase.LoadAssetAtPath<TextAsset>(path);
		}

		TextAsset CreateSHDataFile(GaussianSplatDataProcessor.SplatData[] splats, string dir, string name, int lodLevel)
		{
			string fileName = $"{name}_LOD{lodLevel}_sh.bytes";
			string path = Path.Combine(dir, fileName);

			// For now, create a simplified SH data by remapping from source
			// Full implementation would re-encode SH coefficients
			var sourceSH = m_SourceAsset.shData.GetData<byte>();
			int shStride = (int)(sourceSH.Length / m_SourceAsset.splatCount);

			var data = new List<byte>(splats.Length * shStride);

			foreach (var splat in splats)
			{
				// Copy SH data from original splat
				int sourceOffset = splat.originalIndex * shStride;
				for (int i = 0; i < shStride && sourceOffset + i < sourceSH.Length; i++)
				{
					data.Add(sourceSH[sourceOffset + i]);
				}
			}

			File.WriteAllBytes(path, data.ToArray());
			AssetDatabase.ImportAsset(path);

			return AssetDatabase.LoadAssetAtPath<TextAsset>(path);
		}

		TextAsset CreateChunkDataFile(GaussianSplatDataProcessor.SplatData[] splats, string dir, string name, int lodLevel)
		{
			// For now, create dummy chunk data
			// Full implementation would recalculate chunks for filtered splats
			string fileName = $"{name}_LOD{lodLevel}_chunk.bytes";
			string path = Path.Combine(dir, fileName);

			int chunkCount = (splats.Length + GaussianSplatAsset.kChunkSize - 1) / GaussianSplatAsset.kChunkSize;
			int chunkSize = System.Runtime.InteropServices.Marshal.SizeOf(typeof(GaussianSplatAsset.ChunkInfo));
			byte[] data = new byte[chunkCount * chunkSize];

			File.WriteAllBytes(path, data);
			AssetDatabase.ImportAsset(path);

			return AssetDatabase.LoadAssetAtPath<TextAsset>(path);
		}
	}
}
