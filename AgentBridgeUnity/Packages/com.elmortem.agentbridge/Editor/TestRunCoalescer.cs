using System;
using System.Collections.Generic;
using UnityEditor;
using UnityEngine;

namespace AgentBridge
{
	public static class TestRunCoalescer
	{
		public static void TryAttachPending(List<PendingTaskInfo> pending)
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
			string currentSources = null;

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

				if (currentSources == null)
				{
					currentSources = TestFingerprint.Sources();
				}

				if (startSources != currentSources)
				{
					return;
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
					StartedAtUtc = DateTime.UtcNow.ToString("o")
				};

				record.Logs.Add("attached to running test task " + sourceId);
				TaskJournal.Write(record);
				pending.RemoveAt(i);
			}
		}
	}
}
