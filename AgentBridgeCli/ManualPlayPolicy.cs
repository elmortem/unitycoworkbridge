namespace AgentBridge.Cli;

// Whether a play mode that belongs to nobody should be stopped so an agent task can run.
// Kept free of the file system for the same reason as WakePolicy: this is the decision worth
// testing, and the client half of the takeover is nothing but this decision plus a stopplay.
internal static class ManualPlayPolicy
{
	public const int MaxStops = 3;

	public static bool IsManualPlaying(BridgeStatus? bridge)
	{
		return bridge != null && bridge.IsPlaying && string.IsNullOrEmpty(bridge.PlaySessionAgentId);
	}

	public static bool ShouldStop(BridgeHealth? health, string kind, int stopsSoFar)
	{
		if (stopsSoFar >= MaxStops)
		{
			return false;
		}

		if (kind == "stopplay")
		{
			return false;
		}

		if (health == null || !health.BridgeReady)
		{
			return false;
		}

		return IsManualPlaying(health.Bridge);
	}
}
