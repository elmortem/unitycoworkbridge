namespace AgentBridge.Coordination
{
	// Every refusal the engine or the store can produce has exactly one stable code here.
	// The strings are part of the contract: skills and scripts branch on them.
	public static class CoordinationCodes
	{
		public const string Ok = "ok";
		public const string BadUsage = "bad_usage";
		public const string UnknownOp = "unknown_op";

		public const string SchemaUnsupported = "coordination_schema_unsupported";
		public const string Corrupt = "coordination_corrupt";
		public const string Busy = "coordination_busy";
		public const string RecoveryRequired = "coordination_recovery_required";
		public const string PathUnsupported = "coordination_path_unsupported";
		public const string Required = "coordination_required";

		public const string StaleEpoch = "stale_epoch";
		public const string StaleToken = "stale_token";
		public const string ProjectMismatch = "project_mismatch";

		public const string NotRegistered = "not_registered";
		public const string ParticipantBusy = "participant_busy";
		public const string SpecConflict = "spec_conflict";
		public const string ScopeConflict = "scope_conflict";
		public const string ScopeInvalid = "scope_invalid";

		public const string RequestConflict = "request_conflict";
		public const string RequestActive = "request_active";
		public const string RequestNotFound = "request_not_found";
		public const string PlanInvalid = "plan_invalid";
		public const string SecondsOutOfRange = "seconds_out_of_range";

		public const string EditActive = "edit_active";
		public const string PauseRequested = "pause_requested";
		public const string OrphanedWriter = "orphaned_writer";
		public const string GrantNotFound = "grant_not_found";

		public const string WindowActive = "window_active";
		public const string WindowNotGranted = "window_not_granted";
		public const string WindowDraining = "window_draining";
		public const string EditorBusy = "editor_busy";

		public const string StepUnknown = "step_unknown";
		public const string StepConsumed = "step_consumed";
		public const string StepFailed = "step_failed";
		public const string StepAttached = "step_attached";
		public const string StepMismatch = "step_mismatch";

		public const string ActiveTask = "active_task";
		public const string AlreadyClosed = "already_closed";
		public const string Draining = "draining";
		public const string Granted = "granted";
		public const string Waiting = "waiting";
		public const string NotHolder = "not_holder";
	}
}
