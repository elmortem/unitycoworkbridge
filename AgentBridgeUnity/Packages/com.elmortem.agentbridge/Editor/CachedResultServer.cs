using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using UnityEditor;

namespace AgentBridge
{
	public static class CachedResultServer
	{
		private const int ClaimReplyPollMs = 50;
		private const int ClaimReplyTimeoutMs = 5000;
		private static readonly CacheMissMemo Misses = new CacheMissMemo();

		private static async Task ServeAsync(List<PendingTaskInfo> pending)
		{
			// Both kinds key on the same cheap source hash, and it is the expensive part of the
			// first check, so it is computed once per scan and only if a cacheable task waits.
			//
			// The witnesses cost far more than that key: an observer window keeps the shared hub
			// warm and the stat manifest walks every input. So they are opened at most once per
			// scan, and only once a candidate entry has actually been found — a scan with nothing
			// to serve lets the observers go cold and takes no digest at all.
			string projectRoot = BridgePaths.ProjectRoot;
			string[] roots = ValidationEvidence.CollectRoots();
			string[] excluded = ValidationEvidence.CollectExcludedRoots();
			var ignore = ValidationEvidence.BuildIgnore(PlayModeSceneRecovery.BootstrapScenePath());
			CacheLookupWitness witness = null;
			try
			{
				string sourceFingerprint = await CompileInputContext.StartCapture(projectRoot);
				long nowMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

				// Remembered misses belong to the tasks that are still waiting. The whole queue is
				// in hand here, so this is where the rest are forgotten.
				var live = new HashSet<string>();
				foreach (PendingTaskInfo waiting in pending)
				{
					live.Add(waiting.Id);
				}

				Misses.Retain(live);

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

						// The cheap half of the key names the entries the digest would be compared
						// against. With none of them there is nothing to prove and nothing to serve.
						var ids = new List<string>();
						foreach (TestCacheEntryInfo entry in TestRunDumpStore.ReadIndex().Entries)
						{
							if (entry.TestMode == mode && entry.Validity == EvidenceRecord.Valid
								&& entry.SourceFingerprint == sourceFingerprint)
							{
								ids.Add(entry.Id);
							}
						}

						if (ids.Count == 0) continue;
						ids.Sort(StringComparer.Ordinal);
						string candidateKey = string.Join(",", ids);

						// A digest that just missed against these very sources and these very
						// candidates will miss again a second later. It is asked again when either
						// changes, and otherwise only once the backstop is due.
						if (Misses.ShouldSkip(task.Id, sourceFingerprint, candidateKey, nowMs)) continue;
						if (witness == null) witness = await OpenWitness(roots, excluded, ignore, task.Id);
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
							Misses.Record(task.Id, sourceFingerprint, candidateKey, nowMs);
							continue;
						}

						// A served result consumes its step exactly once, just like a real run.
						var verified = await job.Measure("cache_verify", task.Id);
						string verifiedSources = await CompileInputContext.StartCapture(projectRoot);
						// The witness closes here: every read the decision stands on — both digests
						// and the closing source fingerprint — lies between the two manifests.
						InputStatVerdict stat = await witness.VerifyAsync();
						// Await allowed cancellation, cache eviction and artifact removal to run.
						// Recheck the actual entry and editor context before consuming a step.
						if (context != ValidationEvidence.ContextOf(mode, FilterOf(request))
							|| !TestCacheQuery.TryServe(request, sourceFingerprint, () => inputDigest,
								DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(), out hit))
						{
							Skip(task.Id, "entry_gone_on_recheck");
							continue;
						}

						if (!verified.Complete || inputDigest != verified.Digest)
						{
							Skip(task.Id, "digest_moved");
							continue;
						}

						if (sourceFingerprint != verifiedSources)
						{
							Skip(task.Id, "sources_moved");
							continue;
						}

						if (!CanPublish(task, requestHash, witness.Monitor))
						{
							Skip(task.Id, "cannot_publish");
							continue;
						}

						// Equal digests only say the bytes match now. An input that was edited and put
						// back during the lookup means the answer describes a project that moved.
						if (!stat.Complete || stat.Changed != 0)
						{
							TelemetryLog.Write("cache_skip", "", task.Id, new[] {
								TelemetryField.Text("What", stat.Complete
									? "inputs moved during the cache lookup"
									: stat.Reason),
								TelemetryField.Text("Paths", string.Join(";", stat.Paths)) });
							continue;
						}

						string reserveError = await ReserveCacheAsync(request, task.Id);
						if (reserveError != null)
						{
							Skip(task.Id, reserveError);
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
						Misses.Forget(task.Id);
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

						// The reuse key is itself made of sizes, times and content of the sources, so
						// the second capture below already catches an edit that was put back. Only the
						// observer is needed here, and only now that there is an entry to serve.
						if (witness == null) witness = await OpenWitness(roots, excluded, ignore, task.Id);
						if (sourceFingerprint != await CompileInputContext.StartCapture(projectRoot))
						{
							Skip(task.Id, "sources_moved");
							continue;
						}

						if (!CanPublish(task, requestHash, witness.Monitor))
						{
							Skip(task.Id, "cannot_publish");
							continue;
						}

						string reserveError = await ReserveCacheAsync(request, task.Id);
						if (reserveError != null)
						{
							Skip(task.Id, reserveError);
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
			finally
			{
				if (witness != null)
				{
					witness.Dispose();
				}
			}
		}

		// One open per scan, and it is worth seeing in the log: this is the cost a lookup pays the
		// moment a candidate appears.
		private static async Task<CacheLookupWitness> OpenWitness(string[] roots, string[] excluded, Func<string, bool> ignore, string taskId)
		{
			var watch = System.Diagnostics.Stopwatch.StartNew();
			CacheLookupWitness witness = await CacheLookupWitness.OpenAsync(roots, excluded, ignore);
			TelemetryLog.Write("cache_witness", "", taskId, new[] {
				TelemetryField.Number("ElapsedMs", watch.ElapsedMilliseconds),
				TelemetryField.Number("Files", witness.Start.Entries.Count)
			});
			return witness;
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

		// The dispatcher answers a claim on its own schedule, so the first call only publishes it.
		// The task stays in Checking meanwhile and the coordinator leaves it alone; on timeout the
		// pending request is kept and the coordinator's own start collects the reply.
		private static async Task<string> ReserveCacheAsync(TaskRequest request, string taskId)
		{
			long deadline = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() + ClaimReplyTimeoutMs;
			while (true)
			{
				// TryReserve may refuse without calling the dispatcher at all, so a flag left by
				// someone else's call must not read as our own wait.
				QueueEditorAdapter.LastCallAwaitingReply = false;
				string reason;
				if (CoordinationGate.TryReserve(request, taskId, out reason))
				{
					return null;
				}

				if (!QueueEditorAdapter.LastCallAwaitingReply)
				{
					return "claim_refused: " + reason;
				}

				if (DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() >= deadline)
				{
					return "claim_reply_timeout";
				}

				await Task.Delay(ClaimReplyPollMs);
			}
		}

		private static void Skip(string taskId, string what)
		{
			TelemetryLog.Write("cache_skip", "", taskId, new[] { TelemetryField.Text("What", what) });
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
