using System;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace AgentBridge.SceneShot
{
	// Builds a SceneView pose that frames a scene object, like pressing F on it.
	public static class SceneShotFramer
	{
		public static SceneShotPose Frame(string target, float margin, Vector3 rotationEuler, bool orthographic)
		{
			GameObject go = Resolve(target);
			if (go == null)
			{
				throw new Exception("frame target not found in loaded scenes: " + target);
			}

			Bounds bounds = ComputeBounds(go);
			SceneShotPose pose = new SceneShotPose();
			pose.Pivot = bounds.center;
			pose.Rotation = Quaternion.Euler(rotationEuler);
			pose.Size = Mathf.Max(bounds.extents.magnitude, 0.01f) * margin;
			pose.Orthographic = orthographic;
			return pose;
		}

		private static GameObject Resolve(string target)
		{
			int separator = target.IndexOf('/');
			string rootName = separator < 0 ? target : target.Substring(0, separator);
			string rest = separator < 0 ? null : target.Substring(separator + 1);

			for (int i = 0; i < SceneManager.sceneCount; i++)
			{
				Scene scene = SceneManager.GetSceneAt(i);
				if (!scene.isLoaded)
				{
					continue;
				}

				foreach (GameObject root in scene.GetRootGameObjects())
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
			if (renderers.Length == 0)
			{
				return new Bounds(go.transform.position, Vector3.one);
			}

			Bounds bounds = renderers[0].bounds;
			for (int i = 1; i < renderers.Length; i++)
			{
				bounds.Encapsulate(renderers[i].bounds);
			}

			return bounds;
		}
	}
}
