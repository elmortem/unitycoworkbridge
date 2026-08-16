namespace AgentBridge.Cli;

internal sealed class CliOptions
{
	public string? ProjectPath { get; private set; }
	public int WaitSeconds { get; private set; } = 110;
	public int Seconds { get; private set; }
	public string Format { get; private set; } = "json";
	public string? Session { get; private set; }
	public string? Note { get; private set; }
	public List<string> Arguments { get; } = new();
	public string? Error { get; private set; }

	private const string SessionError = "--session must be 1-64 characters of A-Za-z0-9_-";
	private const string NoteError = "--note must be 1 to 200 characters";
	private const string SecondsError = "--seconds requires an integer from 1 to 86400";

	public static CliOptions Parse(string[] args)
	{
		var options = new CliOptions();

		for (var index = 0; index < args.Length; index++)
		{
			var argument = args[index];
			if (argument == "--project")
			{
				if (!TryTakeValue(args, ref index, out var value))
				{
					options.Error = "--project requires a path";
					return options;
				}

				options.ProjectPath = value;
				continue;
			}

			if (argument.StartsWith("--project=", StringComparison.Ordinal))
			{
				options.ProjectPath = argument[10..];
				continue;
			}

			if (argument == "--wait")
			{
				if (!TryTakeValue(args, ref index, out var value) || !TrySetWait(options, value))
				{
					options.Error = "--wait requires an integer from 1 to 86400";
					return options;
				}

				continue;
			}

			if (argument.StartsWith("--wait=", StringComparison.Ordinal))
			{
				if (!TrySetWait(options, argument[7..]))
				{
					options.Error = "--wait requires an integer from 1 to 86400";
					return options;
				}

				continue;
			}

			if (argument == "--seconds")
			{
				if (!TryTakeValue(args, ref index, out var value) || !TrySetSeconds(options, value))
				{
					options.Error = SecondsError;
					return options;
				}

				continue;
			}

			if (argument.StartsWith("--seconds=", StringComparison.Ordinal))
			{
				if (!TrySetSeconds(options, argument[10..]))
				{
					options.Error = SecondsError;
					return options;
				}

				continue;
			}

			if (argument == "--format")
			{
				if (!TryTakeValue(args, ref index, out var value) || !TrySetFormat(options, value))
				{
					options.Error = "--format must be json or human";
					return options;
				}

				continue;
			}

			if (argument.StartsWith("--format=", StringComparison.Ordinal))
			{
				if (!TrySetFormat(options, argument[9..]))
				{
					options.Error = "--format must be json or human";
					return options;
				}

				continue;
			}

			if (argument == "--session")
			{
				if (!TryTakeValue(args, ref index, out var value) || !TrySetSession(options, value))
				{
					options.Error = SessionError;
					return options;
				}

				continue;
			}

			if (argument.StartsWith("--session=", StringComparison.Ordinal))
			{
				if (!TrySetSession(options, argument[10..]))
				{
					options.Error = SessionError;
					return options;
				}

				continue;
			}

			if (argument == "--note")
			{
				if (!TryTakeValue(args, ref index, out var value) || !TrySetNote(options, value))
				{
					options.Error = NoteError;
					return options;
				}

				continue;
			}

			if (argument.StartsWith("--note=", StringComparison.Ordinal))
			{
				if (!TrySetNote(options, argument[7..]))
				{
					options.Error = NoteError;
					return options;
				}

				continue;
			}

			options.Arguments.Add(argument);
		}

		return options;
	}

	private static bool TryTakeValue(string[] args, ref int index, out string value)
	{
		if (index + 1 >= args.Length)
		{
			value = "";
			return false;
		}

		index++;
		value = args[index];
		return !string.IsNullOrWhiteSpace(value);
	}

	private static bool TrySetWait(CliOptions options, string value)
	{
		if (!int.TryParse(value, out var seconds) || seconds < 1 || seconds > 86400)
		{
			return false;
		}

		options.WaitSeconds = seconds;
		return true;
	}

	private static bool TrySetSession(CliOptions options, string value)
	{
		if (value.Length is < 1 or > 64)
		{
			return false;
		}

		foreach (var character in value)
		{
			var allowed = character is >= 'A' and <= 'Z'
				or >= 'a' and <= 'z'
				or >= '0' and <= '9'
				or '_'
				or '-';
			if (!allowed)
			{
				return false;
			}
		}

		options.Session = value;
		return true;
	}

	private static bool TrySetNote(CliOptions options, string value)
	{
		if (value.Length is < 1 or > 200)
		{
			return false;
		}

		options.Note = value;
		return true;
	}

	private static bool TrySetSeconds(CliOptions options, string value)
	{
		if (!int.TryParse(value, out var seconds) || seconds < 1 || seconds > 86400)
		{
			return false;
		}

		options.Seconds = seconds;
		return true;
	}

	private static bool TrySetFormat(CliOptions options, string value)
	{
		if (value != "json" && value != "human")
		{
			return false;
		}

		options.Format = value;
		return true;
	}
}
