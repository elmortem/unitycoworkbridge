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
		private static TestRunnerApi _api;

		static AgentTestRunner()
		{
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

			try
			{
				TestRunnerApi api = ScriptableObject.CreateInstance<TestRunnerApi>();
				api.Execute(new ExecutionSettings(filter));
			}
			catch
			{
				SessionState.EraseString(CoordinatorTestTaskKey);
				SessionState.EraseString(CoordinatorTestModeKey);
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

		private static void FinalizeCoordinatorRun(string taskId, TestRunResult run, string recoveryError)
		{
			TaskRecord record;
			if (!TaskJournal.TryRead(taskId, out record))
			{
				return;
			}

			record.Tests = run;
			if (run == null || run.aborted || !string.IsNullOrEmpty(recoveryError))
			{
				record.Status = "runtime_error";
				if (!string.IsNullOrEmpty(recoveryError))
				{
					if (record.Logs == null)
					{
						record.Logs = new List<string>();
					}

					record.Logs.Add(recoveryError);
				}
			}
			else
			{
				record.Status = run.failed > 0 || run.inconclusive > 0 ? "test_failure" : "success";
			}

			record.FinishedAtUtc = System.DateTime.UtcNow.ToString("o");
			TaskJournal.Write(record);
		}

		public static void FinalizeRecoveredPlayModeRun(string taskId, TestRunResult run, string recoveryError)
		{
			SessionState.EraseString(CoordinatorTestTaskKey);
			SessionState.EraseString(CoordinatorTestModeKey);

			if (run == null)
			{
				run = new TestRunResult
				{
					aborted = true,
					message = "PlayMode test run ended before a result was recorded."
				};
			}

			FinalizeCoordinatorRun(taskId, run, recoveryError);
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
					if (SessionState.GetString(CoordinatorTestModeKey, "") == TestMode.PlayMode.ToString()
						&& PlayModeSceneRecovery.IsPending)
					{
						PlayModeSceneRecovery.RecordResult(run);
						return;
					}

					SessionState.EraseString(CoordinatorTestTaskKey);
					SessionState.EraseString(CoordinatorTestModeKey);
					FinalizeCoordinatorRun(coordinatorTaskId, run, null);
				}
			}
		}
	}
}
