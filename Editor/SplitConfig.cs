// SPDX-License-Identifier: MIT

using GaussianSplatting.Editor.Utils;
using GaussianSplatting.Runtime;
using System;
using System.Collections.Generic;
using Unity.Collections;
using UnityEditor;
using UnityEngine;

namespace GaussianSplatting.Editor
{
	class SplitConfig
	{
		public SplatSplitter Splitter { get; private set; }

		public bool SplitterInScene => Splitter != null;
		public bool CanCreateSplitter => Splitter == null && SplitterOwner != null;
		public GameObject SplitterOwner { get; set; }

		public int NumAssetsToCreate => Splitter == null ? 1 : Splitter.NumColumns * Splitter.NumRows * Splitter.NumDepth + 1;

		public void AutoDetectSceneObjects()
		{
			Splitter = GameObject.FindAnyObjectByType<SplatSplitter>();
			SplitterOwner = Splitter?.gameObject;
		}

		public void CreateSplitter()
		{
			if (SplitterOwner == null) return;
			if (Splitter != null) return;

			Splitter = SplitterOwner.AddComponent<SplatSplitter>();
		}

		public void DrawEditorGUI(bool enabled)
		{
			EditorGUI.indentLevel++;

			// GS Root object
			GUILayout.BeginHorizontal();
			SplitterOwner = EditorGUILayout.ObjectField("GS root object", SplitterOwner, typeof(GameObject), true) as GameObject;
			GUI.enabled = enabled && CanCreateSplitter;
			if (GUILayout.Button("Add Splitter"))
			{
				CreateSplitter();
			}
			GUI.enabled = enabled;
			GUILayout.EndHorizontal();

			if (Splitter == null)
			{
				EditorGUI.indentLevel--;
				return;
			}

			// Splits settings
			SerializedObject obj = new SerializedObject(Splitter);
			EditorGUILayout.PropertyField(obj.FindProperty(nameof(SplatSplitter.PartitionSize)));
			EditorGUILayout.PropertyField(obj.FindProperty(nameof(SplatSplitter.NumColumns)));
			EditorGUILayout.PropertyField(obj.FindProperty(nameof(SplatSplitter.NumRows)));
			EditorGUILayout.PropertyField(obj.FindProperty(nameof(SplatSplitter.NumDepth)));
			obj.ApplyModifiedProperties();
			EditorGUI.indentLevel--;
		}

		public Dictionary<int, NativeArray<InputSplatData>> CalculatePartitions(NativeArray<InputSplatData> inputData)
		{
			if (Splitter == null)
			{
				return new Dictionary<int, NativeArray<InputSplatData>> { { -1, inputData } };
			}

			Dictionary<int, NativeArray<InputSplatData>> partitions = new();
			Dictionary<int, List<InputSplatData>> tempPartitionLists = new()
			{
				{ -1, new List<InputSplatData>() }
			};

			int numPartitions = Splitter.NumRows * Splitter.NumColumns * Splitter.NumDepth;
			for (int idx = 0; idx < numPartitions; ++idx)
			{
				tempPartitionLists.Add(idx, new List<InputSplatData>());
			}

			for (int idx = 0; idx < inputData.Length; ++idx)
			{
				InputSplatData data = inputData[idx];
				Vector3 worldPos = Splitter.transform.TransformPoint(data.pos);
				int partitionIdx = Splitter.GetPartitionIndex(worldPos);

				if (partitionIdx >= 0)
				{
					Bounds b = Splitter.GetBounds(partitionIdx);
					data.pos = data.pos - Splitter.transform.InverseTransformPoint(b.center);
				}

				tempPartitionLists[partitionIdx].Add(data);
			}

			// Verify no duplicates and log statistics
			int totalAssigned = 0;
			for (int idx = -1; idx < numPartitions; ++idx)
			{
				totalAssigned += tempPartitionLists[idx].Count;
			}

			if (totalAssigned != inputData.Length)
			{
				Debug.LogError($"Partition error: {inputData.Length} input splats but {totalAssigned} assigned! Some splats may be duplicated or lost.");
			}
			else
			{
				Debug.Log($"Partitioning successful: {inputData.Length} splats assigned to {numPartitions + 1} partitions (including Default)");
			}

			for (int idx = -1; idx < numPartitions; ++idx)
			{
				partitions.Add(idx, new NativeArray<InputSplatData>(tempPartitionLists[idx].ToArray(), Allocator.Persistent));
				tempPartitionLists[idx].Clear();
				GC.Collect();
			}

			return partitions;
		}
	}
}
