using System;
using System.Collections.Generic;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace AgentBridge.SceneShot
{
	// Builds a SceneView pose that frames a scene object, like pressing F on it.
	public static class SceneShotFramer
	{
		public static SceneShotPose Frame(string target, float margin, Vector3 rotationEuler, bool orthographic)
		{
			// While a prefab stage is open the Scene View renders only the stage, so an object
			// found in the main scenes would be framed in empty space.
			PrefabStage stage = PrefabStageUtility.GetCurrentPrefabStage();
			GameObject go = Resolve(target, stage != null ? stage.prefabContentsRoot : null, CandidateRoots(stage));
			if (go == null)
			{
				throw new Exception(stage != null
					? "frame target not found in prefab stage " + stage.assetPath + ": " + target
					: "frame target not found in loaded scenes: " + target);
			}

			Bounds bounds = ComputeBounds(go);
			SceneShotPose pose = new SceneShotPose();
			pose.Pivot = bounds.center;
			pose.Rotation = Quaternion.Euler(rotationEuler);
			pose.Size = Mathf.Max(bounds.extents.magnitude, 0.01f) * margin;
			pose.Orthographic = orthographic;
			return pose;
		}

		// The prefab root comes first: a UI prefab sits under a generated environment canvas,
		// and a Root/Child path is written from the prefab root, not from that canvas. In the
		// stage the root is named after the prefab file, whatever it was called when saved.
		private static List<GameObject> CandidateRoots(PrefabStage stage)
		{
			List<GameObject> roots = new List<GameObject>();
			if (stage != null)
			{
				if (stage.prefabContentsRoot != null)
				{
					roots.Add(stage.prefabContentsRoot);
				}

				foreach (GameObject root in stage.scene.GetRootGameObjects())
				{
					if (!roots.Contains(root))
					{
						roots.Add(root);
					}
				}

				return roots;
			}

			for (int i = 0; i < SceneManager.sceneCount; i++)
			{
				Scene scene = SceneManager.GetSceneAt(i);
				if (!scene.isLoaded)
				{
					continue;
				}

				roots.AddRange(scene.GetRootGameObjects());
			}

			return roots;
		}

		private static GameObject Resolve(string target, GameObject prefabRoot, List<GameObject> roots)
		{
			int separator = target.IndexOf('/');
			string rootName = separator < 0 ? target : target.Substring(0, separator);
			string rest = separator < 0 ? null : target.Substring(separator + 1);

			// Inside a prefab a path is most naturally written from the prefab root down,
			// without the root's own name.
			if (prefabRoot != null && rest != null)
			{
				Transform relative = prefabRoot.transform.Find(target);
				if (relative != null)
				{
					return relative.gameObject;
				}
			}

			foreach (GameObject root in roots)
			{
				if (rest == null)
				{
					GameObject found = FindByName(root.transform, target);
					if (found != null)
					{
						return found;
					}
				}
				else if (root.name == rootName)
				{
					Transform child = root.transform.Find(rest);
					if (child != null)
					{
						return child.gameObject;
					}
				}
			}

			return null;
		}

		private static GameObject FindByName(Transform node, string name)
		{
			if (node.name == name)
			{
				return node.gameObject;
			}

			for (int i = 0; i < node.childCount; i++)
			{
				GameObject found = FindByName(node.GetChild(i), name);
				if (found != null)
				{
					return found;
				}
			}

			return null;
		}

		private static Bounds ComputeBounds(GameObject go)
		{
			Renderer[] renderers = go.GetComponentsInChildren<Renderer>();
			if (renderers.Length > 0)
			{
				Bounds bounds = renderers[0].bounds;
				for (int i = 1; i < renderers.Length; i++)
				{
					bounds.Encapsulate(renderers[i].bounds);
				}

				return bounds;
			}

			// uGUI draws through CanvasRenderer, which is not a Renderer: the rect corners are
			// the only extent such an object has.
			RectTransform[] rects = go.GetComponentsInChildren<RectTransform>();
			if (rects.Length > 0)
			{
				Vector3[] corners = new Vector3[4];
				rects[0].GetWorldCorners(corners);
				Bounds bounds = new Bounds(corners[0], Vector3.zero);
				foreach (RectTransform rect in rects)
				{
					rect.GetWorldCorners(corners);
					for (int i = 0; i < corners.Length; i++)
					{
						bounds.Encapsulate(corners[i]);
					}
				}

				return bounds;
			}

			return new Bounds(go.transform.position, Vector3.one);
		}
	}
}
