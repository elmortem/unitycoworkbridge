using System.Collections.Generic;
using UnityEngine;
using UnityEditor;
using UnityEditor.TestTools.TestRunner.Api;

namespace AgentBridge
{
	[InitializeOnLoad]
	public static class AgentTestRunner
	{
		public const string CoordinatorTestTaskKey = "AgentBridge_CoordinatorTestTask";
		public const string CoordinatorTestModeKey = "AgentBridge_CoordinatorTestMode";
		public const string CoordinatorTestSourceKey = "AgentBridge_CoordinatorTestSource";
		public const string CoordinatorTestFilterKey = "AgentBridge_CoordinatorTestFilter";
		public const string CoordinatorTestCatalogKey = "AgentBridge_CoordinatorTestCatalog";
		private static TestRunnerApi _api;
		private const string FinalizationKey = "AgentBridge_TestFinalization";
		[System.Serializable]
		private class PendingFinalization
		{
			public string TaskId;
			public TestRunResult Run;
			public string RecoveryError;
		}
		private static PendingFinalization _finalization;

		static AgentTestRunner()
		{
			if (Application.isBatchMode) return;
			_api = ScriptableObject.CreateInstance<TestRunnerApi>();
			_api.RegisterCallbacks(new TestCallbacks());
			PlayModeSceneRecovery.Start();
			string finalization = SessionState.GetString(FinalizationKey, "");
			if (!string.IsNullOrEmpty(finalization))
			{
				_finalization = JsonUtility.FromJson<PendingFinalization>(finalization);
				EditorApplication.update += PollFinalization;
			}
		}

		public static bool TryRequestRunForCoordinator(string taskId, string testMode, string[] assemblyNames, string[] testNames, string[] categoryNames, out TestRunResult abortedResult, TestNameResolver.CatalogData catalog = null)
		{
			abortedResult = null;

			if (EditorApplication.isPlayingOrWillChangePlaymode)
			{
				abortedResult = new TestRunResult
				{
					aborted = true,
					message = "Editor is in play mode. Exit play mode and re-run the test task."
				};
				return false;
			}

			TestMode mode = ParseMode(testMode);

			// Test Framework 1.1.33 puts SaveModiedSceneTask first in both the EditMode and
			// the PlayMode task list, so the preflight has to cover both modes.
			string preflightError;
			if (!SceneSafetyGuard.TryPrepareForTask(out preflightError))
			{
				abortedResult = new TestRunResult
				{
					aborted = true,
					message = preflightError
				};
				return false;
			}

			if (mode == TestMode.PlayMode)
			{
				string recoveryError;
				if (!PlayModeSceneRecovery.Begin(taskId, out recoveryError))
				{
					abortedResult = new TestRunResult
					{
						aborted = true,
						message = recoveryError
					};
					return false;
				}
			}

			Filter filter = BuildFilter(mode, assemblyNames, testNames, categoryNames);

			SessionState.SetString(CoordinatorTestTaskKey, taskId);
			SessionState.SetString(CoordinatorTestCatalogKey, catalog == null ? "" : JsonUtility.ToJson(catalog));
			SessionState.SetString(CoordinatorTestModeKey, mode.ToString());
			SessionState.SetString(CoordinatorTestSourceKey, ValidationEvidence.PreparedSources);
			SessionState.SetString(CoordinatorTestFilterKey, JsonUtility.ToJson(new TestRunFilter
			{
				TestMode = mode.ToString(),
				AssemblyNames = assemblyNames ?? new string[0],
				TestNames = testNames ?? new string[0],
				CategoryNames = categoryNames ?? new string[0]
			}));

			// The job runner ticks asynchronously after Execute returns, so anything can dirty
			// a scene between the preflight and SaveModiedSceneTask. Verify once more, then
			// arm the watcher before Execute: its update subscription precedes the runner's.
			string verifyError;
			if (!SceneSafetyGuard.TryVerifyClean(out verifyError))
			{
				SessionState.EraseString(CoordinatorTestTaskKey);
				SessionState.EraseString(CoordinatorTestModeKey);
				SessionState.EraseString(CoordinatorTestSourceKey);
				SessionState.EraseString(CoordinatorTestFilterKey);
				SessionState.EraseString(CoordinatorTestCatalogKey);
				if (mode == TestMode.PlayMode)
				{
					PlayModeSceneRecovery.Cancel();
				}

				abortedResult = new TestRunResult
				{
					aborted = true,
					message = verifyError
				};
				return false;
			}

			SceneDirtyWatcher.Arm(taskId);

			try
			{
				TestRunnerApi api = ScriptableObject.CreateInstance<TestRunnerApi>();
				if (mode == TestMode.PlayMode)
				{
					FocusGuard.BeginPlayEntryGuard();
				}

				TestRunLifecycle.Submitted(api.Execute(new ExecutionSettings(filter)));
			}
			catch
			{
				SessionState.EraseString(CoordinatorTestTaskKey);
				SessionState.EraseString(CoordinatorTestModeKey);
				SessionState.EraseString(CoordinatorTestSourceKey);
				SessionState.EraseString(CoordinatorTestFilterKey);
				SessionState.EraseString(CoordinatorTestCatalogKey);
				SceneDirtyWatcher.Disarm(taskId);
				if (mode == TestMode.PlayMode)
				{
					PlayModeSceneRecovery.Cancel();
				}

				throw;
			}

			return true;
		}

		private static Filter BuildFilter(TestMode mode, string[] assemblyNames, string[] testNames, string[] categoryNames)
		{
			Filter filter = new Filter { testMode = mode };
			if (assemblyNames != null && assemblyNames.Length > 0)
			{
				filter.assemblyNames = assemblyNames;
			}
			if (testNames != null && testNames.Length > 0)
			{
				filter.testNames = testNames;
			}
			if (categoryNames != null && categoryNames.Length > 0)
			{
				filter.categoryNames = categoryNames;
			}

			return filter;
		}

		private static TestMode ParseMode(string testMode)
		{
			if (testMode == "PlayMode")
			{
				return TestMode.PlayMode;
			}

			return TestMode.EditMode;
		}

		private static void WritePendingDump(string taskId, ITestResultAdaptor result)
		{
			string filterJson = SessionState.GetString(CoordinatorTestFilterKey, "");
			if (string.IsNullOrEmpty(filterJson))
			{
				return;
			}

			TestRunFilter filter = JsonUtility.FromJson<TestRunFilter>(filterJson);
			if (filter == null)
			{
				return;
			}

			// Fingerprint is stamped at promotion, not here: PlayMode scene recovery still has to
			// delete its temporary scenes, and every one of those imports moves the value.
			var dump = new TestRunDump
			{
				SourceTaskId = taskId,
				Catalog = ReadCatalog(),
				Filter = filter,
				FinishedAtUtc = System.DateTime.UtcNow.ToString("o")
			};

			CollectEntries(result, null, dump.Entries);
			TestRunDumpStore.WritePending(dump);
		}

		private static TestNameResolver.CatalogData ReadCatalog()
		{
			string json = SessionState.GetString(CoordinatorTestCatalogKey, "");
			return string.IsNullOrEmpty(json) ? null : JsonUtility.FromJson<TestNameResolver.CatalogData>(json);
		}

		private static void CollectEntries(ITestResultAdaptor node, string assembly, List<TestCaseResult> entries)
		{
			if (node.Test != null && node.Test.FullName != null
				&& node.Test.FullName.EndsWith(".dll", System.StringComparison.OrdinalIgnoreCase))
			{
				assembly = System.IO.Path.GetFileNameWithoutExtension(node.Test.FullName);
			}

			if (node.HasChildren)
			{
				foreach (ITestResultAdaptor child in node.Children)
				{
					CollectEntries(child, assembly, entries);
				}

				return;
			}

			var entry = new TestCaseResult
			{
				FullName = node.FullName,
				Assembly = assembly ?? "",
				Status = node.TestStatus.ToString(),
				DurationSeconds = node.Duration,
				Message = node.Message,
				StackTrace = node.StackTrace
			};

			if (node.Test != null && node.Test.Categories != null)
			{
				entry.Categories.AddRange(node.Test.Categories);
			}

			entries.Add(entry);
		}

		private static void FinalizeCoordinatorRun(string taskId, TestRunResult run, string recoveryError)
		{
			if (_finalization != null && _finalization.TaskId == taskId) return;
			_finalization = new PendingFinalization { TaskId = taskId, Run = run, RecoveryError = recoveryError };
			SessionState.SetString(FinalizationKey, JsonUtility.ToJson(_finalization));
			EditorApplication.update -= PollFinalization;
			EditorApplication.update += PollFinalization;
		}

		private static void PollFinalization()
		{
			if (_finalization == null) return;
			string taskId = _finalization.TaskId;
			try { TryFinalizeCoordinatorRun(taskId, _finalization.Run, _finalization.RecoveryError); }
			catch (System.Exception error)
			{
				FinalizeCancellation(taskId, "runtime_error", "Finalization failed: " + error.GetBaseException().Message);
			}
			TaskRecord record;
			if (!TaskJournal.TryRead(taskId, out record) || TaskCoordinator.IsTerminal(record.Status) || TestRunLifecycle.IsStopping(taskId))
			{
				_finalization = null;
				SessionState.EraseString(FinalizationKey);
				EditorApplication.update -= PollFinalization;
			}
		}

		private static void TryFinalizeCoordinatorRun(string taskId, TestRunResult run, string recoveryError)
		{
			TaskRecord prior;
			if (TaskJournal.TryRead(taskId, out prior) && TaskCoordinator.IsTerminal(prior.Status)) return;
			if (TestRunLifecycle.IsStopping(taskId)) return;
			EvidenceRecord evidence;
			if (!ValidationEvidence.TryComplete(taskId, prior != null && ArtifactsExist(prior), out evidence))
			{
				return;
			}
			string requestedFilter = SessionState.GetString(CoordinatorTestFilterKey, "");
			string testMode = SessionState.GetString(CoordinatorTestModeKey, "");
			string startSources = SessionState.GetString(CoordinatorTestSourceKey, "");
			SessionState.EraseString(CoordinatorTestTaskKey);
			SessionState.EraseString(CoordinatorTestModeKey);
			SessionState.EraseString(CoordinatorTestSourceKey);
			SessionState.EraseString(CoordinatorTestFilterKey);
			SessionState.EraseString(CoordinatorTestCatalogKey);

			TaskRecord record;
			if (!TaskJournal.TryRead(taskId, out record))
			{
				TestRunDumpStore.DeletePending(testMode);
				ValidationEvidence.Abort();
				TestRunAttachments.Requeue(taskId);
				return;
			}

			if (record.Logs == null)
			{
				record.Logs = new List<string>();
			}

			record.Logs.AddRange(SceneDirtyWatcher.DrainLogs());
			SceneDirtyWatcher.Disarm(taskId);

			TestRunDump dump;
			bool hasDump = TestRunDumpStore.TryTakePending(testMode, out dump) && dump.SourceTaskId == taskId;
			bool ranCleanly = run != null && !run.aborted && string.IsNullOrEmpty(recoveryError);

			// The evidence is computed before the status, because a green NUnit run over inputs
			// that moved is not a success: it is a result about a project that no longer exists.
			// Evidence was completed asynchronously before releasing the run.
			record.Evidence = evidence;
			record.Tests = run;

			if (!ranCleanly)
			{
				record.Status = "runtime_error";
				if (!string.IsNullOrEmpty(recoveryError))
				{
					record.Logs.Add(recoveryError);
				}
			}
			else if (evidence.Validity == EvidenceRecord.Stale)
			{
				record.Status = "stale_input";
				record.Logs.Add("stale_input: " + evidence.Reason);
			}
			else if (evidence.Validity == EvidenceRecord.Unknown && EvidenceClassification.RequiresEvidence())
			{
				// Inside a validation window an unknown result is not a new acceptance. The NUnit
				// numbers stay in the record as diagnostics; the status says they prove nothing.
				record.Status = "evidence_unavailable";
				record.Logs.Add("evidence_unavailable: " + evidence.Reason);
			}
			else
			{
				record.Status = TestResultAggregator.StatusOf(run);
				if (record.Status == "no_tests_matched")
				{
					run.message = "No test cases matched. Check --mode, --assembly, --test and --category. Filters: " + requestedFilter;
					record.Logs.Add(run.message);
				}
			}

			record.FinishedAtUtc = System.DateTime.UtcNow.ToString("o");
			TaskJournal.Write(record);
			TelemetryLog.TaskFinished(record);
			CoordinationGate.ReleaseByRecord(record, record.Status == "success", record.Status);

			bool promoted = hasDump
				&& ranCleanly
				&& run.total > 0
				&& evidence.Validity == EvidenceRecord.Valid
				&& !string.IsNullOrEmpty(startSources)
				&& startSources == ValidationEvidence.CompletedSources;

			if (promoted)
			{
				// Everything that could move the artifact version — the tests themselves, then
				// PlayMode scene recovery — is done, so this is the state the results describe.
				dump.Fingerprint = TestFingerprint.Current();
				dump.SourceFingerprint = startSources;
				dump.InputDigest = evidence.InputDigest;
				dump.Validity = evidence.Validity;
				dump.Artifacts = new List<string>(record.Artifacts);
				TestRunDumpStore.Publish(dump, System.DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
				TestRunAttachments.Resolve(taskId, dump, evidence);
			}
			else if (hasDump && ranCleanly)
			{
				// The run produced real results that simply cannot be accepted. Everyone attached
				// to it gets the same verdict instead of being sent around the queue again.
				TestRunAttachments.Terminate(
					taskId,
					record.Status,
					"the run it joined ended as " + record.Status + ": " + evidence.Reason,
					evidence);
			}
			else
			{
				TestRunAttachments.Requeue(taskId);
			}
		}

		private static bool ArtifactsExist(TaskRecord record)
		{
			if (record.Artifacts == null || record.Artifacts.Count == 0)
			{
				return true;
			}

			foreach (string artifact in record.Artifacts)
			{
				if (string.IsNullOrEmpty(artifact))
				{
					continue;
				}

				string path = System.IO.Path.IsPathRooted(artifact)
					? artifact
					: System.IO.Path.Combine(BridgePaths.WorkingRoot, artifact);
				if (!System.IO.File.Exists(path))
				{
					return false;
				}
			}

			return true;
		}

		public static void FinalizeRecoveredPlayModeRun(string taskId, TestRunResult run, string recoveryError)
		{
			if (run == null)
			{
				run = new TestRunResult
				{
					aborted = true,
					message = "PlayMode test run ended before a result was recorded."
				};
			}

			FinalizeCoordinatorRun(taskId, run, recoveryError);

			// This path finalizes a PlayMode task after a domain reload, past any FinishTask call,
			// so the scheduler learns about the finished task here.
			TaskRecord record;
			if (TaskJournal.TryRead(taskId, out record))
			{
				AgentSessionScheduler.OnTaskFinished(record.AgentSessionId, System.DateTime.UtcNow);
			}
		}

		private static TestRunResult BuildResult(ITestResultAdaptor result)
		{
			TestRunResult run = new TestRunResult
			{
				passed = result.PassCount,
				failed = result.FailCount,
				skipped = result.SkipCount,
				inconclusive = result.InconclusiveCount,
				total = result.PassCount + result.FailCount + result.SkipCount + result.InconclusiveCount,
				duration = result.Duration
			};
			CollectFailures(result, run.failures);
			return run;
		}

		private static void CollectFailures(ITestResultAdaptor node, List<TestFailure> failures)
		{
			if (node.HasChildren)
			{
				foreach (ITestResultAdaptor child in node.Children)
				{
					CollectFailures(child, failures);
				}

				return;
			}

			if (node.TestStatus == TestStatus.Failed || node.TestStatus == TestStatus.Inconclusive)
			{
				failures.Add(new TestFailure
				{
					name = node.FullName,
					message = node.Message,
					stacktrace = node.StackTrace
				});
			}
		}

		public static void FinalizeCancellation(string taskId, string outcome, string reason)
		{
			string mode = SessionState.GetString(CoordinatorTestModeKey, "");
			if (SessionState.GetString(CoordinatorTestTaskKey, "") == taskId)
			{
				SessionState.EraseString(CoordinatorTestTaskKey);
				SessionState.EraseString(CoordinatorTestModeKey);
				SessionState.EraseString(CoordinatorTestSourceKey);
				SessionState.EraseString(CoordinatorTestFilterKey);
				SessionState.EraseString(CoordinatorTestCatalogKey);
			}
			SceneDirtyWatcher.Disarm(taskId);
			ValidationEvidence.Abort();
			if (!string.IsNullOrEmpty(mode)) TestRunDumpStore.DeletePending(mode);
			TaskRecord record;
			if (!TaskJournal.TryRead(taskId, out record) || TaskCoordinator.IsTerminal(record.Status)) return;
			record.Status = outcome;
			record.FinishedAtUtc = System.DateTime.UtcNow.ToString("o");
			System.DateTime started;
			if (System.DateTime.TryParse(record.StartedAtUtc, out started)) record.Timing.TotalMs = (int)(System.DateTime.UtcNow - started.ToUniversalTime()).TotalMilliseconds;
			record.Tests = new TestRunResult { aborted = true, message = reason };
			if (record.Logs == null) record.Logs = new List<string>();
			record.Logs.Add(reason);
			record.Logs.AddRange(SceneDirtyWatcher.DrainLogs());
			TaskJournal.Write(record);
			TelemetryLog.TaskFinished(record);
			TestRunAttachments.Terminate(taskId, outcome, reason, record.Evidence);
			AgentSessionScheduler.OnTaskFinished(record.AgentSessionId, System.DateTime.UtcNow);
			CoordinationGate.ReleaseByRecord(record, false, outcome);
		}

		private class TestCallbacks : ICallbacks
		{
			public void RunStarted(ITestAdaptor testsToRun)
			{
				if (SessionState.GetString(CoordinatorTestModeKey, "") == TestMode.PlayMode.ToString())
				{
					PlayModeSceneRecovery.CaptureBootstrapScene();
				}
			}

			public void TestStarted(ITestAdaptor test)
			{
			}

			public void TestFinished(ITestResultAdaptor result)
			{
			}

			public void RunFinished(ITestResultAdaptor result)
			{
				string coordinatorTaskId = SessionState.GetString(CoordinatorTestTaskKey, "");
				if (!string.IsNullOrEmpty(coordinatorTaskId))
				{
					if (TestRunLifecycle.IsStopping(coordinatorTaskId))
					{
						if (PlayModeSceneRecovery.IsPending)
							PlayModeSceneRecovery.RecordResult(new TestRunResult { aborted = true, message = "Test run canceled" });
						return;
					}
					TaskRecord completed;
					if (TaskJournal.TryRead(coordinatorTaskId, out completed) && TaskCoordinator.IsTerminal(completed.Status)) return;
					TestRunResult run = BuildResult(result);
					WritePendingDump(coordinatorTaskId, result);
					if (SessionState.GetString(CoordinatorTestModeKey, "") == TestMode.PlayMode.ToString()
						&& PlayModeSceneRecovery.IsPending)
					{
						PlayModeSceneRecovery.RecordResult(run);
						return;
					}

					FinalizeCoordinatorRun(coordinatorTaskId, run, null);
				}
			}
		}
	}
}
