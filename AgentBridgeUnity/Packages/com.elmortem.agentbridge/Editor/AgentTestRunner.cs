using System.Collections.Generic;
using UnityEngine;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEditor.TestTools.TestRunner.Api;

namespace AgentBridge
{
	[InitializeOnLoad]
	public static class AgentTestRunner
	{
		public const string CoordinatorTestTaskKey = "AgentBridge_CoordinatorTestTask";
		private static TestRunnerApi _api;

		static AgentTestRunner()
		{
			_api = ScriptableObject.CreateInstance<TestRunnerApi>();
			_api.RegisterCallbacks(new TestCallbacks());
		}

		public static bool TryRequestRunForCoordinator(string taskId, string testMode, string[] assemblyNames, string[] testNames, string[] categoryNames, out TestRunResult abortedResult)
		{
			abortedResult = null;

			if (EditorApplication.isPlaying)
			{
				abortedResult = new TestRunResult
				{
					aborted = true,
					message = "Editor is in play mode. Exit play mode and re-run the test task."
				};
				return false;
			}

			Filter filter = BuildFilter(testMode, assemblyNames, testNames, categoryNames);

			SessionState.SetString(CoordinatorTestTaskKey, taskId);

			TestRunnerApi api = ScriptableObject.CreateInstance<TestRunnerApi>();
			api.Execute(new ExecutionSettings(filter));
			return true;
		}

		private static Filter BuildFilter(string testMode, string[] assemblyNames, string[] testNames, string[] categoryNames)
		{
			TestMode mode = ParseMode(testMode);

			if (mode == TestMode.PlayMode)
			{
				EditorSceneManager.SaveOpenScenes();
			}

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

		private static void FinalizeCoordinatorRun(string taskId, TestRunResult run)
		{
			TaskRecord record;
			if (!TaskJournal.TryRead(taskId, out record))
			{
				return;
			}

			record.Tests = run;
			record.Status = run.failed > 0 || run.inconclusive > 0 ? "test_failure" : "success";
			record.FinishedAtUtc = System.DateTime.UtcNow.ToString("o");
			TaskJournal.Write(record);
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
					SessionState.EraseString(CoordinatorTestTaskKey);

					TestRunResult run = BuildResult(result);
					FinalizeCoordinatorRun(coordinatorTaskId, run);
				}
			}
		}
	}
}
