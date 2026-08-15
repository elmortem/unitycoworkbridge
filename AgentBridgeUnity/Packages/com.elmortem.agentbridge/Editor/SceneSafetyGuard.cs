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

		public static bool TryVerifyClean(out string error)
		{
			error = null;

			try
			{
				SceneDirtyReport report = SceneDirtyScanner.Scan();
				if (report.IsClean)
				{
					return true;
				}

				error = "Scene state became dirty before the operation started: " + FirstDirtyTarget(report);
				return false;
			}
			catch (Exception ex)
			{
				error = "Scene safety verification failed: " + ex.GetBaseException().Message;
				return false;
			}
		}

		private static bool PrepareForTask(out string error)
		{
			error = null;

			SceneDirtyReport report = SceneDirtyScanner.Scan();

			if (report.DirtyUnloadedScenes.Count > 0)
			{
				error = "An open scene is unloaded and has unsaved changes: " + report.DirtyUnloadedScenes[0].path
					+ ". Load and save it, or close it.";
				return false;
			}

			if (report.DirtyUntitledScenes.Count > 0 && !AgentBridgeSettingsStore.GetDiscardDirtyUntitledScenes())
			{
				error = "A dirty untitled scene is open. Save or close it, or enable Discard dirty untitled scenes in Agent Bridge Setup.";
				return false;
			}

			bool saveDirtyScenes = AgentBridgeSettingsStore.GetSaveDirtyScenes();

			if (report.PrefabStageDirty && !saveDirtyScenes)
			{
				error = "A prefab stage has unsaved changes: " + report.PrefabStageAssetPath
					+ ". Save or close it, or enable Save dirty scenes in Agent Bridge Setup.";
				return false;
			}

			if (report.DirtySavedScenes.Count > 0 && !saveDirtyScenes)
			{
				error = "A dirty scene is open: " + report.DirtySavedScenes[0].path
					+ ". Save it, or enable Save dirty scenes in Agent Bridge Setup.";
				return false;
			}

			foreach (Scene scene in report.DirtySavedScenes)
			{
				if (!EditorSceneManager.SaveScene(scene))
				{
					error = "Failed to save dirty scene before an agent task: " + scene.path;
					return false;
				}

				Debug.Log("[AgentBridge] Saved dirty scene before task: " + scene.path);
			}

			if (report.PrefabStageDirty && !SavePrefabStage(out error))
			{
				return false;
			}

			return DiscardTransientScenes(report, out error);
		}

		public static void NormalizeArmed(out List<string> actions, out List<string> blocked)
		{
			actions = new List<string>();
			blocked = new List<string>();

			try
			{
				SceneDirtyReport report = SceneDirtyScanner.Scan();

				// The owning run has already started and Test Framework 1.1.33 offers no way
				// to cancel it, so a scene with a path is saved even under policy Block:
				// the only alternative left at this point is the modal dialog.
				foreach (Scene scene in report.DirtySavedScenes)
				{
					if (EditorSceneManager.SaveScene(scene))
					{
						actions.Add("saved " + scene.path);
					}
					else
					{
						blocked.Add("failed to save " + scene.path);
					}
				}

				// Inside an armed window the only goal is a non-dirty editor. Closing scenes or
				// deleting test scene assets here destroys the state of the run that is already
				// executing: Test Framework creates and dirties its own bootstrap scene mid-run.
				foreach (Scene scene in report.DirtyUntitledScenes)
				{
					if (report.TransientScenes.Contains(scene))
					{
						ClearSceneDirtiness(scene);
						actions.Add("cleared untitled scene " + scene.name);
					}
					else
					{
						blocked.Add("untitled scene " + scene.name + " left dirty by policy Block");
					}
				}

				foreach (Scene scene in report.TransientScenes)
				{
					if (!scene.IsValid() || !scene.isDirty)
					{
						continue;
					}

					ClearSceneDirtiness(scene);
					actions.Add("cleared test scene " + DisplayName(scene));
				}

				foreach (Scene scene in report.DirtyUnloadedScenes)
				{
					blocked.Add("unloaded scene " + scene.path + " left dirty");
				}

				if (report.PrefabStageDirty)
				{
					if (AgentBridgeSettingsStore.GetSaveDirtyScenes())
					{
						string prefabError;
						if (SavePrefabStage(out prefabError))
						{
							actions.Add("saved prefab stage " + report.PrefabStageAssetPath);
						}
						else
						{
							blocked.Add(prefabError);
						}
					}
					else
					{
						blocked.Add("prefab stage " + report.PrefabStageAssetPath + " left dirty by policy Block");
					}
				}
			}
			catch (Exception ex)
			{
				blocked.Add("normalize failed: " + ex.GetBaseException().Message);
			}
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
			for (int i = 0; i < EditorSceneManager.sceneCount; i++)
			{
				Scene scene = EditorSceneManager.GetSceneAt(i);
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

		private static bool DiscardTransientScenes(SceneDirtyReport report, out string error)
		{
			error = null;

			if (report.TransientScenes.Count == 0)
			{
				DeleteAllTestSceneAssets();
				return true;
			}

			foreach (Scene scene in report.TransientScenes)
			{
				ClearSceneDirtiness(scene);
			}

			if (report.TransientScenes.Count == report.OpenSceneCount)
			{
				EditorSceneManager.NewScene(NewSceneSetup.DefaultGameObjects, NewSceneMode.Single);
			}
			else
			{
				foreach (Scene scene in report.TransientScenes)
				{
					if (scene.IsValid() && scene.isLoaded && !EditorSceneManager.CloseScene(scene, true))
					{
						error = "Failed to discard transient scene: " + DisplayName(scene);
						return false;
					}
				}
			}

			foreach (string path in report.TestScenePaths)
			{
				DeleteTestSceneAsset(path);
			}
			DeleteAllTestSceneAssets();

			Debug.Log("[AgentBridge] Discarded " + report.TransientScenes.Count + " transient scene(s) before task.");
			return true;
		}

		private static bool SavePrefabStage(out string error)
		{
			error = null;

			PrefabStage stage = PrefabStageUtility.GetCurrentPrefabStage();
			if (stage == null)
			{
				return true;
			}

			if (string.IsNullOrEmpty(stage.assetPath))
			{
				error = "Prefab stage has no asset path and cannot be saved silently.";
				return false;
			}

			bool saved;
			PrefabUtility.SaveAsPrefabAsset(stage.prefabContentsRoot, stage.assetPath, out saved);
			if (!saved)
			{
				error = "Failed to save prefab stage: " + stage.assetPath;
				return false;
			}

			ClearSceneDirtiness(stage.scene);
			return true;
		}

		private static string FirstDirtyTarget(SceneDirtyReport report)
		{
			if (report.DirtyUnloadedScenes.Count > 0)
			{
				return report.DirtyUnloadedScenes[0].path;
			}

			if (report.DirtySavedScenes.Count > 0)
			{
				return report.DirtySavedScenes[0].path;
			}

			if (report.DirtyUntitledScenes.Count > 0)
			{
				return "<untitled>";
			}

			if (report.PrefabStageDirty)
			{
				return report.PrefabStageAssetPath;
			}

			if (report.TransientScenes.Count > 0)
			{
				return DisplayName(report.TransientScenes[0]);
			}

			return "<unknown>";
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
