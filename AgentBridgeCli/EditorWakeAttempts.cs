namespace AgentBridge.Cli;

internal sealed class EditorWakeAttempts
{
	public int PostAttempts { get; set; }
	public int FocusAttempts { get; set; }
	public DateTime LastAttemptUtc { get; set; } = DateTime.MinValue;

	public bool Exhausted
	{
		get { return PostAttempts >= WakePolicy.MaxPostAttempts && FocusAttempts >= WakePolicy.MaxFocusAttempts; }
	}
}
