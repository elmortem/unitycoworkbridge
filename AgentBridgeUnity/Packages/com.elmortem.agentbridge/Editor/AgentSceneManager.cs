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
	}
}
