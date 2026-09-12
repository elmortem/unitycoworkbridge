using System;

namespace AgentBridge
{
	// The index row of one completed test set. It carries everything a lookup needs so a miss
	// never has to open the entry file, and a corrupt entry can be skipped by id.
	[Serializable]
	public class TestCacheEntryInfo
	{
		public string Id = "";
		public string TestMode = "";
		public string InputDigest = "";
		public string SourceFingerprint = "";
		public string ArtifactVersion = "";
		public string SourceTaskId = "";
		public string Validity = "";
		public string FinishedAtUtc = "";
		public long LastUsedMs;
		public int Passed;
		public int Failed;
		public int Skipped;
		public int Inconclusive;
		public int Total;
	}
}
