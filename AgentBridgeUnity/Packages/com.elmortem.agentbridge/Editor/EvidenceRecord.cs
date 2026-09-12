using System;

namespace AgentBridge
{
	// What a validation result is actually evidence of. Absent Evidence on an old record means
	// unknown for the new acceptance, never an optimistic pass.
	[Serializable]
	public class EvidenceRecord
	{
		public const string Valid = "valid";
		public const string Stale = "stale";
		public const string Unknown = "unknown";

		public string Validity = Unknown;
		public string InputDigest = "";
		public string EndInputDigest = "";
		public string WindowId = "";
		public string Reason = "";
		public bool ArtifactsPresent;

		public static EvidenceRecord UnknownBecause(string reason)
		{
			return new EvidenceRecord { Validity = Unknown, Reason = reason ?? "" };
		}
	}
}
