namespace AgentBridge.Coordination
{
	// The engine never reads the wall clock itself, so every deadline test is reproducible.
	public interface ICoordinationClock
	{
		long UtcNowMs { get; }
	}
}
