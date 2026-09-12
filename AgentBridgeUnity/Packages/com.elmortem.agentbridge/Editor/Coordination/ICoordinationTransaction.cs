using System;

namespace AgentBridge.Coordination
{
	// A transaction holds the cross-process lock for the length of one read-validate-apply-write.
	// Disposing without Commit leaves the published state untouched.
	public interface ICoordinationTransaction : IDisposable
	{
		CoordinationState State { get; }
		void Commit();
	}
}
