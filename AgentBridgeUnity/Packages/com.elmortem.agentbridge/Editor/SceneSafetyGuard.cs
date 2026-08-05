using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace AgentBridge
{
	public static class SceneSafetyGuard
	{
		private static readonly MethodInfo ClearSceneDirtinessMethod = typeof(EditorSceneManager).GetMethod(
			"ClearSceneDirtiness",
			BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static,
			null,
			new[] { typeof(Scene) },
			null);

		public static bool TryPrepareForTask(out string error)
		{
			try
			{
				return PrepareForTask(out error);
			}
			catch (Exception ex)
			{
				error = "Scene safety preflight failed: " + ex.GetBaseException().Message;
				return false;
			}
		}

		private static bool PrepareForTask(out string error)
		{
			error = null;

			var scenes = new List<Scene>();
			var transientScenes = new List<Scene>();
			var testScenePaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

			for (int i = 0; i < SceneManager.sceneCount; i++)
			{
				Scene scene = SceneManager.GetSceneAt(i);
				if (scene.IsValid())
				{
					scenes.Add(scene);
				}
			}

			bool discardUntitledScenes = AgentBridgeSettingsStore.GetDiscardDirtyUntitledScenes();
			foreach (Scene scene in scenes)
			{
				bool testScene = IsTestScenePath(scene.path);
				bool dirtyUntitledScene = scene.isDirty && string.IsNullOrEmpty(scene.path);

				if (dirtyUntitledScene && !discardUntitledScenes)
				{
					error = "A dirty untitled scene is open. Save or close it, or enable Discard dirty untitled scenes in Agent Bridge Setup.";
					return false;
				}

				if (testScene || dirtyUntitledScene)
				{
					transientScenes.Add(scene);
					if (testScene && !string.IsNullOrEmpty(scene.path))
					{
						testScenePaths.Add(scene.path);
					}
				}
			}

			foreach (Scene scene in scenes)
			{
				if (!scene.isDirty || transientScenes.Contains(scene))
				{
					continue;
				}

				if (!EditorSceneManager.SaveScene(scene))
				{
					error = "Failed to save dirty scene before an agent task: " + scene.path;
					return false;
				}

				Debug.Log("[AgentBridge] Saved dirty scene before task: " + scene.path);
			}

			if (transientScenes.Count == 0)
			{
				DeleteAllTestSceneAssets();
				return true;
			}

			foreach (Scene scene in transientScenes)
			{
				ClearSceneDirtiness(scene);
			}

			if (transientScenes.Count == scenes.Count)
			{
				EditorSceneManager.NewScene(NewSceneSetup.DefaultGameObjects, NewSceneMode.Single);
			}
			else
			{
				foreach (Scene scene in transientScenes)
				{
					if (scene.IsValid() && scene.isLoaded && !EditorSceneManager.CloseScene(scene, true))
					{
						error = "Failed to discard transient scene: " + DisplayName(scene);
						return false;
					}
				}
			}

			foreach (string path in testScenePaths)
			{
				DeleteTestSceneAsset(path);
			}
			DeleteAllTestSceneAssets();

			Debug.Log("[AgentBridge] Discarded " + transientScenes.Count + " transient scene(s) before task.");
			return true;
		}

		public static void EnsureSafeForSceneChange()
		{
			string error;
			if (!TryPrepareForTask(out error))
			{
				throw new InvalidOperationException(error);
			}
		}

		public static bool IsTestScenePath(string path)
		{
			if (string.IsNullOrEmpty(path))
			{
				return false;
			}

			string normalized = path.Replace('\\', '/');
			if (PlayModeSceneRecovery.IsBootstrapScenePath(normalized))
			{
				return true;
			}

			return normalized.StartsWith("Assets/InitTestScene", StringComparison.Ordinal)
				&& normalized.EndsWith(".unity", StringComparison.OrdinalIgnoreCase)
				&& normalized.IndexOf('/', "Assets/".Length) < 0;
		}

		public static void ClearOpenSceneDirtiness()
		{
			for (int i = 0; i < SceneManager.sceneCount; i++)
			{
				Scene scene = SceneManager.GetSceneAt(i);
				if (scene.IsValid() && scene.isDirty)
				{
					ClearSceneDirtiness(scene);
				}
			}
		}

		public static void DeleteTestSceneAsset(string path)
		{
			if (!IsTestScenePath(path))
			{
				return;
			}

			string fullPath = Path.Combine(BridgePaths.ProjectRoot, path.Replace('/', Path.DirectorySeparatorChar));
			string metaPath = fullPath + ".meta";
			if (!File.Exists(fullPath) && !File.Exists(metaPath))
			{
				// AssetDatabase can retain a GUID/path mapping until the next refresh after
				// DeleteAsset. Physical absence makes repeated recovery idempotent.
				return;
			}

			if (!AssetDatabase.DeleteAsset(path))
			{
				if (!File.Exists(fullPath) && !File.Exists(metaPath))
				{
					return;
				}

				throw new InvalidOperationException("Failed to delete temporary PlayMode test scene: " + path);
			}
		}

		public static void DeleteAllTestSceneAssets()
		{
			// Scan all scene assets and filter by path. Name-qualified FindAssets queries can
			// miss freshly-created/deleted Test Framework bootstrap scenes while Unity's
			// GUID cache is being refreshed during the play-mode transition.
			foreach (string guid in AssetDatabase.FindAssets("t:Scene", new[] { "Assets" }))
			{
				string path = AssetDatabase.GUIDToAssetPath(guid);
				if (IsTestScenePath(path))
				{
					DeleteTestSceneAsset(path);
				}
			}
		}

		private static void ClearSceneDirtiness(Scene scene)
		{
			if (!scene.IsValid() || !scene.isDirty)
			{
				return;
			}

			if (ClearSceneDirtinessMethod == null)
			{
				throw new MissingMethodException("Unity Editor does not expose EditorSceneManager.ClearSceneDirtiness.");
			}

			ClearSceneDirtinessMethod.Invoke(null, new object[] { scene });
		}

		private static string DisplayName(Scene scene)
		{
			return string.IsNullOrEmpty(scene.path) ? scene.name + " (untitled)" : scene.path;
		}
	}
}
