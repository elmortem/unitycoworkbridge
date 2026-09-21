namespace AgentBridge
{
	// One remembered miss: the sources and the candidate entries the digest was rejected against, and
	// the moment it is worth computing again. Anything else changing is not a reason to recheck, and
	// these two changing is a reason to recheck immediately.
	public sealed class CacheMissEntry
	{
		public string SourceFingerprint = "";
		public string CandidateKey = "";
		public long RetryAtMs;
	}
}
