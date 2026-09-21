using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using UnityEditor;

namespace AgentBridge
{
	public static class CachedResultServer
	{
		private static async Task ServeAsync(List<PendingTaskInfo> pending)
		{
			// Both kinds key on the same cheap source hash, and it is the expensive part of the
			// first check, so it is computed once per scan and only if a cacheable task waits.
			string projectRoot = BridgePaths.ProjectRoot;
			string[] roots = ValidationEvidence.CollectRoots();
			string[] excluded = ValidationEvidence.CollectExcludedRoots();
			var ignore = ValidationEvidence.BuildIgnore(PlayModeSceneRecovery.BootstrapScenePath());
			using var monitor = await ValidationInputMonitor.OpenAsync(roots, excluded, ignore);
			string sourceFingerprint = await CompileInputContext.StartCapture(projectRoot);
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
				if (!TaskRequestReader.TryRead(task.TaskFilePath, out request) || (request.Fresh && task.Kind != "compile"))
				{
					continue;
				}

				string requestHash = TaskFileHash.HashOf(task.TaskFilePath, null);
				if (task.Kind == "compile" && request.Fresh && string.IsNullOrWhiteSpace(request.Note)) continue;

				if (task.Kind == "tests")
				{
					string mode = request.TestMode == "PlayMode" ? "PlayMode" : "EditMode";
					string context = ValidationEvidence.ContextOf(mode, FilterOf(request));
					var job = new InputHashJob(roots, excluded, context, ignore);
					bool candidate = TestRunDumpStore.ReadIndex().Entries.Exists(e => e.TestMode == mode && e.Validity == EvidenceRecord.Valid && e.SourceFingerprint == sourceFingerprint);
					if (!candidate) continue;
					var snapshot = await job.Measure("cache_lookup", task.Id);
					string inputDigest = snapshot.Complete ? snapshot.Digest : "";
					// The content digest is only ever computed once a cheap candidate exists, and
					// it is computed fresh: a memo keyed on sizes and times would hand out a hit
					// for a file that was edited back to its old size.
					string capturedFingerprint = sourceFingerprint;
					TestCacheQuery.Hit hit;
					if (!TestCacheQuery.TryServe(
						request,
						capturedFingerprint,
						() => inputDigest,
						nowMs,
						out hit))
					{
						continue;
					}

					// A served result consumes its step exactly once, just like a real run.
					string reserveError;
					var verified = await job.Measure("cache_verify", task.Id);
					string verifiedSources = await CompileInputContext.StartCapture(projectRoot);
					// Await allowed cancellation, cache eviction and artifact removal to run.
					// Recheck the actual entry and editor context before consuming a step.
					if (context != ValidationEvidence.ContextOf(mode, FilterOf(request))
						|| !TestCacheQuery.TryServe(request, sourceFingerprint, () => inputDigest,
							DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(), out hit)) continue;
					if (!verified.Complete || inputDigest != verified.Digest || sourceFingerprint != verifiedSources
						|| !CanPublish(task, requestHash, monitor)) continue;
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

					if (!CompileCacheStore.CanReuse(entry, sourceFingerprint, request.Fresh, task.CreatedUtc))
					{
						continue;
					}

					string reserveError;
					if (sourceFingerprint != await CompileInputContext.StartCapture(projectRoot)
						|| !CanPublish(task, requestHash, monitor)) continue;
					if (!TryReserveCache(request, task.Id, out reserveError))
					{
						continue;
					}

					TaskRecord record = BuildServedRecord(task, entry.Status, entry.SourceTaskId, request);
					record.Diagnostics = entry.Diagnostics;
					record.ForeignErrors = entry.Diagnostics.Count > 0;
					record.Logs.Add(request.Fresh ? "compile_reuse: shared cycle completed while this fresh request waited" : "compile_reuse: current sources and compilation context match");

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

		private static Task _work;
		private static double _nextLookup;
		private static readonly HashSet<string> Checking = new HashSet<string>();
		public static bool IsChecking(string id) { return Checking.Contains(id); }

		public static void TryServePending(List<PendingTaskInfo> pending)
		{
			if (_work != null && !_work.IsCompleted) return;
			if (EditorApplication.timeSinceStartup < _nextLookup) return;
			var candidates = new List<PendingTaskInfo>();
			foreach (var task in pending)
			{
				TaskRecord record;
				TaskRequest request;
				if ((task.Kind == "compile" || task.Kind == "tests")
					&& !TaskJournal.TryRead(task.Id, out record)
					&& TaskRequestReader.TryRead(task.TaskFilePath, out request) && (!request.Fresh || task.Kind == "compile"))
				{
					candidates.Add(task);
					Checking.Add(task.Id);
				}
			}
			if (candidates.Count > 0) _work = RunLookup(candidates);
		}

		private static async Task RunLookup(List<PendingTaskInfo> pending)
		{
			try { await ServeAsync(pending); }
			catch (Exception error)
			{
				TelemetryLog.Write("cache_skip", "", "", new[] { TelemetryField.Text("What", error.GetBaseException().Message) });
			}
			finally
			{
				Checking.Clear();
				_nextLookup = EditorApplication.timeSinceStartup + 1;
			}
		}

		private static bool CanPublish(PendingTaskInfo task, string requestHash, ValidationInputMonitor monitor)
		{
			TaskRecord record;
			return AgentBridgeSettingsStore.IsEnabled() && !EditorApplication.isCompiling && !EditorApplication.isUpdating
				&& monitor.Observed && monitor.EventCount == 0
				&& !TaskJournal.TryRead(task.Id, out record)
				&& requestHash == TaskFileHash.HashOf(task.TaskFilePath, null);
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
