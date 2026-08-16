using System;

namespace AgentBridge
{
	// The single source of truth about who may stop whose play mode. Everything it needs
	// arrives as a parameter, so the rules are testable without an editor in play mode.
	public static class PlaySessionArbiter
	{
		public static StopVerdict Judge(PlaySessionState state, string callerEffectiveSessionId, bool isPlaying, bool testsPending)
		{
			if (testsPending)
			{
				return StopVerdict.RejectTests;
			}

			if (state == null)
			{
				// Nobody claimed this play mode, so it is either a stuck agent task or a human
				// session that the caller explicitly asked to end.
				return isPlaying ? StopVerdict.StopUnsanctioned : StopVerdict.NotPlaying;
			}

			if (string.Equals(state.OwnerAgentSessionId ?? "", callerEffectiveSessionId ?? "", StringComparison.Ordinal))
			{
				return StopVerdict.StopOwn;
			}

			return StopVerdict.RejectForeign;
		}
	}
}
