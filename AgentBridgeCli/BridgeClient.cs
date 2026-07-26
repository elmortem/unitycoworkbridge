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

	private readonly BridgePaths _paths;

	public BridgeClient(string projectRoot)
	{
		_paths = new BridgePaths(projectRoot);
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

		var payloadName = kind == "ui" ? taskId + ".ui.json" : taskId + ".cs";
		var request = new TaskRequest
		{
			Id = taskId,
			Kind = kind,
			PayloadFile = payloadName
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

	public async Task<int> SubmitCompileAsync(int waitSeconds)
	{
		var request = new TaskRequest
		{
			Id = TaskIdGenerator.NewId(),
			Kind = "compile"
		};
		return await SubmitRequestAsync(request, waitSeconds);
	}

	public async Task<int> SubmitTestsAsync(
		string mode,
		string[] assemblies,
		string[] tests,
		string[] categories,
		int waitSeconds)
	{
		var request = new TaskRequest
		{
			Id = TaskIdGenerator.NewId(),
			Kind = "tests",
			TestMode = mode,
			AssemblyNames = assemblies,
			TestNames = tests,
			CategoryNames = categories
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
		var started = DateTime.UtcNow;
		var nextProgress = TimeSpan.Zero;

		while (true)
		{
			if (TryReadFile(journalFile, out var json) && TryGetTerminalStatus(json, out _))
			{
				Console.Out.WriteLine(json.TrimEnd());
				return ClassifyResult(json);
			}

			var elapsed = DateTime.UtcNow - started;
			if (elapsed.TotalSeconds >= waitSeconds)
			{
				if (TryReadFile(journalFile, out json))
				{
					Console.Out.WriteLine(json.TrimEnd());
				}
				else
				{
					Console.Out.WriteLine(JsonSerializer.Serialize(
						new Dictionary<string, object?>
						{
							["Id"] = taskId,
							["Status"] = "running"
						},
						JsonSupport.Task));
				}

				return 2;
			}

			if (elapsed >= nextProgress)
			{
				Console.Error.WriteLine("[agentbridge] " + taskId + " " + (int)elapsed.TotalSeconds + "s");
				nextProgress += TimeSpan.FromSeconds(5);
			}

			await Task.Delay(250);
		}
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
				Console.Out.WriteLine(JsonSerializer.Serialize(conflict, JsonSupport.Task));
				result = 1;
				return true;
			}

			if (TerminalStatuses.Contains(status))
			{
				Console.Out.WriteLine(json.TrimEnd());
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

	private static int WriteError(string code, string message)
	{
		JsonSupport.Write(new
		{
			Ok = false,
			Code = code,
			Message = message
		});
		return 3;
	}
}
