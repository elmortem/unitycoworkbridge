using UnityEditor.SceneManagement;
using UnityEngine.SceneManagement;

namespace AgentBridge
{
	public static class SceneDirtyScanner
	{
		public static SceneDirtyReport Scan()
		{
			var report = new SceneDirtyReport();
			bool discardUntitledScenes = AgentBridgeSettingsStore.GetDiscardDirtyUntitledScenes();

			// EditorSceneManager enumerates open-but-unloaded scenes too; the runtime
			// SceneManager hides them and would leave an unsavable dirty scene behind.
			for (int i = 0; i < EditorSceneManager.sceneCount; i++)
			{
				Scene scene = EditorSceneManager.GetSceneAt(i);
				if (!scene.IsValid())
				{
					continue;
				}

				report.OpenSceneCount++;

				if (SceneSafetyGuard.IsTestScenePath(scene.path))
				{
					report.TransientScenes.Add(scene);
					if (!string.IsNullOrEmpty(scene.path))
					{
						report.TestScenePaths.Add(scene.path);
					}

					continue;
				}

				if (!scene.isDirty)
				{
					continue;
				}

				if (!scene.isLoaded)
				{
					report.DirtyUnloadedScenes.Add(scene);
					continue;
				}

				if (string.IsNullOrEmpty(scene.path))
				{
					report.DirtyUntitledScenes.Add(scene);
					if (discardUntitledScenes)
					{
						report.TransientScenes.Add(scene);
					}

					continue;
				}

				report.DirtySavedScenes.Add(scene);
			}

			PrefabStage stage = PrefabStageUtility.GetCurrentPrefabStage();
			if (stage != null && stage.scene.IsValid() && stage.scene.isDirty)
			{
				report.PrefabStageDirty = true;
				report.PrefabStageAssetPath = stage.assetPath;
			}

			return report;
		}
	}
}
