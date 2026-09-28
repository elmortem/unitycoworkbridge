using System;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace AgentBridge
{
	public static class AgentSceneManager
	{
		public static Scene OpenScene(string scenePath, OpenSceneMode mode = OpenSceneMode.Single)
		{
			SceneSafetyGuard.EnsureSafeForSceneChange();
			return EditorSceneManager.OpenScene(scenePath, mode);
		}

		public static Scene NewScene(NewSceneSetup setup = NewSceneSetup.DefaultGameObjects, NewSceneMode mode = NewSceneMode.Single)
		{
			SceneSafetyGuard.EnsureSafeForSceneChange();
			return EditorSceneManager.NewScene(setup, mode);
		}

		public static bool CloseScene(Scene scene, bool removeScene = true)
		{
			SceneSafetyGuard.EnsureSafeForSceneChange();
			return !scene.IsValid() || !scene.isLoaded || EditorSceneManager.CloseScene(scene, removeScene);
		}

		public static void RestoreSceneManagerSetup(SceneSetup[] setup)
		{
			SceneSafetyGuard.EnsureSafeForSceneChange();
			EditorSceneManager.RestoreSceneManagerSetup(setup);
		}

		public static void LoadScene(string sceneName, LoadSceneMode mode = LoadSceneMode.Single)
		{
			SceneSafetyGuard.EnsureSafeForSceneChange();
			SceneManager.LoadScene(sceneName, mode);
		}

		public static AsyncOperation LoadSceneAsync(string sceneName, LoadSceneMode mode = LoadSceneMode.Single)
		{
			SceneSafetyGuard.EnsureSafeForSceneChange();
			return SceneManager.LoadSceneAsync(sceneName, mode);
		}

		public static AsyncOperation UnloadSceneAsync(string sceneName)
		{
			SceneSafetyGuard.EnsureSafeForSceneChange();
			return SceneManager.UnloadSceneAsync(sceneName);
		}

		// Leaving a dirty prefab stage is what raises Unity's own save prompt, so the stage
		// switch goes through the same preflight as a scene change: saved under policy Save,
		// refused with an exception under policy Block. Opening the prefab that is already
		// open keeps the current stage and its unsaved state.
		public static PrefabStage OpenPrefab(string prefabPath)
		{
			if (string.IsNullOrEmpty(prefabPath))
			{
				throw new ArgumentException("prefab path is empty");
			}

			string normalized = prefabPath.Replace('\\', '/');
			if (!normalized.EndsWith(".prefab", StringComparison.OrdinalIgnoreCase))
			{
				throw new ArgumentException("not a prefab asset path: " + prefabPath);
			}

			if (AssetDatabase.LoadAssetAtPath<GameObject>(normalized) == null)
			{
				throw new ArgumentException("prefab not found: " + prefabPath);
			}

			PrefabStage current = PrefabStageUtility.GetCurrentPrefabStage();
			if (current != null && string.Equals(current.assetPath, normalized, StringComparison.OrdinalIgnoreCase))
			{
				return current;
			}

			SceneSafetyGuard.EnsureSafeForSceneChange();
			PrefabStage stage = PrefabStageUtility.OpenPrefab(normalized);
			if (stage == null)
			{
				throw new InvalidOperationException("prefab stage did not open: " + prefabPath);
			}

			return stage;
		}

		public static PrefabStage GetOpenPrefab()
		{
			return PrefabStageUtility.GetCurrentPrefabStage();
		}

		// Returns to the main stage. False when no prefab stage was open.
		public static bool ClosePrefab()
		{
			if (PrefabStageUtility.GetCurrentPrefabStage() == null)
			{
				return false;
			}

			SceneSafetyGuard.EnsureSafeForSceneChange();
			StageUtility.GoToMainStage();
			return true;
		}
	}
}
