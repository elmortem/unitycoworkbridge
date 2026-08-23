namespace AgentBridge.Cli;

internal static class WakePolicy
{
	public const long StaleThresholdMs = 5000;
	public const int MaxPostAttempts = 5;
	public const int MaxFocusAttempts = 1;
	public const double AttemptIntervalSeconds = 3d;

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

		if (editorIsForeground)
		{
			return WakeAction.None;
		}

		if (postAttempts < MaxPostAttempts)
		{
			return WakeAction.Post;
		}

		if (focusAttempts < MaxFocusAttempts)
		{
			return WakeAction.Focus;
		}

		return WakeAction.None;
	}
}
