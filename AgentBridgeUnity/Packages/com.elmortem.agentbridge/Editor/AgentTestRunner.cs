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
		private static TestRunnerApi _api;

		static AgentTestRunner()
		{
			if (Application.isBatchMode) return;
			_api = ScriptableObject.CreateInstance<TestRunnerApi>();
			_api.RegisterCallbacks(new TestCallbacks());
			PlayModeSceneRecovery.Start();
		}

		public static bool TryRequestRunForCoordinator(string taskId, string testMode, string[] assemblyNames, string[] testNames, string[] categoryNames, out TestRunResult abortedResult)
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
			SessionState.SetString(CoordinatorTestModeKey, mode.ToString());
			SessionState.SetString(CoordinatorTestSourceKey, TestFingerprint.Sources());
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

				api.Execute(new ExecutionSettings(filter));
			}
			catch
			{
				SessionState.EraseString(CoordinatorTestTaskKey);
				SessionState.EraseString(CoordinatorTestModeKey);
				SessionState.EraseString(CoordinatorTestSourceKey);
				SessionState.EraseString(CoordinatorTestFilterKey);
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
				Filter = filter,
				FinishedAtUtc = System.DateTime.UtcNow.ToString("o")
			};

			CollectEntries(result, null, dump.Entries);
			TestRunDumpStore.WritePending(dump);
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
			string testMode = SessionState.GetString(CoordinatorTestModeKey, "");
			string startSources = SessionState.GetString(CoordinatorTestSourceKey, "");
			SessionState.EraseString(CoordinatorTestTaskKey);
			SessionState.EraseString(CoordinatorTestModeKey);
			SessionState.EraseString(CoordinatorTestSourceKey);
			SessionState.EraseString(CoordinatorTestFilterKey);

			TaskRecord record;
			if (!TaskJournal.TryRead(taskId, out record))
			{
				TestRunDumpStore.DeletePending(testMode);
				TestRunAttachments.Requeue(taskId);
				return;
			}

			if (record.Logs == null)
			{
				record.Logs = new List<string>();
			}

			record.Logs.AddRange(SceneDirtyWatcher.DrainLogs());
			SceneDirtyWatcher.Disarm(taskId);

			record.Tests = run;
			if (run == null || run.aborted || !string.IsNullOrEmpty(recoveryError))
			{
				record.Status = "runtime_error";
				if (!string.IsNullOrEmpty(recoveryError))
				{
					record.Logs.Add(recoveryError);
				}
			}
			else
			{
				record.Status = run.failed > 0 || run.inconclusive > 0 ? "test_failure" : "success";
			}

			record.FinishedAtUtc = System.DateTime.UtcNow.ToString("o");
			TaskJournal.Write(record);
			TelemetryLog.TaskFinished(record);

			TestRunDump dump;
			bool promoted = TestRunDumpStore.TryTakePending(testMode, out dump)
				&& dump.SourceTaskId == taskId
				&& run != null && !run.aborted && string.IsNullOrEmpty(recoveryError)
				&& !string.IsNullOrEmpty(startSources)
				&& startSources == TestFingerprint.Sources();

			if (promoted)
			{
				// Everything that could move the artifact version — the tests themselves, then
				// PlayMode scene recovery — is done, so this is the state the results describe.
				dump.Fingerprint = TestFingerprint.Current();
				dump.SourceFingerprint = startSources;
				TestRunDumpStore.Write(dump);
				TestRunAttachments.Resolve(taskId, dump);
			}
			else
			{
				TestRunAttachments.Requeue(taskId);
			}
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
