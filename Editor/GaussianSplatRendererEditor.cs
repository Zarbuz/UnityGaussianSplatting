// SPDX-License-Identifier: MIT

using GaussianSplatting.Runtime;
using System.Collections.Generic;
using Unity.Mathematics;
using UnityEditor;
using UnityEngine;
using GaussianSplatRenderer = GaussianSplatting.Runtime.GaussianSplatRenderer;

namespace GaussianSplatting.Editor
{
	[CustomEditor(typeof(GaussianSplatRenderer))]
	[CanEditMultipleObjects]
	public class GaussianSplatRendererEditor : UnityEditor.Editor
	{
		const string kPrefExportBake = "nesnausk.GaussianSplatting.ExportBakeTransform";

		SerializedProperty m_PropAsset;
		SerializedProperty m_PropRenderOrder;
		SerializedProperty m_PropSplatScale;
		SerializedProperty m_PropOpacityScale;
		SerializedProperty m_PropSHOrder;
		SerializedProperty m_PropSHOnly;
		SerializedProperty m_PropSortNthFrame;
		SerializedProperty m_PropAdaptiveSortingEnabled;
		SerializedProperty m_PropCameraMovementThreshold;
		SerializedProperty m_PropCameraRotationThreshold;
		SerializedProperty m_PropFastSortFrequency;
		SerializedProperty m_PropChunkSortCacheEnabled;
		SerializedProperty m_PropChunkCacheDistanceThreshold;
		SerializedProperty m_PropDistanceBasedSortEnabled;
		SerializedProperty m_PropDistantChunkThreshold;
		SerializedProperty m_PropFrustumCullingEnabled;
		SerializedProperty m_PropFrustumCullingTolerance;
		SerializedProperty m_PropRenderMode;
		SerializedProperty m_PropPointDisplaySize;
		SerializedProperty m_PropShaderSplats;
		SerializedProperty m_PropShaderComposite;
		SerializedProperty m_PropShaderDebugPoints;
		SerializedProperty m_PropShaderDebugBoxes;
		SerializedProperty m_PropCSSplatUtilitiesFFX;
		SerializedProperty m_PropCSStreamCompact;

		bool m_ResourcesExpanded = false;
		int m_CameraIndex = 0;

		bool m_ExportBakeTransform;

		static int s_EditStatsUpdateCounter = 0;

		static HashSet<GaussianSplatRendererEditor> s_AllEditors = new();

		public static void BumpGUICounter()
		{
			++s_EditStatsUpdateCounter;
		}

		public static void RepaintAll()
		{
			foreach (var e in s_AllEditors)
				e.Repaint();
		}

		public void OnEnable()
		{
			m_ExportBakeTransform = EditorPrefs.GetBool(kPrefExportBake, false);

			m_PropAsset = serializedObject.FindProperty("m_Asset");
			m_PropRenderOrder = serializedObject.FindProperty("m_RenderOrder");
			m_PropSplatScale = serializedObject.FindProperty("m_SplatScale");
			m_PropOpacityScale = serializedObject.FindProperty("m_OpacityScale");
			m_PropSHOrder = serializedObject.FindProperty("m_SHOrder");
			m_PropSHOnly = serializedObject.FindProperty("m_SHOnly");
			m_PropSortNthFrame = serializedObject.FindProperty("m_SortNthFrame");
			m_PropAdaptiveSortingEnabled = serializedObject.FindProperty("m_AdaptiveSortingEnabled");
			m_PropCameraMovementThreshold = serializedObject.FindProperty("m_CameraMovementThreshold");
			m_PropCameraRotationThreshold = serializedObject.FindProperty("m_CameraRotationThreshold");
			m_PropFastSortFrequency = serializedObject.FindProperty("m_FastSortFrequency");
			m_PropChunkSortCacheEnabled = serializedObject.FindProperty("m_ChunkSortCacheEnabled");
			m_PropChunkCacheDistanceThreshold = serializedObject.FindProperty("m_ChunkCacheDistanceThreshold");
			m_PropDistanceBasedSortEnabled = serializedObject.FindProperty("m_DistanceBasedSortEnabled");
			m_PropDistantChunkThreshold = serializedObject.FindProperty("m_DistantChunkThreshold");
			m_PropFrustumCullingEnabled = serializedObject.FindProperty("m_FrustumCullingEnabled");
			m_PropFrustumCullingTolerance = serializedObject.FindProperty("m_FrustumCullingTolerance");
			m_PropRenderMode = serializedObject.FindProperty("m_RenderMode");
			m_PropPointDisplaySize = serializedObject.FindProperty("m_PointDisplaySize");
			m_PropShaderSplats = serializedObject.FindProperty("m_ShaderSplats");
			m_PropShaderComposite = serializedObject.FindProperty("m_ShaderComposite");
			m_PropShaderDebugPoints = serializedObject.FindProperty("m_ShaderDebugPoints");
			m_PropShaderDebugBoxes = serializedObject.FindProperty("m_ShaderDebugBoxes");
			m_PropCSSplatUtilitiesFFX = serializedObject.FindProperty("m_CSSplatUtilitiesFFX");
			m_PropCSStreamCompact = serializedObject.FindProperty("m_CSStreamCompact");

			s_AllEditors.Add(this);
		}

		public void OnDisable()
		{
			s_AllEditors.Remove(this);
		}

		public override void OnInspectorGUI()
		{
			var gs = target as GaussianSplatRenderer;
			if (!gs)
				return;

			serializedObject.Update();

			GUILayout.Label("Data Asset", EditorStyles.boldLabel);
			EditorGUILayout.PropertyField(m_PropAsset);

			if (!gs.HasValidAsset)
			{
				var msg = gs.asset != null && gs.asset.formatVersion != GaussianSplatAsset.kCurrentVersion
					? "Gaussian Splat asset version is not compatible, please recreate the asset"
					: "Gaussian Splat asset is not assigned or is empty";
				EditorGUILayout.HelpBox(msg, MessageType.Error);
			}

			EditorGUILayout.Space();
			GUILayout.Label("Render Options", EditorStyles.boldLabel);
			EditorGUILayout.PropertyField(m_PropRenderOrder);
			EditorGUILayout.PropertyField(m_PropSplatScale);
			EditorGUILayout.PropertyField(m_PropOpacityScale);
			EditorGUILayout.PropertyField(m_PropSHOrder);
			EditorGUILayout.PropertyField(m_PropSHOnly);
			EditorGUILayout.PropertyField(m_PropSortNthFrame);
			EditorGUILayout.PropertyField(m_PropAdaptiveSortingEnabled);
			if (m_PropAdaptiveSortingEnabled.boolValue)
			{
				EditorGUI.indentLevel++;
				EditorGUILayout.PropertyField(m_PropCameraMovementThreshold);
				EditorGUILayout.PropertyField(m_PropCameraRotationThreshold);
				EditorGUILayout.PropertyField(m_PropFastSortFrequency);
				EditorGUILayout.PropertyField(m_PropChunkSortCacheEnabled);
				if (m_PropChunkSortCacheEnabled.boolValue)
				{
					EditorGUI.indentLevel++;
					EditorGUILayout.PropertyField(m_PropChunkCacheDistanceThreshold);
					EditorGUI.indentLevel--;
				}
				EditorGUILayout.PropertyField(m_PropDistanceBasedSortEnabled);
				if (m_PropDistanceBasedSortEnabled.boolValue)
				{
					EditorGUI.indentLevel++;
					EditorGUILayout.PropertyField(m_PropDistantChunkThreshold);
					EditorGUI.indentLevel--;
				}
				EditorGUI.indentLevel--;
			}
			EditorGUILayout.PropertyField(m_PropFrustumCullingEnabled);
			EditorGUILayout.PropertyField(m_PropFrustumCullingTolerance);
			EditorGUILayout.Space();
			GUILayout.Label("Debugging Tweaks", EditorStyles.boldLabel);
			EditorGUILayout.PropertyField(m_PropRenderMode);
			if (m_PropRenderMode.intValue is (int)GaussianSplatRenderer.RenderMode.DebugPoints or (int)GaussianSplatRenderer.RenderMode.DebugPointIndices)
				EditorGUILayout.PropertyField(m_PropPointDisplaySize);

			EditorGUILayout.Space();
			m_ResourcesExpanded = EditorGUILayout.Foldout(m_ResourcesExpanded, "Resources", true, EditorStyles.foldoutHeader);
			if (m_ResourcesExpanded)
			{
				EditorGUILayout.PropertyField(m_PropShaderSplats);
				EditorGUILayout.PropertyField(m_PropShaderComposite);
				EditorGUILayout.PropertyField(m_PropShaderDebugPoints);
				EditorGUILayout.PropertyField(m_PropShaderDebugBoxes);
				EditorGUILayout.PropertyField(m_PropCSSplatUtilitiesFFX);
				EditorGUILayout.PropertyField(m_PropCSStreamCompact);
			}
			bool validAndEnabled = gs && gs.enabled && gs.gameObject.activeInHierarchy && gs.HasValidAsset;
			if (validAndEnabled && !gs.HasValidRenderSetup)
			{
				EditorGUILayout.HelpBox("Shader resources are not set up", MessageType.Error);
				validAndEnabled = false;
			}

			if (validAndEnabled && targets.Length == 1)
			{
				EditCameras(gs);
			}

			serializedObject.ApplyModifiedProperties();
		}

		void EditCameras(GaussianSplatRenderer gs)
		{
			var asset = gs.asset;
			var cameras = asset.cameras;
			if (cameras != null && cameras.Length != 0)
			{
				EditorGUILayout.Space();
				GUILayout.Label("Cameras", EditorStyles.boldLabel);
				var camIndex = EditorGUILayout.IntSlider("Camera", m_CameraIndex, 0, cameras.Length - 1);
				camIndex = math.clamp(camIndex, 0, cameras.Length - 1);
				if (camIndex != m_CameraIndex)
				{
					m_CameraIndex = camIndex;
					gs.ActivateCamera(camIndex);
				}
			}
		}


		bool HasFrameBounds()
		{
			return true;
		}

		Bounds OnGetFrameBounds()
		{
			var gs = target as GaussianSplatRenderer;
			if (!gs || !gs.HasValidRenderSetup)
				return new Bounds(Vector3.zero, Vector3.one);
			Bounds bounds = default;
			bounds.SetMinMax(gs.asset.boundsMin, gs.asset.boundsMax);
			if (gs.editSelectedSplats > 0)
			{
				bounds = gs.editSelectedBounds;
			}
			bounds.extents *= 0.7f;
			return TransformBounds(gs.transform, bounds);
		}

		public static Bounds TransformBounds(Transform tr, Bounds bounds)
		{
			var center = tr.TransformPoint(bounds.center);

			var ext = bounds.extents;
			var axisX = tr.TransformVector(ext.x, 0, 0);
			var axisY = tr.TransformVector(0, ext.y, 0);
			var axisZ = tr.TransformVector(0, 0, ext.z);

			// sum their absolute value to get the world extents
			ext.x = Mathf.Abs(axisX.x) + Mathf.Abs(axisY.x) + Mathf.Abs(axisZ.x);
			ext.y = Mathf.Abs(axisX.y) + Mathf.Abs(axisY.y) + Mathf.Abs(axisZ.y);
			ext.z = Mathf.Abs(axisX.z) + Mathf.Abs(axisY.z) + Mathf.Abs(axisZ.z);

			return new Bounds { center = center, extents = ext };
		}

	}
}