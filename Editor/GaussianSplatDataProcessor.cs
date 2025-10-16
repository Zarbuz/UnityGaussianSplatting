// SPDX-License-Identifier: MIT

using GaussianSplatting.Runtime;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Unity.Collections;
using Unity.Mathematics;
using UnityEditor;
using UnityEngine;

namespace GaussianSplatting.Editor
{
	/// <summary>
	/// Utility class for reading, processing, and generating LOD data for Gaussian Splat assets
	/// </summary>
	public class GaussianSplatDataProcessor
	{
		public struct SplatData
		{
			public float3 position;
			public float4 rotation;
			public float3 scale;
			public Color32 color;
			public float opacity;
			public float importanceScore;
			public int originalIndex;
		}

		readonly GaussianSplatAsset m_Asset;
		SplatData[] m_Splats;

		public SplatData[] splats => m_Splats;
		public int splatCount => m_Splats?.Length ?? 0;

		public GaussianSplatDataProcessor(GaussianSplatAsset asset)
		{
			m_Asset = asset;
		}

		/// <summary>
		/// Load and decode all splat data from the asset
		/// </summary>
		public bool LoadSplatData(Action<string, float> progressCallback = null)
		{
			try
			{
				progressCallback?.Invoke("Loading splat data...", 0f);

				int count = m_Asset.splatCount;
				m_Splats = new SplatData[count];

				// Load position data
				progressCallback?.Invoke("Decoding positions...", 0.2f);
				LoadPositions();

				// Load rotation/scale data
				progressCallback?.Invoke("Decoding rotations and scales...", 0.4f);
				LoadRotationsAndScales();

				// Load color data
				progressCallback?.Invoke("Decoding colors...", 0.6f);
				LoadColors();

				// Set original indices
				for (int i = 0; i < count; i++)
				{
					m_Splats[i].originalIndex = i;
				}

				progressCallback?.Invoke("Loading complete!", 1f);
				return true;
			}
			catch (Exception e)
			{
				Debug.LogError($"[SplatDataProcessor] Failed to load splat data: {e.Message}");
				return false;
			}
		}

		void LoadPositions()
		{
			var posData = m_Asset.posData.GetData<byte>();
			int count = m_Asset.splatCount;

			switch (m_Asset.posFormat)
			{
				case GaussianSplatAsset.VectorFormat.Float32:
					LoadPositionsFloat32(posData, count);
					break;
				case GaussianSplatAsset.VectorFormat.Norm16:
					LoadPositionsNorm16(posData, count);
					break;
				case GaussianSplatAsset.VectorFormat.Norm11:
					LoadPositionsNorm11(posData, count);
					break;
				case GaussianSplatAsset.VectorFormat.Norm6:
					LoadPositionsNorm6(posData, count);
					break;
			}
		}

		void LoadPositionsFloat32(NativeArray<byte> data, int count)
		{
			var bounds = new Bounds();
			bounds.SetMinMax(m_Asset.boundsMin, m_Asset.boundsMax);

			for (int i = 0; i < count; i++)
			{
				int offset = i * 12;
				float x = BitConverter.ToSingle(data.GetSubArray(offset, 4).ToArray(), 0);
				float y = BitConverter.ToSingle(data.GetSubArray(offset + 4, 4).ToArray(), 0);
				float z = BitConverter.ToSingle(data.GetSubArray(offset + 8, 4).ToArray(), 0);
				m_Splats[i].position = new float3(x, y, z);
			}
		}

		void LoadPositionsNorm16(NativeArray<byte> data, int count)
		{
			var bounds = new Bounds();
			bounds.SetMinMax(m_Asset.boundsMin, m_Asset.boundsMax);

			for (int i = 0; i < count; i++)
			{
				int offset = i * 6;
				ushort x = BitConverter.ToUInt16(data.GetSubArray(offset, 2).ToArray(), 0);
				ushort y = BitConverter.ToUInt16(data.GetSubArray(offset + 2, 2).ToArray(), 0);
				ushort z = BitConverter.ToUInt16(data.GetSubArray(offset + 4, 2).ToArray(), 0);

				float fx = x / 65535f;
				float fy = y / 65535f;
				float fz = z / 65535f;

				m_Splats[i].position = new float3(
					Mathf.Lerp(bounds.min.x, bounds.max.x, fx),
					Mathf.Lerp(bounds.min.y, bounds.max.y, fy),
					Mathf.Lerp(bounds.min.z, bounds.max.z, fz)
				);
			}
		}

		void LoadPositionsNorm11(NativeArray<byte> data, int count)
		{
			var bounds = new Bounds();
			bounds.SetMinMax(m_Asset.boundsMin, m_Asset.boundsMax);

			for (int i = 0; i < count; i++)
			{
				int offset = i * 4;
				uint packed = BitConverter.ToUInt32(data.GetSubArray(offset, 4).ToArray(), 0);

				uint x = packed & 0x7FF;
				uint y = (packed >> 11) & 0x3FF;
				uint z = (packed >> 21) & 0x7FF;

				float fx = x / 2047f;
				float fy = y / 1023f;
				float fz = z / 2047f;

				m_Splats[i].position = new float3(
					Mathf.Lerp(bounds.min.x, bounds.max.x, fx),
					Mathf.Lerp(bounds.min.y, bounds.max.y, fy),
					Mathf.Lerp(bounds.min.z, bounds.max.z, fz)
				);
			}
		}

		void LoadPositionsNorm6(NativeArray<byte> data, int count)
		{
			var bounds = new Bounds();
			bounds.SetMinMax(m_Asset.boundsMin, m_Asset.boundsMax);

			for (int i = 0; i < count; i++)
			{
				int offset = i * 2;
				ushort packed = BitConverter.ToUInt16(data.GetSubArray(offset, 2).ToArray(), 0);

				uint x = (uint)(packed & 0x3F);
				uint y = (uint)((packed >> 6) & 0x1F);
				uint z = (uint)((packed >> 11) & 0x1F);

				float fx = x / 63f;
				float fy = y / 31f;
				float fz = z / 31f;

				m_Splats[i].position = new float3(
					Mathf.Lerp(bounds.min.x, bounds.max.x, fx),
					Mathf.Lerp(bounds.min.y, bounds.max.y, fy),
					Mathf.Lerp(bounds.min.z, bounds.max.z, fz)
				);
			}
		}

		void LoadRotationsAndScales()
		{
			var otherData = m_Asset.otherData.GetData<byte>();
			int count = m_Asset.splatCount;
			int stride = GaussianSplatAsset.GetOtherSizeNoSHIndex(m_Asset.scaleFormat);

			for (int i = 0; i < count; i++)
			{
				int offset = i * stride;

				// Rotation (quaternion, 4 bytes)
				uint rotPacked = BitConverter.ToUInt32(otherData.GetSubArray(offset, 4).ToArray(), 0);
				m_Splats[i].rotation = UnpackQuaternion(rotPacked);

				// Scale (depends on format)
				offset += 4;
				m_Splats[i].scale = LoadScale(otherData, offset, m_Asset.scaleFormat);
			}
		}

		float4 UnpackQuaternion(uint packed)
		{
			// Unpack quaternion from uint (see shader implementation)
			int maxComp = (int)((packed >> 30) & 3);
			float a = ((packed >> 20) & 0x3FF) / 1023f * 2f - 1f;
			float b = ((packed >> 10) & 0x3FF) / 1023f * 2f - 1f;
			float c = (packed & 0x3FF) / 1023f * 2f - 1f;

			float d = Mathf.Sqrt(Mathf.Max(0, 1f - a * a - b * b - c * c));

			float4 q = new float4(a, b, c, d);
			// Reconstruct full quaternion based on which component was largest
			float4 result = float4.zero;
			for (int i = 0; i < 4; i++)
			{
				result[i] = q[(i + maxComp) % 4];
			}
			return math.normalize(result);
		}

		float3 LoadScale(NativeArray<byte> data, int offset, GaussianSplatAsset.VectorFormat format)
		{
			switch (format)
			{
				case GaussianSplatAsset.VectorFormat.Float32:
					{
						float x = BitConverter.ToSingle(data.GetSubArray(offset, 4).ToArray(), 0);
						float y = BitConverter.ToSingle(data.GetSubArray(offset + 4, 4).ToArray(), 0);
						float z = BitConverter.ToSingle(data.GetSubArray(offset + 8, 4).ToArray(), 0);
						return new float3(x, y, z);
					}
				case GaussianSplatAsset.VectorFormat.Norm16:
					{
						ushort x = BitConverter.ToUInt16(data.GetSubArray(offset, 2).ToArray(), 0);
						ushort y = BitConverter.ToUInt16(data.GetSubArray(offset + 2, 2).ToArray(), 0);
						ushort z = BitConverter.ToUInt16(data.GetSubArray(offset + 4, 2).ToArray(), 0);
						return new float3(x / 65535f, y / 65535f, z / 65535f) * 2f; // Scale range
					}
				case GaussianSplatAsset.VectorFormat.Norm11:
					{
						uint packed = BitConverter.ToUInt32(data.GetSubArray(offset, 4).ToArray(), 0);
						uint x = packed & 0x7FF;
						uint y = (packed >> 11) & 0x3FF;
						uint z = (packed >> 21) & 0x7FF;
						return new float3(x / 2047f, y / 1023f, z / 2047f) * 2f;
					}
				default:
					return float3.zero;
			}
		}

		void LoadColors()
		{
			// Load color texture
			var (width, height) = GaussianSplatAsset.CalcTextureSize(m_Asset.splatCount);
			var format = GaussianSplatAsset.ColorFormatToGraphics(m_Asset.colorFormat);

			// Create temporary texture to read pixel data
			var colorData = m_Asset.colorData.GetData<byte>();
			var tex = new Texture2D(width, height, format, UnityEngine.Experimental.Rendering.TextureCreationFlags.DontInitializePixels);
			tex.SetPixelData(colorData.ToArray(), 0);
			tex.Apply();

			// Read colors and opacity
			for (int i = 0; i < m_Asset.splatCount; i++)
			{
				int x = i % width;
				int y = i / width;
				Color col = tex.GetPixel(x, y);
				m_Splats[i].color = new Color32(
					(byte)(col.r * 255),
					(byte)(col.g * 255),
					(byte)(col.b * 255),
					255
				);
				m_Splats[i].opacity = col.a;
			}

			UnityEngine.Object.DestroyImmediate(tex);
		}

		/// <summary>
		/// Calculate importance scores for all splats based on LODGE paper
		/// Score = opacity × size × visibility_contribution
		/// </summary>
		public void CalculateImportanceScores()
		{
			for (int i = 0; i < m_Splats.Length; i++)
			{
				var splat = m_Splats[i];

				// Factor 1: Opacity (splats with low opacity are less important)
				float opacityFactor = splat.opacity;

				// Factor 2: Size (very small or very large splats may be less important)
				float avgScale = (splat.scale.x + splat.scale.y + splat.scale.z) / 3f;
				float sizeFactor = Mathf.Clamp01(avgScale / 0.1f); // Normalize around typical scale

				// Factor 3: Color variance (splats with low saturation/brightness less important)
				float brightness = (splat.color.r + splat.color.g + splat.color.b) / (3f * 255f);
				float colorFactor = Mathf.Lerp(0.5f, 1f, brightness);

				// Combined importance score
				splat.importanceScore = opacityFactor * sizeFactor * colorFactor;
				m_Splats[i] = splat;
			}
		}

		/// <summary>
		/// Prune splats based on importance threshold and pruning ratio
		/// Returns a new array of filtered splats
		/// </summary>
		public SplatData[] PruneSplats(float pruningRatio, float importanceThreshold)
		{
			if (pruningRatio <= 0f || m_Splats == null || m_Splats.Length == 0)
				return m_Splats;

			// Sort by importance (descending)
			var sorted = m_Splats.OrderByDescending(s => s.importanceScore).ToArray();

			// Calculate target count based on pruning ratio
			int targetCount = Mathf.RoundToInt(m_Splats.Length * (1f - pruningRatio));
			targetCount = Mathf.Max(100, targetCount); // Keep at least 100 splats

			// Also filter by importance threshold
			var filtered = new List<SplatData>();
			for (int i = 0; i < targetCount && i < sorted.Length; i++)
			{
				if (sorted[i].importanceScore >= importanceThreshold || filtered.Count < 100)
				{
					filtered.Add(sorted[i]);
				}
			}

			Debug.Log($"[SplatDataProcessor] Pruned {m_Splats.Length} → {filtered.Count} splats " +
			          $"(ratio: {pruningRatio:P1}, threshold: {importanceThreshold:F3})");

			return filtered.ToArray();
		}
	}
}
