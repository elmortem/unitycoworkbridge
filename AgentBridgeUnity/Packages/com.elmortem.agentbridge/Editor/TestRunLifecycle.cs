using System;
using System.Collections.Generic;
using UnityEditor;
using UnityEngine;

namespace AgentBridge
{
	// Cancellation state survives the domain reloads caused by PlayMode tests.
	public static class TestRunLifecycle
	{
		private const string Key = "AgentBridge_TestRunLifecycle";
		[Serializable] private class State
		{
			public string Id, JobId, Outcome, Reason, CancellationDiagnostic;
			public long StopRequested, InactiveSince;
			public bool Submitted;
		}
		// A Unity job that ends without RunFinished leaves no result and no activity to wait for.
		public const long LostRunGraceMs = 25000;

		public static long TrackInactivity(bool idle, long inactiveSince, long now)
		{
			if (!idle)
			{
				return 0;
			}
			if (inactiveSince == 0)
			{
				return now;
			}
			return inactiveSince;
		}

		public static bool IsLost(long inactiveSince, long now)
		{
			return inactiveSince != 0 && now - inactiveSince >= LostRunGraceMs;
		}

		private static State Read() { string json = SessionState.GetString(Key, ""); return string.IsNullOrEmpty(json) ? null : JsonUtility.FromJson<State>(json); }
		private static void Save(State state) { SessionState.SetString(Key, JsonUtility.ToJson(state)); }
		public static string TaskId { get { State state = Read(); return state == null ? "" : state.Id; } }
		public static bool IsStopping(string id) { State state = Read(); return state != null && state.Id == id && !string.IsNullOrEmpty(state.Outcome); }
		public static void Begin(string id)
		{
			Save(new State { Id = id });
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
				state = new State { Id = legacyId, Submitted = true };
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
					// A lost or failed job still runs its error-mode cleanup; cancelling interrupts it.
					string diagnostic = state.Outcome == "canceled" ? TestRunnerCancellation.Request(state.JobId) : "";
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
			// Reached only while the run has no outcome: a submitted job that shows no activity
			// anywhere for the grace window has ended without delivering RunFinished.
			if (!terminal && hasRecord && state.Submitted)
			{
				bool idle = !EditorApplication.isPlayingOrWillChangePlaymode
					&& !EditorApplication.isCompiling
					&& !PlayModeSceneRecovery.IsPending
					&& !AgentTestRunner.HasPendingFinalization(state.Id)
					&& !TestRunnerCancellation.IsRunning();
				long inactiveSince = TrackInactivity(idle, state.InactiveSince, now);
				if (inactiveSince != state.InactiveSince)
				{
					state.InactiveSince = inactiveSince;
					Save(state);
				}
				if (IsLost(inactiveSince, now))
				{
					RequestStop(state.Id, "runtime_error", "Unity test job ended without RunFinished; no results for " + (LostRunGraceMs / 1000) + " s");
					return;
				}
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
