using System;
using System.Collections.Generic;

namespace AgentBridge
{
	public static class CachedResultServer
	{
		public static void TryServePending(List<PendingTaskInfo> pending)
		{
			// Both kinds key on the same source hash, and it is the expensive part of the check,
			// so it is computed once per scan and only if a cacheable task is actually waiting.
			string sourceFingerprint = null;

			for (int i = pending.Count - 1; i >= 0; i--)
			{
				PendingTaskInfo task = pending[i];
				if (task.Kind != "tests" && task.Kind != "compile")
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

				if (sourceFingerprint == null)
				{
					sourceFingerprint = TestFingerprint.Sources();
				}

				if (task.Kind == "tests")
				{
					TestRunResult result;
					string sourceTaskId;
					string status;
					if (!TestCacheQuery.TryServe(request, sourceFingerprint, out result, out sourceTaskId, out status))
					{
						continue;
					}

					TaskRecord record = BuildServedRecord(task, status, sourceTaskId);
					record.Tests = result;
					TaskJournal.Write(record);
					TelemetryLog.TaskFinished(record);
				}
				else
				{
					CompileCacheEntry entry;
					if (!CompileCacheStore.TryRead(out entry))
					{
						continue;
					}

					if (entry.Fingerprint != sourceFingerprint)
					{
						continue;
					}

					TaskRecord record = BuildServedRecord(task, entry.Status, entry.SourceTaskId);
					record.Diagnostics = entry.Diagnostics;
					record.ForeignErrors = entry.Diagnostics.Count > 0;
					TaskJournal.Write(record);
					TelemetryLog.TaskFinished(record);
				}

				pending.RemoveAt(i);
			}
		}

		private static TaskRecord BuildServedRecord(PendingTaskInfo task, string status, string sourceTaskId)
		{
			string now = DateTime.UtcNow.ToString("o");
			var record = new TaskRecord
			{
				Id = task.Id,
				Kind = task.Kind,
				Status = status,
				Hash = TaskFileHash.HashOf(task.TaskFilePath, null),
				Cached = true,
				SourceTaskId = sourceTaskId,
				SessionId = BridgeStatusWriter.Current.SessionId,
				AgentSessionId = task.EffectiveSessionId,
				StartedAtUtc = now,
				FinishedAtUtc = now
			};

			record.Logs.Add("served from cache; source task " + sourceTaskId);
			return record;
		}
	}
}
