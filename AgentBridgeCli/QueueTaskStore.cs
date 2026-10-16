using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using AgentBridge.Coordination;
using AgentBridge.Queueing;

namespace AgentBridge.Cli;

internal sealed class QueueTaskStore
{
	private readonly string _project;
	private readonly BridgePaths _paths;
	private readonly Dictionary<string, QueueTask> _tasks = new(StringComparer.Ordinal);
	private readonly Dictionary<string, long> _journals = new(StringComparer.Ordinal);
	private long _order;
	private long _lastImport;
	private long _catalogRevision = -1;
	private string _catalogEpoch = "";
	private string OrderPath => Path.Combine(QueueFiles.Root(_project), "order.txt");
	internal Func<TaskRequest, QueueReply?>? Admission;
	internal Func<QueueMessage, QueueTaskInfo, QueueReply?>? Cancellation;
	internal QueueTaskStore(string project)
	{
		_project = project;
		_paths = new BridgePaths(project);
		string directory = Path.Combine(QueueFiles.Root(project), "Tasks");
		Directory.CreateDirectory(directory);
		Directory.CreateDirectory(_paths.Inbox);
		Directory.CreateDirectory(_paths.Journal);
		foreach (string path in Directory.GetFiles(directory, "*.json"))
		{
			var task = JsonSerializer.Deserialize<QueueTask>(SharedFile.ReadAllText(path), QueueClient.Json) ?? throw new IOException("invalid queue task: " + path);
			_tasks.Add(task.Info.Id, task);
			_order = Math.Max(_order, task.Order);
		}
	}
	private void Save(QueueTask task)
	{
		string path = QueueFiles.Task(_project, task.Info.Id);
		try { QueueFiles.Write(path, JsonSerializer.Serialize(task, QueueClient.Json)); }
		catch
		{
			// Mutations may hold the in-memory object. A failed publication must restore the
			// last durable value before any subsequent get/claim observes it.
			if (SharedFile.TryReadAllText(path, out var saved)) _tasks[task.Info.Id] = JsonSerializer.Deserialize<QueueTask>(saved, QueueClient.Json)!;
			else _tasks.Remove(task.Info.Id);
			throw;
		}
		_tasks[task.Info.Id] = task;
	}
	private static TaskRequest Request(QueueTask task) => JsonSerializer.Deserialize<TaskRequest>(task.RequestJson, JsonSupport.Read) ?? throw new ArgumentException("invalid task request");
	internal QueueReply Submit(string json, string payload, string authorizedCancelTarget = "")
	{
		var request = JsonSerializer.Deserialize<TaskRequest>(json, JsonSupport.Read) ?? throw new ArgumentException("invalid task request");
		QueueFiles.Safe(request.Id);
		if (request.Kind is not ("compile" or "tests" or "csharp" or "ui" or "sceneshot" or "cancel" or "release" or "play" or "stopplay")) throw new ArgumentException("invalid task kind");
		if (!string.IsNullOrEmpty(request.PayloadFile)) QueueFiles.Safe(request.PayloadFile);
		byte[] data = Convert.FromBase64String(payload);
		if (data.Length > CoordinationBatch.MaxPayloadBytes) throw new ArgumentException("task payload exceeds 4 MiB");
		string hash = Hash(json, data);
		var old = Adopt(request.Id);
		if (old != null)
			return old.Hash == hash ? Reply(old, "accepted") : QueueServer.Failure("id_conflict", "Task id already identifies different content.");
		if (authorizedCancelTarget.Length == 0)
		{
			if (request.Kind == "cancel") return QueueServer.Failure("negotiation_required", "Use cancel <TaskId> through the queue; raw cancellation tasks are not admitted.");
			if (request.Kind == "stopplay" && request.TargetTaskId.Length > 0
				&& (!_tasks.TryGetValue(request.TargetTaskId, out var waiting) || waiting.Info.Status != "queued"))
				return QueueServer.Failure("automatic_stop_obsolete", "The task already started or finished; do not stop its Play Mode.");
			if (request.Kind == "stopplay" && _tasks.Values.Any(t => !QueueFiles.Terminal(t.Info.Status) && t.Info.Status is not ("queued" or "attached")
				&& t.Info.Kind is "tests" or "csharp" && !TaskCancellationPolicy.IsOwner(t.Info.AgentSessionId, request.AgentSessionId)))
				return QueueServer.Failure("negotiation_required", "stopplay cannot interrupt another agent's running task; use queue request-stop followed by cancel.");
			var refusal = Admission?.Invoke(request);
			if (refusal != null) return refusal;
		}
		if (SharedFile.TryReadAllText(Path.Combine(_paths.Journal, request.Id + ".json"), out var previousResult))
		{
			using var prior = JsonDocument.Parse(previousResult);
			if (Text(prior.RootElement, "Hash") != hash) return QueueServer.Failure("id_conflict", "Task id already has a journal with different or unknown input bytes.");
		}
		var task = new QueueTask { AuthorizedCancelTarget = authorizedCancelTarget, RequestJson = json, Payload = payload, Hash = hash, Order = ++_order,
			Info = new QueueTaskInfo { Id = request.Id, Kind = request.Kind, AgentSessionId = request.AgentSessionId, AcceptedAtUtc = DateTime.UtcNow.ToString("o") } };
		Save(task); // Acceptance is durable before dispatch or acknowledgment.
		ReadResult(task);
		Publish(task);
		return Reply(task, "accepted");
	}
	private void Publish(QueueTask task)
	{
		if (task.Published || QueueFiles.Terminal(task.Info.Status) || task.Info.BatchRequestId.Length > 0) return;
		// Older packages do not participate in the atomic start/cancel protocol. Keep the
		// accepted task durable until the matching executor advertises its capability.
		var health = BridgeInspector.Inspect(_project);
		if (health.Bridge?.Capabilities.Contains(QueueFiles.Capability) != true || !health.ProjectMatches || !health.ProtocolCompatible) return;
		var request = Request(task);
		if (!string.IsNullOrEmpty(request.PayloadFile))
		{
			byte[] payload = Convert.FromBase64String(task.Payload);
			string path = Path.Combine(_paths.Inbox, request.PayloadFile);
			// Payloads are UTF-8 source/JSON; preserve their exact bytes for request hashing.
			string temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
			File.WriteAllBytes(temporary, payload);
			File.Move(temporary, path, true);
		}
		QueueFiles.Write(Path.Combine(_paths.Inbox, request.Id + ".task.json"), task.RequestJson);
		task.Published = true;
		Save(task);
	}
	internal void Synchronize()
	{
		if (Environment.TickCount64 - _lastImport > 1000)
		{
			foreach (var path in Directory.GetFiles(_paths.Inbox, "*.task.json"))
			{
				try { Adopt(Path.GetFileName(path)[..^10]); }
				catch (Exception error) when (error is JsonException or IOException or ArgumentException) { /* Isolate incomplete legacy submissions. */ }
			}
			_lastImport = Environment.TickCount64;
		}
		foreach (var task in _tasks.Values.ToArray())
		{
			try { ReadResult(task); Publish(task); if (task.Info.CancelRequested && !QueueFiles.Terminal(task.Info.Status)) EnsureCancellation(task); }
			catch (Exception error) when (error is JsonException or IOException) { /* A damaged result must not block unrelated admission. */ }
		}
	}
	private QueueTask? Adopt(string id)
	{
		QueueFiles.Safe(id);
		if (_tasks.TryGetValue(id, out var task) && (task.Info.BatchRequestId.Length == 0 || task.Published)) return task;
		string path = Path.Combine(_paths.Inbox, id + ".task.json");
		if (!SharedFile.TryReadAllText(path, out var json)) return task;
		var request = JsonSerializer.Deserialize<TaskRequest>(json, JsonSupport.Read);
		if (request == null || request.Id != id) return task;
		byte[] payload = Array.Empty<byte>();
		if (!string.IsNullOrEmpty(request.PayloadFile)) payload = File.ReadAllBytes(Path.Combine(_paths.Inbox, QueueFiles.Safe(request.PayloadFile)));
		task ??= new QueueTask { Order = ++_order, Info = new QueueTaskInfo { Id = id, Kind = request.Kind, AgentSessionId = request.AgentSessionId, AcceptedAtUtc = File.GetCreationTimeUtc(path).ToString("o") } };
		task.RequestJson = json; task.Payload = Convert.ToBase64String(payload); task.Hash = Hash(json, payload); task.Published = true;
		Save(task);
		ReadResult(task);
		return task;
	}
	private void ReadResult(QueueTask task)
	{
		string path = Path.Combine(_paths.Journal, task.Info.Id + ".json");
		if (QueueFiles.Terminal(task.Info.Status))
		{
			// Recover a server crash between the durable cancel decision and journal publication.
			if (task.Info.Status == "canceled" && !File.Exists(path) && File.Exists(Path.Combine(_paths.Inbox, task.Info.Id + ".task.json")))
				QueueFiles.Write(path, task.ResultJson);
			return;
		}
		long stamp = File.GetLastWriteTimeUtc(path).Ticks;
		if (_journals.TryGetValue(task.Info.Id, out long previous) && stamp == previous) return;
		if (!SharedFile.TryReadAllText(path, out var json)) return;
		using var doc = JsonDocument.Parse(json);
		string status = Text(doc.RootElement, "Status");
		if (status.Length == 0) return;
		task.ResultJson = json;
		task.Info.Status = task.Info.CancelRequested && !QueueFiles.Terminal(status) ? "cancel_requested" : status == "queued" ? "running" : status;
		task.Info.StartedAtUtc = Text(doc.RootElement, "StartedAtUtc");
		Save(task);
		_journals[task.Info.Id] = stamp;
	}
	internal QueueReply Get(string id)
	{
		var task = Adopt(id);
		if (task != null) { ReadResult(task); return Reply(task, "ok"); }
		// Preserve access to existing results, even when the old Inbox was trimmed.
		if (SharedFile.TryReadAllText(Path.Combine(_paths.Journal, QueueFiles.Safe(id) + ".json"), out var json))
		{
			using var doc = JsonDocument.Parse(json);
			return new QueueReply { Ok = true, Code = "ok", ResultJson = json, Task = new QueueTaskInfo { Id = id, Kind = Text(doc.RootElement, "Kind"), Status = Text(doc.RootElement, "Status") } };
		}
		return QueueServer.Failure("task_not_found", "No accepted task with id " + id);
	}
	internal QueueTaskInfo[] List()
	{
		var result = Ordered().Select(t => t.Info).ToArray();
		int position = 0;
		foreach (var info in result) info.Position = info.Status == "queued" ? ++position : 0;
		return result;
	}
	internal bool HasUncoordinatedExecution => _tasks.Values.Any(t => !QueueFiles.Terminal(t.Info.Status)
		&& (t.ClaimOwner.Length > 0 || t.Info.Status != "queued")
		&& t.Info.Kind is not ("cancel" or "stopplay" or "release") && string.IsNullOrEmpty(Request(t).CoordinationWindowToken));
	internal QueueReply Claim(QueueMessage message)
	{
		if (string.IsNullOrEmpty(message.Incarnation)) throw new ArgumentException("executor incarnation required");
		var task = Adopt(message.TaskId);
		if (task == null) return QueueServer.Failure("task_not_found", message.TaskId);
		ReadResult(task);
		if (message.RequestHash != task.Hash) return QueueServer.Failure("id_conflict", "Executor input differs from the accepted immutable task.");
		if (QueueFiles.Terminal(task.Info.Status) || task.Info.CancelRequested) return QueueServer.Failure("task_canceled_or_finished", "Task cannot begin after cancellation or completion.");
		if (task.ClaimOwner.Length > 0 && task.ClaimOwner != message.Incarnation) return QueueServer.Failure("execution_unconfirmed", "A previous editor owns this task; recovery must resolve it before another execution.");
		task.ClaimOwner = message.Incarnation;
		task.Info.Status = "running";
		if (string.IsNullOrEmpty(task.Info.StartedAtUtc)) task.Info.StartedAtUtc = DateTime.UtcNow.ToString("o");
		Save(task);
		return Reply(task, "claimed");
	}
	internal void ReleaseClaim(string id, string incarnation)
	{
		if (_tasks.TryGetValue(id, out var task) && task.ClaimOwner == incarnation && task.ResultJson.Length == 0)
		{ task.ClaimOwner = ""; task.Info.Status = "queued"; task.Info.StartedAtUtc = ""; Save(task); }
	}
	internal QueueReply Requeue(string id, string incarnation)
	{
		if (!_tasks.TryGetValue(id, out var task)) return QueueServer.Failure("task_not_found", id);
		if (QueueFiles.Terminal(task.Info.Status)) return Reply(task, "already_finished");
		if (task.ClaimOwner.Length > 0 && task.ClaimOwner != incarnation) return QueueServer.Failure("execution_unconfirmed", "Only the owning editor may requeue an attachment.");
		task.ClaimOwner = ""; task.ResultJson = ""; task.Info.StartedAtUtc = "";
		if (task.Info.CancelRequested) FinishCanceled(task, "Canceled while attachment was requeued");
		else { task.Info.Status = "queued"; Save(task); }
		_journals.Remove(id);
		return Reply(task, "ok");
	}
	internal void RecoverIncarnation(string incarnation)
	{
		foreach (var task in _tasks.Values.ToArray())
		{
			if (task.ClaimOwner.Length == 0 || task.ClaimOwner == incarnation || QueueFiles.Terminal(task.Info.Status)) continue;
			ReadResult(task);
			// The new editor performs journal/scenes recovery for started work. A lost claim
			// with no journal is also never replayed: its execution outcome is unknowable.
			if (task.ResultJson.Length > 0) continue;
			task.Info.Status = "interrupted_by_domain_reload";
			task.ResultJson = JsonSerializer.Serialize(new { Id = task.Info.Id, Kind = task.Info.Kind,
				Status = task.Info.Status, Hash = task.Hash, FinishedAtUtc = DateTime.UtcNow.ToString("o"),
				Logs = new[] { "Previous editor ended after claiming this task; execution was not replayed." } }, JsonSupport.Task);
			Save(task);
			QueueFiles.Write(Path.Combine(_paths.Journal, task.Info.Id + ".json"), task.ResultJson);
		}
	}
	internal QueueReply Cancel(QueueMessage message, CoordinationState? coordination)
	{
		var task = Adopt(message.TaskId);
		if (task == null) return QueueServer.Failure("task_not_found", message.TaskId);
		ReadResult(task);
		if (QueueFiles.Terminal(task.Info.Status)) return Reply(task, "already_finished");
		if (task.Info.CancelRequested) return Reply(task, "cancel_requested");
		var request = Request(task);
		bool running = task.Info.Status != "attached" && (task.ClaimOwner.Length > 0 || task.Info.Status != "queued");
		if (!message.Human)
		{
			if (running && TaskCancellationPolicy.IsProtected(task.Info.AgentSessionId, message.Session, task.Info.StartedAtUtc, DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()))
				return QueueServer.Failure("task_protected", "Foreign running tasks are protected for 300 seconds.");
			if (running && !TaskCancellationPolicy.IsOwner(task.Info.AgentSessionId, message.Session))
			{
				var refusal = Cancellation?.Invoke(message, task.Info) ?? (Cancellation == null ? QueueServer.Failure("negotiation_required", "Request agreement through queue-server before foreign cancellation.") : null);
				if (refusal != null) return refusal;
			}
			var grant = coordination?.FindGrantByToken(request.CoordinationWindowToken);
			if (!running && grant != null && grant.State == CoordinationLimits.GrantActive && grant.DeadlineMs > DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() && grant.Session != message.Session)
				return QueueServer.Failure("coordinated_window_held", grant.Session);
		}
		if (!running && task.Info.Status != "attached")
		{
			FinishCanceled(task, "Canceled before execution");
			return Reply(task, "canceled");
		}
		task.Info.CancelRequested = true;
		task.Info.CanceledBy = message.Human ? "human" : message.Session;
		task.Info.CancellationReason = message.Reason;
		if (!message.Human && !TaskCancellationPolicy.IsOwner(task.Info.AgentSessionId, message.Session))
		{
			task.Info.YieldToSession = message.Session;
			task.Info.YieldUntilMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() + 60000;
			task.Info.YieldUntilTaskId = Ordered().FirstOrDefault(t => t.Info.AgentSessionId == message.Session && !QueueFiles.Terminal(t.Info.Status) && t.Info.Kind is not ("cancel" or "release" or "stopplay"))?.Info.Id ?? "";
		}
		task.Info.Status = "cancel_requested";
		Save(task);
		EnsureCancellation(task);
		return Reply(task, "cancel_requested");
	}
	private void EnsureCancellation(QueueTask task)
	{
		// The target stores the authorized decision before control publication, so a crash
		// between these writes is repaired by Synchronize without weakening the policy.
		string controlId = "Cancel_" + CoordinationDigest.Sha256(task.Info.Id).Substring(0, 24);
		var control = new TaskRequest { Id = controlId, Kind = "cancel", TargetTaskId = task.Info.Id,
			AgentSessionId = task.Info.CanceledBy, Note = "Canceled by " + task.Info.CanceledBy + ": " + task.Info.CancellationReason };
		Submit(JsonSerializer.Serialize(control, JsonSupport.Task), "", task.Info.Id);
	}
	private void FinishCanceled(QueueTask task, string reason)
	{
		task.Info.Status = "canceled";
		task.ResultJson = JsonSerializer.Serialize(new { Id = task.Info.Id, Kind = task.Info.Kind, Status = "canceled", Hash = task.Hash,
			AgentSessionId = task.Info.AgentSessionId, FinishedAtUtc = DateTime.UtcNow.ToString("o"), Logs = new[] { reason } }, JsonSupport.Task);
		Save(task);
		QueueFiles.Write(Path.Combine(_paths.Journal, task.Info.Id + ".json"), task.ResultJson);
	}
	private IEnumerable<QueueTask> Ordered()
	{
		var ids = SharedFile.TryReadAllText(OrderPath, out var text) ? text.Split('\n') : Array.Empty<string>();
		return _tasks.Values.OrderBy(t => { int i = Array.IndexOf(ids, t.Info.Id); return i < 0 ? int.MaxValue : i; }).ThenBy(t => t.Order);
	}
	internal QueueReply? CheckYield(string session)
	{
		foreach (var task in _tasks.Values.Where(t => t.Info.AgentSessionId == session && t.Info.YieldToSession.Length > 0).ToArray())
		{
			var info = task.Info;
			if (info.YieldUntilTaskId.Length == 0)
			{
				var next = Ordered().FirstOrDefault(t => t.Info.AgentSessionId == info.YieldToSession && t.Info.Kind is not ("cancel" or "release" or "stopplay")
					&& DateTimeOffset.TryParse(t.Info.AcceptedAtUtc, out var accepted) && accepted.ToUnixTimeMilliseconds() >= info.YieldUntilMs - 60000
					&& accepted.ToUnixTimeMilliseconds() <= info.YieldUntilMs);
				if (next != null) { info.YieldUntilTaskId = next.Info.Id; Save(task); }
				else if (DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() >= info.YieldUntilMs) continue;
			}
			if (info.YieldUntilTaskId.Length > 0 && _tasks.TryGetValue(info.YieldUntilTaskId, out var peer))
			{
				bool done = peer.Info.BatchRequestId.Length > 0
					? _tasks.Values.Where(t => t.Info.BatchRequestId == peer.Info.BatchRequestId).All(t => QueueFiles.Terminal(t.Info.Status))
					: QueueFiles.Terminal(peer.Info.Status);
				if (done) continue;
			}
			return QueueServer.Failure("yield_pending", "Task " + info.Id + " yielded to " + info.YieldToSession + ". Wait for "
				+ (info.YieldUntilTaskId.Length > 0 ? info.YieldUntilTaskId : "their submission (up to 60 seconds)") + "; do not recreate the canceled run. Reason: " + info.CancellationReason);
		}
		return null;
	}
	internal QueueReply ClearOrder()
	{
		QueueFiles.Write(OrderPath, "");
		return new QueueReply { Ok = true, Code = "order_cleared" };
	}
	internal QueueReply Move(string id, string beforeId)
	{
		var task = Adopt(id);
		if (!string.IsNullOrEmpty(beforeId)) Adopt(beforeId);
		if (task == null || task.Info.Status != "queued") return QueueServer.Failure("task_not_waiting", id);
		var pending = Ordered().Where(t => t.Info.Status == "queued" && t.Info.Id != id).ToList();
		int index = string.IsNullOrEmpty(beforeId) ? pending.Count : pending.FindIndex(t => t.Info.Id == beforeId);
		if (index < 0) return QueueServer.Failure("task_not_waiting", beforeId);
		pending.Insert(index, task);
		// One atomic commit is shared by the server and the Unity scheduler, including after restart.
		QueueFiles.Write(OrderPath, string.Join("\n", pending.Select(t => t.Info.Id)));
		return Reply(task, "moved");
	}
	internal void Catalog(CoordinationState state)
	{
		if (_catalogRevision == state.Revision && _catalogEpoch == state.Epoch) return;
		foreach (var batch in state.Requests.Where(r => r.Automatic))
		{
			for (int i = 0; i < batch.Plan.Steps.Count; i++)
			{
				string id = CoordinationBatch.TaskId(batch, i);
				if (!_tasks.TryGetValue(id, out var task))
				{
					var step = batch.Plan.Steps[i];
					var request = new TaskRequest { Id = id, Kind = step.Kind, AgentSessionId = batch.Session, TestMode = step.Mode, AssemblyNames = step.Assemblies, TestNames = step.Tests, CategoryNames = step.Categories,
						CoordinationWindowToken = batch.Token, CoordinationStepId = step.Id };
					task = new QueueTask { RequestJson = JsonSerializer.Serialize(request, JsonSupport.Task), Order = ++_order,
						Info = new QueueTaskInfo { Id = id, Kind = step.Kind, AgentSessionId = batch.Session, BatchRequestId = batch.Id, AcceptedAtUtc = DateTimeOffset.FromUnixTimeMilliseconds(batch.CreatedAtMs).ToString("o") } };
					Save(task);
				}
				Adopt(id);
				ReadResult(task);
				if (batch.IsTerminal && task.Info.Status == "queued") FinishCanceled(task, "Batch closed: " + batch.Reason);
			}
		}
		_catalogRevision = state.Revision; _catalogEpoch = state.Epoch;
	}
	private static string Hash(string request, byte[] payload)
	{
		using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
		hash.AppendData(Encoding.UTF8.GetBytes(request)); hash.AppendData(payload);
		return Convert.ToHexString(hash.GetHashAndReset()).ToLowerInvariant();
	}
	private static string Text(JsonElement json, string key) => json.TryGetProperty(key, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() ?? "" : "";
	private static QueueReply Reply(QueueTask task, string code) => new() { Ok = true, Code = code, Task = task.Info, ResultJson = task.ResultJson, RequestHash = task.Hash };
}
