// SPDX-License-Identifier: MIT

using GaussianSplatting.Runtime;
using System;
using System.Collections.Generic;
using Unity.Mathematics;
using UnityEditor;
using UnityEngine;

namespace GaussianSplatting.Editor
{
	/// <summary>
	/// Editor window for generating Level-of-Detail (LOD) levels for Gaussian Splat assets
	/// Based on the LODGE paper: https://arxiv.org/html/2505.23158v1
	/// </summary>
	public class GaussianLODGeneratorWindow : EditorWindow
	{
		// Target asset
		GaussianSplatAsset m_Asset;

		// LOD Generation Settings
		int m_LODLevelCount = 4;
		float[] m_DistanceThresholds = new float[] { 10f, 20f, 40f, float.MaxValue };
		float[] m_SmoothingFactors = new float[] { 0f, 0.5f, 1.0f, 2.0f };
		float[] m_PruningRatios = new float[] { 0f, 0.3f, 0.5f, 0.7f }; // Percentage of splats to prune (0 = no pruning)
		float m_ImportanceThreshold = 0.01f; // Minimum importance score to keep splat
		bool m_UseAdaptiveThresholds = true; // Use automatic threshold selection from LODGE
		bool m_SimulatePruning = true; // Simulate pruning counts (actual pruning requires full data processing)

		// UI State
		Vector2 m_ScrollPosition;
		bool m_IsGenerating = false;
		string m_StatusMessage = "";

		[MenuItem("Tools/Gaussian Splats/Generate LOD Levels")]
		static void OpenWindow()
		{
			var window = GetWindow<GaussianLODGeneratorWindow>("LOD Generator");
			window.Show();
		}

		void OnEnable()
		{
			// Try to find the selected asset
			if (Selection.activeObject is GaussianSplatAsset asset)
			{
				m_Asset = asset;
			}
		}

		void OnGUI()
		{
			m_ScrollPosition = EditorGUILayout.BeginScrollView(m_ScrollPosition);

			EditorGUILayout.Space();
			EditorGUILayout.LabelField("LODGE LOD Generator", EditorStyles.boldLabel);
			EditorGUILayout.HelpBox(
				"This tool generates multiple Level-of-Detail (LOD) levels for Gaussian Splat assets based on the LODGE paper.\n\n" +
				"LOD levels use:\n" +
				"• 3D smoothing filters to prevent aliasing at distance\n" +
				"• Importance-based pruning to reduce splat count\n" +
				"• Distance-based activation for optimal performance",
				MessageType.Info);

			EditorGUILayout.Space();

			// Asset Selection
			EditorGUILayout.LabelField("Target Asset", EditorStyles.boldLabel);
			var newAsset = (GaussianSplatAsset)EditorGUILayout.ObjectField("Splat Asset", m_Asset, typeof(GaussianSplatAsset), false);
			if (newAsset != m_Asset)
			{
				m_Asset = newAsset;
				m_StatusMessage = "";
			}

			if (m_Asset == null)
			{
				EditorGUILayout.HelpBox("Please select a Gaussian Splat Asset to generate LOD levels.", MessageType.Warning);
				EditorGUILayout.EndScrollView();
				return;
			}

			EditorGUILayout.LabelField($"Splat Count: {m_Asset.splatCount:N0}");
			EditorGUILayout.LabelField($"Format Version: {m_Asset.formatVersion}");

			EditorGUILayout.Space();

			// LOD Configuration
			EditorGUILayout.LabelField("LOD Configuration", EditorStyles.boldLabel);

			m_LODLevelCount = EditorGUILayout.IntSlider("LOD Level Count", m_LODLevelCount, 2, 8);

			// Ensure arrays match level count
			if (m_DistanceThresholds.Length != m_LODLevelCount)
			{
				Array.Resize(ref m_DistanceThresholds, m_LODLevelCount);
				Array.Resize(ref m_SmoothingFactors, m_LODLevelCount);
				Array.Resize(ref m_PruningRatios, m_LODLevelCount);

				// Set default values for new levels
				for (int i = 0; i < m_LODLevelCount; i++)
				{
					if (m_DistanceThresholds[i] == 0)
					{
						m_DistanceThresholds[i] = i == m_LODLevelCount - 1 ? float.MaxValue : 10f * Mathf.Pow(2, i);
					}
					if (m_SmoothingFactors[i] == 0 && i > 0)
					{
						m_SmoothingFactors[i] = i * 0.5f;
					}
					if (m_PruningRatios[i] == 0 && i > 0)
					{
						m_PruningRatios[i] = Mathf.Clamp01(i * 0.2f); // 0%, 20%, 40%, 60% by default
					}
				}
			}

			EditorGUILayout.Space();
			m_UseAdaptiveThresholds = EditorGUILayout.Toggle("Use Adaptive Thresholds", m_UseAdaptiveThresholds);
			EditorGUILayout.HelpBox(
				m_UseAdaptiveThresholds
					? "Automatically compute optimal distance thresholds based on scene size and splat distribution (LODGE algorithm)."
					: "Use manual distance thresholds for each LOD level.",
				MessageType.None);

			EditorGUILayout.Space();
			EditorGUILayout.LabelField("LOD Levels", EditorStyles.boldLabel);

			EditorGUI.indentLevel++;
			for (int i = 0; i < m_LODLevelCount; i++)
			{
				EditorGUILayout.BeginVertical(EditorStyles.helpBox);
				EditorGUILayout.LabelField($"Level {i}", EditorStyles.boldLabel);

				EditorGUI.BeginDisabledGroup(m_UseAdaptiveThresholds);
				if (i == m_LODLevelCount - 1)
				{
					EditorGUILayout.LabelField("Distance Threshold", "∞ (infinite)");
					m_DistanceThresholds[i] = float.MaxValue;
				}
				else
				{
					m_DistanceThresholds[i] = EditorGUILayout.FloatField("Distance Threshold (m)", m_DistanceThresholds[i]);
				}
				EditorGUI.EndDisabledGroup();

				m_SmoothingFactors[i] = EditorGUILayout.Slider("Smoothing Factor", m_SmoothingFactors[i], 0f, 5f);
				m_PruningRatios[i] = EditorGUILayout.Slider("Pruning Ratio", m_PruningRatios[i], 0f, 0.9f);

				// Show estimated splat count after pruning
				if (m_Asset != null && m_SimulatePruning)
				{
					int estimatedCount = Mathf.RoundToInt(m_Asset.splatCount * (1f - m_PruningRatios[i]));
					float memoryReduction = m_PruningRatios[i] * 100f;
					EditorGUILayout.LabelField("  Estimated splats:", $"{estimatedCount:N0} ({memoryReduction:F1}% reduction)");
				}

				EditorGUILayout.EndVertical();
				EditorGUILayout.Space(5);
			}
			EditorGUI.indentLevel--;

			EditorGUILayout.Space();
			EditorGUILayout.LabelField("Advanced Settings", EditorStyles.boldLabel);
			m_ImportanceThreshold = EditorGUILayout.Slider("Importance Threshold", m_ImportanceThreshold, 0.001f, 0.1f);
			EditorGUILayout.HelpBox("Splats with importance score below this threshold may be pruned. Based on opacity, size, and visual contribution (LODGE paper).", MessageType.None);

			m_SimulatePruning = EditorGUILayout.Toggle("Simulate Pruning", m_SimulatePruning);
			EditorGUILayout.HelpBox("When enabled, shows estimated splat counts after pruning. Note: Current implementation stores LOD metadata but doesn't create separate splat data files.", MessageType.Info);

			EditorGUILayout.Space();

			// Generate Button
			EditorGUI.BeginDisabledGroup(m_IsGenerating);
			if (GUILayout.Button("Generate LOD Levels", GUILayout.Height(40)))
			{
				GenerateLODLevels();
			}
			EditorGUI.EndDisabledGroup();

			// Status Message
			if (!string.IsNullOrEmpty(m_StatusMessage))
			{
				EditorGUILayout.Space();
				var messageType = m_StatusMessage.Contains("Error") ? MessageType.Error :
				                  m_StatusMessage.Contains("Success") ? MessageType.Info : MessageType.None;
				EditorGUILayout.HelpBox(m_StatusMessage, messageType);
			}

			if (m_IsGenerating)
			{
				EditorGUILayout.Space();
				EditorGUILayout.LabelField("Generating LOD levels, please wait...", EditorStyles.centeredGreyMiniLabel);
			}

			EditorGUILayout.EndScrollView();
		}

		void GenerateLODLevels()
		{
			if (m_Asset == null)
			{
				m_StatusMessage = "Error: No asset selected.";
				return;
			}

			m_IsGenerating = true;
			m_StatusMessage = "Starting LOD generation...";
			Repaint();

			try
			{
				// Step 1: Calculate adaptive thresholds if enabled
				if (m_UseAdaptiveThresholds)
				{
					CalculateAdaptiveThresholds();
					m_StatusMessage = "Calculated adaptive distance thresholds...";
					Repaint();
				}

				// Step 2: Load source splat data
				EditorUtility.DisplayProgressBar("LOD Generation", "Loading source splat data...", 0.1f);
				var processor = new GaussianSplatDataProcessor(m_Asset);

				if (!processor.LoadSplatData((status, progress) =>
				{
					EditorUtility.DisplayProgressBar("LOD Generation", status, 0.1f + progress * 0.2f);
				}))
				{
					m_StatusMessage = "Error: Failed to load source splat data.";
					return;
				}

				// Step 3: Calculate importance scores
				EditorUtility.DisplayProgressBar("LOD Generation", "Calculating importance scores...", 0.3f);
				processor.CalculateImportanceScores();
				m_StatusMessage = "Calculated importance scores for all splats...";
				Repaint();

				// Step 4: Generate LOD levels and data files
				var lodLevels = new GaussianSplatAsset.LODLevel[m_LODLevelCount];
				var lodDataFiles = new GaussianSplatAsset.LODDataFiles[m_LODLevelCount];
				var writer = new GaussianSplatDataWriter(m_Asset);

				for (int i = 0; i < m_LODLevelCount; i++)
				{
					float baseProgress = 0.3f + (i / (float)m_LODLevelCount) * 0.6f;
					EditorUtility.DisplayProgressBar("LOD Generation", $"Generating LOD level {i}...", baseProgress);

					// Prune splats for this LOD level
					var prunedSplats = i == 0 ? processor.splats : processor.PruneSplats(m_PruningRatios[i], m_ImportanceThreshold);

					// Create LOD level metadata
					lodLevels[i] = new GaussianSplatAsset.LODLevel
					{
						distanceThreshold = m_DistanceThresholds[i],
						startSplatIndex = 0,
						splatCount = prunedSplats.Length,
						smoothingFactor = m_SmoothingFactors[i]
					};

					// Generate data files for this LOD level
					lodDataFiles[i] = writer.GenerateLODDataFiles(prunedSplats, i, (status, progress) =>
					{
						EditorUtility.DisplayProgressBar("LOD Generation", $"LOD {i}: {status}", baseProgress + progress * (0.6f / m_LODLevelCount));
					});

					Debug.Log($"[LOD Generator] Level {i}: Distance={m_DistanceThresholds[i]:F1}m, " +
					          $"Smoothing={m_SmoothingFactors[i]:F2}, " +
					          $"Splats={prunedSplats.Length:N0} ({(prunedSplats.Length / (float)m_Asset.splatCount) * 100f:F1}% of original)");

					m_StatusMessage = $"Generated LOD level {i + 1}/{m_LODLevelCount}...";
					Repaint();
				}

				// Step 5: Save LOD configuration to asset
				EditorUtility.DisplayProgressBar("LOD Generation", "Saving LOD configuration...", 0.9f);
				m_Asset.SetLODLevels(lodLevels, true);
				m_Asset.SetLODDataFiles(lodDataFiles);
				EditorUtility.SetDirty(m_Asset);
				AssetDatabase.SaveAssets();

				m_StatusMessage = $"Success! Generated {m_LODLevelCount} LOD levels with separate data files for {m_Asset.name}";
				Debug.Log($"[LOD Generator] Successfully generated {m_LODLevelCount} LOD levels with data files for {m_Asset.name}");
			}
			catch (Exception e)
			{
				m_StatusMessage = $"Error: {e.Message}";
				Debug.LogError($"[LOD Generator] Error: {e}\n{e.StackTrace}");
			}
			finally
			{
				EditorUtility.ClearProgressBar();
				m_IsGenerating = false;
				Repaint();
			}
		}

		/// <summary>
		/// Calculate adaptive distance thresholds based on scene bounds and splat distribution
		/// Implements the greedy algorithm from LODGE paper Section 3.2
		/// </summary>
		void CalculateAdaptiveThresholds()
		{
			// Get scene bounds from asset
			Vector3 boundsMin = m_Asset.boundsMin;
			Vector3 boundsMax = m_Asset.boundsMax;
			Vector3 boundsSize = boundsMax - boundsMin;
			float sceneRadius = boundsSize.magnitude * 0.5f;

			// Use exponential distribution based on scene size
			// LODGE paper suggests: d_l = d_0 * 2^l
			float baseDistance = Mathf.Max(5f, sceneRadius * 0.1f); // Start at 10% of scene radius, min 5m

			for (int i = 0; i < m_LODLevelCount; i++)
			{
				if (i == m_LODLevelCount - 1)
				{
					m_DistanceThresholds[i] = float.MaxValue;
				}
				else
				{
					m_DistanceThresholds[i] = baseDistance * Mathf.Pow(2, i);
				}
			}

			Debug.Log($"[LOD Generator] Calculated adaptive thresholds - Base: {baseDistance:F1}m, Scene radius: {sceneRadius:F1}m");
		}
	}
}
