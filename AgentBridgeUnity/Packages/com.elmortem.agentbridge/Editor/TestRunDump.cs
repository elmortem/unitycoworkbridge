using System;
using System.Collections.Generic;

namespace AgentBridge
{
	[Serializable]
	public class TestRunDump
	{
		public int Version = 3;
		public TestNameResolver.CatalogData Catalog;
		public string Fingerprint;
		public string SourceFingerprint;
		public string SourceTaskId;

		// The evidence-v1 content digest of the inputs this run describes, and how much that
		// digest is worth. Only a valid set is ever served.
		public string InputDigest = "";
		public string Validity = EvidenceRecord.Unknown;

		public TestRunFilter Filter = new TestRunFilter();
		public string FinishedAtUtc;
		public List<TestCaseResult> Entries = new List<TestCaseResult>();
		public List<string> Artifacts = new List<string>();
	}
}
