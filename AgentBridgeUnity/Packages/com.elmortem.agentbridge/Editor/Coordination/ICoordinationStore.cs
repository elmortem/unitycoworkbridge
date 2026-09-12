namespace AgentBridge.Coordination
{
	public interface ICoordinationStore
	{
		// A read may observe an older whole revision; issuing a right always needs TryBegin.
		CoordinationState Read();

		// Non-blocking on purpose: the Unity main thread calls this from its update tick and
		// must never sit on a file lock.
		bool TryBegin(out ICoordinationTransaction transaction);
	}
}
