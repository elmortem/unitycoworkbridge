namespace AgentBridge
{
	public static class TestCacheQuery
	{
		// sourceFingerprint is passed in rather than computed here: the caller already needs the
		// same hash for compile tasks, and it is the expensive half of the check.
		public static bool TryServe(
			TaskRequest request,
			string sourceFingerprint,
			out TestRunResult result,
			out string sourceTaskId,
			out string status)
		{
			result = null;
			sourceTaskId = null;
			status = null;

			string mode = request.TestMode == "PlayMode" ? "PlayMode" : "EditMode";
			TestRunDump dump;
			if (!TestRunDumpStore.TryRead(mode, out dump))
			{
				return false;
			}

			if (string.IsNullOrEmpty(dump.SourceFingerprint) || dump.SourceFingerprint != sourceFingerprint)
			{
				return false;
			}

			if (dump.Fingerprint != TestFingerprint.Current())
			{
				return false;
			}

			if (!TestFilterCoverage.Covers(dump, request))
			{
				return false;
			}

			result = TestResultAggregator.Aggregate(TestFilterCoverage.Select(dump.Entries, request));
			sourceTaskId = dump.SourceTaskId;
			status = TestResultAggregator.StatusOf(result);
			return true;
		}
	}
}
