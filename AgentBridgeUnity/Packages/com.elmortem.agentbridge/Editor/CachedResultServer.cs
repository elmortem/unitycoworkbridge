using System;
using System.Collections.Generic;

namespace AgentBridge
{
	public static class CachedResultServer
	{
		public static void TryServePending(List<PendingTaskInfo> pending)
		{
			// Both kinds key on the same cheap source hash, and it is the expensive part of the
			// first check, so it is computed once per scan and only if a cacheable task waits.
			string sourceFingerprint = null;
			long nowMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

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
					string inputDigest = null;
					// The content digest is only ever computed once a cheap candidate exists, and
					// it is computed fresh: a memo keyed on sizes and times would hand out a hit
					// for a file that was edited back to its old size.
					string capturedFingerprint = sourceFingerprint;
					TestCacheQuery.Hit hit;
					if (!TestCacheQuery.TryServe(
						request,
						capturedFingerprint,
						delegate
						{
							if (inputDigest == null)
							{
								inputDigest = CurrentInputDigest(request);
							}

							return inputDigest;
						},
						nowMs,
						out hit))
					{
						continue;
					}

					// A served result consumes its step exactly once, just like a real run.
					string reserveError;
					if (inputDigest != CurrentInputDigest(request) || sourceFingerprint != TestFingerprint.Sources()) continue;
					if (!TryReserveCache(request, task.Id, out reserveError))
					{
						continue;
					}

					TaskRecord record = BuildServedRecord(task, hit.Status, hit.SourceTaskId, request);
					record.Tests = hit.Result;
					record.Artifacts.AddRange(hit.Artifacts);
					record.Evidence = new EvidenceRecord
					{
						Validity = EvidenceRecord.Valid,
						InputDigest = inputDigest,
						EndInputDigest = inputDigest,
						Reason = "served from cache entry " + hit.EntryId,
						ArtifactsPresent = true
					};
					TaskJournal.Write(record);
					TelemetryLog.TaskFinished(record);
					CoordinationGate.Release(request, task.Id, true, "cache_hit");
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

					string reserveError;
					if (sourceFingerprint != TestFingerprint.Sources()) continue;
					if (!TryReserveCache(request, task.Id, out reserveError))
					{
						continue;
					}

					TaskRecord record = BuildServedRecord(task, entry.Status, entry.SourceTaskId, request);
					record.Diagnostics = entry.Diagnostics;
					record.ForeignErrors = entry.Diagnostics.Count > 0;

					// The compile cache is keyed on the legacy fingerprint, which is a reuse key
					// and not an input digest. Saying so is more useful than claiming evidence.
					record.Evidence = EvidenceRecord.UnknownBecause(
						"served from the compile reuse cache; no evidence-v1 input digest was taken");
					TaskJournal.Write(record);
					TelemetryLog.TaskFinished(record);
					CoordinationGate.Release(request, task.Id, true, "cache_hit");
				}

				pending.RemoveAt(i);
			}
		}

		private static bool TryReserveCache(TaskRequest request, string taskId, out string reason)
		{
			reason = "";
			// Uncoordinated readers do not acquire editor ownership. Explicit coordinated
			// steps still need validation and accounting, including cache hits.
			return string.IsNullOrEmpty(request.CoordinationWindowToken)
				|| CoordinationGate.TryReserve(request, taskId, out reason);
		}

		public static string CurrentInputDigest(TaskRequest request)
		{
			string mode = request != null && request.TestMode == "PlayMode" ? "PlayMode" : "EditMode";
			string filter = request == null ? "" : FilterOf(request);
			// The same roots, exclusions and ignore rule a real run uses. A lookup that defined the
			// digest even slightly differently would simply never hit.
			ValidationInputSnapshot snapshot = ValidationInputSnapshot.Capture(
				ValidationEvidence.CollectRoots(),
				ValidationEvidence.CollectExcludedRoots(),
				ValidationEvidence.ContextOf(mode, filter),
				ValidationEvidence.BuildIgnore(PlayModeSceneRecovery.BootstrapScenePath()));
			return snapshot.Complete ? snapshot.Digest : "";
		}

		public static string FilterOf(TaskRequest request)
		{
			return string.Join(",", request.AssemblyNames ?? new string[0])
				+ "|" + string.Join(",", request.TestNames ?? new string[0])
				+ "|" + string.Join(",", request.CategoryNames ?? new string[0]);
		}

		private static TaskRecord BuildServedRecord(
			PendingTaskInfo task,
			string status,
			string sourceTaskId,
			TaskRequest request)
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
				CoordinationWindowToken = request != null ? request.CoordinationWindowToken : null,
				CoordinationStepId = request != null ? request.CoordinationStepId : null,
				StartedAtUtc = now,
				FinishedAtUtc = now
			};

			record.Logs.Add("served from cache; source task " + sourceTaskId);
			return record;
		}
	}
}
