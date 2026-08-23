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

	private readonly BridgePaths _paths;
	private readonly string _format;
	private readonly string _projectRoot;
	private readonly string? _session;
	private readonly string? _note;

	public BridgeClient(string projectRoot, string format, string? session, string? note)
	{
		_paths = new BridgePaths(projectRoot);
		_format = format;
		_projectRoot = projectRoot;
		_session = session;
		_note = note;
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
				: await WaitForTaskAsync(taskId, waitSeconds);
		}

		Directory.CreateDirectory(_paths.Inbox);
		Directory.CreateDirectory(_paths.Journal);
		AtomicWrite(Path.Combine(_paths.Inbox, payloadName), payloadBytes);
		AtomicWrite(Path.Combine(_paths.Inbox, taskId + ".task.json"), requestBytes);
		return await WaitForTaskAsync(taskId, waitSeconds);
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

	public async Task<int> WaitForTaskAsync(string taskId, int waitSeconds)
	{
		if (!IsSafeTaskId(taskId))
		{
			return WriteError("invalid_task_id", "Task id contains invalid path characters.");
		}

		var journalFile = Path.Combine(_paths.Journal, taskId + ".json");
		DateTime? runningSince = null;
		var queuedSince = DateTime.UtcNow;
		var nextProgress = TimeSpan.Zero;
		var nextQueueReport = TimeSpan.Zero;
		var attempts = new EditorWakeAttempts();
		var nextHealthPoll = DateTime.MinValue;
		BridgeHealth? lastHealth = null;

		while (true)
		{
			var hasRecord = TryReadFile(journalFile, out var json);
			if (hasRecord && TryGetTerminalStatus(json, out _))
			{
				WriteResult(json);
				return ClassifyResult(json);
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
					return WriteError("bridge_unavailable", "Bridge became unavailable while the task was waiting: " + pulse.Code);
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
					}
					else if (action == WakeAction.Focus)
					{
						attempts.FocusAttempts++;
						attempts.LastAttemptUtc = now;
						EditorWaker.TryFocus(pid);
						Console.Error.WriteLine("[agentbridge] editor still asleep, focus poke");
					}
					else if (attempts.Exhausted)
					{
						return WriteError(
							"bridge_asleep",
							"The Unity editor stopped ticking and did not wake up. Focus the editor window, "
							+ "and set Preferences > General > Interaction Mode to No Throttling.");
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
					return 2;
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
				return WriteError("task_not_found", "No queued or recorded task with id " + taskId + ".");
			}

			var queued = now - queuedSince;
			if (queued.TotalSeconds >= QueueWaitCapSeconds)
			{
				WriteResult(JsonSerializer.Serialize(
					new Dictionary<string, object?>
					{
						["Id"] = taskId,
						["Status"] = "queued"
					},
					JsonSupport.Task));
				return 2;
			}

			if (queued >= nextQueueReport && lastHealth != null)
			{
				nextQueueReport = queued + TimeSpan.FromSeconds(5);
				Console.Error.WriteLine("[agentbridge] " + taskId + " " + DescribeQueuePosition(lastHealth, taskId, (int)queued.TotalSeconds));
			}

			await Task.Delay(250);
		}
	}

	private static string DescribeQueuePosition(BridgeHealth health, string taskId, int queuedSeconds)
	{
		var queue = health.Bridge?.QueuedTasks ?? Array.Empty<QueuedTaskStatus>();
		foreach (var entry in queue)
		{
			if (!string.Equals(entry.Id, taskId, StringComparison.Ordinal))
			{
				continue;
			}

			var holder = string.IsNullOrEmpty(health.Bridge?.HolderAgentSessionId)
				? "none"
				: health.Bridge!.HolderAgentSessionId;
			return "queued " + queuedSeconds + "s, position " + entry.Position + "/" + queue.Length + ", holder " + holder;
		}

		return "queued " + queuedSeconds + "s";
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
		return await WaitForTaskAsync(request.Id, waitSeconds);
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
