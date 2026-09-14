using System;
using System.Globalization;

namespace AgentBridge
{
	public static class TaskCancellationPolicy
	{
		public const int ProtectedSeconds = 300;
		public const string Capability = "long-running-tasks-v1";

		public static long ElapsedSeconds(string startedAtUtc, long nowMs)
		{
			DateTimeOffset started;
			if (!DateTimeOffset.TryParse(startedAtUtc, CultureInfo.InvariantCulture,
				DateTimeStyles.AssumeUniversal, out started)) return -1;
			return Math.Max(0, (nowMs - started.ToUnixTimeMilliseconds()) / 1000);
		}

		public static bool IsOwner(string owner, string requester)
		{
			// An omitted session is not proof of ownership. Human editor controls bypass this policy.
			return !string.IsNullOrEmpty(owner) && owner == requester;
		}

		public static bool IsProtected(string owner, string requester, string startedAtUtc, long nowMs)
		{
			return !IsOwner(owner, requester) && ElapsedSeconds(startedAtUtc, nowMs) < ProtectedSeconds;
		}

		public static string Reason(string owner, string requester, string startedAtUtc, long nowMs)
		{
			string actor = string.IsNullOrEmpty(requester) ? "anonymous CLI agent" : requester;
			long elapsed = ElapsedSeconds(startedAtUtc, nowMs);
			return !IsOwner(owner, requester) && elapsed >= ProtectedSeconds
				? "preempted_after_300s: Canceled by " + actor + " after " + elapsed + " seconds; exceeded the protected period of 300 seconds."
				: "Canceled through CLI by " + actor;
		}
	}
}
