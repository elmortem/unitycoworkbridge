using System.Globalization;
using System.Text;
using System.Text.Json;

namespace AgentBridge.Cli;

internal static class TaskResultFormatter
{
	public static string FormatHuman(string json)
	{
		try
		{
			using var document = JsonDocument.Parse(json);
			return FormatHuman(document.RootElement);
		}
		catch (JsonException error)
		{
			return "agentbridge: invalid result" + Environment.NewLine + "Message: " + error.Message;
		}
	}

	private static string FormatHuman(JsonElement root)
	{
		if (!root.TryGetProperty("Status", out var statusElement))
		{
			return FormatClientError(root);
		}

		var kind = GetString(root, "Kind") ?? "task";
		var status = statusElement.GetString() ?? "unknown";
		var details = new List<string>();
		var id = GetString(root, "Id");
		if (!string.IsNullOrWhiteSpace(id))
		{
			details.Add(id);
		}

		if (root.TryGetProperty("Cached", out var cachedElement) && cachedElement.ValueKind == JsonValueKind.True)
		{
			var sourceTaskId = GetString(root, "SourceTaskId");
			details.Add(string.IsNullOrWhiteSpace(sourceTaskId) ? "cached" : "cached from " + sourceTaskId);
		}

		JsonElement tests = default;
		var hasTests = kind == "tests"
			&& root.TryGetProperty("Tests", out tests)
			&& tests.ValueKind == JsonValueKind.Object;
		if (hasTests)
		{
			details.Add(GetInt(tests, "passed") + " passed");
			details.Add(GetInt(tests, "failed") + " failed");
			details.Add(GetInt(tests, "skipped") + " skipped");
			details.Add(GetInt(tests, "inconclusive") + " inconclusive");
			details.Add(GetInt(tests, "total") + " total");
			if (TryGetDouble(tests, "duration", out var testDuration))
			{
				details.Add(FormatSeconds(testDuration));
			}
		}
		else
		{
			if ((kind == "compile" || status == "compiler_error")
				&& root.TryGetProperty("ForeignErrors", out var foreignErrors)
				&& foreignErrors.ValueKind is JsonValueKind.True or JsonValueKind.False)
			{
				details.Add("foreign errors: " + (foreignErrors.GetBoolean() ? "yes" : "no"));
			}

			if (root.TryGetProperty("Timing", out var timing)
				&& timing.ValueKind == JsonValueKind.Object
				&& TryGetInt(timing, "TotalMs", out var totalMs)
				&& totalMs > 0)
			{
				details.Add(FormatSeconds(totalMs / 1000d));
			}
		}

		var output = new StringBuilder();
		output.Append(kind).Append(": ").Append(status);
		if (details.Count > 0)
		{
			output.Append(" (").Append(string.Join(", ", details)).Append(')');
		}

		if (hasTests)
		{
			AppendTestDetails(output, tests);
		}

		AppendLabeledValue(output, "Result", GetString(root, "ReturnValue"));
		AppendStringArray(output, root, "Logs", "Logs");
		AppendDiagnostics(output, root);
		AppendStringArray(output, root, "Artifacts", "Artifacts");
		AppendContention(output, root);

		return output.ToString();
	}

	private static string FormatClientError(JsonElement root)
	{
		var code = GetString(root, "Code") ?? "unknown";
		var output = new StringBuilder("agentbridge: error (").Append(code).Append(')');
		AppendLabeledValue(output, "Message", GetString(root, "Message"));
		return output.ToString();
	}

	private static void AppendTestDetails(StringBuilder output, JsonElement tests)
	{
		if (tests.TryGetProperty("aborted", out var aborted)
			&& aborted.ValueKind == JsonValueKind.True)
		{
			output.AppendLine().Append("Aborted: yes");
		}

		AppendLabeledValue(output, "Message", GetString(tests, "message"));

		if (!tests.TryGetProperty("failures", out var failures) || failures.ValueKind != JsonValueKind.Array)
		{
			return;
		}

		var wroteHeader = false;
		foreach (var failure in failures.EnumerateArray())
		{
			if (!wroteHeader)
			{
				output.AppendLine().Append("Failures:");
				wroteHeader = true;
			}

			var name = GetString(failure, "name") ?? "unknown test";
			var message = GetString(failure, "message");
			output.AppendLine().Append("- ").Append(name);
			if (!string.IsNullOrWhiteSpace(message))
			{
				output.Append(": ").Append(message);
			}

			AppendIndented(output, GetString(failure, "stacktrace"));
		}
	}

	private static void AppendDiagnostics(StringBuilder output, JsonElement root)
	{
		if (!root.TryGetProperty("Diagnostics", out var diagnostics) || diagnostics.ValueKind != JsonValueKind.Array)
		{
			return;
		}

		var wroteHeader = false;
		foreach (var diagnostic in diagnostics.EnumerateArray())
		{
			if (!wroteHeader)
			{
				output.AppendLine().Append("Diagnostics:");
				wroteHeader = true;
			}

			var file = GetString(diagnostic, "File");
			var severity = GetString(diagnostic, "Severity");
			var code = GetString(diagnostic, "Code");
			var message = GetString(diagnostic, "Message") ?? "";
			output.AppendLine().Append("- ");
			if (!string.IsNullOrWhiteSpace(file))
			{
				output.Append(file);
				if (TryGetInt(diagnostic, "Line", out var line) && line > 0)
				{
					output.Append('(').Append(line);
					if (TryGetInt(diagnostic, "Column", out var column) && column > 0)
					{
						output.Append(',').Append(column);
					}

					output.Append(')');
				}

				output.Append(": ");
			}

			if (!string.IsNullOrWhiteSpace(severity))
			{
				output.Append(severity).Append(' ');
			}

			if (!string.IsNullOrWhiteSpace(code))
			{
				output.Append(code).Append(": ");
			}

			output.Append(message);
		}
	}

	private static void AppendContention(StringBuilder output, JsonElement root)
	{
		if (!root.TryGetProperty("Contention", out var contention) || contention.ValueKind != JsonValueKind.Object)
		{
			return;
		}

		var waiting = GetInt(contention, "WaitingSessions");
		if (waiting <= 0)
		{
			return;
		}

		output.AppendLine()
			.Append("Contention: ")
			.Append(waiting)
			.Append(" waiting, oldest ")
			.Append(GetInt(contention, "OldestWaitSeconds"))
			.Append('s');

		if (!contention.TryGetProperty("Notes", out var notes) || notes.ValueKind != JsonValueKind.Array)
		{
			return;
		}

		foreach (var note in notes.EnumerateArray())
		{
			if (note.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(note.GetString()))
			{
				output.AppendLine().Append("- ").Append(note.GetString());
			}
		}
	}

	private static void AppendStringArray(StringBuilder output, JsonElement root, string propertyName, string label)
	{
		if (!root.TryGetProperty(propertyName, out var values) || values.ValueKind != JsonValueKind.Array)
		{
			return;
		}

		var wroteHeader = false;
		foreach (var value in values.EnumerateArray())
		{
			if (value.ValueKind != JsonValueKind.String || string.IsNullOrWhiteSpace(value.GetString()))
			{
				continue;
			}

			if (!wroteHeader)
			{
				output.AppendLine().Append(label).Append(':');
				wroteHeader = true;
			}

			output.AppendLine().Append("- ").Append(value.GetString());
		}
	}

	private static void AppendLabeledValue(StringBuilder output, string label, string? value)
	{
		if (!string.IsNullOrWhiteSpace(value))
		{
			output.AppendLine().Append(label).Append(": ").Append(value);
		}
	}

	private static void AppendIndented(StringBuilder output, string? value)
	{
		if (string.IsNullOrWhiteSpace(value))
		{
			return;
		}

		foreach (var line in value.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n'))
		{
			output.AppendLine().Append("  ").Append(line);
		}
	}

	private static string? GetString(JsonElement element, string name)
	{
		return element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
			? value.GetString()
			: null;
	}

	private static int GetInt(JsonElement element, string name)
	{
		return TryGetInt(element, name, out var value) ? value : 0;
	}

	private static bool TryGetInt(JsonElement element, string name, out int value)
	{
		value = 0;
		return element.TryGetProperty(name, out var property) && property.TryGetInt32(out value);
	}

	private static bool TryGetDouble(JsonElement element, string name, out double value)
	{
		value = 0;
		return element.TryGetProperty(name, out var property) && property.TryGetDouble(out value);
	}

	private static string FormatSeconds(double seconds)
	{
		return seconds.ToString("0.###", CultureInfo.InvariantCulture) + "s";
	}
}
