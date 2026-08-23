using System;
using System.Collections.Generic;

namespace AgentBridge
{
	public static class AgentSessionScheduler
	{
		private const string AnonymousPrefix = "anon:";
		private const int MaxContexts = 8;
		private const int MaxNotes = 4;
		private const int MaxNoteLength = 200;

		public static string EffectiveSessionId(string agentSessionId, string taskId)
		{
			if (!string.IsNullOrEmpty(agentSessionId))
			{
				return agentSessionId;
			}

			return AnonymousPrefix + taskId;
		}

		public static bool IsAnonymous(string effectiveSessionId)
		{
			return !string.IsNullOrEmpty(effectiveSessionId)
				&& effectiveSessionId.StartsWith(AnonymousPrefix, StringComparison.Ordinal);
		}

		public static bool TryPick(
			List<PendingTaskInfo> pending,
			DateTime nowUtc,
			out PendingTaskInfo next,
			out bool holderChanged,
			out string previousHolder)
		{
			next = null;
			holderChanged = false;
			previousHolder = "";

			if (pending == null || pending.Count == 0)
			{
				return false;
			}

			SchedulerState state = SchedulerStateStore.State;
			string holder = state.HolderAgentSessionId ?? "";

			PendingTaskInfo foreignRelease = OldestForeignRelease(pending, holder);
			if (foreignRelease != null)
			{
				// A release from a session that does not hold the lease changes nothing in the
				// editor: it answers right away instead of rotating the holder out of its slice.
				next = foreignRelease;
				holderChanged = false;
				return true;
			}

			if (holder.Length == 0)
			{
				next = Oldest(pending, null, false);
				holderChanged = true;
				return next != null;
			}

			PendingTaskInfo holderTask = Oldest(pending, holder, true);
			PendingTaskInfo foreignTask = Oldest(pending, holder, false);

			if (foreignTask == null)
			{
				if (!string.IsNullOrEmpty(state.ContentionStartedUtc))
				{
					state.ContentionStartedUtc = "";
					SchedulerStateStore.Save();
				}

				if (holderTask == null)
				{
					return false;
				}

				next = holderTask;
				holderChanged = false;
				return true;
			}

			if (string.IsNullOrEmpty(state.ContentionStartedUtc))
			{
				state.ContentionStartedUtc = nowUtc.ToString("o");
				SchedulerStateStore.Save();
			}

			bool rotate = holderTask == null || SliceExpired(state.ContentionStartedUtc, nowUtc);
			if (!rotate)
			{
				next = holderTask;
				holderChanged = false;
				return true;
			}

			// The rotation target is the session owning the oldest foreign task; state stays
			// untouched here so a failed context save can leave the current holder in place.
			next = Oldest(pending, foreignTask.EffectiveSessionId, true);
			holderChanged = true;
			previousHolder = holder;
			return next != null;
		}

		public static void CommitStart(PendingTaskInfo task, bool holderChanged)
		{
			SchedulerState state = SchedulerStateStore.State;
			state.HolderLastActivityUtc = DateTime.UtcNow.ToString("o");

			if (holderChanged)
			{
				state.HolderAgentSessionId = task.EffectiveSessionId;
				state.HolderSinceUtc = DateTime.UtcNow.ToString("o");
				state.ContentionStartedUtc = "";
				state.HolderContextRestored = FindContext(task.EffectiveSessionId) == null;
			}

			SchedulerStateStore.Save();
		}

		public static void OnTaskFinished(string effectiveSessionId, DateTime nowUtc)
		{
			SchedulerState state = SchedulerStateStore.State;
			state.HolderLastActivityUtc = nowUtc.ToString("o");

			// An anonymous session is a single task: keeping the lease after it finished would
			// stall every named session until the idle timeout.
			if (IsAnonymous(effectiveSessionId) && IsHolder(state, effectiveSessionId))
			{
				ClearHolder(state);
			}

			SchedulerStateStore.Save();
		}

		public static bool Release(string effectiveSessionId)
		{
			SchedulerState state = SchedulerStateStore.State;
			if (!IsHolder(state, effectiveSessionId))
			{
				return false;
			}

			ClearHolder(state);
			SchedulerStateStore.Save();
			return true;
		}

		public static void TickIdle(DateTime nowUtc, bool hasPending)
		{
			SchedulerState state = SchedulerStateStore.State;
			if (string.IsNullOrEmpty(state.HolderAgentSessionId))
			{
				return;
			}

			if (hasPending)
			{
				return;
			}

			DateTime lastActivity;
			if (!TryParseUtc(state.HolderLastActivityUtc, out lastActivity))
			{
				state.HolderLastActivityUtc = nowUtc.ToString("o");
				SchedulerStateStore.Save();
				return;
			}

			if ((nowUtc - lastActivity).TotalSeconds <= AgentBridgeSettingsStore.GetLeaseIdleTimeoutSeconds())
			{
				return;
			}

			ClearHolder(state);
			SchedulerStateStore.Save();
		}

		// How long the current holder has owned the lease. Reads the state and mutates nothing,
		// so telemetry can ask for it at any point without disturbing the scheduler.
		public static long HeldMs(DateTime nowUtc)
		{
			DateTime since;
			if (!TryParseUtc(SchedulerStateStore.State.HolderSinceUtc, out since))
			{
				return 0;
			}

			return (long)(nowUtc - since).TotalMilliseconds;
		}

		public static ContentionInfo BuildContention(List<PendingTaskInfo> pending, DateTime nowUtc)
		{
			var info = new ContentionInfo();
			if (pending == null || pending.Count == 0)
			{
				return info;
			}

			string holder = SchedulerStateStore.State.HolderAgentSessionId ?? "";
			var sessions = new List<string>();
			DateTime oldest = DateTime.MaxValue;

			foreach (PendingTaskInfo task in pending)
			{
				if (string.Equals(task.EffectiveSessionId, holder, StringComparison.Ordinal))
				{
					continue;
				}

				if (!sessions.Contains(task.EffectiveSessionId))
				{
					sessions.Add(task.EffectiveSessionId);
				}

				if (task.CreatedUtc < oldest)
				{
					oldest = task.CreatedUtc;
				}

				if (info.Notes.Count >= MaxNotes || string.IsNullOrEmpty(task.Note))
				{
					continue;
				}

				string note = task.Note.Length > MaxNoteLength ? task.Note.Substring(0, MaxNoteLength) : task.Note;
				if (!info.Notes.Contains(note))
				{
					info.Notes.Add(note);
				}
			}

			info.WaitingSessions = sessions.Count;
			if (sessions.Count > 0)
			{
				double waited = (nowUtc - oldest).TotalSeconds;
				info.OldestWaitSeconds = waited > 0 ? (int)waited : 0;
			}

			return info;
		}

		public static QueuedTaskStatus[] BuildQueue(List<PendingTaskInfo> pending)
		{
			if (pending == null || pending.Count == 0)
			{
				return new QueuedTaskStatus[0];
			}

			string holder = SchedulerStateStore.State.HolderAgentSessionId ?? "";
			List<PendingTaskInfo> ordered = OrderBySession(pending, holder);

			var queue = new QueuedTaskStatus[ordered.Count];
			for (int i = 0; i < ordered.Count; i++)
			{
				queue[i] = new QueuedTaskStatus
				{
					Id = ordered[i].Id,
					AgentSessionId = ordered[i].EffectiveSessionId,
					Position = i + 1
				};
			}

			return queue;
		}

		public static void SaveContextFor(string effectiveSessionId, SceneSetupState[] setup, string prefabStagePath, DateTime nowUtc)
		{
			if (string.IsNullOrEmpty(effectiveSessionId) || IsAnonymous(effectiveSessionId))
			{
				return;
			}

			SchedulerState state = SchedulerStateStore.State;
			var context = new SessionContext
			{
				AgentSessionId = effectiveSessionId,
				Setup = setup ?? new SceneSetupState[0],
				PrefabStagePath = prefabStagePath ?? "",
				SavedAtUtc = nowUtc.ToString("o")
			};

			for (int i = state.Contexts.Count - 1; i >= 0; i--)
			{
				if (string.Equals(state.Contexts[i].AgentSessionId, effectiveSessionId, StringComparison.Ordinal))
				{
					state.Contexts.RemoveAt(i);
				}
			}

			state.Contexts.Add(context);
			TrimContexts(state);
			SchedulerStateStore.Save();
		}

		public static SessionContext FindContext(string effectiveSessionId)
		{
			if (string.IsNullOrEmpty(effectiveSessionId))
			{
				return null;
			}

			foreach (SessionContext context in SchedulerStateStore.State.Contexts)
			{
				if (string.Equals(context.AgentSessionId, effectiveSessionId, StringComparison.Ordinal))
				{
					return context;
				}
			}

			return null;
		}

		private static void TrimContexts(SchedulerState state)
		{
			while (state.Contexts.Count > MaxContexts)
			{
				int oldestIndex = 0;
				for (int i = 1; i < state.Contexts.Count; i++)
				{
					if (string.CompareOrdinal(state.Contexts[i].SavedAtUtc, state.Contexts[oldestIndex].SavedAtUtc) < 0)
					{
						oldestIndex = i;
					}
				}

				state.Contexts.RemoveAt(oldestIndex);
			}
		}

		private static bool IsHolder(SchedulerState state, string effectiveSessionId)
		{
			return !string.IsNullOrEmpty(effectiveSessionId)
				&& string.Equals(state.HolderAgentSessionId, effectiveSessionId, StringComparison.Ordinal);
		}

		private static void ClearHolder(SchedulerState state)
		{
			state.HolderAgentSessionId = "";
			state.HolderSinceUtc = "";
			state.ContentionStartedUtc = "";
			state.HolderContextRestored = true;
		}

		private static bool SliceExpired(string contentionStartedUtc, DateTime nowUtc)
		{
			DateTime started;
			if (!TryParseUtc(contentionStartedUtc, out started))
			{
				return true;
			}

			return (nowUtc - started).TotalSeconds >= AgentBridgeSettingsStore.GetContentionSliceSeconds();
		}

		private static bool TryParseUtc(string value, out DateTime result)
		{
			result = DateTime.MinValue;
			if (string.IsNullOrEmpty(value))
			{
				return false;
			}

			return DateTime.TryParse(
				value,
				System.Globalization.CultureInfo.InvariantCulture,
				System.Globalization.DateTimeStyles.AdjustToUniversal | System.Globalization.DateTimeStyles.AssumeUniversal,
				out result);
		}

		private static PendingTaskInfo OldestForeignRelease(List<PendingTaskInfo> pending, string holder)
		{
			PendingTaskInfo best = null;
			foreach (PendingTaskInfo task in pending)
			{
				if (task.Kind != "release" || string.Equals(task.EffectiveSessionId, holder, StringComparison.Ordinal))
				{
					continue;
				}

				if (best == null || task.CreatedUtc < best.CreatedUtc)
				{
					best = task;
				}
			}

			return best;
		}

		private static PendingTaskInfo Oldest(List<PendingTaskInfo> pending, string sessionId, bool matching)
		{
			PendingTaskInfo best = null;
			foreach (PendingTaskInfo task in pending)
			{
				if (sessionId != null)
				{
					bool same = string.Equals(task.EffectiveSessionId, sessionId, StringComparison.Ordinal);
					if (same != matching)
					{
						continue;
					}
				}

				if (best == null || task.CreatedUtc < best.CreatedUtc)
				{
					best = task;
				}
			}

			return best;
		}

		private static List<PendingTaskInfo> OrderBySession(List<PendingTaskInfo> pending, string holder)
		{
			var sessions = new List<string>();
			var oldestBySession = new Dictionary<string, DateTime>(StringComparer.Ordinal);

			foreach (PendingTaskInfo task in pending)
			{
				DateTime known;
				if (oldestBySession.TryGetValue(task.EffectiveSessionId, out known))
				{
					if (task.CreatedUtc < known)
					{
						oldestBySession[task.EffectiveSessionId] = task.CreatedUtc;
					}

					continue;
				}

				sessions.Add(task.EffectiveSessionId);
				oldestBySession[task.EffectiveSessionId] = task.CreatedUtc;
			}

			sessions.Sort(delegate(string left, string right)
			{
				bool leftHolder = string.Equals(left, holder, StringComparison.Ordinal);
				bool rightHolder = string.Equals(right, holder, StringComparison.Ordinal);
				if (leftHolder != rightHolder)
				{
					return leftHolder ? -1 : 1;
				}

				int byTime = oldestBySession[left].CompareTo(oldestBySession[right]);
				return byTime != 0 ? byTime : string.CompareOrdinal(left, right);
			});

			var ordered = new List<PendingTaskInfo>(pending.Count);
			foreach (string session in sessions)
			{
				var group = new List<PendingTaskInfo>();
				foreach (PendingTaskInfo task in pending)
				{
					if (string.Equals(task.EffectiveSessionId, session, StringComparison.Ordinal))
					{
						group.Add(task);
					}
				}

				group.Sort(delegate(PendingTaskInfo left, PendingTaskInfo right)
				{
					int byTime = left.CreatedUtc.CompareTo(right.CreatedUtc);
					return byTime != 0 ? byTime : string.CompareOrdinal(left.Id, right.Id);
				});

				ordered.AddRange(group);
			}

			return ordered;
		}
	}
}
