using System.Diagnostics;
using System.Text.Json;
using AgentBridge.Coordination;
using AgentBridge.Queueing;

namespace AgentBridge.Cli;

internal sealed class QueueServer
{
	internal const int ReplySweepIntervalMs = 60000;
	internal const int SlowPhaseMs = 1000;
	internal static readonly TimeSpan ReplyLifetime = TimeSpan.FromDays(1);

	private readonly string _project;
	private readonly string _root;
	private Task? _sweep;
	private readonly QueueTaskStore _tasks;
	private readonly CoordinationFileStore _coordination;
	private readonly TelemetryLog _log;
	private long _lastUse;
	private long _lastSweep;
	private bool _stop;

	internal QueueServer(string project)
	{
		_project = project;
		_root = QueueFiles.Root(project);
		_tasks = new QueueTaskStore(project);
		var paths = new BridgePaths(project);
		string projectId = SharedFile.TryReadAllText(paths.ProjectIdFile, out var id) ? id.Trim() : Guid.NewGuid().ToString("N");
		if (!File.Exists(paths.ProjectIdFile)) QueueFiles.Write(paths.ProjectIdFile, projectId);
		_coordination = new CoordinationFileStore(CoordinationPathPolicy.CoordinationRoot(project), CoordinationJsonCodec.Instance,
			CoordinationSystemClock.Instance, projectId, () => Guid.NewGuid().ToString("N"));
		_log = new TelemetryLog(project, true);
	}

	public static async Task<int> RunAsync(string project)
	{
		if (!CoordinationPathPolicy.TryResolveProjectRoot(project, out project, out _)) return 3;
		string root = QueueFiles.Root(project);
		Directory.CreateDirectory(root);
		FileStream ownership;
		try { ownership = new FileStream(Path.Combine(root, "server.lock"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None); }
		catch (IOException) { return 0; }
		using (ownership)
		{
			var server = new QueueServer(project);
			QueueFiles.Write(Path.Combine(root, "server.json"), JsonSerializer.Serialize(new { Protocol = QueueFiles.Capability, Pid = Environment.ProcessId, StartedAtUtc = DateTime.UtcNow.ToString("o") }));
			await server.Loop();
		}
		return 0;
	}

	private async Task Loop()
	{
		Directory.CreateDirectory(Path.Combine(_root, "Requests"));
		_lastUse = Environment.TickCount64;
		long lastBeat = 0, lastSync = 0, lastCleanup = 0;
		string? lastLaunchJson = null;
		BridgeHealth? health = null;
		long lastInspect = 0;
		while (!_stop)
		{
			long now = Environment.TickCount64;
			// A live editor on another OS keeps its own dispatcher; hand the queue over to it.
			if (health == null || now - lastInspect >= DispatcherHostPolicy.InspectIntervalMs)
			{
				health = BridgeInspector.Inspect(_project);
				lastInspect = now;
				if (DispatcherHostPolicy.HostOwnsDispatcher(health)) { Log("queue_handover", "", "", health.HostOs); break; }
			}
			// A newer CLI publishes its immutable runtime before sending a request. Yield
			// ownership between transactions; its client restarts us without losing the queue.
			// A launcher this machine cannot run (another OS's client) is ignored.
			if (SharedFile.TryReadAllText(Path.Combine(_root, "launch.json"), out var launchJson) && launchJson != lastLaunchJson)
			{
				var launch = JsonSerializer.Deserialize<QueueLaunch>(launchJson, QueueClient.Json);
				if (launch != null && !QueueRuntime.IsCurrent(launch))
				{
					if (DispatcherHostPolicy.ShouldYield(File.Exists(QueueRuntime.Artifact(launch)), health.ForeignHost))
					{
						Log("queue_runtime_changed", "", "", launch.Executable);
						break;
					}
					Log("queue_launch_ignored", "", "", launch.Executable);
				}
				lastLaunchJson = launchJson;
			}
			if (now - lastBeat >= 1000)
			{
				long started = Environment.TickCount64;
				QueueFiles.Write(Path.Combine(_root, "heartbeat"), DateTimeOffset.UtcNow.ToUnixTimeMilliseconds().ToString());
				ReportSlow("heartbeat", started);
				lastBeat = now;
			}
			foreach (var path in Directory.GetFiles(Path.Combine(_root, "Requests"), "*.json").OrderBy(File.GetCreationTimeUtc))
			{
				_lastUse = now;
				HandleFile(path);
			}
			if (now - lastSync >= 250)
			{
				long started = Environment.TickCount64;
				try
				{
					_tasks.Synchronize();
					if (_coordination.Exists)
					{
						if (now - _lastSweep >= 1000)
						{
							ApplyCoordination(new CoordinationCommand { Op = CoordinationEngine.OpSweep, Nonce = Guid.NewGuid().ToString("N") });
							_lastSweep = now;
						}
						_tasks.Catalog(_coordination.Read());
					}
				}
				catch (Exception error) { Log("queue_sync_error", "", "", error.Message); }
				ReportSlow("sync", started);
				lastSync = now;
			}
			// The sweep runs off the loop: a large Replies folder must not stall the heartbeat
			// or request handling. Exit does not wait for it; deletion is idempotent.
			if (now - lastCleanup > ReplySweepIntervalMs && (_sweep == null || _sweep.IsCompleted))
			{
				string replies = Path.Combine(_root, "Replies");
				_sweep = Task.Run(() => SweepAndLog(replies));
				lastCleanup = now;
			}
			// Idle shutdown is safe even with queued work: the next client or editor handshake
			// restarts this process, and the complete queue is already durable. The server on
			// the machine of a live editor stays: the editor would only start it again.
			if (DispatcherHostPolicy.MayIdleExit(now - _lastUse, health)) break;
			await Task.Delay(20);
		}
		File.Delete(Path.Combine(_root, "heartbeat"));
	}

	private void HandleFile(string path)
	{
		long started = Environment.TickCount64;
		string id = Path.GetFileNameWithoutExtension(path);
		string replyPath = QueueFiles.Reply(_project, id);
		if (File.Exists(replyPath)) { File.Delete(path); return; }
		QueueMessage? message = null;
		QueueReply reply;
		try
		{
			message = JsonSerializer.Deserialize<QueueMessage>(SharedFile.ReadAllText(path), QueueClient.Json);
			if (message == null || message.Id != id) throw new ArgumentException("invalid queue request");
			reply = Handle(message);
		}
		catch (CoordinationStoreException error) { reply = Failure(error.Code, error.Message); }
		catch (UnauthorizedAccessException error) { reply = Failure("access_denied", error.Message); }
		catch (IOException error) { reply = Failure("io_error", error.Message); }
		catch (Exception error) { reply = Failure("bad_usage", error.Message); }
		QueueFiles.Write(replyPath, JsonSerializer.Serialize(reply, QueueClient.Json));
		File.Delete(path);
		ReportSlow("request", started, message?.Op ?? "");
		if (message?.Op is not ("get" or "list" or "coord")) Log("queue_" + message?.Op, message?.Session ?? "", message?.TaskId ?? "", reply.Code);
	}

	internal QueueReply Handle(QueueMessage message)
	{
		if (message.Op == "hello")
		{
			if (string.IsNullOrEmpty(message.Incarnation)) throw new ArgumentException("executor incarnation required");
			if (BridgeInspector.Inspect(_project).ForeignHost) return new QueueReply { Ok = true, Code = "ok" };
			using var editor = Process.GetProcessById(message.EditorPid);
			if (editor.HasExited || editor.StartTime.ToUniversalTime().Ticks != message.EditorStartedTicks)
				return Failure("stale_executor", "Editor process has ended.");
			_tasks.RecoverIncarnation(message.Incarnation);
			return new QueueReply { Ok = true, Code = "ok" };
		}
		if (message.Op == "coord")
		{
			var command = JsonSerializer.Deserialize<CoordinationCommand>(message.CoordinationJson, QueueClient.Json) ?? throw new ArgumentException("missing coordination command");
			var result = ApplyCoordination(command);
			if (_coordination.Exists) _tasks.Catalog(_coordination.Read());
			Log("queue_coord", command.Session, command.RequestId, result.Code);
			return new QueueReply { Ok = result.Ok, Code = result.Code, Message = result.Message, CoordinationJson = JsonSerializer.Serialize(result, QueueClient.Json) };
		}
		if (message.Op == "shutdown") { _stop = true; return new QueueReply { Ok = true, Code = "stopping" }; }
		_tasks.Synchronize();
		try { if (_coordination.Exists) _tasks.Catalog(_coordination.Read()); }
		catch (CoordinationStoreException) when (message.Op is "submit" or "get" or "list" or "move" or "clear-order") { }
		switch (message.Op)
		{
			case "submit": return _tasks.Submit(message.RequestJson, message.Payload);
			case "get": return _tasks.Get(message.TaskId);
			case "list": return new QueueReply { Ok = true, Code = "ok", Tasks = _tasks.List() };
			case "clear-order": return _tasks.ClearOrder();
			case "move": return _tasks.Move(message.TaskId, message.BeforeId);
			case "requeue": return _tasks.Requeue(message.TaskId, message.Incarnation);
			case "cancel": return _tasks.Cancel(message, _coordination.Exists ? _coordination.Read() : null);
			case "claim":
				var claimed = _tasks.Claim(message);
				if (!claimed.Ok) return claimed;
				if (!string.IsNullOrEmpty(message.CoordinationJson))
				{
					var command = JsonSerializer.Deserialize<CoordinationCommand>(message.CoordinationJson, QueueClient.Json)!;
					var result = ApplyCoordination(command);
					if (!result.Ok) { _tasks.ReleaseClaim(message.TaskId, message.Incarnation); return Failure(result.Code, result.Message); }
				}
				else if (_coordination.Exists && claimed.Task?.Kind is not ("cancel" or "stopplay" or "release"))
				{
					var window = _coordination.Read().FindWindowGrant();
					if (window != null) { _tasks.ReleaseClaim(message.TaskId, message.Incarnation); return Failure(CoordinationCodes.Busy, "A coordinated window owns the executor."); }
				}
				return claimed;
			default: return Failure("bad_usage", "unknown queue operation: " + message.Op);
		}
	}

	private CoordinationReply ApplyCoordination(CoordinationCommand command)
	{
		// Only this short transaction runs under the store lock; no Unity callback, RPC wait,
		// payload, input hashing or result delivery may hold it.
		if (command.Op == CoordinationEngine.OpWindowConfirm)
		{
			_tasks.Synchronize();
			if (_tasks.HasUncoordinatedExecution) return CoordinationReply.Fail(CoordinationCodes.Busy, "An ordinary task already owns the executor.");
		}
		using var transaction = _coordination.BeginWithRetry(100);
		var reply = new CoordinationEngine().Apply(transaction.State, command, CoordinationSystemClock.Instance.UtcNowMs);
		if (reply.Changed) transaction.Commit();
		return reply;
	}
	internal readonly record struct ReplySweep(int Deleted, int Failed, int Kept, long ElapsedMs);

	// Enumeration carries each file's timestamp (on Windows without a per-file metadata call).
	// Covers *.json replies and *.tmp leftovers of interrupted atomic writes.
	internal static ReplySweep SweepReplies(string directory, DateTime cutoffUtc)
	{
		var watch = Stopwatch.StartNew();
		int deleted = 0, failed = 0, kept = 0;
		try
		{
			if (Directory.Exists(directory))
				foreach (var file in new DirectoryInfo(directory).EnumerateFiles())
				{
					if (file.LastWriteTimeUtc >= cutoffUtc) { kept++; continue; }
					try { file.Delete(); deleted++; }
					catch (Exception error) when (error is IOException or UnauthorizedAccessException) { failed++; }
				}
		}
		catch (Exception error) when (error is IOException or UnauthorizedAccessException) { failed++; }
		return new ReplySweep(deleted, failed, kept, watch.ElapsedMilliseconds);
	}

	private void SweepAndLog(string directory)
	{
		var sweep = SweepReplies(directory, DateTime.UtcNow - ReplyLifetime);
		if (sweep.Deleted > 0 || sweep.Failed > 0 || sweep.ElapsedMs >= SlowPhaseMs)
			_log.Write("queue_cleanup", "", "", new Dictionary<string, object?>
			{
				["Deleted"] = sweep.Deleted, ["Failed"] = sweep.Failed, ["Kept"] = sweep.Kept,
				["ElapsedMs"] = sweep.ElapsedMs, ["Host"] = HostPlatform.Current
			});
	}

	private void ReportSlow(string phase, long startedAt, string op = "")
	{
		long elapsed = Environment.TickCount64 - startedAt;
		if (elapsed >= SlowPhaseMs)
			_log.Write("queue_slow", "", "", new Dictionary<string, object?>
			{
				["Phase"] = phase, ["Op"] = op, ["ElapsedMs"] = elapsed, ["Host"] = HostPlatform.Current
			});
	}

	private void Log(string name, string session, string id, string result) => _log.Write(name, session, id, new Dictionary<string, object?> { ["Result"] = result });
	internal static QueueReply Failure(string code, string message) => new() { Code = code, Message = message };
}
