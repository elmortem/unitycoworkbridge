using System;
using System.IO;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace AgentBridge
{
	public static class PlayModeSceneRecovery
	{
		private static bool _started;
		private static bool _recoveryScheduled;

		public static bool IsPending
		{
			get { return File.Exists(BridgePaths.PlayModeSceneStateFile); }
		}

		public static void Start()
		{
			EditorApplication.playModeStateChanged -= OnPlayModeStateChanged;
			EditorApplication.playModeStateChanged += OnPlayModeStateChanged;
			EditorApplication.update -= OnRecoveryUpdate;
			EditorApplication.update += OnRecoveryUpdate;
			_started = true;

			if (IsPending && !EditorApplication.isPlayingOrWillChangePlaymode)
			{
				ScheduleRecovery();
			}
		}

		public static void Stop()
		{
			EditorApplication.playModeStateChanged -= OnPlayModeStateChanged;
			EditorApplication.update -= OnRecoveryUpdate;
			EditorApplication.delayCall -= CompleteRecovery;
			_started = false;
			_recoveryScheduled = false;
		}

		public static bool Begin(string taskId, out string error)
		{
			error = null;
			if (!_started)
			{
				Start();
			}

			if (!SceneSafetyGuard.TryPrepareForTask(out error))
			{
				return false;
			}

			SceneSetup[] setup = EditorSceneManager.GetSceneManagerSetup();
			var state = new PlayModeSceneState
			{
				TaskId = taskId,
				OriginalSetup = ToState(setup)
			};

			Write(state);
			return true;
		}

		public static void Tick()
		{
			if (!IsPending)
			{
				return;
			}

			PlayModeSceneState state = Read();
			if (state == null || !state.HasResult)
			{
				return;
			}

			if (EditorApplication.isPlaying)
			{
				try
				{
					SceneSafetyGuard.ClearOpenSceneDirtiness();
					EditorApplication.ExitPlaymode();
				}
				catch (Exception ex)
				{
					RecordRecoveryError("Failed to leave PlayMode after tests: " + ex.GetBaseException().Message);
				}

				return;
			}

			if (!EditorApplication.isPlayingOrWillChangePlaymode)
			{
				ScheduleRecovery();
			}
		}

		public static void CaptureBootstrapScene()
		{
			PlayModeSceneState state = Read();
			if (state == null)
			{
				return;
			}

			string path = SceneManager.GetActiveScene().path;
			if (!string.IsNullOrEmpty(path) && SceneSafetyGuard.IsTestScenePath(path))
			{
				state.BootstrapScenePath = path;
				Write(state);
			}
		}

		public static void RecordResult(TestRunResult result)
		{
			PlayModeSceneState state = Read();
			if (state == null)
			{
				return;
			}

			// Test Framework may ask Unity to leave PlayMode immediately after this
			// callback. Clear the bootstrap scene before that transition begins; doing it
			// only from ExitingPlayMode is too late on versions that show the save prompt
			// before dispatching the state-change event.
			try
			{
				SceneSafetyGuard.ClearOpenSceneDirtiness();
			}
			catch (Exception ex)
			{
				state.RecoveryError = AppendError(state.RecoveryError,
					"Failed to clear PlayMode scene dirtiness after the test run: " + ex.GetBaseException().Message);
			}

			state.Result = result;
			state.HasResult = true;
			Write(state);
			ScheduleRecovery();
			Tick();

			if (!EditorApplication.isPlayingOrWillChangePlaymode)
			{
				ScheduleRecovery();
			}
		}

		public static void Cancel()
		{
			PlayModeSceneState state = Read();
			SceneDirtyWatcher.Disarm(state != null ? state.TaskId : "");
			DeleteStateFile();
		}

		public static bool IsBootstrapScenePath(string path)
		{
			PlayModeSceneState state = Read();
			return state != null
				&& !string.IsNullOrEmpty(state.BootstrapScenePath)
				&& string.Equals(state.BootstrapScenePath.Replace('\\', '/'), path.Replace('\\', '/'), StringComparison.OrdinalIgnoreCase);
		}

		private static void OnPlayModeStateChanged(PlayModeStateChange state)
		{
			if (!IsPending)
			{
				return;
			}

			if (state == PlayModeStateChange.ExitingPlayMode)
			{
				try
				{
					SceneSafetyGuard.ClearOpenSceneDirtiness();
				}
				catch (Exception ex)
				{
					RecordRecoveryError("Failed to clear PlayMode scene dirtiness: " + ex.GetBaseException().Message);
				}
			}
			else if (state == PlayModeStateChange.EnteredEditMode)
			{
				ScheduleRecovery();
			}
		}

		private static void OnRecoveryUpdate()
		{
			if (!IsPending)
			{
				return;
			}

			PlayModeSceneState state = Read();
			if (state != null && state.HasResult && !EditorApplication.isPlayingOrWillChangePlaymode)
			{
				// A dedicated subscription survives coordinator state loss and does not rely
				// on delayCall ordering inside Unity Test Framework's job runner.
				CompleteRecovery();
				return;
			}

			Tick();
		}

		private static void ScheduleRecovery()
		{
			if (_recoveryScheduled)
			{
				return;
			}

			_recoveryScheduled = true;
			EditorApplication.delayCall += CompleteRecovery;
		}

		private static void CompleteRecovery()
		{
			_recoveryScheduled = false;
			if (!IsPending)
			{
				return;
			}

			if (EditorApplication.isPlayingOrWillChangePlaymode)
			{
				// RunFinished can arrive while Unity is still completing the transition.
				// Keep the recovery alive without depending on a later state callback or on
				// TaskCoordinator retaining its update subscription across Test Framework.
				ScheduleRecovery();
				return;
			}

			PlayModeSceneState state = Read();
			if (state == null)
			{
				RecoverCorruptState();
				return;
			}

			string recoveryError = state.RecoveryError;
			try
			{
				SceneSafetyGuard.ClearOpenSceneDirtiness();
				SceneSetup[] setup = FromState(state.OriginalSetup);
				if (setup.Length > 0)
				{
					EditorSceneManager.RestoreSceneManagerSetup(setup);
				}
				else
				{
					EditorSceneManager.NewScene(NewSceneSetup.DefaultGameObjects, NewSceneMode.Single);
				}
			}
			catch (Exception ex)
			{
				recoveryError = "Failed to restore the pre-test scene setup: " + ex.GetBaseException().Message;
				try
				{
					SceneSafetyGuard.ClearOpenSceneDirtiness();
					EditorSceneManager.NewScene(NewSceneSetup.DefaultGameObjects, NewSceneMode.Single);
				}
				catch (Exception fallbackException)
				{
					recoveryError += "; fallback failed: " + fallbackException.GetBaseException().Message;
				}
			}

			try
			{
				DeleteTemporaryTestScenes(state.BootstrapScenePath);
			}
			catch (Exception ex)
			{
				recoveryError = AppendError(recoveryError, ex.GetBaseException().Message);
			}

			// Restoring the setup can leave the editor dirty again; normalize before the task
			// is finalized so the next task never inherits a scene that opens a save dialog.
			string tailError;
			if (!SceneSafetyGuard.TryPrepareForTask(out tailError) && !string.IsNullOrEmpty(tailError))
			{
				recoveryError = AppendError(recoveryError, tailError);
			}

			try
			{
				AgentTestRunner.FinalizeRecoveredPlayModeRun(state.TaskId, state.HasResult ? state.Result : null, recoveryError);
				DeleteStateFile();
			}
			catch (Exception ex)
			{
				Debug.LogException(ex);
				try
				{
					RecordRecoveryError("Failed to finalize PlayMode recovery: " + ex.GetBaseException().Message);
				}
				catch
				{
				}
				ScheduleRecovery();
			}
		}

		private static void RecoverCorruptState()
		{
			string taskId = SessionState.GetString(AgentTestRunner.CoordinatorTestTaskKey, "");
			string recoveryError = "PlayMode scene recovery state is missing or invalid.";

			try
			{
				SceneSafetyGuard.ClearOpenSceneDirtiness();
				EditorSceneManager.NewScene(NewSceneSetup.DefaultGameObjects, NewSceneMode.Single);
				DeleteTemporaryTestScenes(null);
			}
			catch (Exception ex)
			{
				recoveryError = AppendError(recoveryError, ex.GetBaseException().Message);
			}

			if (!string.IsNullOrEmpty(taskId))
			{
				AgentTestRunner.FinalizeRecoveredPlayModeRun(taskId, null, recoveryError);
			}

			DeleteStateFile();
		}

		private static void RecordRecoveryError(string error)
		{
			PlayModeSceneState state = Read();
			if (state == null)
			{
				return;
			}

			state.RecoveryError = AppendError(state.RecoveryError, error);
			Write(state);
		}

		private static void DeleteTemporaryTestScenes(string bootstrapScenePath)
		{
			if (!string.IsNullOrEmpty(bootstrapScenePath))
			{
				SceneSafetyGuard.DeleteTestSceneAsset(bootstrapScenePath);
			}

			SceneSafetyGuard.DeleteAllTestSceneAssets();
		}

		private static SceneSetupState[] ToState(SceneSetup[] setup)
		{
			var result = new SceneSetupState[setup.Length];
			for (int i = 0; i < setup.Length; i++)
			{
				result[i] = new SceneSetupState
				{
					Path = setup[i].path,
					IsLoaded = setup[i].isLoaded,
					IsActive = setup[i].isActive,
					IsSubScene = setup[i].isSubScene
				};
			}

			return result;
		}

		private static SceneSetup[] FromState(SceneSetupState[] setup)
		{
			if (setup == null)
			{
				return new SceneSetup[0];
			}

			var result = new SceneSetup[setup.Length];
			for (int i = 0; i < setup.Length; i++)
			{
				result[i] = new SceneSetup
				{
					path = setup[i].Path,
					isLoaded = setup[i].IsLoaded,
					isActive = setup[i].IsActive,
					isSubScene = setup[i].IsSubScene
				};
			}

			return result;
		}

		private static PlayModeSceneState Read()
		{
			try
			{
				if (!File.Exists(BridgePaths.PlayModeSceneStateFile))
				{
					return null;
				}

				return JsonUtility.FromJson<PlayModeSceneState>(File.ReadAllText(BridgePaths.PlayModeSceneStateFile));
			}
			catch
			{
				return null;
			}
		}

		private static void Write(PlayModeSceneState state)
		{
			string path = BridgePaths.PlayModeSceneStateFile;
			string temporaryPath = path + ".new";
			File.WriteAllText(temporaryPath, JsonUtility.ToJson(state, true));

			if (!File.Exists(path))
			{
				File.Move(temporaryPath, path);
				return;
			}

			try
			{
				File.Replace(temporaryPath, path, null);
			}
			catch
			{
				File.Copy(temporaryPath, path, true);
				File.Delete(temporaryPath);
			}
		}

		private static void DeleteStateFile()
		{
			if (File.Exists(BridgePaths.PlayModeSceneStateFile))
			{
				File.Delete(BridgePaths.PlayModeSceneStateFile);
			}
		}

		private static string AppendError(string current, string next)
		{
			return string.IsNullOrEmpty(current) ? next : current + "; " + next;
		}
	}
}
