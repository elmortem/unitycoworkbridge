namespace AgentBridge
{
	// Where evidence is merely informative and where it is the acceptance itself.
	//
	// Outside a coordinated validation window the bridge keeps answering exactly as before: the
	// Evidence record is attached, and a missing snapshot does not turn a green run red. Inside
	// one, a result nobody can tie to a known project state is not an acceptance.
	public static class EvidenceClassification
	{
		public static bool RequiresEvidence()
		{
			string windowId;
			return CoordinationGate.InValidationWindow(out windowId);
		}

		public static string WindowId()
		{
			string windowId;
			CoordinationGate.InValidationWindow(out windowId);
			return windowId;
		}
	}
}
