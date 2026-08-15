using System;
using System.Collections.Generic;
using System.IO;
using UnityEditor;
using UnityEditor.SceneManagement;

namespace AgentBridge
{
	public static class SessionContextSwitcher
	{
		public static bool TrySaveContext(string effectiveSessionId, out string error)
		{
			error = null;

			// An anonymous session is a single task with no continuation, so it has no context
			// worth carrying across a rotation.
			if (string.IsNullOrEmpty(effectiveSessionId) || AgentSessionScheduler.IsAnonymous(effectiveSessionId))
			{
				return true;
			}

			// Saving the setup of a dirty editor would hand the next session a save dialog.
			if (!SceneSafetyGuard.TryPrepareForTask(out error))
			{
				return false;
			}

			string prefabStagePath = "";
			try
			{
				PrefabStage stage = PrefabStageUtility.GetCurrentPrefabStage();
				if (stage != null)
				{
					prefabStagePath = stage.assetPath ?? "";
					StageUtility.GoToMainStage();
				}
			}
			catch (Exception ex)
			{
				error = "context save failed: " + ex.GetBaseException().Message;
				return false;
			}

			SceneSetupState[] setup;
			try
			{
				setup = Filter(SceneSetupStateConverter.ToState(EditorSceneManager.GetSceneManagerSetup()));
			}
			catch (Exception ex)
			{
				error = "context save failed: " + ex.GetBaseException().Message;
				return false;
			}

			AgentSessionScheduler.SaveContextFor(effectiveSessionId, setup, prefabStagePath, DateTime.UtcNow);
			return true;
		}

		public static void RestoreContext(string effectiveSessionId, List<string> logs)
		{
			SessionContext context = AgentSessionScheduler.FindContext(effectiveSessionId);
			if (context == null)
			{
				return;
			}

			SceneSetupState[] existing = ExistingScenes(context.Setup);
			if (!HasLoadedScene(existing))
			{
				AddLog(logs, "context restore skipped: saved scenes are missing");
				return;
			}

			try
			{
				EditorSceneManager.RestoreSceneManagerSetup(SceneSetupStateConverter.FromState(existing));
			}
			catch (Exception ex)
			{
				AddLog(logs, "context restore failed: " + ex.GetBaseException().Message);
				return;
			}

			if (string.IsNullOrEmpty(context.PrefabStagePath) || !AssetExists(context.PrefabStagePath))
			{
				return;
			}

			try
			{
				if (PrefabStageUtility.OpenPrefab(context.PrefabStagePath) == null)
				{
					AddLog(logs, "prefab stage restore failed: " + context.PrefabStagePath);
				}
			}
			catch
			{
				AddLog(logs, "prefab stage restore failed: " + context.PrefabStagePath);
			}
		}

		private static SceneSetupState[] Filter(SceneSetupState[] setup)
		{
			var result = new List<SceneSetupState>();
			foreach (SceneSetupState state in setup)
			{
				if (state != null && !string.IsNullOrEmpty(state.Path))
				{
					result.Add(state);
				}
			}

			return result.ToArray();
		}

		private static SceneSetupState[] ExistingScenes(SceneSetupState[] setup)
		{
			var result = new List<SceneSetupState>();
			if (setup == null)
			{
				return result.ToArray();
			}

			foreach (SceneSetupState state in setup)
			{
				if (state != null && AssetExists(state.Path))
				{
					result.Add(state);
				}
			}

			return result.ToArray();
		}

		private static bool HasLoadedScene(SceneSetupState[] setup)
		{
			foreach (SceneSetupState state in setup)
			{
				if (state.IsLoaded)
				{
					return true;
				}
			}

			return false;
		}

		private static bool AssetExists(string assetPath)
		{
			if (string.IsNullOrEmpty(assetPath))
			{
				return false;
			}

			try
			{
				string fullPath = Path.Combine(
					BridgePaths.ProjectRoot,
					assetPath.Replace('/', Path.DirectorySeparatorChar).Replace('\\', Path.DirectorySeparatorChar));
				return File.Exists(fullPath);
			}
			catch
			{
				return false;
			}
		}

		private static void AddLog(List<string> logs, string message)
		{
			if (logs != null)
			{
				logs.Add(message);
			}
		}
	}
}
