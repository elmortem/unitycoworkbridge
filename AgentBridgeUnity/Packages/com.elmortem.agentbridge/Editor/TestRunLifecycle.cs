using System;
using System.Collections.Generic;
using UnityEditor;
using UnityEngine;

namespace AgentBridge
{
	// The owner and deadline survive the domain reloads caused by PlayMode tests.
	public static class TestRunLifecycle
	{
		private const string Key = "AgentBridge_TestRunLifecycle";
		[Serializable] private class State
		{
			public string Id, JobId, Outcome, Reason, CancellationDiagnostic;
			public long Deadline, StopRequested;
			public bool Submitted;
		}
		private static State Read() { string json = SessionState.GetString(Key, ""); return string.IsNullOrEmpty(json) ? null : JsonUtility.FromJson<State>(json); }
		private static void Save(State state) { SessionState.SetString(Key, JsonUtility.ToJson(state)); }
		public static string TaskId { get { State state = Read(); return state == null ? "" : state.Id; } }
		public static bool IsStopping(string id) { State state = Read(); return state != null && state.Id == id && !string.IsNullOrEmpty(state.Outcome); }
		public static int LimitSeconds(int configured) { return configured <= 0 ? 300 : Math.Min(300, configured); }
		public static int TimeoutSeconds { get { return LimitSeconds(AgentBridgeSettingsStore.GetTaskTimeoutSeconds()); } }
		public static void Begin(string id)
		{
			long deadline = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() + TimeoutSeconds * 1000L;
			TaskRecord record;
			var snapshot = CoordinationEditorAdapter.Snapshot;
			if (snapshot != null && TaskJournal.TryRead(id, out record) && !string.IsNullOrEmpty(record.CoordinationWindowToken))
			{
				var window = snapshot.FindGrantByToken(record.CoordinationWindowToken);
				if (window != null) deadline = Math.Min(deadline, window.DeadlineMs);
			}
			Save(new State { Id = id, Deadline = deadline });
		}
		public static void Submitted(string jobId)
		{
			State state = Read(); if (state == null) return;
			state.JobId = jobId; state.Submitted = true; Save(state);
		}
		public static bool RequestStop(string id, string outcome, string reason)
		{
			State state = Read();
			if (state == null || state.Id != id) return false;
			if (string.IsNullOrEmpty(state.Outcome))
			{
				state.Outcome = outcome; state.Reason = reason; state.StopRequested = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(); Save(state);
				TaskRecord record;
				if (TaskJournal.TryRead(id, out record) && !TaskCoordinator.IsTerminal(record.Status))
				{
					record.Status = "canceling"; TaskJournal.Write(record);
				}
			}
			return true;
		}
		public static void Tick()
		{
			State state = Read();
			if (state == null)
			{
				string legacyId = SessionState.GetString(AgentTestRunner.CoordinatorTestTaskKey, "");
				if (string.IsNullOrEmpty(legacyId)) legacyId = PlayModeSceneRecovery.PendingTaskId;
				TaskRecord legacy;
				if (string.IsNullOrEmpty(legacyId) || !TaskJournal.TryRead(legacyId, out legacy)) return;
				DateTime started;
				if (!DateTime.TryParse(legacy.StartedAtUtc, out started)) started = DateTime.UtcNow.AddSeconds(-300);
				state = new State { Id = legacyId, Submitted = true,
					Deadline = new DateTimeOffset(started.ToUniversalTime()).ToUnixTimeMilliseconds() + TimeoutSeconds * 1000L };
				Save(state);
				if (TaskCoordinator.IsTerminal(legacy.Status))
				{
					RequestStop(legacyId, "canceled", "Stopping leftover execution of an already completed test task");
					state = Read();
				}
			}
			long now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
			TaskRecord record;
			bool hasRecord = TaskJournal.TryRead(state.Id, out record);
			bool terminal = hasRecord && TaskCoordinator.IsTerminal(record.Status);
			if (string.IsNullOrEmpty(state.Outcome) && now >= state.Deadline)
			{
				RequestStop(state.Id, "timeout", "Test run exceeded its deadline (maximum " + TimeoutSeconds + " seconds, or the coordinated window). Split long tests and suites into smaller runs.");
				state = Read();
			}
			if (!string.IsNullOrEmpty(state.Outcome))
			{
				// Ending PlayMode stops its coroutines. Calling the legacy runner's StopRun
				// from an editor update can leave its coroutine executing after cancellation.
				if (EditorApplication.isPlaying)
				{
					if (TestRunnerCancellation.PlayRunnerStarted())
						PlayModeSceneRecovery.CompleteAbandonedRun(state.Id, state.Reason);
					return;
				}
				if (EditorApplication.isPlayingOrWillChangePlaymode) return;
				if (state.Submitted)
				{
					string diagnostic = TestRunnerCancellation.Request(state.JobId);
					if (now - state.StopRequested >= 30000)
						diagnostic += " Waiting: " + TestRunnerCancellation.RunningReason();
					if (!string.IsNullOrEmpty(diagnostic) && diagnostic != state.CancellationDiagnostic)
					{
						state.CancellationDiagnostic = diagnostic; Save(state);
						if (hasRecord)
						{
							if (record.Logs == null) record.Logs = new List<string>();
							record.Logs.Add("Cancellation: " + diagnostic); TaskJournal.Write(record);
						}
					}
				}
				if (state.Submitted && TestRunnerCancellation.IsRunning()) return;
				if (PlayModeSceneRecovery.IsPending)
				{
					PlayModeSceneRecovery.CompleteAbandonedRun(state.Id, state.Reason);
					return;
				}
				if (EditorApplication.isPlayingOrWillChangePlaymode) return;
				AgentTestRunner.FinalizeCancellation(state.Id, state.Outcome, state.Reason);
				SessionState.EraseString(Key);
				return;
			}
			if (terminal && !PlayModeSceneRecovery.IsPending && !TestRunnerCancellation.IsRunning()) SessionState.EraseString(Key);
		}
		public static string BlockReason
		{
			get
			{
				State state = Read(); if (state == null) return "";
				if (string.IsNullOrEmpty(state.Outcome)) return "test_run:" + state.Id;
				return (DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() - state.StopRequested >= 30000 ? "cancel_blocked:" : "canceling:") + state.Id;
			}
		}
	}
}
