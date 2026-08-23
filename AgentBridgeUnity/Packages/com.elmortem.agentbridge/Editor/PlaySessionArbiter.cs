using System;

namespace AgentBridge
{
	// The single source of truth about who may stop whose play mode. Everything it needs
	// arrives as a parameter, so the rules are testable without an editor in play mode.
	public static class PlaySessionArbiter
	{
		public static StopVerdict Judge(
			PlaySessionState state,
			string callerEffectiveSessionId,
			bool isPlaying,
			bool testsPending,
			DateTime nowUtc,
			int ownerIdleSeconds)
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

			if (CanPreempt(state, nowUtc, ownerIdleSeconds))
			{
				return StopVerdict.StopPreempt;
			}

			return StopVerdict.RejectForeign;
		}

		// A foreign session may take the editor back when the owner has run out of time or has
		// stopped working; while the owner keeps submitting tasks the session is untouchable.
		public static bool CanPreempt(PlaySessionState state, DateTime nowUtc, int ownerIdleSeconds)
		{
			DateTime deadlineUtc;
			if (TryParseUtc(state.DeadlineUtc, out deadlineUtc) && nowUtc >= deadlineUtc)
			{
				return true;
			}

			DateTime lastActivityUtc;
			if (!TryParseUtc(state.OwnerLastActivityUtc, out lastActivityUtc))
			{
				// A session written by an older package version has no activity field; the
				// deadline alone decides for it.
				return false;
			}

			return (nowUtc - lastActivityUtc).TotalSeconds >= ownerIdleSeconds;
		}

		private static bool TryParseUtc(string value, out DateTime result)
		{
			return DateTime.TryParse(
				value,
				System.Globalization.CultureInfo.InvariantCulture,
				System.Globalization.DateTimeStyles.RoundtripKind,
				out result);
		}
	}
}
