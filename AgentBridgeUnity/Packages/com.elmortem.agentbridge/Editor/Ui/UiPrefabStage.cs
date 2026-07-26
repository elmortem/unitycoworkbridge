using System;
using TMPro;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.SceneManagement;
using UnityEngine.UI;

namespace AgentBridge.Ui
{
	// Builds a throwaway scene with a camera-backed canvas at a fixed reference
	// resolution, instantiates the prefab under it and prepares it for
	// measurement (dump) or offscreen rendering (shot).
	//
	// A camera + RenderTexture fixes the canvas pixel size to the requested
	// reference resolution regardless of the editor game view, so screen rects
	// are deterministic. Only the shot path (render = true) actually renders,
	// disables the SRP and isolates the UI on layer 31.
	public static class UiPrefabStage
	{
		private const int IsolationLayer = 31;

		public static UiStage Open(string prefabPath, int width, int height, bool render)
		{
			var asset = AssetDatabase.LoadAssetAtPath<GameObject>(prefabPath);
			if (asset == null)
				throw new Exception("Prefab not found: " + prefabPath);

			var stage = new UiStage { Width = width, Height = height, Render = render };
			stage.Scene = EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Additive);

			if (render)
			{
				stage.PipelineOverridden = true;
				stage.SavedQualityPipeline = QualitySettings.renderPipeline;
				stage.SavedGraphicsPipeline = GraphicsSettings.defaultRenderPipeline;
				QualitySettings.renderPipeline = null;
				GraphicsSettings.defaultRenderPipeline = null;
			}

			var camGo = new GameObject("UiStageCamera");
			SceneManager.MoveGameObjectToScene(camGo, stage.Scene);
			stage.Camera = camGo.AddComponent<Camera>();
			stage.Camera.orthographic = true;
			stage.Camera.orthographicSize = height * 0.5f;
			stage.Camera.clearFlags = CameraClearFlags.SolidColor;
			stage.Camera.backgroundColor = new Color32(0x0D, 0x0D, 0x0D, 0xFF);
			stage.Camera.nearClipPlane = 0.01f;
			stage.Camera.farClipPlane = 1000f;
			stage.Camera.cullingMask = render ? (1 << IsolationLayer) : ~0;
			camGo.transform.position = new Vector3(0f, 0f, -100f);

			stage.Texture = new RenderTexture(width, height, 24, RenderTextureFormat.ARGB32);
			stage.Texture.Create();
			stage.Camera.targetTexture = stage.Texture;

			var canvasGo = new GameObject("UiStageCanvas", typeof(Canvas), typeof(CanvasScaler), typeof(GraphicRaycaster));
			SceneManager.MoveGameObjectToScene(canvasGo, stage.Scene);
			stage.Canvas = canvasGo.GetComponent<Canvas>();
			stage.Canvas.renderMode = RenderMode.ScreenSpaceCamera;
			stage.Canvas.worldCamera = stage.Camera;
			stage.Canvas.planeDistance = 100f;

			var scaler = canvasGo.GetComponent<CanvasScaler>();
			scaler.uiScaleMode = CanvasScaler.ScaleMode.ScaleWithScreenSize;
			scaler.referenceResolution = new Vector2(width, height);
			scaler.screenMatchMode = CanvasScaler.ScreenMatchMode.MatchWidthOrHeight;
			scaler.matchWidthOrHeight = 0.5f;

			stage.Instance = (GameObject)PrefabUtility.InstantiatePrefab(asset);
			stage.Instance.transform.SetParent(canvasGo.transform, false);

			DisableForeignBehaviours(stage.Instance);

			var group = stage.Instance.GetComponent<CanvasGroup>();
			if (group != null)
				group.alpha = 1f;

			if (render)
				SetLayerRecursive(canvasGo, IsolationLayer);

			foreach (var tmp in stage.Instance.GetComponentsInChildren<TMP_Text>(true))
				tmp.ForceMeshUpdate(true, true);

			Canvas.ForceUpdateCanvases();

			return stage;
		}

		public static void Close(UiStage stage)
		{
			if (stage == null)
				return;

			if (stage.Camera != null)
				stage.Camera.targetTexture = null;

			if (stage.Texture != null)
			{
				stage.Texture.Release();
				UnityEngine.Object.DestroyImmediate(stage.Texture);
				stage.Texture = null;
			}

			if (stage.PipelineOverridden)
			{
				QualitySettings.renderPipeline = stage.SavedQualityPipeline;
				GraphicsSettings.defaultRenderPipeline = stage.SavedGraphicsPipeline;
			}

			if (stage.Scene.IsValid())
				EditorSceneManager.CloseScene(stage.Scene, true);
		}

		// Screen rect in reference pixels with the origin at the top-left corner.
		public static UnityEngine.Rect ScreenRect(UiStage stage, RectTransform rt)
		{
			var corners = new Vector3[4];
			rt.GetWorldCorners(corners);

			Camera cam = stage.Canvas != null && stage.Canvas.renderMode == RenderMode.ScreenSpaceOverlay
				? null
				: stage.Camera;

			Vector2 bottomLeft = RectTransformUtility.WorldToScreenPoint(cam, corners[0]);
			Vector2 topRight = RectTransformUtility.WorldToScreenPoint(cam, corners[2]);

			float x = bottomLeft.x;
			float w = topRight.x - bottomLeft.x;
			float h = topRight.y - bottomLeft.y;
			float yTop = stage.Height - topRight.y;

			return new UnityEngine.Rect(x, yTop, w, h);
		}

		private static void DisableForeignBehaviours(GameObject root)
		{
			foreach (var behaviour in root.GetComponentsInChildren<MonoBehaviour>(true))
			{
				if (behaviour == null)
					continue;

				string ns = behaviour.GetType().Namespace;
				bool unityOwned = ns != null && (ns.StartsWith("UnityEngine") || ns.StartsWith("TMPro"));
				if (!unityOwned)
					behaviour.enabled = false;
			}
		}

		private static void SetLayerRecursive(GameObject go, int layer)
		{
			go.layer = layer;
			foreach (Transform child in go.transform)
				SetLayerRecursive(child.gameObject, layer);
		}
	}
}
