using System;
using AgentBridge.Coordination;
using UnityEditor;
using UnityEngine;

namespace AgentBridge
{
	// The editor half of coordination-v1. It owns the one store instance in this process, takes at
	// most one non-blocking lock attempt per tick, and never waits for anything on the main thread.
	public static class CoordinationEditorAdapter
	{
		public const string IncarnationKey = "AgentBridge_CoordinationIncarnation";

		private const double SnapshotIntervalSeconds = 0.5;
		private const double TickIntervalSeconds = 0.5;

		private static CoordinationFileStore _store;
		private static CoordinationEngine _engine;
		private static bool _resolved;
		private static string _unavailableReason = "";
		private static CoordinationState _snapshot;
		private static double _lastSnapshotTime = double.MinValue;
		private static double _lastTickTime = double.MinValue;
		private static string _incarnation;
		private static bool _incarnationPublished;
		private static string _lastWindowSession = "";

		public static string UnavailableReason
		{
			get
			{
				Resolve();
				return _unavailableReason;
			}
		}

		public static bool Available
		{
			get
			{
				Resolve();
				return _store != null;
			}
		}

		// A project nobody registered in behaves exactly as it did before coordination existed.
		public static bool Coordinated
		{
			get
			{
				CoordinationState state = Snapshot;
				return state != null && state.Participants.Count > 0;
			}
		}

		public static CoordinationState Snapshot
		{
			get
			{
				Resolve();
				if (_store == null)
				{
					return null;
				}

				double now = EditorApplication.timeSinceStartup;
				if (_snapshot != null && now - _lastSnapshotTime < SnapshotIntervalSeconds)
				{
					return _snapshot;
				}

				_lastSnapshotTime = now;

				// The common case is a project nobody registered in. Asking the store first keeps
				// the coordinator tick from raising a "no state" exception twice a second.
				if (!_store.Exists)
				{
					_snapshot = null;
					return null;
				}

				try
				{
					_snapshot = _store.Read();
				}
				catch (CoordinationStoreException)
				{
					_snapshot = null;
				}
				catch (Exception)
				{
					_snapshot = null;
				}

				return _snapshot;
			}
		}

		public static string CoordinationSummary()
		{
			CoordinationState state = Snapshot;
			if (state == null)
			{
				return "";
			}

			CoordinationGrant window = state.FindWindowGrant();
			string holder = window != null ? window.Session : "";
			return "rev=" + state.Revision
				+ ";participants=" + state.Participants.Count
				+ ";grants=" + state.Grants.Count
				+ (string.IsNullOrEmpty(holder) ? "" : ";window=" + holder);
		}

		// Called from the coordinator update. Publishes this editor incarnation once, then keeps
		// the queue moving: expiry, and the window confirmation that only the editor can give.
		public static void Tick(bool editorFree)
		{
			Resolve();
			if (_store == null)
			{
				return;
			}

			double now = EditorApplication.timeSinceStartup;
			if (now - _lastTickTime < TickIntervalSeconds)
			{
				return;
			}

			_lastTickTime = now;

			if (!_incarnationPublished)
			{
				if (!_store.Exists)
				{
					// No coordinator in this project yet. Creating the state just to stamp an
					// incarnation would turn every idle editor into a registered project.
					return;
				}

				CoordinationCommand stamp = NewCommand(CoordinationEngine.OpIncarnation);
				stamp.Nonce = Incarnation;

				CoordinationReply reply;
				if (TryApply(stamp, out reply) && reply.Ok)
				{
					_incarnationPublished = true;
					RecoverInterruptedSteps();
				}

				return;
			}

			if (!Coordinated)
			{
				return;
			}

			CoordinationState state = Snapshot;
			if (state == null)
			{
				return;
			}

			ReleaseLeaseOfClosedWindow(state);
			string cycle = CompileTaskExecutor.LastCycleId;
			string compilation = CompileTaskExecutor.LastCycleStatus;
			if (!string.IsNullOrEmpty(cycle) && cycle != state.CompilerCycleId && (compilation == "success" || compilation == "compiler_error"))
			{
				CoordinationCommand compiler = NewCommand(CoordinationEngine.OpCompilerState);
				compiler.TaskId = cycle; compiler.Reason = compilation;
				CoordinationReply compilerReply;
				if (!TryApply(compiler, out compilerReply)) return;
				state = Snapshot;
			}
			if (editorFree && state.FindWindowGrant() != null)
			{
				CoordinationBatchPump.Tick();
				state = Snapshot;
				ReleaseLeaseOfClosedWindow(state);
			}

			bool wantsSweep = NeedsSweep(state, CoordinationSystemClock.Instance.UtcNowMs);
			bool wantsWindow = editorFree && HasWaitingWindow(state) && state.FindWindowGrant() == null;

			if (!wantsSweep && !wantsWindow)
			{
				return;
			}

			CoordinationReply result;
			TryApply(NewCommand(wantsWindow ? CoordinationEngine.OpWindowConfirm : CoordinationEngine.OpSweep), out result);
			if (result != null && result.Ok && result.Code == CoordinationCodes.Granted)
			{
				CoordinationBatchPump.Tick();
				TelemetryLog.Write("coord_window", result.Session, result.RequestId, new[]
				{
					TelemetryField.Text("What", "granted"),
					TelemetryField.Number("Revision", result.Revision)
				});
			}
		}

		// One non-blocking attempt. A busy lock is not a failure: the editor returns to its update
		// and tries again on the next tick.
		public static bool TryApply(CoordinationCommand command, out CoordinationReply reply)
		{
			reply = null;
			Resolve();
			if (_store == null)
			{
				return false;
			}

			ICoordinationTransaction transaction;
			try
			{
				if (!_store.TryBegin(out transaction))
				{
					return false;
				}
			}
			catch (CoordinationStoreException exception)
			{
				reply = CoordinationReply.Fail(exception.Code, exception.Message);
				return true;
			}

			using (transaction)
			{
				try
				{
					reply = _engine.Apply(transaction.State, command, CoordinationSystemClock.Instance.UtcNowMs);
				}
				catch (Exception exception)
				{
					reply = CoordinationReply.Fail(CoordinationCodes.Corrupt, exception.Message);
					return true;
				}

				if (reply.Changed)
				{
					transaction.Commit();
					_snapshot = transaction.State;
					_lastSnapshotTime = EditorApplication.timeSinceStartup;
				}
			}

			return true;
		}

		public static CoordinationCommand NewCommand(string op)
		{
			return new CoordinationCommand
			{
				Op = op,
				Nonce = Guid.NewGuid().ToString("N")
			};
		}

		public static void Invalidate()
		{
			_snapshot = null;
			_lastSnapshotTime = double.MinValue;
		}

		// Ending a coordination window also hands the scheduler lease back: otherwise the closed
		// window would keep its scenes and its slice until the idle timeout.
		private static void ReleaseLeaseOfClosedWindow(CoordinationState state)
		{
			CoordinationGrant window = state.FindWindowGrant();
			string session = window != null ? window.Session : "";
			if (session == _lastWindowSession)
			{
				return;
			}

			if (!string.IsNullOrEmpty(_lastWindowSession) && string.IsNullOrEmpty(session))
			{
				AgentSessionScheduler.Release(_lastWindowSession);
			}

			_lastWindowSession = session;
		}

		// Tasks recorded by the previous editor process died with it. Reporting them as failed is
		// what lets the interrupted window close instead of blocking the queue forever.
		private static void RecoverInterruptedSteps()
		{
			CoordinationState state = _store.Read();
			foreach (CoordinationGrant grant in state.Grants)
			{
				if (grant.State != CoordinationLimits.GrantInterrupted)
				{
					continue;
				}

				foreach (string taskId in grant.ActiveTaskIds)
				{
					CoordinationStepUse use = null;
					foreach (CoordinationStepUse step in grant.Steps)
					{
						if (step.TaskId == taskId)
						{
							use = step;
							break;
						}
					}

					if (use == null)
					{
						continue;
					}

					CoordinationCommand fail = NewCommand(CoordinationEngine.OpStepFail);
					fail.Session = grant.Session;
					fail.Token = grant.Token;
					fail.StepId = use.StepId;
					fail.TaskId = taskId;
					fail.Reason = "editor_restart";

					CoordinationReply reply;
					TryApply(fail, out reply);
				}
			}
		}

		private static bool NeedsSweep(CoordinationState state, long nowMs)
		{
			foreach (CoordinationGrant grant in state.Grants)
			{
				CoordinationRequest request = state.FindRequest(grant.RequestId);
				if (CoordinationLimits.IsWindowKind(grant.Kind) && request != null && !request.Automatic) return true;
				if (grant.State == CoordinationLimits.GrantActive && nowMs > grant.DeadlineMs)
				{
					return true;
				}

				if (grant.State != CoordinationLimits.GrantActive && grant.ActiveTaskIds.Length == 0)
				{
					return true;
				}
			}

			return false;
		}

		private static bool HasWaitingWindow(CoordinationState state)
		{
			foreach (CoordinationRequest request in state.Requests)
			{
				if (CoordinationLimits.IsWindowKind(request.Kind) && request.State == CoordinationLimits.StateWaiting)
				{
					return true;
				}
			}

			return false;
		}

		private static void Resolve()
		{
			if (_resolved)
			{
				return;
			}

			_resolved = true;
			_engine = new CoordinationEngine();

			// SessionState survives a domain reload and dies with the editor process, which is
			// exactly the line coordination-v1 draws between a reload and a restart.
			_incarnation = SessionState.GetString(IncarnationKey, "");
			if (string.IsNullOrEmpty(_incarnation))
			{
				_incarnation = Guid.NewGuid().ToString("N");
				SessionState.SetString(IncarnationKey, _incarnation);
			}

			string canonical;
			string error;
			if (!CoordinationPathPolicy.TryResolveProjectRoot(BridgePaths.ProjectRoot, out canonical, out error))
			{
				_unavailableReason = error;
				return;
			}

			_store = new CoordinationFileStore(
				CoordinationPathPolicy.CoordinationRoot(canonical),
				CoordinationUnityCodec.Instance,
				CoordinationSystemClock.Instance,
				ProjectIdentity.Ensure(),
				null);
		}

		// The editor process incarnation. A domain reload keeps it; only a new editor process
		// produces a new one, and that is what invalidates a window.
		public static string Incarnation
		{
			get
			{
				Resolve();
				return _incarnation ?? "";
			}
		}
	}
}
