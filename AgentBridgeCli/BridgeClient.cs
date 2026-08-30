using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace AgentBridge.Cli;

internal sealed class BridgeClient
{
	private static readonly HashSet<string> TerminalStatuses = new(StringComparer.Ordinal)
	{
		"success",
		"test_failure",
		"compiler_error",
		"runtime_error",
		"timeout",
		"canceled",
		"interrupted_by_domain_reload",
		"rejected"
	};

	private const int QueueWaitCapSeconds = 3600;

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
		var requestBytes = JsonSerializer.SerializeToUtf8Bytes(request, JsonSupport.Task);
		var payloadBytes = await File.ReadAllBytesAsync(fullSourcePath);
		var expectedHash = ComputeHash(requestBytes, payloadBytes);

		if (TryResolveExisting(taskId, kind, expectedHash, out var replay))
		{
			return replay.HasValue
				? replay.Value
				: await WaitForTaskAsync(taskId, waitSeconds, kind);
		}

		Directory.CreateDirectory(_paths.Inbox);
		Directory.CreateDirectory(_paths.Journal);
		AtomicWrite(Path.Combine(_paths.Inbox, payloadName), payloadBytes);
		AtomicWrite(Path.Combine(_paths.Inbox, taskId + ".task.json"), requestBytes);

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
		DateTime? runningSince = null;
		var queuedSince = DateTime.UtcNow;
		var nextProgress = TimeSpan.Zero;
		var nextQueueReport = TimeSpan.Zero;
		var attempts = new EditorWakeAttempts();
		var nextHealthPoll = DateTime.MinValue;
		var manualStops = 0;
		BridgeHealth? lastHealth = null;

		// Every exit from this method is an answer to the agent, and the whole point of the
		// client half of the telemetry is that no answer leaves without being recorded.
		int Complete(int code, string status)
		{
			var finishedUtc = DateTime.UtcNow;
			var runningMs = runningSince == null
				? 0
				: (long)(finishedUtc - runningSince.Value).TotalMilliseconds;
			var queuedMs = (long)((runningSince ?? finishedUtc) - queuedSince).TotalMilliseconds;

			_telemetry.Write("cli_exit", _session, taskId, new Dictionary<string, object?>
			{
				["Cmd"] = kind,
				["Code"] = code,
				["Status"] = status,
				["QueuedMs"] = queuedMs,
				["RunningMs"] = runningMs,
				["Posts"] = attempts.PostAttempts,
				["Focuses"] = attempts.FocusAttempts
			});

			return code;
		}

		if (!IsSafeTaskId(taskId))
		{
			return Complete(
				WriteError("invalid_task_id", "Task id contains invalid path characters."),
				"invalid_task_id");
		}

		var journalFile = Path.Combine(_paths.Journal, taskId + ".json");

		while (true)
		{
			var hasRecord = TryReadFile(journalFile, out var json);
			if (hasRecord && TryGetTerminalStatus(json, out _))
			{
				WriteResult(json);
				return Complete(ClassifyResult(json), StatusOf(json));
			}

			var now = DateTime.UtcNow;

			// A running task needs the same watchdog as a queued one: the editor can fall asleep
			// mid-run, and then nothing but an external poke gets the main loop ticking again.
			if (now >= nextHealthPoll)
			{
				nextHealthPoll = now.AddSeconds(3);
				var pulse = BridgeInspector.Inspect(_projectRoot);
				lastHealth = pulse;
				var asleep = pulse.Problems.Contains("heartbeat_stale");

				if (!pulse.BridgeReady && !asleep)
				{
					return Complete(
						WriteError("bridge_unavailable", "Bridge became unavailable while the task was waiting: " + pulse.Code),
						"bridge_unavailable");
				}

				if (asleep)
				{
					var pid = pulse.Bridge?.EditorPid ?? 0;
					var action = WakePolicy.Decide(
						pulse.HeartbeatAgeMs,
						EditorWaker.IsEditorForeground(pid),
						attempts.PostAttempts,
						attempts.FocusAttempts,
						(now - attempts.LastAttemptUtc).TotalSeconds);

					if (action == WakeAction.Post)
					{
						attempts.PostAttempts++;
						attempts.LastAttemptUtc = now;
						EditorWaker.TryPost(pid);
						Console.Error.WriteLine("[agentbridge] editor asleep for "
							+ (pulse.HeartbeatAgeMs ?? 0) / 1000 + "s, waking (post #" + attempts.PostAttempts + ")");
						WriteWakeEvent(taskId, "post", pulse.HeartbeatAgeMs);
					}
					else if (action == WakeAction.Focus)
					{
						attempts.FocusAttempts++;
						attempts.LastAttemptUtc = now;
						EditorWaker.TryFocus(pid);
						Console.Error.WriteLine("[agentbridge] editor still asleep, focus poke");
						WriteWakeEvent(taskId, "focus", pulse.HeartbeatAgeMs);
					}
					else if (attempts.Exhausted)
					{
						return Complete(
							WriteError(
								"bridge_asleep",
								"The Unity editor stopped ticking and did not wake up. Focus the editor window, "
								+ "and set Preferences > General > Interaction Mode to No Throttling."),
							"bridge_asleep");
					}
				}
				else
				{
					attempts.PostAttempts = 0;
					attempts.FocusAttempts = 0;
				}
			}

			// The client budget covers the task itself. Time spent behind another agent session
			// in the editor queue is waited out separately, against the queue cap.
			if (hasRecord)
			{
				runningSince ??= now;
				var running = now - runningSince.Value;
				if (running.TotalSeconds >= waitSeconds)
				{
					WriteResult(json);
					return Complete(2, "running");
				}

				if (running >= nextProgress)
				{
					Console.Error.WriteLine("[agentbridge] " + taskId + " running " + (int)running.TotalSeconds + "s");
					nextProgress += TimeSpan.FromSeconds(5);
				}

				await Task.Delay(250);
				continue;
			}

			// No journal record and no task file means there is nothing in the editor queue to
			// wait for: an unknown id must not consume the whole queue budget.
			if (!File.Exists(Path.Combine(_paths.Inbox, taskId + ".task.json")))
			{
				return Complete(
					WriteError("task_not_found", "No queued or recorded task with id " + taskId + "."),
					"task_not_found");
			}

			// A play mode nobody owns outranks nothing: the coordinator only takes stopplay out of
			// the queue while it runs, so the task would sit here until the cap. Clearing the last
			// health forces a fresh status read before another takeover can be decided.
			if (ManualPlayPolicy.ShouldStop(lastHealth, kind, manualStops))
			{
				manualStops++;
				Console.Error.WriteLine("[agentbridge] " + taskId
					+ " editor is in play mode without an agent session; stopping it (stopplay #" + manualStops + ")");
				await StopManualPlayAsync(taskId);
				lastHealth = null;
				nextHealthPoll = DateTime.MinValue;
				continue;
			}

			var queued = now - queuedSince;
			if (queued.TotalSeconds >= QueueWaitCapSeconds)
			{
				var payload = new Dictionary<string, object?>
				{
					["Id"] = taskId,
					["Status"] = "queued"
				};
				if (ManualPlayPolicy.IsManualPlaying(lastHealth?.Bridge))
				{
					payload["Reason"] = "editor_playing_manual";
				}

				WriteResult(JsonSerializer.Serialize(payload, JsonSupport.Task));
				return Complete(2, "queued");
			}

			if (queued >= nextQueueReport && lastHealth != null)
			{
				nextQueueReport = queued + TimeSpan.FromSeconds(5);
				Console.Error.WriteLine("[agentbridge] " + taskId + " " + DescribeQueuePosition(lastHealth, taskId, (int)queued.TotalSeconds));
			}

			await Task.Delay(250);
		}
	}

	// The stopplay is an implementation detail of waiting for the original task, so it keeps its
	// whole life on stderr: stdout carries exactly one result, and that result is the agent's task.
	private async Task StopManualPlayAsync(string forTaskId)
	{
		var stopId = TaskIdGenerator.NewId();
		var request = new TaskRequest
		{
			Id = stopId,
			Kind = "stopplay",
			AgentSessionId = _session ?? "",
			Note = "auto-stop manual play for " + forTaskId
		};
		Directory.CreateDirectory(_paths.Inbox);
		Directory.CreateDirectory(_paths.Journal);
		AtomicWrite(
			Path.Combine(_paths.Inbox, stopId + ".task.json"),
			JsonSerializer.SerializeToUtf8Bytes(request, JsonSupport.Task));

		var journalFile = Path.Combine(_paths.Journal, stopId + ".json");
		var deadline = DateTime.UtcNow.AddSeconds(StopManualPlaySeconds);
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

	private void WriteWakeEvent(string taskId, string action, long? heartbeatAgeMs)
	{
		_telemetry.Write("cli_wake", _session, taskId, new Dictionary<string, object?>
		{
			["Action"] = action,
			["AgeMs"] = heartbeatAgeMs ?? 0
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
				&& kindElement.GetString() == "tests"
				&& root.TryGetProperty("Tests", out var testsElement)
				&& testsElement.ValueKind == JsonValueKind.Object)
			{
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

	private async Task<int> SubmitRequestAsync(TaskRequest request, int waitSeconds)
	{
		Directory.CreateDirectory(_paths.Inbox);
		Directory.CreateDirectory(_paths.Journal);
		var requestBytes = JsonSerializer.SerializeToUtf8Bytes(request, JsonSupport.Task);
		AtomicWrite(Path.Combine(_paths.Inbox, request.Id + ".task.json"), requestBytes);

		_telemetry.Write("cli_submit", _session, request.Id, new Dictionary<string, object?>
		{
			["Cmd"] = request.Kind,
			["Note"] = _note ?? ""
		});

		return await WaitForTaskAsync(request.Id, waitSeconds, request.Kind);
	}

	private bool TryResolveExisting(string taskId, string kind, string expectedHash, out int? result)
	{
		var journalFile = Path.Combine(_paths.Journal, taskId + ".json");
		if (!TryReadFile(journalFile, out var json))
		{
			result = null;
			return false;
		}

		try
		{
			using var document = JsonDocument.Parse(json);
			var root = document.RootElement;
			var existingHash = root.TryGetProperty("Hash", out var hashElement)
				? hashElement.GetString()
				: null;
			var status = root.TryGetProperty("Status", out var statusElement)
				? statusElement.GetString()
				: null;

			if (string.IsNullOrEmpty(existingHash) || string.IsNullOrEmpty(status))
			{
				result = WriteError("journal_invalid", "Existing journal record is missing Hash or Status.");
				return true;
			}

			if (!string.Equals(existingHash, expectedHash, StringComparison.Ordinal))
			{
				var conflict = new Dictionary<string, object?>
				{
					["Id"] = taskId,
					["Kind"] = kind,
					["Status"] = "rejected",
					["Hash"] = expectedHash,
					["ReturnValue"] = "",
					["Logs"] = new[] { "id_conflict" },
					["Diagnostics"] = Array.Empty<object>(),
					["ForeignErrors"] = false,
					["Artifacts"] = Array.Empty<string>()
				};
				WriteResult(JsonSerializer.Serialize(conflict, JsonSupport.Task));
				result = 1;
				return true;
			}

			if (TerminalStatuses.Contains(status))
			{
				WriteResult(json);
				result = ClassifyResult(json);
				return true;
			}

			result = null;
			return true;
		}
		catch
		{
			result = WriteError("journal_invalid", "Existing journal record is not valid JSON.");
			return true;
		}
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

	private static void AtomicWrite(string destination, byte[] bytes)
	{
		var temporary = destination + "." + Guid.NewGuid().ToString("N") + ".tmp";
		try
		{
			File.WriteAllBytes(temporary, bytes);
			File.Move(temporary, destination, true);
		}
		finally
		{
			if (File.Exists(temporary))
			{
				File.Delete(temporary);
			}
		}
	}

	private static bool TryReadFile(string path, out string json)
	{
		try
		{
			using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
			using var reader = new StreamReader(stream, Encoding.UTF8);
			json = reader.ReadToEnd();
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
