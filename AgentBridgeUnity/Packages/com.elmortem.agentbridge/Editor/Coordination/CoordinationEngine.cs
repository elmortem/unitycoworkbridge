using System;
using System.Collections.Generic;
using System.Text;

namespace AgentBridge.Coordination
{
	// The whole protocol lives here as a pure state transformation: no files, no Unity, no waiting
	// and no clock of its own. Every refusal validates before it mutates, so a rejected command
	// never leaves half a reservation behind.
	public sealed class CoordinationEngine
	{
		public const string OpRegister = "register";
		public const string OpScope = "scope";
		public const string OpEditBegin = "edit-begin";
		public const string OpEditEnd = "edit-end";
		public const string OpRenew = "renew";
		public const string OpRequest = "request";
		public const string OpCancel = "cancel";
		public const string OpFinish = "finish";
		public const string OpLeave = "leave";
		public const string OpAbandon = "abandon";

		// Editor-side operations. They are reachable from the CLI only through the package.
		public const string OpWindowConfirm = "window-confirm";
		public const string OpStepBegin = "step-begin";
		public const string OpStepFinish = "step-finish";
		public const string OpStepFail = "step-fail";
		public const string OpIncarnation = "editor-incarnation";
		public const string OpSweep = "sweep";

		public CoordinationReply Apply(CoordinationState state, CoordinationCommand command, long nowMs)
		{
			if (state == null || command == null)
			{
				return CoordinationReply.Fail(CoordinationCodes.BadUsage, "state and command are required");
			}

			state.Normalize();
			command.Normalize();

			if (state.SchemaVersion != CoordinationLimits.SchemaVersion)
			{
				return Stamp(state, CoordinationReply.Fail(
					CoordinationCodes.SchemaUnsupported,
					"coordination state schema " + state.SchemaVersion + " is not supported"));
			}

			bool swept = Sweep(state, nowMs, true);

			CoordinationReply reply = Dispatch(state, command, nowMs);
			if (reply.Ok && reply.Changed)
			{
				// Issuing rights is part of every accepted mutation: a closed grant may release the
				// next FIFO edit in the very same transaction.
				Sweep(state, nowMs, true);
				state.Revision++;
			}
			else if (swept)
			{
				// The sweep itself is a meaningful change: an expired writer became orphaned.
				reply.Changed = true;
				state.Revision++;
			}
			else
			{
				reply.Changed = false;
			}

			Stamp(state, reply);
			return reply;
		}

		public CoordinationReply Inspect(CoordinationState state, string session, string requestId, long nowMs)
		{
			if (state == null)
			{
				return CoordinationReply.Fail(CoordinationCodes.BadUsage, "state is required");
			}

			state.Normalize();

			// Reading applies expiry to the snapshot the caller holds, so an orphaned writer is
			// reported honestly. It deliberately does not issue anything: a right that exists only
			// in a reader's memory would let two clients believe they hold the same one.
			Sweep(state, nowMs, false);

			session = CoordinationText.Safe(session);
			CoordinationRequest request = ResolveRequest(state, session, requestId);

			CoordinationReply reply = CoordinationReply.Success(CoordinationCodes.Ok);
			reply.Session = session;
			if (request != null)
			{
				reply.RequestId = request.Id;
				reply.State = request.State;
				reply.Token = TokenFor(state, request);
			}
			else if (!string.IsNullOrEmpty(session))
			{
				CoordinationGrant grant = state.FindGrantBySession(session, null);
				if (grant != null)
				{
					reply.RequestId = grant.RequestId;
					reply.State = grant.State;
					reply.Token = grant.Token;
				}
				else
				{
					CoordinationParticipant participant = state.FindParticipant(session);
					reply.State = participant != null ? participant.Lifecycle : "";
				}
			}

			reply.Blockers = BuildBlockers(state, session, request);
			reply.Changed = false;
			Stamp(state, reply);
			return reply;
		}

		private CoordinationReply Dispatch(CoordinationState state, CoordinationCommand command, long nowMs)
		{
			switch (command.Op)
			{
				case OpRegister:
					return Register(state, command, nowMs);
				case OpScope:
					return SetScope(state, command, nowMs);
				case OpEditBegin:
					return EditBegin(state, command, nowMs);
				case OpEditEnd:
					return EditEnd(state, command, nowMs);
				case OpRenew:
					return Renew(state, command, nowMs);
				case OpRequest:
					return RequestWindow(state, command, nowMs);
				case OpCancel:
					return Cancel(state, command, nowMs);
				case OpFinish:
					return Finish(state, command, nowMs);
				case OpLeave:
					return Leave(state, command, nowMs);
				case OpAbandon:
					return Abandon(state, command, nowMs);
				case OpWindowConfirm:
					return WindowConfirm(state, command, nowMs);
				case OpStepBegin:
					return StepBegin(state, command, nowMs);
				case OpStepFinish:
					return StepFinish(state, command, nowMs, CoordinationLimits.StepDone);
				case OpStepFail:
					return StepFinish(state, command, nowMs, CoordinationLimits.StepFailed);
				case OpIncarnation:
					return Incarnation(state, command, nowMs);
				case OpSweep:
					return CoordinationReply.Success(CoordinationCodes.Ok);
				default:
					return CoordinationReply.Fail(CoordinationCodes.UnknownOp, "unknown coordination operation: " + command.Op);
			}
		}

		// ---------------------------------------------------------------- registration

		private CoordinationReply Register(CoordinationState state, CoordinationCommand command, long nowMs)
		{
			if (string.IsNullOrEmpty(command.Session) || string.IsNullOrEmpty(command.SpecId))
			{
				return CoordinationReply.Fail(CoordinationCodes.BadUsage, "register requires --session and --spec");
			}

			string[] paths;
			string error;
			if (!CoordinationScope.TryNormalize(command.Paths, out paths, out error))
			{
				return CoordinationReply.Fail(CoordinationCodes.ScopeInvalid, error);
			}

			CoordinationParticipant existing = state.FindParticipant(command.Session);
			string conflictSession;
			string conflictPath;
			if (Conflicts(state, command.Session, paths, out conflictSession, out conflictPath))
			{
				return CoordinationReply.Fail(
					CoordinationCodes.ScopeConflict,
					"scope path " + conflictPath + " is already reserved by session " + conflictSession);
			}

			if (existing != null)
			{
				bool identical = string.Equals(existing.SpecId, command.SpecId, StringComparison.Ordinal)
					&& SameSet(existing.Paths, paths)
					&& string.Equals(existing.Owner, command.Owner, StringComparison.Ordinal)
					&& string.Equals(existing.RepoRoot, command.RepoRoot, StringComparison.Ordinal);
				if (identical)
				{
					existing.LastSeenMs = nowMs;
					CoordinationReply same = CoordinationReply.Success(CoordinationCodes.Ok);
					same.Session = command.Session;
					same.State = existing.Lifecycle;
					same.Changed = false;
					return same;
				}

				if (IsBusy(state, command.Session))
				{
					return CoordinationReply.Fail(
						CoordinationCodes.ParticipantBusy,
						"session " + command.Session + " holds a right or a pending request; close it before re-registering");
				}

				existing.SpecId = command.SpecId;
				existing.Owner = command.Owner;
				existing.RepoRoot = command.RepoRoot;
				existing.Paths = paths;
				existing.LastSeenMs = nowMs;
				CoordinationReply updated = CoordinationReply.Success(CoordinationCodes.Ok);
				updated.Session = command.Session;
				updated.State = existing.Lifecycle;
				updated.Changed = true;
				return updated;
			}

			var participant = new CoordinationParticipant
			{
				Session = command.Session,
				SpecId = command.SpecId,
				Owner = command.Owner,
				RepoRoot = command.RepoRoot,
				Paths = paths,
				Generation = 1,
				Lifecycle = CoordinationLimits.LifecycleIdle,
				RegisteredAtMs = nowMs,
				LastSeenMs = nowMs
			};
			state.Participants.Add(participant);

			CoordinationReply reply = CoordinationReply.Success(CoordinationCodes.Ok);
			reply.Session = command.Session;
			reply.State = CoordinationLimits.LifecycleIdle;
			reply.Changed = true;
			return reply;
		}

		private CoordinationReply SetScope(CoordinationState state, CoordinationCommand command, long nowMs)
		{
			CoordinationParticipant participant = state.FindParticipant(command.Session);
			if (participant == null)
			{
				return NotRegistered(command.Session);
			}

			if (IsBusy(state, command.Session))
			{
				return CoordinationReply.Fail(
					CoordinationCodes.ParticipantBusy,
					"scope can only be replaced while the session holds no right and no pending request");
			}

			string[] paths;
			string error;
			if (!CoordinationScope.TryNormalize(command.Paths, out paths, out error))
			{
				return CoordinationReply.Fail(CoordinationCodes.ScopeInvalid, error);
			}

			string conflictSession;
			string conflictPath;
			if (Conflicts(state, command.Session, paths, out conflictSession, out conflictPath))
			{
				// All or nothing: an expansion that collides anywhere leaves the old scope intact.
				return CoordinationReply.Fail(
					CoordinationCodes.ScopeConflict,
					"scope path " + conflictPath + " is already reserved by session " + conflictSession);
			}

			bool changed = !SameSet(participant.Paths, paths);
			participant.Paths = paths;
			participant.LastSeenMs = nowMs;

			CoordinationReply reply = CoordinationReply.Success(CoordinationCodes.Ok);
			reply.Session = command.Session;
			reply.State = participant.Lifecycle;
			reply.Changed = changed;
			return reply;
		}

		private CoordinationReply Leave(CoordinationState state, CoordinationCommand command, long nowMs)
		{
			CoordinationParticipant participant = state.FindParticipant(command.Session);
			if (participant == null)
			{
				CoordinationReply gone = CoordinationReply.Success(CoordinationCodes.AlreadyClosed);
				gone.Session = command.Session;
				gone.Changed = false;
				return gone;
			}

			if (state.FindGrantBySession(command.Session, null) != null)
			{
				return CoordinationReply.Fail(
					CoordinationCodes.ParticipantBusy,
					"leave requires no active or orphaned right; close it first");
			}

			foreach (CoordinationRequest request in state.Requests)
			{
				if (string.Equals(request.Session, command.Session, StringComparison.Ordinal) && !request.IsTerminal)
				{
					return CoordinationReply.Fail(
						CoordinationCodes.ParticipantBusy,
						"leave requires no pending request; cancel " + request.Id + " first");
				}
			}

			state.Participants.Remove(participant);
			RemoveWhereSession(state.Requests, command.Session);
			RemoveTombstones(state, command.Session);

			CoordinationReply reply = CoordinationReply.Success(CoordinationCodes.Ok);
			reply.Session = command.Session;
			reply.State = CoordinationLimits.StateClosed;
			reply.Changed = true;
			return reply;
		}

		// ---------------------------------------------------------------- edit grants

		private CoordinationReply EditBegin(CoordinationState state, CoordinationCommand command, long nowMs)
		{
			if (string.IsNullOrEmpty(command.Uuid))
			{
				return CoordinationReply.Fail(CoordinationCodes.BadUsage, "edit-begin requires --request <uuid>");
			}

			if (string.IsNullOrEmpty(command.Nonce))
			{
				return CoordinationReply.Fail(CoordinationCodes.BadUsage, "edit-begin requires a nonce");
			}

			CoordinationParticipant participant = state.FindParticipant(command.Session);
			if (participant == null)
			{
				return NotRegistered(command.Session);
			}

			int seconds = command.Seconds > 0 ? command.Seconds : CoordinationLimits.EditDefaultSeconds;
			if (seconds < CoordinationLimits.EditMinSeconds || seconds > CoordinationLimits.EditMaxSeconds)
			{
				return CoordinationReply.Fail(
					CoordinationCodes.SecondsOutOfRange,
					"edit grant seconds must be between " + CoordinationLimits.EditMinSeconds
					+ " and " + CoordinationLimits.EditMaxSeconds);
			}

			string digest = EditDigest(command.Uuid, seconds);
			CoordinationReply replay = ReplayOf(state, command.Session, command.Uuid, digest);
			if (replay != null)
			{
				return replay;
			}

			CoordinationGrant held = state.FindGrantBySession(command.Session, null);
			if (held != null)
			{
				if (held.State == CoordinationLimits.GrantOrphaned)
				{
					return CoordinationReply.Fail(
						CoordinationCodes.OrphanedWriter,
						"the previous edit grant expired; confirm your writers finished with edit-end first");
				}

				if (CoordinationLimits.IsWindowKind(held.Kind))
				{
					return CoordinationReply.Fail(CoordinationCodes.WindowActive, "finish the window before editing files");
				}

				return CoordinationReply.Fail(CoordinationCodes.EditActive, "this session already holds an edit grant");
			}

			CoordinationRequest pending = PendingRequestOf(state, command.Session);
			if (pending != null)
			{
				return CoordinationReply.Fail(
					CoordinationCodes.RequestActive,
					"session already has a pending request " + pending.Id);
			}

			var request = new CoordinationRequest
			{
				Id = NextRequestId(state),
				Uuid = command.Uuid,
				Ticket = state.NextTicket++,
				Session = command.Session,
				Kind = CoordinationLimits.KindEdit,
				PayloadDigest = digest,
				Token = "e-" + command.Nonce,
				State = CoordinationLimits.StateWaiting,
				Seconds = seconds,
				CreatedAtMs = nowMs,
				UpdatedAtMs = nowMs
			};
			state.Requests.Add(request);
			participant.LastSeenMs = nowMs;

			// The grant itself is handed out by the shared issuing pass, so a waiting window in
			// front of this ticket keeps the writer queued instead of racing it.
			TryIssue(state, nowMs);

			CoordinationReply reply = CoordinationReply.Success(
				request.State == CoordinationLimits.StateGranted ? CoordinationCodes.Granted : CoordinationCodes.Waiting);
			reply.Session = command.Session;
			reply.RequestId = request.Id;
			reply.State = request.State;
			reply.Token = TokenFor(state, request);
			reply.Changed = true;
			return reply;
		}

		private CoordinationReply EditEnd(CoordinationState state, CoordinationCommand command, long nowMs)
		{
			if (string.IsNullOrEmpty(command.Token))
			{
				return CoordinationReply.Fail(CoordinationCodes.BadUsage, "edit-end requires --token");
			}

			CoordinationTombstone tombstone = state.FindTombstone(command.Session, TokenKey(command.Token, OpEditEnd));
			if (tombstone != null)
			{
				CoordinationReply replay = CoordinationReply.Success(CoordinationCodes.AlreadyClosed);
				replay.Session = command.Session;
				replay.RequestId = tombstone.RequestId;
				replay.State = tombstone.State;
				replay.Changed = false;
				return replay;
			}

			CoordinationGrant grant = state.FindGrantByToken(command.Token);
			if (grant == null)
			{
				return CoordinationReply.Fail(CoordinationCodes.StaleToken, "no live grant matches this token");
			}

			if (!string.Equals(grant.Session, command.Session, StringComparison.Ordinal))
			{
				return CoordinationReply.Fail(CoordinationCodes.NotHolder, "this token belongs to another session");
			}

			if (grant.Kind != CoordinationLimits.KindEdit)
			{
				return CoordinationReply.Fail(CoordinationCodes.BadUsage, "this token is a window token; use finish");
			}

			string stale = ValidateGrantIdentity(state, grant);
			if (stale != null)
			{
				return CoordinationReply.Fail(CoordinationCodes.StaleToken, stale);
			}

			CloseGrant(state, grant, "edit_end", nowMs);
			AddTombstone(state, grant.Session, TokenKey(command.Token, OpEditEnd), "",
				grant.RequestId, CoordinationLimits.StateClosed, CoordinationCodes.AlreadyClosed, "edit_end", nowMs);

			CoordinationReply reply = CoordinationReply.Success(CoordinationCodes.Ok);
			reply.Session = command.Session;
			reply.RequestId = grant.RequestId;
			reply.State = CoordinationLimits.StateClosed;
			reply.Changed = true;
			return reply;
		}

		private CoordinationReply Renew(CoordinationState state, CoordinationCommand command, long nowMs)
		{
			CoordinationGrant grant = state.FindGrantByToken(command.Token);
			if (grant == null)
			{
				return CoordinationReply.Fail(CoordinationCodes.StaleToken, "no live grant matches this token");
			}

			if (!string.Equals(grant.Session, command.Session, StringComparison.Ordinal))
			{
				return CoordinationReply.Fail(CoordinationCodes.NotHolder, "this token belongs to another session");
			}

			if (grant.Kind != CoordinationLimits.KindEdit)
			{
				return CoordinationReply.Fail(CoordinationCodes.BadUsage, "a window is not renewed; request a new one");
			}

			if (grant.State == CoordinationLimits.GrantOrphaned)
			{
				return CoordinationReply.Fail(
					CoordinationCodes.OrphanedWriter,
					"the grant already expired; confirm your writers finished with edit-end");
			}

			if (HasWaitingWindow(state))
			{
				return CoordinationReply.Fail(
					CoordinationCodes.PauseRequested,
					"a window is waiting; finish the current package and call edit-end");
			}

			int seconds = command.Seconds > 0 ? command.Seconds : CoordinationLimits.EditDefaultSeconds;
			if (seconds < CoordinationLimits.EditMinSeconds || seconds > CoordinationLimits.EditMaxSeconds)
			{
				return CoordinationReply.Fail(
					CoordinationCodes.SecondsOutOfRange,
					"edit grant seconds must be between " + CoordinationLimits.EditMinSeconds
					+ " and " + CoordinationLimits.EditMaxSeconds);
			}

			grant.DeadlineMs = nowMs + seconds * 1000L;
			TouchParticipant(state, grant.Session, nowMs);

			CoordinationReply reply = CoordinationReply.Success(CoordinationCodes.Ok);
			reply.Session = command.Session;
			reply.RequestId = grant.RequestId;
			reply.State = grant.State;
			reply.Token = grant.Token;
			reply.Changed = true;
			return reply;
		}

		// ---------------------------------------------------------------- windows

		private CoordinationReply RequestWindow(CoordinationState state, CoordinationCommand command, long nowMs)
		{
			if (string.IsNullOrEmpty(command.Uuid))
			{
				return CoordinationReply.Fail(CoordinationCodes.BadUsage, "request requires --request <uuid>");
			}

			if (string.IsNullOrEmpty(command.Nonce))
			{
				return CoordinationReply.Fail(CoordinationCodes.BadUsage, "request requires a nonce");
			}

			if (!CoordinationLimits.IsWindowKind(command.Kind))
			{
				return CoordinationReply.Fail(CoordinationCodes.BadUsage, "--kind must be editor or validation");
			}

			CoordinationParticipant participant = state.FindParticipant(command.Session);
			if (participant == null)
			{
				return NotRegistered(command.Session);
			}

			int seconds = command.Seconds > 0 ? command.Seconds : CoordinationLimits.WindowDefaultSeconds;
			if (seconds < CoordinationLimits.WindowMinSeconds || seconds > CoordinationLimits.WindowMaxSeconds)
			{
				return CoordinationReply.Fail(
					CoordinationCodes.SecondsOutOfRange,
					"window seconds must be between " + CoordinationLimits.WindowMinSeconds
					+ " and " + CoordinationLimits.WindowMaxSeconds);
			}

			string planError;
			if (!CoordinationPlanRules.Validate(command.Plan, command.Kind, out planError))
			{
				return CoordinationReply.Fail(CoordinationCodes.PlanInvalid, planError);
			}

			string digest = CoordinationPlanRules.Digest(command.Kind, seconds, command.Plan);
			CoordinationReply replay = ReplayOf(state, command.Session, command.Uuid, digest);
			if (replay != null)
			{
				return replay;
			}

			CoordinationGrant held = state.FindGrantBySession(command.Session, null);
			if (held != null)
			{
				if (held.Kind == CoordinationLimits.KindEdit)
				{
					return CoordinationReply.Fail(
						held.State == CoordinationLimits.GrantOrphaned
							? CoordinationCodes.OrphanedWriter
							: CoordinationCodes.EditActive,
						"close the edit grant before requesting a window");
				}

				return CoordinationReply.Fail(CoordinationCodes.WindowActive, "this session already holds a window");
			}

			CoordinationRequest pending = PendingRequestOf(state, command.Session);
			if (pending != null)
			{
				return CoordinationReply.Fail(
					CoordinationCodes.RequestActive,
					"session already has a pending request " + pending.Id);
			}

			var request = new CoordinationRequest
			{
				Id = NextRequestId(state),
				Uuid = command.Uuid,
				Ticket = state.NextTicket++,
				Session = command.Session,
				Kind = command.Kind,
				PayloadDigest = digest,
				Token = "w-" + command.Nonce,
				Plan = command.Plan,
				State = CoordinationLimits.StateWaiting,
				Seconds = seconds,
				CreatedAtMs = nowMs,
				UpdatedAtMs = nowMs
			};
			state.Requests.Add(request);
			participant.LastSeenMs = nowMs;

			CoordinationReply reply = CoordinationReply.Success(CoordinationCodes.Waiting);
			reply.Session = command.Session;
			reply.RequestId = request.Id;
			reply.State = request.State;
			reply.Blockers = BuildBlockers(state, command.Session, request);
			reply.Changed = true;
			return reply;
		}

		// The editor calls this once it is genuinely free. The engine still re-checks the whole
		// precondition set, because the editor's idea of "free" is a tick old by the time it lands.
		private CoordinationReply WindowConfirm(CoordinationState state, CoordinationCommand command, long nowMs)
		{
			if (state.FindWindowGrant() != null)
			{
				return CoordinationReply.Fail(CoordinationCodes.WindowActive, "a window is already granted");
			}

			foreach (CoordinationGrant grant in state.Grants)
			{
				if (grant.Kind == CoordinationLimits.KindEdit)
				{
					return CoordinationReply.Fail(
						grant.State == CoordinationLimits.GrantOrphaned
							? CoordinationCodes.OrphanedWriter
							: CoordinationCodes.EditActive,
						"session " + grant.Session + " still holds an edit grant");
				}
			}

			CoordinationRequest head = HeadWaitingWindow(state);
			if (head == null)
			{
				return CoordinationReply.Fail(CoordinationCodes.RequestNotFound, "no window request is waiting");
			}

			if (!string.IsNullOrEmpty(command.RequestId) && command.RequestId != head.Id)
			{
				return CoordinationReply.Fail(
					CoordinationCodes.RequestNotFound,
					"request " + command.RequestId + " is not at the head of the queue");
			}

			CoordinationParticipant participant = state.FindParticipant(head.Session);
			if (participant == null)
			{
				head.State = CoordinationLimits.StateRejected;
				head.Reason = CoordinationCodes.NotRegistered;
				head.UpdatedAtMs = nowMs;
				CoordinationReply dropped = CoordinationReply.Success(CoordinationCodes.NotRegistered);
				dropped.RequestId = head.Id;
				dropped.Session = head.Session;
				dropped.State = head.State;
				dropped.Changed = true;
				return dropped;
			}

			var window = new CoordinationGrant
			{
				Session = head.Session,
				RequestId = head.Id,
				Token = head.Token,
				Epoch = state.Epoch,
				ParticipantGeneration = participant.Generation,
				Kind = head.Kind,
				State = CoordinationLimits.GrantActive,
				DeadlineMs = nowMs + head.Seconds * 1000L,
				GrantedAtMs = nowMs
			};
			state.Grants.Add(window);
			head.State = CoordinationLimits.StateGranted;
			head.UpdatedAtMs = nowMs;
			participant.LastSeenMs = nowMs;

			CoordinationReply reply = CoordinationReply.Success(CoordinationCodes.Granted);
			reply.Session = head.Session;
			reply.RequestId = head.Id;
			reply.State = head.State;
			reply.Token = window.Token;
			reply.Changed = true;
			return reply;
		}

		private CoordinationReply Finish(CoordinationState state, CoordinationCommand command, long nowMs)
		{
			CoordinationTombstone tombstone = state.FindTombstone(command.Session, TokenKey(command.Token, OpFinish));
			if (tombstone != null)
			{
				CoordinationReply replay = CoordinationReply.Success(CoordinationCodes.AlreadyClosed);
				replay.Session = command.Session;
				replay.RequestId = tombstone.RequestId;
				replay.State = tombstone.State;
				replay.Changed = false;
				return replay;
			}

			CoordinationGrant grant = state.FindGrantByToken(command.Token);
			if (grant == null)
			{
				return CoordinationReply.Fail(CoordinationCodes.StaleToken, "no live grant matches this token");
			}

			if (!string.Equals(grant.Session, command.Session, StringComparison.Ordinal))
			{
				return CoordinationReply.Fail(CoordinationCodes.NotHolder, "this token belongs to another session");
			}

			if (grant.Kind == CoordinationLimits.KindEdit)
			{
				return CoordinationReply.Fail(CoordinationCodes.BadUsage, "an edit grant is closed with edit-end");
			}

			if (grant.ActiveTaskIds.Length > 0)
			{
				// A running Unity task keeps the window: draining stops new steps, it does not
				// abort what is already on the main thread.
				bool changed = grant.State != CoordinationLimits.GrantDraining;
				grant.State = CoordinationLimits.GrantDraining;
				CoordinationReply draining = CoordinationReply.Success(CoordinationCodes.Draining);
				draining.Session = command.Session;
				draining.RequestId = grant.RequestId;
				draining.State = grant.State;
				draining.Blockers = new[] { "active_task:" + string.Join(",", grant.ActiveTaskIds) };
				draining.Changed = changed;
				return draining;
			}

			CloseGrant(state, grant, "finish", nowMs);
			AddTombstone(state, grant.Session, TokenKey(command.Token, OpFinish), "",
				grant.RequestId, CoordinationLimits.StateClosed, CoordinationCodes.AlreadyClosed, "finish", nowMs);

			CoordinationReply reply = CoordinationReply.Success(CoordinationCodes.Ok);
			reply.Session = command.Session;
			reply.RequestId = grant.RequestId;
			reply.State = CoordinationLimits.StateClosed;
			reply.Changed = true;
			return reply;
		}

		private CoordinationReply Cancel(CoordinationState state, CoordinationCommand command, long nowMs)
		{
			CoordinationRequest request = state.FindRequestByUuid(command.Session, command.Uuid);
			if (request == null)
			{
				CoordinationTombstone tombstone = state.FindTombstone(command.Session, UuidKey(command.Uuid));
				if (tombstone != null)
				{
					CoordinationReply replay = CoordinationReply.Success(CoordinationCodes.AlreadyClosed);
					replay.Session = command.Session;
					replay.RequestId = tombstone.RequestId;
					replay.State = tombstone.State;
					replay.Changed = false;
					return replay;
				}

				return CoordinationReply.Fail(CoordinationCodes.RequestNotFound, "no request with this uuid");
			}

			if (request.State == CoordinationLimits.StateGranted)
			{
				return CoordinationReply.Fail(
					CoordinationCodes.WindowActive,
					"the request was already granted; close it with finish or edit-end");
			}

			if (request.IsTerminal)
			{
				CoordinationReply same = CoordinationReply.Success(CoordinationCodes.AlreadyClosed);
				same.Session = command.Session;
				same.RequestId = request.Id;
				same.State = request.State;
				same.Changed = false;
				return same;
			}

			request.State = CoordinationLimits.StateCanceled;
			request.Reason = "canceled";
			request.UpdatedAtMs = nowMs;
			AddTombstone(state, command.Session, UuidKey(command.Uuid), request.PayloadDigest,
				request.Id, CoordinationLimits.StateCanceled, CoordinationCodes.AlreadyClosed, "canceled", nowMs);

			CoordinationReply reply = CoordinationReply.Success(CoordinationCodes.Ok);
			reply.Session = command.Session;
			reply.RequestId = request.Id;
			reply.State = request.State;
			reply.Changed = true;
			return reply;
		}

		private CoordinationReply Abandon(CoordinationState state, CoordinationCommand command, long nowMs)
		{
			if (string.IsNullOrEmpty(command.TargetSession) || string.IsNullOrEmpty(command.Reason))
			{
				return CoordinationReply.Fail(
					CoordinationCodes.BadUsage,
					"abandon requires --target-session and --reason");
			}

			CoordinationParticipant participant = state.FindParticipant(command.TargetSession);
			if (participant == null)
			{
				return NotRegistered(command.TargetSession);
			}

			foreach (CoordinationGrant grant in state.Grants)
			{
				if (string.Equals(grant.Session, command.TargetSession, StringComparison.Ordinal)
					&& grant.ActiveTaskIds.Length > 0)
				{
					return CoordinationReply.Fail(
						CoordinationCodes.ActiveTask,
						"session " + command.TargetSession + " still has a running Unity task");
				}
			}

			// Only the target loses its rights: another writer's live grant is untouched, and the
			// shared Epoch stays put so nobody else's token is invalidated.
			for (int i = state.Grants.Count - 1; i >= 0; i--)
			{
				if (string.Equals(state.Grants[i].Session, command.TargetSession, StringComparison.Ordinal))
				{
					state.Grants.RemoveAt(i);
				}
			}

			foreach (CoordinationRequest request in state.Requests)
			{
				if (string.Equals(request.Session, command.TargetSession, StringComparison.Ordinal) && !request.IsTerminal)
				{
					request.State = CoordinationLimits.StateRejected;
					request.Reason = "abandoned: " + command.Reason;
					request.UpdatedAtMs = nowMs;
				}
			}

			RemoveTombstones(state, command.TargetSession);
			participant.Generation++;
			participant.Paths = new string[0];
			participant.Lifecycle = CoordinationLimits.LifecycleIdle;

			CoordinationReply reply = CoordinationReply.Success(CoordinationCodes.Ok);
			reply.Session = command.TargetSession;
			reply.State = CoordinationLimits.LifecycleIdle;
			reply.Changed = true;
			return reply;
		}

		// ---------------------------------------------------------------- steps

		private CoordinationReply StepBegin(CoordinationState state, CoordinationCommand command, long nowMs)
		{
			if (string.IsNullOrEmpty(command.StepId) || string.IsNullOrEmpty(command.TaskId))
			{
				return CoordinationReply.Fail(CoordinationCodes.BadUsage, "step-begin requires a step id and a task id");
			}

			CoordinationGrant grant = state.FindGrantByToken(command.Token);
			if (grant == null)
			{
				return CoordinationReply.Fail(CoordinationCodes.StaleToken, "no live window matches this token");
			}

			if (!string.Equals(grant.Session, command.Session, StringComparison.Ordinal))
			{
				return CoordinationReply.Fail(CoordinationCodes.NotHolder, "this token belongs to another session");
			}

			if (!CoordinationLimits.IsWindowKind(grant.Kind))
			{
				return CoordinationReply.Fail(CoordinationCodes.WindowNotGranted, "this token is not a window token");
			}

			string stale = ValidateGrantIdentity(state, grant);
			if (stale != null)
			{
				return CoordinationReply.Fail(CoordinationCodes.StaleToken, stale);
			}

			CoordinationStepUse used = grant.FindStep(command.StepId);
			if (used != null)
			{
				if (string.Equals(used.TaskId, command.TaskId, StringComparison.Ordinal)
					&& used.State == CoordinationLimits.StepReserved)
				{
					// The same task id after a domain reload rejoins its own reservation.
					CoordinationReply rejoin = CoordinationReply.Success(CoordinationCodes.StepAttached);
					rejoin.Session = command.Session;
					rejoin.RequestId = grant.RequestId;
					rejoin.State = grant.State;
					rejoin.Changed = false;
					return rejoin;
				}

				return CoordinationReply.Fail(
					CoordinationCodes.StepConsumed,
					"step " + command.StepId + " was already consumed by task " + used.TaskId
					+ " (" + used.State + (string.IsNullOrEmpty(used.Reason) ? "" : ": " + used.Reason) + ")");
			}

			if (grant.State != CoordinationLimits.GrantActive)
			{
				return CoordinationReply.Fail(
					CoordinationCodes.WindowDraining,
					"the window is " + grant.State + " and accepts no new steps");
			}

			CoordinationRequest request = state.FindRequest(grant.RequestId);
			CoordinationStep planned = request != null ? request.Plan.Find(command.StepId) : null;
			if (planned == null)
			{
				return CoordinationReply.Fail(CoordinationCodes.StepUnknown, "step " + command.StepId + " is not in the plan");
			}

			string mismatch;
			if (!CoordinationPlanRules.Matches(planned, command.Actual, out mismatch))
			{
				return CoordinationReply.Fail(CoordinationCodes.StepMismatch, mismatch);
			}

			grant.Steps.Add(new CoordinationStepUse
			{
				StepId = command.StepId,
				TaskId = command.TaskId,
				State = CoordinationLimits.StepReserved,
				Reason = ""
			});
			grant.ActiveTaskIds = CoordinationText.Add(grant.ActiveTaskIds, command.TaskId);

			CoordinationReply reply = CoordinationReply.Success(CoordinationCodes.Ok);
			reply.Session = command.Session;
			reply.RequestId = grant.RequestId;
			reply.State = grant.State;
			reply.Changed = true;
			return reply;
		}

		private CoordinationReply StepFinish(CoordinationState state, CoordinationCommand command, long nowMs, string stepState)
		{
			CoordinationGrant grant = state.FindGrantByToken(command.Token);
			if (grant == null)
			{
				// The window may already be gone; a late terminal report is not an error.
				CoordinationReply gone = CoordinationReply.Success(CoordinationCodes.AlreadyClosed);
				gone.Session = command.Session;
				gone.Changed = false;
				return gone;
			}

			CoordinationStepUse used = grant.FindStep(command.StepId);
			if (used == null)
			{
				return CoordinationReply.Fail(CoordinationCodes.StepUnknown, "step " + command.StepId + " was never reserved");
			}

			bool changed = used.State != stepState || CoordinationText.Contains(grant.ActiveTaskIds, used.TaskId);
			used.State = stepState;
			used.Reason = command.Reason;
			grant.ActiveTaskIds = CoordinationText.Remove(grant.ActiveTaskIds, used.TaskId);

			CoordinationReply reply = CoordinationReply.Success(CoordinationCodes.Ok);
			reply.Session = grant.Session;
			reply.RequestId = grant.RequestId;
			reply.State = grant.State;
			reply.Changed = changed;
			return reply;
		}

		private CoordinationReply Incarnation(CoordinationState state, CoordinationCommand command, long nowMs)
		{
			if (string.IsNullOrEmpty(command.Nonce))
			{
				return CoordinationReply.Fail(CoordinationCodes.BadUsage, "editor-incarnation requires a nonce");
			}

			if (string.Equals(state.EditorIncarnation, command.Nonce, StringComparison.Ordinal))
			{
				CoordinationReply same = CoordinationReply.Success(CoordinationCodes.Ok);
				same.Changed = false;
				return same;
			}

			bool first = string.IsNullOrEmpty(state.EditorIncarnation);
			state.EditorIncarnation = command.Nonce;

			if (!first)
			{
				// A real editor restart. Registrations, scopes and edit grants survive it; windows
				// do not, because the tasks they were running died with the process.
				foreach (CoordinationGrant grant in state.Grants)
				{
					if (CoordinationLimits.IsWindowKind(grant.Kind))
					{
						grant.State = CoordinationLimits.GrantInterrupted;
					}
				}
			}

			CoordinationReply reply = CoordinationReply.Success(CoordinationCodes.Ok);
			reply.Changed = true;
			return reply;
		}

		// ---------------------------------------------------------------- sweeping

		private static bool Sweep(CoordinationState state, long nowMs, bool issue)
		{
			bool changed = false;

			for (int i = state.Grants.Count - 1; i >= 0; i--)
			{
				CoordinationGrant grant = state.Grants[i];
				if (grant.Kind == CoordinationLimits.KindEdit)
				{
					if (grant.State == CoordinationLimits.GrantActive && nowMs > grant.DeadlineMs)
					{
						grant.State = CoordinationLimits.GrantOrphaned;
						changed = true;
					}

					continue;
				}

				if (grant.State == CoordinationLimits.GrantActive && nowMs > grant.DeadlineMs)
				{
					grant.State = CoordinationLimits.GrantDraining;
					changed = true;
				}

				if (grant.State != CoordinationLimits.GrantActive && grant.ActiveTaskIds.Length == 0)
				{
					CloseGrant(state, grant, grant.State == CoordinationLimits.GrantInterrupted ? "editor_restart" : "expired", nowMs);
					changed = true;
				}
			}

			if (issue)
			{
				changed |= TryIssue(state, nowMs);
			}

			changed |= RefreshLifecycle(state);
			changed |= TrimTombstones(state);
			return changed;
		}

		// The single place a right is handed out. Windows are confirmed by the editor; edits are
		// released here, in ticket order, and never in front of an older waiting window.
		private static bool TryIssue(CoordinationState state, long nowMs)
		{
			if (state.FindWindowGrant() != null)
			{
				return false;
			}

			long windowTicket = FirstWaitingWindowTicket(state);
			List<CoordinationRequest> waiting = WaitingEdits(state);
			bool changed = false;

			foreach (CoordinationRequest request in waiting)
			{
				if (windowTicket >= 0 && request.Ticket > windowTicket)
				{
					continue;
				}

				CoordinationParticipant participant = state.FindParticipant(request.Session);
				if (participant == null)
				{
					continue;
				}

				if (state.FindGrantBySession(request.Session, null) != null)
				{
					continue;
				}

				state.Grants.Add(new CoordinationGrant
				{
					Session = request.Session,
					RequestId = request.Id,
					Token = request.Token,
					Epoch = state.Epoch,
					ParticipantGeneration = participant.Generation,
					Kind = CoordinationLimits.KindEdit,
					State = CoordinationLimits.GrantActive,
					DeadlineMs = nowMs + request.Seconds * 1000L,
					GrantedAtMs = nowMs
				});

				request.State = CoordinationLimits.StateGranted;
				request.UpdatedAtMs = nowMs;
				changed = true;
			}

			return changed;
		}

		private static bool RefreshLifecycle(CoordinationState state)
		{
			bool changed = false;
			foreach (CoordinationParticipant participant in state.Participants)
			{
				CoordinationGrant grant = state.FindGrantBySession(participant.Session, null);
				string lifecycle = CoordinationLimits.LifecycleIdle;
				if (grant != null)
				{
					if (grant.State == CoordinationLimits.GrantOrphaned)
					{
						lifecycle = CoordinationLimits.LifecycleOrphaned;
					}
					else if (grant.Kind == CoordinationLimits.KindEdit)
					{
						lifecycle = CoordinationLimits.LifecycleWriting;
					}
					else
					{
						lifecycle = CoordinationLimits.LifecycleWindow;
					}
				}

				if (participant.Lifecycle != lifecycle)
				{
					participant.Lifecycle = lifecycle;
					changed = true;
				}
			}

			return changed;
		}

		private static bool TrimTombstones(CoordinationState state)
		{
			if (state.Tombstones.Count <= CoordinationLimits.MaxTombstones)
			{
				return false;
			}

			state.Tombstones.Sort(delegate(CoordinationTombstone left, CoordinationTombstone right)
			{
				return left.ClosedAtMs.CompareTo(right.ClosedAtMs);
			});

			while (state.Tombstones.Count > CoordinationLimits.MaxTombstones)
			{
				state.Tombstones.RemoveAt(0);
			}

			return true;
		}

		// ---------------------------------------------------------------- helpers

		private static void CloseGrant(CoordinationState state, CoordinationGrant grant, string reason, long nowMs)
		{
			state.Grants.Remove(grant);
			CoordinationRequest request = state.FindRequest(grant.RequestId);
			if (request != null && !request.IsTerminal)
			{
				request.State = CoordinationLimits.StateClosed;
				request.Reason = reason;
				request.UpdatedAtMs = nowMs;
				AddTombstone(state, request.Session, UuidKey(request.Uuid), request.PayloadDigest,
					request.Id, CoordinationLimits.StateClosed, CoordinationCodes.AlreadyClosed, reason, nowMs);
			}
		}

		private static void AddTombstone(
			CoordinationState state,
			string session,
			string key,
			string digest,
			string requestId,
			string requestState,
			string code,
			string reason,
			long nowMs)
		{
			CoordinationTombstone existing = state.FindTombstone(session, key);
			if (existing != null)
			{
				return;
			}

			state.Tombstones.Add(new CoordinationTombstone
			{
				Session = session,
				Key = key,
				PayloadDigest = digest,
				RequestId = requestId,
				State = requestState,
				Code = code,
				Reason = reason,
				ClosedAtMs = nowMs
			});
		}

		// Replay protection for the uuid-carrying mutations. The same uuid with the same payload
		// returns the existing answer; the same uuid with a different payload is a conflict.
		private CoordinationReply ReplayOf(CoordinationState state, string session, string uuid, string digest)
		{
			CoordinationRequest existing = state.FindRequestByUuid(session, uuid);
			if (existing != null)
			{
				if (!string.Equals(existing.PayloadDigest, digest, StringComparison.Ordinal))
				{
					return CoordinationReply.Fail(
						CoordinationCodes.RequestConflict,
						"request uuid " + uuid + " was already used with a different payload");
				}

				CoordinationReply reply = CoordinationReply.Success(
					existing.State == CoordinationLimits.StateGranted ? CoordinationCodes.Granted : CoordinationCodes.Waiting);
				reply.Session = session;
				reply.RequestId = existing.Id;
				reply.State = existing.State;
				reply.Token = TokenFor(state, existing);
				reply.Changed = false;
				return reply;
			}

			CoordinationTombstone tombstone = state.FindTombstone(session, UuidKey(uuid));
			if (tombstone == null)
			{
				return null;
			}

			if (!string.IsNullOrEmpty(tombstone.PayloadDigest)
				&& !string.Equals(tombstone.PayloadDigest, digest, StringComparison.Ordinal))
			{
				return CoordinationReply.Fail(
					CoordinationCodes.RequestConflict,
					"request uuid " + uuid + " was already used with a different payload");
			}

			CoordinationReply closed = CoordinationReply.Success(CoordinationCodes.AlreadyClosed);
			closed.Session = session;
			closed.RequestId = tombstone.RequestId;
			closed.State = tombstone.State;
			closed.Changed = false;
			return closed;
		}

		private static string TokenFor(CoordinationState state, CoordinationRequest request)
		{
			if (request == null || request.State != CoordinationLimits.StateGranted)
			{
				return "";
			}

			CoordinationGrant grant = state.FindGrantByToken(request.Token);
			return grant != null ? grant.Token : "";
		}

		private static string ValidateGrantIdentity(CoordinationState state, CoordinationGrant grant)
		{
			if (!string.Equals(grant.Epoch, state.Epoch, StringComparison.Ordinal))
			{
				return "the coordination epoch changed since this token was issued";
			}

			CoordinationParticipant participant = state.FindParticipant(grant.Session);
			if (participant == null)
			{
				return "the session that owns this token is no longer registered";
			}

			if (participant.Generation != grant.ParticipantGeneration)
			{
				return "this token was issued before the session was recovered";
			}

			return null;
		}

		private static CoordinationReply NotRegistered(string session)
		{
			return CoordinationReply.Fail(
				CoordinationCodes.NotRegistered,
				"session " + session + " is not registered; run coord register first");
		}

		private static bool Conflicts(
			CoordinationState state,
			string session,
			string[] paths,
			out string conflictSession,
			out string conflictPath)
		{
			conflictSession = "";
			conflictPath = "";
			foreach (CoordinationParticipant other in state.Participants)
			{
				if (string.Equals(other.Session, session, StringComparison.Ordinal))
				{
					continue;
				}

				string mine;
				string theirs;
				if (CoordinationScope.AnyOverlap(paths, other.Paths, out mine, out theirs))
				{
					conflictSession = other.Session;
					conflictPath = mine;
					return true;
				}
			}

			return false;
		}

		private static bool IsBusy(CoordinationState state, string session)
		{
			if (state.FindGrantBySession(session, null) != null)
			{
				return true;
			}

			return PendingRequestOf(state, session) != null;
		}

		private static CoordinationRequest PendingRequestOf(CoordinationState state, string session)
		{
			foreach (CoordinationRequest request in state.Requests)
			{
				if (string.Equals(request.Session, session, StringComparison.Ordinal) && !request.IsTerminal)
				{
					return request;
				}
			}

			return null;
		}

		private static CoordinationRequest ResolveRequest(CoordinationState state, string session, string requestId)
		{
			if (string.IsNullOrEmpty(requestId))
			{
				return PendingRequestOf(state, session);
			}

			CoordinationRequest byId = state.FindRequest(requestId);
			if (byId != null)
			{
				return byId;
			}

			return state.FindRequestByUuid(session, requestId);
		}

		private static List<CoordinationRequest> WaitingEdits(CoordinationState state)
		{
			var waiting = new List<CoordinationRequest>();
			foreach (CoordinationRequest request in state.Requests)
			{
				if (request.Kind == CoordinationLimits.KindEdit && request.State == CoordinationLimits.StateWaiting)
				{
					waiting.Add(request);
				}
			}

			waiting.Sort(delegate(CoordinationRequest left, CoordinationRequest right)
			{
				return left.Ticket.CompareTo(right.Ticket);
			});
			return waiting;
		}

		private static long FirstWaitingWindowTicket(CoordinationState state)
		{
			long ticket = -1;
			foreach (CoordinationRequest request in state.Requests)
			{
				if (!CoordinationLimits.IsWindowKind(request.Kind) || request.State != CoordinationLimits.StateWaiting)
				{
					continue;
				}

				if (ticket < 0 || request.Ticket < ticket)
				{
					ticket = request.Ticket;
				}
			}

			return ticket;
		}

		private static CoordinationRequest HeadWaitingWindow(CoordinationState state)
		{
			CoordinationRequest head = null;
			foreach (CoordinationRequest request in state.Requests)
			{
				if (!CoordinationLimits.IsWindowKind(request.Kind) || request.State != CoordinationLimits.StateWaiting)
				{
					continue;
				}

				if (head == null || request.Ticket < head.Ticket)
				{
					head = request;
				}
			}

			return head;
		}

		private static bool HasWaitingWindow(CoordinationState state)
		{
			return FirstWaitingWindowTicket(state) >= 0;
		}

		public static string[] BuildBlockers(CoordinationState state, string session, CoordinationRequest request)
		{
			var blockers = new List<string>();

			foreach (CoordinationGrant grant in state.Grants)
			{
				bool mine = string.Equals(grant.Session, session, StringComparison.Ordinal);
				if (grant.State == CoordinationLimits.GrantOrphaned)
				{
					blockers.Add("orphaned_writer:" + grant.Session);
					continue;
				}

				if (mine)
				{
					continue;
				}

				blockers.Add((grant.Kind == CoordinationLimits.KindEdit ? "edit_active:" : "window_active:") + grant.Session);
			}

			if (!string.IsNullOrEmpty(session) && HasWaitingWindow(state))
			{
				CoordinationGrant own = state.FindGrantBySession(session, CoordinationLimits.KindEdit);
				if (own != null && own.State == CoordinationLimits.GrantActive)
				{
					blockers.Add(CoordinationCodes.PauseRequested);
				}
			}

			if (request != null && request.State == CoordinationLimits.StateWaiting)
			{
				int ahead = 0;
				foreach (CoordinationRequest other in state.Requests)
				{
					if (other.State == CoordinationLimits.StateWaiting && other.Ticket < request.Ticket)
					{
						ahead++;
					}
				}

				if (ahead > 0)
				{
					blockers.Add("waiting_ahead:" + ahead);
				}
			}

			return blockers.ToArray();
		}

		private static void TouchParticipant(CoordinationState state, string session, long nowMs)
		{
			CoordinationParticipant participant = state.FindParticipant(session);
			if (participant != null)
			{
				participant.LastSeenMs = nowMs;
			}
		}

		private static void RemoveWhereSession(List<CoordinationRequest> requests, string session)
		{
			for (int i = requests.Count - 1; i >= 0; i--)
			{
				if (string.Equals(requests[i].Session, session, StringComparison.Ordinal))
				{
					requests.RemoveAt(i);
				}
			}
		}

		private static void RemoveTombstones(CoordinationState state, string session)
		{
			for (int i = state.Tombstones.Count - 1; i >= 0; i--)
			{
				if (string.Equals(state.Tombstones[i].Session, session, StringComparison.Ordinal))
				{
					state.Tombstones.RemoveAt(i);
				}
			}
		}

		private static string NextRequestId(CoordinationState state)
		{
			long number = state.NextRequestNumber++;
			return "R" + number.ToString("0000");
		}

		private static string EditDigest(string uuid, int seconds)
		{
			return CoordinationDigest.Sha256("edit\n" + uuid + "\n" + seconds);
		}

		private static string UuidKey(string uuid)
		{
			return "uuid:" + uuid;
		}

		private static string TokenKey(string token, string op)
		{
			return "token:" + op + ":" + token;
		}

		private static bool SameSet(string[] left, string[] right)
		{
			if (left == null)
			{
				left = new string[0];
			}

			if (right == null)
			{
				right = new string[0];
			}

			if (left.Length != right.Length)
			{
				return false;
			}

			foreach (string item in left)
			{
				if (!CoordinationText.Contains(right, item))
				{
					return false;
				}
			}

			return true;
		}

		private static CoordinationReply Stamp(CoordinationState state, CoordinationReply reply)
		{
			reply.ProjectId = state.ProjectId;
			reply.Epoch = state.Epoch;
			reply.Revision = state.Revision;
			reply.Blockers = CoordinationText.Safe(reply.Blockers);
			return reply;
		}
	}
}
