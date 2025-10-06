// SPDX-License-Identifier: MIT

using System;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;

namespace GaussianSplatting.Runtime
{
	public class SplatSplitter : MonoBehaviour
	{
		//Split settings
		[Tooltip("Size of each partition (X=width, Y=height, Z=depth)")]
		public Vector3 PartitionSize = new Vector3(10f, 10f, 10f);

		[Tooltip("Offset from the GameObject position to center the grid")]
		public Vector3 CenterOffset;

		[Tooltip("Number of partition columns (along X axis)")]
		public int NumColumns = 2;

		[Tooltip("Number of partition rows (along Z axis)")]
		public int NumRows = 2;

		[Tooltip("Number of partition layers (along Y axis - vertical)")]
		public int NumDepth = 1;

		private Bounds[] _bounds = null;

		// Runtime partition management (currently unused - requires full partition loading system)
		private GaussianSplatRenderer _defaultRenderer;
		private Dictionary<int, SplatPartition> _partitions;
		[SerializeField]
		private GameObject _splatPartitionPrefab;

#if UNITY_EDITOR
		private void OnValidate()
		{
			if (Application.isPlaying) return;

			_bounds = new Bounds[NumRows * NumColumns * NumDepth];
			for (int idx = 0; idx < _bounds.Length; ++idx)
			{
				_bounds[idx] = CalculateBounds(idx);
			}
			_partitions = new Dictionary<int, SplatPartition>();
		}
#endif

		private void Awake()
		{
			_defaultRenderer = GetComponent<GaussianSplatRenderer>();
			_bounds = new Bounds[NumRows * NumColumns * NumDepth];
			for (int idx = 0; idx < _bounds.Length; ++idx)
			{
				_bounds[idx] = CalculateBounds(idx);
			}
			_partitions = new Dictionary<int, SplatPartition>();
		}

		void Update()
		{
			for (int idx = 0; idx < _partitions.Count; ++idx)
			{
				if (!_partitions.ContainsKey(idx)) continue;
				if (_partitions[idx] == null) continue;
				if (_partitions[idx].StartIndex == -1) continue;

				_partitions[idx].gameObject.SetActive(IsVisibleInCamera(idx));

				if (_partitions[idx].ShouldRender)
				{
					Vector3 toCenter = _bounds[idx].center - Camera.main.transform.position;
					_partitions[idx].RenderOrder = Mathf.RoundToInt(toCenter.sqrMagnitude);
				}
			}
		}

		public int[] GetPartitionOrder()
		{
			(int index, int order)[] orderArray = new (int, int)[NumColumns * NumRows * NumDepth];
			for (int idx = 0; idx < orderArray.Length; ++idx)
			{
				Vector3 toCenter = _bounds[idx].center - Camera.main.transform.position;
				orderArray[idx].index = idx;
				orderArray[idx].order = Mathf.RoundToInt(toCenter.sqrMagnitude);
				if (!IsVisibleInCamera(idx))
				{
					orderArray[idx].order += 1000000;
				}
			}

			Array.Sort(orderArray, (a, b) => { return a.order.CompareTo(b.order); });
			return orderArray.Select(t => t.index).ToArray();
		}

		public SplatPartition CreatePartition(int partitionIndex)
		{
			GameObject instance = GameObject.Instantiate(_splatPartitionPrefab, transform);
			instance.transform.localRotation = Quaternion.identity;
			instance.transform.localScale = Vector3.one;
			instance.gameObject.SetActive(false);
			if (partitionIndex >= 0)
			{
				Bounds b = GetBounds(partitionIndex);
				instance.transform.position = b.center;
			}
			else
			{
				instance.transform.position = transform.position;
			}

			var partition = instance.GetComponent<SplatPartition>();
			partition.PartitionIndex = partitionIndex;
			if (partitionIndex == -1)
			{
				partition.RenderOrder = 1000;
			}
			_partitions.Add(partitionIndex, partition);
			return partition;
		}

		public void InitializeRenderer()
		{
			_defaultRenderer.ReserveResources(_partitions.Values.ToArray());
		}

		public void SplatLoaded(int partitionIndex, GaussianSplatAsset asset)
		{
			var partition = _partitions[partitionIndex];
			partition.Asset = asset;

			GaussianSplatRenderSystem.instance.SetSplatActive(partition, true);
			partition.enabled = true;
			if (IsVisibleInCamera(partitionIndex))
			{
				partition.gameObject.SetActive(true);
			}
		}

		public SplatPartition GetPartition(int partitionIndex)
		{
			return _partitions.GetValueOrDefault(partitionIndex);
		}

		public bool IsVisibleInCamera(int partitionIndex)
		{
			if (partitionIndex == -1) return true;

			Bounds bounds = GetBounds(partitionIndex);
			if (bounds.Contains(Camera.main.transform.position))
			{
				return true;
			}

			int vertRays = 5;
			int horRays = 7;
			float xSpacing = horRays <= 1 ? 0 : 1f / (horRays - 1);
			float ySpacing = vertRays <= 1 ? 0 : 1f / (vertRays - 1);
			float startX = horRays <= 1 ? 0.5f : 0f;
			float startY = vertRays <= 1 ? 0.5f : 0f;
			for (int x = 0; x < horRays; ++x)
			{
				for (int y = 0; y < vertRays; ++y)
				{
					Vector3 viewPortPoint = new Vector3(startX + x * xSpacing, startY + y * ySpacing);
					Ray r = Camera.main.ViewportPointToRay(viewPortPoint);

					if (bounds.IntersectRay(r, out float dist))
					{
						if (dist > Camera.main.nearClipPlane && dist < Camera.main.farClipPlane)
							return true;
					}

					Ray r2 = new Ray(r.origin + r.direction * Camera.main.farClipPlane, -r.direction);

					if (bounds.IntersectRay(r2, out float dist2))
					{
						if (dist2 > 0 && dist2 < Camera.main.farClipPlane - Camera.main.nearClipPlane)
							return true;
					}
				}
			}
			return false;
		}

#if UNITY_EDITOR
		private void OnDrawGizmos()
		{
			for (int depth = 0; depth < NumDepth; ++depth)
			{
				for (int row = 0; row < NumRows; ++row)
				{
					for (int col = 0; col < NumColumns; ++col)
					{
						int partitionIndex = GetPartitionIndex(row, col, depth);

						if (Application.isPlaying && !IsVisibleInCamera(partitionIndex)) continue;

						Bounds bounds = CalculateBounds(partitionIndex);
						Gizmos.color = Color.yellow;
						Gizmos.DrawWireCube(bounds.center, bounds.size);
					}
				}
			}

		}
#endif

		public int GetPartitionIndex(int row, int column, int depth)
		{
			return depth * (NumRows * NumColumns) + row * NumColumns + column;
		}

		public Bounds GetBounds(int partitionIndex)
		{
			return _bounds[partitionIndex];
		}

		private Bounds CalculateBounds(int partitionIndex)
		{
			Vector3 center = transform.position + CenterOffset;
			Vector3 gridSize = new Vector3(
				PartitionSize.x * NumColumns,
				PartitionSize.y * NumDepth,
				PartitionSize.z * NumRows
			);
			Vector3 startPos = center - gridSize * 0.5f + PartitionSize * 0.5f;

			// Decode partition index to 3D coordinates
			int totalPerLayer = NumRows * NumColumns;
			int depthIdx = partitionIndex / totalPerLayer;
			int remainingIdx = partitionIndex % totalPerLayer;
			int rowIdx = remainingIdx / NumColumns;
			int colIdx = remainingIdx % NumColumns;

			Vector3 partitionCenter = startPos + new Vector3(
				PartitionSize.x * colIdx,
				PartitionSize.y * depthIdx,
				PartitionSize.z * rowIdx
			);

			return new Bounds(partitionCenter, PartitionSize);
		}

		public bool IsInBounds(int partitionIndex, Vector3 pos)
		{
			return GetBounds(partitionIndex).Contains(pos);
		}

		public int GetPartitionIndex(Vector3 pos)
		{
			for (int idx = 0; idx < _bounds.Length; ++idx)
			{
				if (IsInBounds(idx, pos)) return idx;
			}
			return -1;
		}
	}
}
