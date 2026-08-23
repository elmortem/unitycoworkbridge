using System.Globalization;
using System.Text;
using System.Text.Json;

namespace AgentBridge.Cli;

// The client half of the bridge telemetry. Only the CLI knows how long an agent really waited
// and what exit code it walked away with; the editor writes its own half of the same story into
// a sibling file, and the two are joined by task id.
internal sealed class TelemetryLog
{
	private const int WriteAttempts = 3;
	private const int RetryDelayMs = 20;
	private const int MaxTextLength = 200;

	private static readonly UTF8Encoding Utf8 = new(false);

	private readonly string _logsRoot;
	private readonly bool _enabled;

	public TelemetryLog(string projectRoot, bool enabled)
	{
		_logsRoot = Path.Combine(projectRoot, "Logs");
		_enabled = enabled;
	}

	public void Write(string eventName, string? session, string? taskId, Dictionary<string, object?> fields)
	{
		if (!_enabled)
		{
			return;
		}

		try
		{
			var nowUtc = DateTime.UtcNow;
			var payload = new Dictionary<string, object?>
			{
				["T"] = new DateTimeOffset(nowUtc, TimeSpan.Zero).ToUnixTimeMilliseconds(),
				["W"] = "client",
				["E"] = eventName,
				["S"] = Trim(session),
				["Id"] = Trim(taskId)
			};

			foreach (var pair in fields)
			{
				payload[pair.Key] = pair.Value is string text ? Trim(text) : pair.Value;
			}

			Directory.CreateDirectory(_logsRoot);
			var path = Path.Combine(
				_logsRoot,
				"AgentBridge-client-" + nowUtc.ToString("yyyyMMdd", CultureInfo.InvariantCulture) + ".jsonl");
			Append(path, JsonSerializer.Serialize(payload, JsonSupport.Task));
		}
		catch
		{
		}
	}

	private static string Trim(string? value)
	{
		if (string.IsNullOrEmpty(value))
		{
			return "";
		}

		return value.Length > MaxTextLength ? value[..MaxTextLength] : value;
	}

	private static void Append(string path, string line)
	{
		var bytes = Utf8.GetBytes(line + "\n");

		for (var attempt = 0; attempt < WriteAttempts; attempt++)
		{
			try
			{
				using var stream = new FileStream(path, FileMode.Append, FileAccess.Write, FileShare.ReadWrite);
				stream.Write(bytes, 0, bytes.Length);
				return;
			}
			catch (IOException)
			{
				Thread.Sleep(RetryDelayMs);
			}
		}
	}
}
