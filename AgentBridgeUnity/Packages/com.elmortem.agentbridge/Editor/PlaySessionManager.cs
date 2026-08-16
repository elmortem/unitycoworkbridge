using System;
using System.Collections.Generic;
using UnityEditor;
using UnityEngine;

namespace AgentBridge
{
	// Drives a sanctioned agent play session. Entering and leaving play mode both cross a
	// domain reload, so nothing is kept in memory: Reconcile() reads the persistent state
	// on every editor tick, moves it one phase forward when the editor allows it and
	// finalizes the journal record whose task opened or closed the session.
	public static class PlaySessionManager
	{
		private const double EnterTimeoutSeconds = 15d;
		private const double ExitRequestIntervalSeconds = 1d;

		private static double _lastExitRequestTime;

		public static bool IsSessionActive
		{
			get
			{
				PlaySessionState state = PlaySessionStore.Read();
				return state != null && state.Phase == PlaySessionPhases.Active;
			}
		}

		public static void Reconcile()
		{
			PlaySessionState state = PlaySessionStore.Read();
			if (state == null)
			{
				return;
			}

			switch (state.Phase)
			{
				case PlaySessionPhases.Entering:
					ReconcileEntering(state);
					break;
				case PlaySessionPhases.Active:
					ReconcileActive(state);
					break;
				case PlaySessionPhases.Exiting:
					ReconcileExiting(state);
					break;
			}
		}

		private static void ReconcileEntering(PlaySessionState state)
		{
			if (EditorApplication.isPlaying)
			{
				state.Phase = PlaySessionPhases.Active;
				PlaySessionStore.Write(state);
				FinalizeRecord(state.TaskId, "success", "playing_until:" + state.DeadlineUtc, null);
				WriteStatus(state);
				return;
			}

			if (EditorApplication.isPlayingOrWillChangePlaymode)
			{
				return;
			}

			DateTime startedAtUtc;
			if (!TryParseUtc(state.StartedAtUtc, out startedAtUtc))
			{
				startedAtUtc = DateTime.UtcNow;
			}

			if ((DateTime.UtcNow - startedAtUtc).TotalSeconds <= EnterTimeoutSeconds)
			{
				return;
			}

			// Unity silently refuses to enter play mode while the project does not compile,
			// so the request has to time out instead of leaving the task running forever.
			FinalizeRecord(state.TaskId, "runtime_error", null, new List<string> { "failed to enter play mode" });
			PlaySessionStore.Delete();
			WriteStatus(null);
		}

		private static void ReconcileActive(PlaySessionState state)
		{
			if (!EditorApplication.isPlaying && !EditorApplication.isPlayingOrWillChangePlaymode)
			{
				if (string.IsNullOrEmpty(state.PendingStopTaskId))
				{
					Debug.LogWarning("[AgentBridge] play session ended externally");
				}

				FinalizeRecord(state.PendingStopTaskId, "success", "stopped:external", null);
				PlaySessionStore.Delete();
				WriteStatus(null);
				return;
			}

			DateTime deadlineUtc;
			if (!TryParseUtc(state.DeadlineUtc, out deadlineUtc))
			{
				return;
			}

			if (DateTime.UtcNow < deadlineUtc)
			{
				return;
			}

			state.Phase = PlaySessionPhases.Exiting;
			state.StopReason = "deadline";
			PlaySessionStore.Write(state);
			RequestExit();
		}

		private static void ReconcileExiting(PlaySessionState state)
		{
			if (EditorApplication.isPlaying)
			{
				// The static throttle is reset by a domain reload, and a repeated exit request
				// while the editor is already leaving play mode is harmless.
				if (EditorApplication.timeSinceStartup - _lastExitRequestTime < ExitRequestIntervalSeconds)
				{
					return;
				}

				RequestExit();
				return;
			}

			if (EditorApplication.isPlayingOrWillChangePlaymode)
			{
				return;
			}

			List<string> logs = null;
			try
			{
				string sceneError;
				if (!SceneSafetyGuard.TryPrepareForTask(out sceneError))
				{
					logs = new List<string> { sceneError };
				}
			}
			catch (Exception ex)
			{
				logs = new List<string> { ex.GetBaseException().Message };
			}

			string reason = string.IsNullOrEmpty(state.StopReason) ? "external" : state.StopReason;
			FinalizeRecord(state.PendingStopTaskId, "success", "stopped:" + reason, logs);
			PlaySessionStore.Delete();
			WriteStatus(null);
			Debug.Log("[AgentBridge] play session stopped: " + reason);
		}

		public static bool BeginPlay(TaskRequest request, TaskRecord record, out string error)
		{
			if (string.IsNullOrEmpty(request.AgentSessionId))
			{
				error = "play requires --session";
				return false;
			}

			if (PlayModeSceneRecovery.IsPending)
			{
				error = "tests are running";
				return false;
			}

			if (PlaySessionStore.Exists)
			{
				error = "play session already active";
				return false;
			}

			if (EditorApplication.isPlayingOrWillChangePlaymode)
			{
				error = "editor is already playing";
				return false;
			}

			int seconds = request.PlaySeconds;
			if (seconds <= 0)
			{
				seconds = AgentBridgeSettingsStore.GetPlaySessionDefaultSeconds();
			}

			int maxSeconds = AgentBridgeSettingsStore.GetPlaySessionMaxSeconds();
			if (seconds > maxSeconds)
			{
				seconds = maxSeconds;
			}

			DateTime nowUtc = DateTime.UtcNow;
			var state = new PlaySessionState
			{
				TaskId = request.Id,
				OwnerAgentSessionId = record != null ? record.AgentSessionId : request.AgentSessionId,
				Phase = PlaySessionPhases.Entering,
				StartedAtUtc = nowUtc.ToString("o"),
				DeadlineUtc = nowUtc.AddSeconds(seconds).ToString("o"),
				PendingStopTaskId = "",
				StopReason = ""
			};

			PlaySessionStore.Write(state);
			WriteStatus(state);
			EditorApplication.EnterPlaymode();
			error = null;
			return true;
		}

		public static void BeginStop(string taskId, string reason)
		{
			PlaySessionState state = PlaySessionStore.Read();
			if (state == null)
			{
				DateTime nowUtc = DateTime.UtcNow;
				state = new PlaySessionState
				{
					TaskId = "",
					OwnerAgentSessionId = "",
					StartedAtUtc = nowUtc.ToString("o"),
					DeadlineUtc = nowUtc.ToString("o")
				};
			}

			state.Phase = PlaySessionPhases.Exiting;
			state.PendingStopTaskId = taskId ?? "";
			state.StopReason = reason;
			PlaySessionStore.Write(state);

			if (EditorApplication.isPlaying)
			{
				RequestExit();
				return;
			}

			// Nothing is playing, so no play mode state change will ever arrive to drive the
			// exit: the session has to be closed out here and now.
			Reconcile();
		}

		private static void RequestExit()
		{
			_lastExitRequestTime = EditorApplication.timeSinceStartup;

			try
			{
				SceneSafetyGuard.ClearOpenSceneDirtiness();
			}
			catch (Exception ex)
			{
				Debug.LogWarning("[AgentBridge] Failed to clear scene dirtiness before leaving play mode: "
					+ ex.GetBaseException().Message);
			}

			EditorApplication.ExitPlaymode();
		}

		private static void FinalizeRecord(string taskId, string status, string returnValue, List<string> extraLogs)
		{
			if (string.IsNullOrEmpty(taskId))
			{
				return;
			}

			TaskRecord record;
			if (!TaskJournal.TryRead(taskId, out record))
			{
				return;
			}

			if (IsTerminal(record.Status))
			{
				return;
			}

			if (record.Logs == null)
			{
				record.Logs = new List<string>();
			}

			if (extraLogs != null)
			{
				record.Logs.AddRange(extraLogs);
			}

			DateTime nowUtc = DateTime.UtcNow;
			record.Status = status;
			record.ReturnValue = returnValue;
			record.FinishedAtUtc = nowUtc.ToString("o");
			TaskJournal.Write(record);
			AgentSessionScheduler.OnTaskFinished(record.AgentSessionId, nowUtc);
		}

		private static void WriteStatus(PlaySessionState state)
		{
			BridgeStatusWriter.Current.IsPlaying = EditorApplication.isPlayingOrWillChangePlaymode;
			BridgeStatusWriter.Current.PlaySessionAgentId = state != null && !string.IsNullOrEmpty(state.OwnerAgentSessionId)
				? state.OwnerAgentSessionId
				: null;
			BridgeStatusWriter.Current.PlaySessionDeadlineUtc = state != null ? state.DeadlineUtc : null;
			BridgeStatusWriter.Write();
		}

		private static bool TryParseUtc(string value, out DateTime result)
		{
			return DateTime.TryParse(value, System.Globalization.CultureInfo.InvariantCulture,
				System.Globalization.DateTimeStyles.RoundtripKind, out result);
		}

		private static bool IsTerminal(string status)
		{
			switch (status)
			{
				case "success":
				case "test_failure":
				case "compiler_error":
				case "runtime_error":
				case "timeout":
				case "canceled":
				case "interrupted_by_domain_reload":
				case "rejected":
					return true;
				default:
					return false;
			}
		}
	}
}
