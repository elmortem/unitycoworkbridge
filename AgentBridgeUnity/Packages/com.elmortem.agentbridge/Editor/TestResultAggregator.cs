using System.Collections.Generic;

namespace AgentBridge
{
	public static class TestResultAggregator
	{
		public static TestRunResult Aggregate(List<TestCaseResult> entries)
		{
			var run = new TestRunResult();

			foreach (TestCaseResult entry in entries)
			{
				run.duration += entry.DurationSeconds;

				switch (entry.Status)
				{
					case "Passed":
						run.passed++;
						break;
					case "Failed":
						run.failed++;
						AddFailure(run, entry);
						break;
					case "Skipped":
						run.skipped++;
						break;
					case "Inconclusive":
						run.inconclusive++;
						AddFailure(run, entry);
						break;
				}
			}

			run.total = run.passed + run.failed + run.skipped + run.inconclusive;
			return run;
		}

		public static string StatusOf(TestRunResult run)
		{
			return run.failed > 0 || run.inconclusive > 0 ? "test_failure" : "success";
		}

		private static void AddFailure(TestRunResult run, TestCaseResult entry)
		{
			run.failures.Add(new TestFailure
			{
				name = entry.FullName,
				message = entry.Message,
				stacktrace = entry.StackTrace
			});
		}
	}
}
