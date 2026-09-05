namespace AgentBridge.Cli;

internal static class WakePolicy
{
	public const long StaleThresholdMs = 5000;
	public const int MaxPostAttempts = 5;
	public const int MaxFocusAttempts = 1;
	public const double AttemptIntervalSeconds = 3d;
	public const double RecoveryTimeoutSeconds = 120d;

	// A stale heartbeat is recoverable only for this live, enabled, compatible editor.
	// Never let heartbeat_stale mask a second operational failure (or poke a foreign PID).
	public static bool CanRecover(BridgeHealth health)
	{
		return !health.ForeignHost && health.EditorProcessAlive == true
			&& health.PackageDeclared && health.ProjectMatches && health.ProtocolCompatible
			&& health.Bridge?.Enabled == true && health.HeartbeatAgeMs != null
			&& health.Problems.All(problem => problem is "heartbeat_stale" or "roslyn_not_ready");
	}

	public static WakeAction Decide(
		long? heartbeatAgeMs,
		bool editorIsForeground,
		int postAttempts,
		int focusAttempts,
		double secondsSinceLastAttempt)
	{
		if (heartbeatAgeMs == null)
		{
			return WakeAction.None;
		}

		if (heartbeatAgeMs < StaleThresholdMs)
		{
			return WakeAction.None;
		}

		if (secondsSinceLastAttempt < AttemptIntervalSeconds)
		{
			return WakeAction.None;
		}

		if (postAttempts < MaxPostAttempts)
		{
			return WakeAction.Post;
		}

		if (!editorIsForeground && focusAttempts < MaxFocusAttempts)
		{
			return WakeAction.Focus;
		}

		return WakeAction.None;
	}
}
