using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using AgentBridge.Queueing;

namespace AgentBridge.Cli;

internal sealed class BridgeClient
{
	private static readonly HashSet<string> TerminalStatuses = new(StringComparer.Ordinal)
	{
		"success",
		"test_failure",
		"no_tests_matched",
		"ambiguous_test_filter",
		"compiler_error",
		"runtime_error",
		"timeout",
		"canceled",
		"interrupted_by_domain_reload",
		"rejected",
		"stale_input",
		"evidence_unavailable"
	};


	// Leaving play mode drags a domain reload behind it, and on a heavy project that is slow.
	private const int StopManualPlaySeconds = 120;

	private readonly BridgePaths _paths;
	private readonly string _format;
	private readonly string _projectRoot;
	private readonly string? _session;
	private readonly string? _note;
	private readonly TelemetryLog _telemetry;

	public BridgeClient(string projectRoot, string format, string? session, string? note, TelemetryLog telemetry)
	{
		_paths = new BridgePaths(projectRoot);
		_format = format;
		_projectRoot = projectRoot;
		_session = session;
		_note = note;
		_telemetry = telemetry;
	}

	// coordination-v1. Empty for a legacy submission, which an uncoordinated project still accepts.
	public string? CoordinationWindowToken { get; init; }
	public string? CoordinationStepId { get; init; }

	public async Task<int> SubmitPayloadAsync(string kind, string sourcePath, int waitSeconds)
	{
		var fullSourcePath = Path.GetFullPath(sourcePath);
		if (!File.Exists(fullSourcePath))
		{
			return WriteError("payload_not_found", "Payload file not found: " + fullSourcePath);
		}

		var taskId = GetPayloadTaskId(kind, fullSourcePath);
		if (!IsSafeTaskId(taskId))
		{
			return WriteError("invalid_task_id", "Payload file name is not a safe task id.");
		}

		var payloadName = kind switch
		{
			"ui" => taskId + ".ui.json",
			"sceneshot" => taskId + ".sceneshot.json",
			_ => taskId + ".cs"
		};
		var request = new TaskRequest
		{
			Id = taskId,
			Kind = kind,
			PayloadFile = payloadName,
			AgentSessionId = _session ?? "",
			Note = _note ?? ""
		};
		Decorate(request);
		var requestBytes = JsonSerializer.SerializeToUtf8Bytes(request, JsonSupport.Task);
		var payloadBytes = await File.ReadAllBytesAsync(fullSourcePath);
		var admitted = await new QueueClient(_projectRoot).CallAsync(new QueueMessage
		{ Op = "submit", TaskId = taskId, RequestJson = Encoding.UTF8.GetString(requestBytes), Payload = Convert.ToBase64String(payloadBytes) });
		if (!admitted.Ok) return WriteError(admitted.Code, admitted.Message);
		Console.Error.WriteLine("[agentbridge] accepted " + taskId);

		_telemetry.Write("cli_submit", _session, request.Id, new Dictionary<string, object?>
		{
			["Cmd"] = request.Kind,
			["Note"] = _note ?? ""
		});

		return await WaitForTaskAsync(taskId, waitSeconds, kind);
	}

	public async Task<int> SubmitCompileAsync(int waitSeconds, bool fresh)
	{
		var request = new TaskRequest
		{
			Id = TaskIdGenerator.NewId(),
			Kind = "compile",
			Fresh = fresh,
			AgentSessionId = _session ?? "",
			Note = _note ?? ""
		};
		return await SubmitRequestAsync(request, waitSeconds);
	}

	public async Task<int> SubmitReleaseAsync(int waitSeconds)
	{
		var request = new TaskRequest
		{
			Id = TaskIdGenerator.NewId(),
			Kind = "release",
			AgentSessionId = _session ?? "",
			Note = _note ?? ""
		};
		return await SubmitRequestAsync(request, waitSeconds);
	}

	public async Task<int> SubmitCancelAsync(string taskId, int waitSeconds)
	{
		if (!IsSafeTaskId(taskId)) return WriteError("invalid_task_id", "Invalid target task id.");
		var reply = await new QueueClient(_projectRoot).CallAsync(new QueueMessage { Op = "cancel", TaskId = taskId, Session = _session ?? "" });
		if (!reply.Ok) return WriteError(reply.Code, reply.Message);
		WriteResult(JsonSerializer.Serialize(new { Id = taskId, Kind = "cancel", Status = reply.Code, Accepted = true,
			TargetStatus = reply.Task?.Status, Resume = "agentbridge wait " + taskId }, JsonSupport.Task));
		return 0;
	}

	public async Task<int> SubmitPlayAsync(int seconds, int waitSeconds)
	{
		var request = new TaskRequest
		{
			Id = TaskIdGenerator.NewId(),
			Kind = "play",
			PlaySeconds = seconds,
			AgentSessionId = _session ?? "",
			Note = _note ?? ""
		};
		return await SubmitRequestAsync(request, waitSeconds);
	}

	public async Task<int> SubmitStopplayAsync(int waitSeconds)
	{
		var request = new TaskRequest
		{
			Id = TaskIdGenerator.NewId(),
			Kind = "stopplay",
			AgentSessionId = _session ?? "",
			Note = _note ?? ""
		};
		return await SubmitRequestAsync(request, waitSeconds);
	}

	public async Task<int> SubmitTestsAsync(
		string mode,
		string[] assemblies,
		string[] tests,
		string[] categories,
		int waitSeconds,
		bool fresh)
	{
		var request = new TaskRequest
		{
			Id = TaskIdGenerator.NewId(),
			Kind = "tests",
			TestMode = mode,
			AssemblyNames = assemblies,
			TestNames = tests,
			CategoryNames = categories,
			Fresh = fresh,
			AgentSessionId = _session ?? "",
			Note = _note ?? ""
		};
		return await SubmitRequestAsync(request, waitSeconds);
	}

	public async Task<int> WaitForTaskAsync(string taskId, int waitSeconds, string kind)
	{
		if (!IsSafeTaskId(taskId)) return WriteError("invalid_task_id", "Invalid task id.");
		var watch = System.Diagnostics.Stopwatch.StartNew();
		var client = new QueueClient(_projectRoot);
		var health = BridgeInspector.Inspect(_projectRoot);
		var nextHealth = TimeSpan.Zero;
		int posts = 0, manualStops = 0;
		while (true)
		{
			var reply = await client.CallAsync(new QueueMessage { Op = "get", TaskId = taskId });
			if (!reply.Ok) return WriteError(reply.Code, reply.Message);
			var task = reply.Task!;
			if (QueueFiles.Terminal(task.Status))
			{
				WriteResult(reply.ResultJson);
				int code = ClassifyResult(reply.ResultJson);
				_telemetry.Write("cli_exit", _session, taskId, new Dictionary<string, object?> { ["Cmd"] = kind, ["Code"] = code, ["Status"] = task.Status });
				return code;
			}
			if (watch.Elapsed >= nextHealth)
			{
				health = BridgeInspector.Inspect(_projectRoot);
				nextHealth = watch.Elapsed.Add(TimeSpan.FromSeconds(3));
				if (WakePolicy.CanRecover(health) && health.HeartbeatAgeMs >= WakePolicy.StaleThresholdMs && posts < WakePolicy.MaxPostAttempts)
				{
					posts++;
					WriteWakeEvent(taskId, "post", health.HeartbeatAgeMs, EditorWaker.TryPost(health.Bridge!.EditorPid));
				}
			}
			if (watch.Elapsed.TotalSeconds >= waitSeconds)
			{
				int code = waitSeconds == 0 ? 0 : 2;
				WriteResult(JsonSerializer.Serialize(new { Id = taskId, Kind = task.Kind, Status = task.Status, Accepted = true,
					WaitExpired = waitSeconds > 0, CancelRequested = task.CancelRequested, EditorAvailable = health.BridgeReady && health.Bridge?.Capabilities.Contains(QueueFiles.Capability) == true,
					EditorState = health.BridgeReady && health.Bridge?.Capabilities.Contains(QueueFiles.Capability) != true ? "executor_update_required" : health.Code, Reason = task.BatchRequestId.Length > 0 ? "batch_step_pending" : "awaiting_executor",
					Resume = "agentbridge wait " + taskId }, JsonSupport.Task));
				_telemetry.Write("cli_exit", _session, taskId, new Dictionary<string, object?> { ["Cmd"] = kind, ["Code"] = code, ["Status"] = task.Status, ["EditorState"] = health.Code });
				return code;
			}
			if (ManualPlayPolicy.ShouldStop(health, kind, manualStops))
			{
				manualStops++;
				await StopManualPlayAsync(taskId, DateTime.UtcNow.AddSeconds(Math.Max(0, waitSeconds - watch.Elapsed.TotalSeconds)));
			}
			await Task.Delay(250);
		}
	}

	private async Task StopManualPlayAsync(string forTaskId, DateTime clientDeadline)
	{
		var stopId = TaskIdGenerator.NewId();
		var request = new TaskRequest
		{
			Id = stopId,
			Kind = "stopplay",
			AgentSessionId = _session ?? "",
			Note = "auto-stop manual play for " + forTaskId
		};
		await new QueueClient(_projectRoot).CallAsync(new QueueMessage { Op = "submit", TaskId = stopId,
			RequestJson = JsonSerializer.Serialize(request, JsonSupport.Task) });

		var journalFile = Path.Combine(_paths.Journal, stopId + ".json");
		var deadline = DateTime.UtcNow.AddSeconds(StopManualPlaySeconds);
		if (clientDeadline < deadline) deadline = clientDeadline;
		var status = "timeout";
		while (DateTime.UtcNow < deadline)
		{
			if (TryReadFile(journalFile, out var json) && TryGetTerminalStatus(json, out var terminal))
			{
				status = terminal;
				break;
			}

			await Task.Delay(250);
		}

		if (status != "success")
		{
			Console.Error.WriteLine("[agentbridge] auto stopplay " + stopId + " ended as " + status
				+ "; the task stays queued");
		}

		_telemetry.Write("cli_autostop", _session, stopId, new Dictionary<string, object?>
		{
			["For"] = forTaskId,
			["Status"] = status
		});
	}

	private void WriteWakeEvent(string taskId, string action, long? heartbeatAgeMs, bool sent)
	{
		_telemetry.Write("cli_wake", _session, taskId, new Dictionary<string, object?>
		{
			["Action"] = action,
			["AgeMs"] = heartbeatAgeMs ?? 0,
			["Sent"] = sent
		});
	}

	private static string StatusOf(string json)
	{
		try
		{
			using var document = JsonDocument.Parse(json);
			return document.RootElement.TryGetProperty("Status", out var element)
				? element.GetString() ?? ""
				: "";
		}
		catch
		{
			return "";
		}
	}

	private static string DescribeQueuePosition(BridgeHealth health, string taskId, int queuedSeconds)
	{
		var queue = health.Bridge?.QueuedTasks ?? Array.Empty<QueuedTaskStatus>();
		var suffix = ManualPlayPolicy.IsManualPlaying(health.Bridge)
			? ", editor playing (manual), run 'agentbridge stopplay' to take over"
			: "";
		if (!string.IsNullOrEmpty(health.Bridge?.QueueBlockReason)) suffix += ", blocked: " + health.Bridge.QueueBlockReason;
		foreach (var entry in queue)
		{
			if (!string.Equals(entry.Id, taskId, StringComparison.Ordinal))
			{
				continue;
			}

			var holder = string.IsNullOrEmpty(health.Bridge?.HolderAgentSessionId)
				? "none"
				: health.Bridge!.HolderAgentSessionId;
			return "queued " + queuedSeconds + "s, position " + entry.Position + "/" + queue.Length
				+ ", holder " + holder + suffix;
		}

		return "queued " + queuedSeconds + "s" + suffix;
	}

	internal static int ClassifyResult(string json)
	{
		try
		{
			using var document = JsonDocument.Parse(json);
			var root = document.RootElement;
			if (!root.TryGetProperty("Status", out var statusElement))
			{
				return 1;
			}

			var status = statusElement.GetString();
			if (status != "success")
			{
				return 1;
			}

			if (root.TryGetProperty("Kind", out var kindElement)
				&& kindElement.GetString() == "tests")
			{
				if (!root.TryGetProperty("Tests", out var testsElement)
					|| testsElement.ValueKind != JsonValueKind.Object
					|| GetInt(testsElement, "total") <= 0
					|| (testsElement.TryGetProperty("aborted", out var aborted) && aborted.ValueKind == JsonValueKind.True))
				{
					return 1;
				}
				var failed = GetInt(testsElement, "failed");
				var inconclusive = GetInt(testsElement, "inconclusive");
				if (failed > 0 || inconclusive > 0)
				{
					return 1;
				}
			}

			return 0;
		}
		catch
		{
			return 1;
		}
	}

	// The window token travels with the task, not with the client: the package checks it again
	// immediately before cache, attach or execution.
	private void Decorate(TaskRequest request)
	{
		request.CoordinationWindowToken = CoordinationWindowToken ?? "";
		request.CoordinationStepId = CoordinationStepId ?? "";
	}

	private async Task<int> SubmitRequestAsync(TaskRequest request, int waitSeconds)
	{
		Decorate(request);
		var reply = await new QueueClient(_projectRoot).CallAsync(new QueueMessage { Op = "submit", TaskId = request.Id,
			RequestJson = JsonSerializer.Serialize(request, JsonSupport.Task) });
		if (!reply.Ok) return WriteError(reply.Code, reply.Message);
		Console.Error.WriteLine("[agentbridge] accepted " + request.Id);

		_telemetry.Write("cli_submit", _session, request.Id, new Dictionary<string, object?>
		{
			["Cmd"] = request.Kind,
			["Note"] = _note ?? ""
		});

		return await WaitForTaskAsync(request.Id, waitSeconds, request.Kind);
	}

	private static string GetPayloadTaskId(string kind, string sourcePath)
	{
		var fileName = Path.GetFileName(sourcePath);
		if (kind == "ui" && fileName.EndsWith(".ui.json", StringComparison.OrdinalIgnoreCase))
		{
			return fileName[..^8];
		}

		if (kind == "sceneshot" && fileName.EndsWith(".sceneshot.json", StringComparison.OrdinalIgnoreCase))
		{
			return fileName[..^15];
		}

		return Path.GetFileNameWithoutExtension(fileName);
	}

	private static bool IsSafeTaskId(string taskId)
	{
		return !string.IsNullOrWhiteSpace(taskId)
			&& taskId == Path.GetFileName(taskId)
			&& taskId.IndexOfAny(Path.GetInvalidFileNameChars()) < 0;
	}

	private static string ComputeHash(byte[] requestBytes, byte[] payloadBytes)
	{
		using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
		hash.AppendData(requestBytes);
		hash.AppendData(payloadBytes);
		return Convert.ToHexString(hash.GetHashAndReset()).ToLowerInvariant();
	}

	private static bool TryReadFile(string path, out string json)
	{
		try
		{
			json = AgentBridge.Coordination.SharedFile.ReadAllText(path);
			return !string.IsNullOrWhiteSpace(json);
		}
		catch
		{
			json = "";
			return false;
		}
	}

	private static bool TryGetTerminalStatus(string json, out string status)
	{
		try
		{
			using var document = JsonDocument.Parse(json);
			if (document.RootElement.TryGetProperty("Status", out var element))
			{
				status = element.GetString() ?? "";
				return TerminalStatuses.Contains(status);
			}
		}
		catch
		{
		}

		status = "";
		return false;
	}

	private static int GetInt(JsonElement element, string name)
	{
		return element.TryGetProperty(name, out var value) && value.TryGetInt32(out var result)
			? result
			: 0;
	}

	private void WriteResult(string json)
	{
		Console.Out.WriteLine(
			_format == "human"
				? TaskResultFormatter.FormatHuman(json)
				: json.TrimEnd());
	}

	private int WriteError(string code, string message)
	{
		var json = JsonSerializer.Serialize(new
		{
			Ok = false,
			Code = code,
			Message = message
		}, JsonSupport.Task);
		WriteResult(json);
		return 3;
	}
}
