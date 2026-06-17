using System.Collections.Generic;
using System.IO;
using UnityEngine;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEditor.TestTools.TestRunner.Api;

namespace CoworkBridge
{
	[InitializeOnLoad]
	public static class CoworkTestRunner
	{
		private const string TestTaskKey = "CoworkBridge_TestTask";
		private static TestRunnerApi _api;

		static CoworkTestRunner()
		{
			_api = ScriptableObject.CreateInstance<TestRunnerApi>();
			_api.RegisterCallbacks(new TestCallbacks());
		}

		public static string RequestRun(string taskId, string testMode, string[] assemblyNames, string[] testNames, string[] categoryNames)
		{
			if (EditorApplication.isPlaying)
			{
				WriteAborted(taskId, "Editor is in play mode. Exit play mode and re-run the test task.");
				return "Test run aborted: editor in play mode";
			}

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

			SessionState.SetString(TestTaskKey, taskId);

			TestRunnerApi api = ScriptableObject.CreateInstance<TestRunnerApi>();
			api.Execute(new ExecutionSettings(filter));
			return "Test run started";
		}

		private static TestMode ParseMode(string testMode)
		{
			if (testMode == "PlayMode")
			{
				return TestMode.PlayMode;
			}

			return TestMode.EditMode;
		}

		private static void WriteAborted(string taskId, string message)
		{
			TestRunResult run = new TestRunResult
			{
				aborted = true,
				message = message
			};
			WriteResult(taskId, run);
		}

		private static void WriteResult(string taskId, TestRunResult run)
		{
			string dir = Path.Combine(Application.dataPath, "Editor", "CoworkBridge");
			string jsonPath = Path.Combine(dir, "testresult_" + taskId + ".json");
			string donePath = Path.Combine(dir, "testresult_" + taskId + ".done");

			File.WriteAllText(jsonPath, JsonUtility.ToJson(run, true));
			File.WriteAllText(donePath, "");
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
				string taskId = SessionState.GetString(TestTaskKey, "");
				if (string.IsNullOrEmpty(taskId))
				{
					return;
				}

				SessionState.EraseString(TestTaskKey);

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
				WriteResult(taskId, run);
				Debug.Log("[CoworkBridge] Tests " + taskId + ": passed " + run.passed + ", failed " + run.failed);
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
		}
	}
}
