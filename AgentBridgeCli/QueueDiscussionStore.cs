using System.Text.Json;
using AgentBridge.Coordination;
using AgentBridge.Queueing;

namespace AgentBridge.Cli;

// Owned by queue-server. Read a fresh snapshot per transaction so a failed atomic write
// cannot leak an approval, reset a deadline, or consume a reservation in memory.
internal sealed class QueueDiscussionStore
{
	internal const string Capability = "long-run-negotiation-v1";
	internal const int ReplySeconds = 15;
	private readonly string _path;
	private readonly Func<long> _now;
	internal sealed class State
	{
		public int Schema = 1;
		public long Revision;
		public List<QueueDiscussion> Discussions = new();
	}
	internal QueueDiscussionStore(string project, Func<long>? now = null)
	{
		_path = Path.Combine(QueueFiles.Root(project), "discussions.json");
		_now = now ?? (() => DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
	}
	private State Read()
	{
		State state;
		try { state = JsonSerializer.Deserialize<State>(SharedFile.ReadAllText(_path), QueueClient.Json) ?? throw new IOException("Invalid discussion state"); }
		catch (FileNotFoundException) { state = new State(); }
		catch (DirectoryNotFoundException) { state = new State(); }
		if (state.Schema != 1) throw new IOException("Unsupported discussion schema");
		return state;
	}
	private void Save(State state, QueueDiscussion item)
	{
		item.Revision = ++state.Revision;
		QueueFiles.Write(_path, JsonSerializer.Serialize(state, QueueClient.Json));
	}
	private static QueueReply Reply(State state, QueueDiscussion item) => new()
	{
		Ok = true, Code = item.State, Discussion = item, DiscussionRevision = state.Revision,
		Message = item.Kind == "stop" ? "Discuss remaining time and interruption cost. Age alone is not a reason to cancel." : "Submit the long run only after this reservation is granted."
	};
	private static void Identity(string session, string reason)
	{
		if (string.IsNullOrWhiteSpace(session) || session.Length > 64 || string.IsNullOrWhiteSpace(reason) || reason.Length > 2000)
			throw new ArgumentException("A named --session and a concrete --reason (1-2000 characters) are required.");
	}
	internal QueueReply Inbox(string session, long after)
	{
		var state = Read();
		return new QueueReply { Ok = true, Code = "ok", DiscussionRevision = state.Revision,
			Discussions = state.Discussions.Where(d => d.Revision > after && !string.IsNullOrEmpty(session)
				&& (d.Session == session || d.Owner == session || d.Blockers.Contains(session))).ToArray() };
	}
	internal QueueReply Reserve(QueueMessage message, QueueTaskInfo[] tasks, CoordinationState? coordination)
	{
		Identity(message.Session, message.Reason);
		if (message.Seconds <= 300 || message.Seconds > 86400 || string.IsNullOrWhiteSpace(message.DiscussionId))
			throw new ArgumentException("reserve requires --request KEY and --seconds 301..86400; ordinary runs use the queue.");
		var state = Read();
		string id = "reserve_" + CoordinationDigest.Sha256(message.Session + "\n" + message.DiscussionId)[..24];
		var item = state.Discussions.FirstOrDefault(d => d.Id == id);
		if (item != null)
		{
			if (item.Seconds != message.Seconds || item.Reason != message.Reason) return QueueServer.Failure("id_conflict", "Reservation key already names different work.");
			if (item.State is "used" or "withdrawn") return Reply(state, item);
		}
		else
		{
			item = new QueueDiscussion { Id = id, Kind = "reservation", Session = message.Session,
				RequestKey = message.DiscussionId, Seconds = message.Seconds, Reason = message.Reason };
			state.Discussions.Add(item);
		}
		RefreshReservation(state, item, tasks, coordination);
		Save(state, item);
		return Reply(state, item);
	}
	private void RefreshReservation(State state, QueueDiscussion item, QueueTaskInfo[] tasks, CoordinationState? coordination)
	{
		var blockers = tasks.Where(t => !QueueFiles.Terminal(t.Status) && t.Kind is not ("cancel" or "release" or "stopplay"))
			.Select(t => t.AgentSessionId).Concat(coordination?.Grants.Select(g => g.Session) ?? Array.Empty<string>())
			.Concat(state.Discussions.Where(d => d.Id != item.Id && d.Kind == "reservation" && d.State == "granted" && d.WaitUntilMs > _now()).Select(d => d.Session))
			.Where(s => s != item.Session).Distinct().OrderBy(s => s, StringComparer.Ordinal).ToArray();
		// Anonymous work is a blocker too; it must finish before a reservation can be issued.
		item.Blockers = blockers;
		item.State = blockers.All(s => s.Length > 0 && item.Answers.Any(a => a.Session == s && a.Decision == "allow")) ? "granted" : "pending";
		item.WaitUntilMs = _now() + 60000; // unsubmitted reservations do not hold an idle project forever
	}
	internal QueueReply RequestStop(QueueMessage message, QueueTaskInfo task)
	{
		Identity(message.Session, message.Reason);
		if (QueueFiles.Terminal(task.Status)) return QueueServer.Failure("already_finished", "Task already finished; do not cancel or restart it.");
		if (task.Status is "queued" or "attached" || task.AgentSessionId == message.Session)
			return QueueServer.Failure("not_foreign_running", "Request-stop is for another agent's executing task.");
		if (task.Kind is not ("tests" or "csharp"))
			return QueueServer.Failure("cancel_not_supported_for_running_task", "Only test runs and cooperative C# scripts can be interrupted; wait for this operation.");
		if (TaskCancellationPolicy.IsProtected(task.AgentSessionId, message.Session, task.StartedAtUtc, _now()))
			return QueueServer.Failure("task_protected", "Wait in the queue for the first 300 seconds.");
		var state = Read();
		string id = "stop_" + CoordinationDigest.Sha256(task.Id + "\n" + message.Session)[..24];
		var item = state.Discussions.FirstOrDefault(d => d.Id == id);
		if (item != null && item.State is not ("withdrawn" or "agreed")) return Reply(state, item);
		if (item?.State == "agreed" && item.WaitUntilMs > _now()) return Reply(state, item);
		if (item == null)
		{
			item = new QueueDiscussion { Id = id, Kind = "stop", Session = message.Session, Owner = task.AgentSessionId, TaskId = task.Id };
			state.Discussions.Add(item);
		}
		item.Reason = message.Reason;
		item.State = "pending";
		item.Answers = Array.Empty<QueueDiscussionAnswer>();
		item.ReplyByMs = _now() + ReplySeconds * 1000;
		Save(state, item);
		return Reply(state, item);
	}
	internal QueueReply Respond(QueueMessage message)
	{
		Identity(message.Session, message.Reason);
		var state = Read();
		var item = state.Discussions.FirstOrDefault(d => d.Id == message.DiscussionId);
		if (item == null) return QueueServer.Failure("discussion_not_found", message.DiscussionId);
		if (item.State is "used" or "withdrawn" or "cancel_requested") return QueueServer.Failure("discussion_closed", item.State);
		if (message.Session == item.Session && message.Decision == "withdraw") item.State = "withdrawn";
		else if (item.Kind == "reservation" && item.Blockers.Contains(message.Session) && message.Decision is "allow" or "deny")
		{
			item.Answers = item.Answers.Where(a => a.Session != message.Session).Append(Answer(message)).ToArray();
			item.State = "pending"; // requester must recheck live competitors using reserve
		}
		else if (item.Kind == "stop" && message.Session == item.Owner && message.Decision is "yield" or "continue")
		{
			if (item.State == "agreed") return QueueServer.Failure("agreement_active", "The requester already agreed to wait.");
			item.Answers = new[] { Answer(message) };
			item.State = "answered";
		}
		else if (item.Kind == "stop" && message.Session == item.Session && message.Decision == "wait" && message.Seconds is > 0 and <= 86400)
		{
			item.State = "agreed";
			item.WaitUntilMs = _now() + message.Seconds * 1000L;
			item.Answers = item.Answers.Where(a => a.Session != message.Session).Append(Answer(message)).ToArray();
		}
		else return QueueServer.Failure("bad_response", "Only a blocker may allow/deny; the owner may yield/continue; the requester may wait/withdraw.");
		Save(state, item);
		return Reply(state, item);
	}
	private static QueueDiscussionAnswer Answer(QueueMessage m) => new() { Session = m.Session, Decision = m.Decision, Reason = m.Reason, Seconds = m.Seconds };
	internal QueueReply? AuthorizeCancel(QueueMessage message, QueueTaskInfo task)
	{
		if (string.IsNullOrWhiteSpace(message.Session) || string.IsNullOrWhiteSpace(message.Reason))
			return QueueServer.Failure("negotiation_required", "Foreign cancellation needs --session, --request DISCUSSION and --reason. First use queue request-stop.");
		var state = Read();
		var item = state.Discussions.FirstOrDefault(d => d.Id == message.DiscussionId && d.Kind == "stop" && d.TaskId == task.Id && d.Session == message.Session);
		if (item == null) return QueueServer.Failure("negotiation_required", "First use queue request-stop <TaskId> --session S --reason TEXT; inspect the owner's reply before deciding.");
		if (item.State == "agreed" || item.State == "withdrawn") return QueueServer.Failure("agreement_active", "Honor the agreement; request-stop again after the agreed wait if still needed.");
		if (item.State == "pending" && _now() < item.ReplyByMs) return QueueServer.Failure("reply_pending", "Owner has 15 seconds from request registration to answer.");
		if (item.State == "answered" && message.AfterRevision < item.Revision)
			return new QueueReply { Code = "response_unread", Message = "Read the owner's answer. To cancel despite it, supply --after its revision and --reason explaining why waiting is unsuitable.", Discussion = item, DiscussionRevision = state.Revision };
		return null;
	}
	internal void Canceled(QueueMessage message)
	{
		var state = Read();
		var item = state.Discussions.FirstOrDefault(d => d.Id == message.DiscussionId && d.Kind == "stop" && d.TaskId == message.TaskId && d.Session == message.Session);
		if (item == null || item.State == "cancel_requested") return;
		item.State = "cancel_requested";
		item.Answers = item.Answers.Append(Answer(new QueueMessage { Session = message.Session, Decision = "cancel", Reason = message.Reason })).ToArray();
		Save(state, item);
	}
	internal QueueReply? Admit(string session, string reservation, int expectedSeconds, string workId, QueueTaskInfo[] tasks, CoordinationState? coordination)
	{
		if (expectedSeconds < 0 || expectedSeconds > 86400) return QueueServer.Failure("bad_usage", "Expected duration must be 0..86400 seconds.");
		if (expectedSeconds <= 300 && reservation.Length == 0) return null;
		var state = Read();
		var item = state.Discussions.FirstOrDefault(d => d.Id == reservation && d.Session == session && d.Kind == "reservation");
		if (item == null) return QueueServer.Failure("reservation_required", "Expected runs over 300 seconds require queue reserve, then --reservation ID.");
		if (item.State == "used") return item.BoundWorkId == workId ? null : QueueServer.Failure("reservation_used", "This reservation already belongs to " + item.BoundWorkId + "; inspect that work instead of submitting a duplicate.");
		if (item.State == "withdrawn") return QueueServer.Failure("reservation_closed", item.Id);
		if (expectedSeconds > item.Seconds) return QueueServer.Failure("reservation_too_short", "The estimate exceeds the agreed duration.");
		RefreshReservation(state, item, tasks, coordination);
		if (item.State != "granted")
		{
			Save(state, item);
			return new QueueReply { Code = "negotiation_required", Message = "Competitors have not agreed to this long run.", Discussion = item, DiscussionRevision = state.Revision };
		}
		// Binding precedes admission. Retrying the SAME immutable work id repairs a crash
		// between these writes; a new id can never consume the same permission twice.
		item.State = "used";
		item.BoundWorkId = workId;
		Save(state, item);
		return null;
	}
}
