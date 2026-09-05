namespace AgentBridge.Cli;

internal sealed class EditorWakeAttempts
{
	public int PostAttempts { get; set; }
	public int FocusAttempts { get; set; }
	public DateTime LastAttemptUtc { get; set; } = DateTime.MinValue;
	public DateTime? StalledSinceUtc { get; set; }
	public long? LastHeartbeatUtcMs { get; set; }

	public void Observe(long? heartbeatAgeMs, DateTime now)
	{
		long? heartbeatUtcMs = heartbeatAgeMs == null ? null
			: new DateTimeOffset(now).ToUnixTimeMilliseconds() - heartbeatAgeMs.Value;
		// Inspect and this sample have slightly different clocks. Ignore sub-second jitter.
		bool advanced = heartbeatUtcMs != null && LastHeartbeatUtcMs != null
			&& heartbeatUtcMs > LastHeartbeatUtcMs + 500;
		if (advanced || heartbeatAgeMs < WakePolicy.StaleThresholdMs)
		{
			PostAttempts = 0;
			FocusAttempts = 0;
			LastAttemptUtc = DateTime.MinValue;
			StalledSinceUtc = null;
		}
		if (heartbeatAgeMs >= WakePolicy.StaleThresholdMs) StalledSinceUtc ??= now;
		LastHeartbeatUtcMs = heartbeatUtcMs;
	}

	public bool TimedOut(DateTime now)
	{
		return StalledSinceUtc != null
			&& (now - StalledSinceUtc.Value).TotalSeconds >= WakePolicy.RecoveryTimeoutSeconds;
	}
}
