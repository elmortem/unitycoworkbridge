using System;
using System.Collections.Generic;
using UnityEditor;
using UnityEngine;

namespace AgentBridge
{
	// An agent task that reaches play mode by itself hangs the bridge forever: the
	// coordinator stops taking tasks while the editor plays and the task that started it is
	// already gone. This guard marks play mode entered by an agent and no play session, exits
	// it on the next tick and tells the culprit's journal record what happened.
	public static class UnsanctionedPlayGuard
	{
		public const string UnsanctionedPlayTaskIdKey = "AgentBridge.UnsanctionedPlayTaskId";
		public const string LastTaskFinishUtcKey = "AgentBridge.LastTaskFinishUtc";
		public const string LastTaskFinishIdKey = "AgentBridge.LastTaskFinishId";

		private static bool _warned;

		public static void Start()
		{
			EditorApplication.playModeStateChanged -= OnPlayModeStateChanged;
			EditorApplication.playModeStateChanged += OnPlayModeStateChanged;
		}

		public static void Stop()
		{
			EditorApplication.playModeStateChanged -= OnPlayModeStateChanged;
		}

		public static void RecordTaskFinish(string taskId)
		{
			SessionState.SetString(LastTaskFinishUtcKey, DateTime.UtcNow.ToString("o"));
			SessionState.SetString(LastTaskFinishIdKey, taskId ?? "");
		}

		public static void ClearMark()
		{
			SessionState.EraseString(UnsanctionedPlayTaskIdKey);
			_warned = false;
		}

		private static void OnPlayModeStateChanged(PlayModeStateChange change)
		{
			// ExitingEditMode is the last callback before the domain reload, so the running task
			// that asked for play mode is still known here; by EnteredPlayMode it is gone.
			if (change != PlayModeStateChange.ExitingEditMode)
			{
				return;
			}

			if (PlayModeSceneRecovery.IsPending || PlaySessionStore.Exists)
			{
				return;
			}

			string lastFinishId = SessionState.GetString(LastTaskFinishIdKey, "");
			bool agentCaused = TaskCoordinator.HasActiveTask || IsInsideGraceWindow();
			if (!agentCaused)
			{
				return;
			}

			string culprit = TaskCoordinator.ActiveTaskId;
			if (string.IsNullOrEmpty(culprit))
			{
				culprit = lastFinishId;
			}

			SessionState.SetString(UnsanctionedPlayTaskIdKey, string.IsNullOrEmpty(culprit) ? "unknown" : culprit);
		}

		private static bool IsInsideGraceWindow()
		{
			string lastFinishUtc = SessionState.GetString(LastTaskFinishUtcKey, "");
			DateTime finishedAtUtc;
			if (!DateTime.TryParse(lastFinishUtc, System.Globalization.CultureInfo.InvariantCulture,
				System.Globalization.DateTimeStyles.RoundtripKind, out finishedAtUtc))
			{
				return false;
			}

			return (DateTime.UtcNow - finishedAtUtc).TotalSeconds <= AgentBridgeSettingsStore.GetAgentPlayGraceSeconds();
		}

		public static void Tick()
		{
			string culprit = SessionState.GetString(UnsanctionedPlayTaskIdKey, "");
			if (string.IsNullOrEmpty(culprit))
			{
				return;
			}

			if (EditorApplication.isPlaying)
			{
				if (PlaySessionStore.Exists)
				{
					return;
				}

				try
				{
					SceneSafetyGuard.ClearOpenSceneDirtiness();
				}
				catch (Exception ex)
				{
					Debug.LogWarning("[AgentBridge] Failed to clear scene dirtiness before the automatic play mode exit: "
						+ ex.GetBaseException().Message);
				}

				EditorApplication.ExitPlaymode();

				if (!_warned)
				{
					_warned = true;
					Debug.LogWarning("[AgentBridge] unsanctioned play mode entered by agent task " + culprit
						+ "; exiting automatically");
				}

				return;
			}

			if (EditorApplication.isPlayingOrWillChangePlaymode)
			{
				return;
			}

			try
			{
				TaskRecord record;
				if (TaskJournal.TryRead(culprit, out record))
				{
					if (record.Logs == null)
					{
						record.Logs = new List<string>();
					}

					record.Logs.Add("this task entered play mode; the bridge exited it automatically");
					TaskJournal.Write(record);
				}
			}
			catch
			{
				// The note is a courtesy: a missing or unwritable record must not keep the
				// marker alive and re-run this branch on every tick.
			}

			ClearMark();
		}
	}
}
