using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using UnityEditor;
using UnityEngine;

namespace AgentBridge
{
	public static class TestRunCoalescer
	{
		private static Task _work;
		public static void TryAttachPending(List<PendingTaskInfo> pending)
		{
			if (_work == null || _work.IsCompleted) _work = AttachSafely(new List<PendingTaskInfo>(pending));
		}
		private static async Task AttachSafely(List<PendingTaskInfo> pending)
		{
			try { await AttachAsync(pending); }
			catch (Exception error) { TelemetryLog.Write("attach_skip", "", "", new[] { TelemetryField.Text("What", error.GetBaseException().Message) }); }
		}
		private static async Task AttachAsync(List<PendingTaskInfo> pending)
		{
			string sourceId = SessionState.GetString(AgentTestRunner.CoordinatorTestTaskKey, "");
			if (string.IsNullOrEmpty(sourceId))
			{
				return;
			}

			string startSources = SessionState.GetString(AgentTestRunner.CoordinatorTestSourceKey, "");
			if (string.IsNullOrEmpty(startSources))
			{
				return;
			}

			string filterJson = SessionState.GetString(AgentTestRunner.CoordinatorTestFilterKey, "");
			if (string.IsNullOrEmpty(filterJson))
			{
				return;
			}

			TestRunFilter filter = JsonUtility.FromJson<TestRunFilter>(filterJson);
			if (filter == null)
			{
				return;
			}

			// Hashing every source file is far too expensive for a tick that runs once a second,
			// so it happens at most once per scan, and only once a task has cleared every cheap
			// check and is otherwise ready to attach.
			using var monitor = new ValidationInputMonitor(ValidationEvidence.CollectRoots(), ValidationEvidence.CollectExcludedRoots(),
				ValidationEvidence.BuildIgnore(PlayModeSceneRecovery.BootstrapScenePath()));
			string currentSources = null;
			string projectRoot = BridgePaths.ProjectRoot;

			for (int i = pending.Count - 1; i >= 0; i--)
			{
				PendingTaskInfo task = pending[i];
				if (task.Kind != "tests" || task.Id == sourceId)
				{
					continue;
				}

				TaskRecord existing;
				if (TaskJournal.TryRead(task.Id, out existing))
				{
					continue;
				}

				TaskRequest request;
				if (!TaskRequestReader.TryRead(task.TaskFilePath, out request) || request.Fresh)
				{
					continue;
				}

				string mode = request.TestMode == "PlayMode" ? "PlayMode" : "EditMode";
				if (mode != filter.TestMode)
				{
					continue;
				}

				if (!TestFilterCoverage.CoversFilterOnly(filter, request))
				{
					continue;
				}

				string requestHash = TaskFileHash.HashOf(task.TaskFilePath, null);
				if (currentSources == null)
				{
					currentSources = await Task.Run(() => CompileFingerprint.Capture(projectRoot));
				}

				if (startSources != currentSources || !monitor.Observed || monitor.EventCount != 0)
				{
					return;
				}

				if (sourceId != SessionState.GetString(AgentTestRunner.CoordinatorTestTaskKey, "")
					|| TestRunLifecycle.IsStopping(sourceId) || TaskJournal.TryRead(task.Id, out existing)
					|| requestHash != TaskFileHash.HashOf(task.TaskFilePath, null)) continue;
				// Joining a run consumes the step exactly like starting one: the plan authorises a
				// result, not a process.
				string reserveError;
				if (!CoordinationGate.TryReserve(request, task.Id, out reserveError))
				{
					continue;
				}

				var record = new TaskRecord
				{
					Id = task.Id,
					Kind = "tests",
					Status = "attached",
					AttachedToTaskId = sourceId,
					Hash = TaskFileHash.HashOf(task.TaskFilePath, null),
					SessionId = BridgeStatusWriter.Current.SessionId,
					AgentSessionId = task.EffectiveSessionId,
					CoordinationWindowToken = request.CoordinationWindowToken,
					CoordinationStepId = request.CoordinationStepId,
					StartedAtUtc = DateTime.UtcNow.ToString("o")
				};

				record.Logs.Add("attached to running test task " + sourceId);
				TaskJournal.Write(record);
				pending.RemoveAt(i);
			}
		}
	}
}
