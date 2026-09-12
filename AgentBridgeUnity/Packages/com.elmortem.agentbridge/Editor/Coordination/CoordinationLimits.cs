namespace AgentBridge.Coordination
{
	// Budgets and sizes of coordination-v1. They live next to the engine because both the CLI
	// parser and the editor gate have to refuse the same values before a transaction is opened.
	public static class CoordinationLimits
	{
		public const int SchemaVersion = 1;

		public const int EditDefaultSeconds = 120;
		public const int EditMinSeconds = 15;
		public const int EditMaxSeconds = 300;

		public const int WindowDefaultSeconds = 120;
		public const int WindowMinSeconds = 15;
		public const int WindowMaxSeconds = 600;

		public const int MaxScopePaths = 256;
		public const int MaxPlanSteps = 64;
		public const int MaxTombstones = 256;

		public const string KindEdit = "edit";
		public const string KindEditor = "editor";
		public const string KindValidation = "validation";

		public const string StateWaiting = "waiting";
		public const string StateGranted = "granted";
		public const string StateClosed = "closed";
		public const string StateCanceled = "canceled";
		public const string StateRejected = "rejected";

		public const string GrantActive = "active";
		public const string GrantOrphaned = "orphaned";
		public const string GrantDraining = "draining";
		public const string GrantInterrupted = "interrupted";

		public const string LifecycleIdle = "idle";
		public const string LifecycleWriting = "writing";
		public const string LifecycleWindow = "window";
		public const string LifecycleOrphaned = "orphaned";

		public const string StepReserved = "reserved";
		public const string StepDone = "done";
		public const string StepFailed = "failed";

		public static bool IsWindowKind(string kind)
		{
			return kind == KindEditor || kind == KindValidation;
		}

		public static bool IsLiveGrant(string state)
		{
			return state == GrantActive
				|| state == GrantOrphaned
				|| state == GrantDraining
				|| state == GrantInterrupted;
		}
	}
}
