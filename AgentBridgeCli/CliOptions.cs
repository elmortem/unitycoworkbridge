namespace AgentBridge.Cli;

internal sealed class CliOptions
{
	public string? ProjectPath { get; private set; }
	public int WaitSeconds { get; private set; } = 110;
	public int Seconds { get; private set; }
	public string Format { get; private set; } = "json";
	public string? Session { get; private set; }
	public string? Note { get; private set; }
	public bool Fresh { get; private set; }

	// coordination-v1
	public string? SpecId { get; private set; }
	public string? RepoRoot { get; private set; }
	public string? ScopeFile { get; private set; }
	public string? PlanFile { get; private set; }
	public string? Owner { get; private set; }
	public string? RequestUuid { get; private set; }
	public string? Token { get; private set; }
	public string? Kind { get; private set; }
	public string? TargetSession { get; private set; }
	public string? Reason { get; private set; }
	public string? CoordinationWindowToken { get; private set; }
	public string? CoordinationStepId { get; private set; }
	public long AfterRevision { get; private set; }

	public List<string> Arguments { get; } = new();
	public string? Error { get; private set; }

	private const string SessionError = "--session must be 1-64 characters of A-Za-z0-9_-";
	private const string NoteError = "--note must be 1 to 200 characters";
	private const string SecondsError = "--seconds requires an integer from 1 to 86400";
	private const string AfterError = "--after requires a non-negative revision number";

	public static CliOptions Parse(string[] args)
	{
		var options = new CliOptions();

		for (var index = 0; index < args.Length; index++)
		{
			var argument = args[index];
			if (argument == "--fresh")
			{
				options.Fresh = true;
				continue;
			}

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

			if (TryTakeText(options, args, ref index, argument, "--spec", value => options.SpecId = value, out var handled))
			{
				if (!handled)
				{
					return options;
				}

				continue;
			}

			if (TryTakeText(options, args, ref index, argument, "--repo", value => options.RepoRoot = value, out handled))
			{
				if (!handled)
				{
					return options;
				}

				continue;
			}

			if (TryTakeText(options, args, ref index, argument, "--scope", value => options.ScopeFile = value, out handled))
			{
				if (!handled)
				{
					return options;
				}

				continue;
			}

			if (TryTakeText(options, args, ref index, argument, "--plan", value => options.PlanFile = value, out handled))
			{
				if (!handled)
				{
					return options;
				}

				continue;
			}

			if (TryTakeText(options, args, ref index, argument, "--owner", value => options.Owner = value, out handled))
			{
				if (!handled)
				{
					return options;
				}

				continue;
			}

			if (TryTakeText(options, args, ref index, argument, "--request", value => options.RequestUuid = value, out handled))
			{
				if (!handled)
				{
					return options;
				}

				continue;
			}

			if (TryTakeText(options, args, ref index, argument, "--token", value => options.Token = value, out handled))
			{
				if (!handled)
				{
					return options;
				}

				continue;
			}

			if (TryTakeText(options, args, ref index, argument, "--kind", value => options.Kind = value, out handled))
			{
				if (!handled)
				{
					return options;
				}

				continue;
			}

			if (TryTakeText(options, args, ref index, argument, "--target-session", value => options.TargetSession = value, out handled))
			{
				if (!handled)
				{
					return options;
				}

				continue;
			}

			if (TryTakeText(options, args, ref index, argument, "--reason", value => options.Reason = value, out handled))
			{
				if (!handled)
				{
					return options;
				}

				continue;
			}

			if (TryTakeText(options, args, ref index, argument, "--coord-window",
					value => options.CoordinationWindowToken = value, out handled))
			{
				if (!handled)
				{
					return options;
				}

				continue;
			}

			if (TryTakeText(options, args, ref index, argument, "--coord-step",
					value => options.CoordinationStepId = value, out handled))
			{
				if (!handled)
				{
					return options;
				}

				continue;
			}

			if (argument == "--after")
			{
				if (!TryTakeValue(args, ref index, out var value) || !TrySetAfter(options, value))
				{
					options.Error = AfterError;
					return options;
				}

				continue;
			}

			if (argument.StartsWith("--after=", StringComparison.Ordinal))
			{
				if (!TrySetAfter(options, argument[8..]))
				{
					options.Error = AfterError;
					return options;
				}

				continue;
			}

			// An unknown flag is a usage error, not a positional argument: a misspelled option must
			// never become the name of a task file. Two flags that are really commands and the
			// per-command filters of 'tests' stay positional, because their own parser owns them.
			if (argument.StartsWith("--", StringComparison.Ordinal) && !IsCommandLocal(argument))
			{
				options.Error = "unknown option: " + argument;
				return options;
			}

			options.Arguments.Add(argument);
		}

		if (options.Arguments.Count > 0 && options.Arguments[0] == "compile" && options.Fresh && string.IsNullOrWhiteSpace(options.Note))
			options.Error = "compile --fresh requires --note with a diagnostic reason; after edits or waiting use ordinary compile";
		return options;
	}

	// Both spellings of a plain text option in one place. handled=false means the flag matched but
	// its value was missing, and options.Error already says so.
	private static bool TryTakeText(
		CliOptions options,
		string[] args,
		ref int index,
		string argument,
		string name,
		Action<string> assign,
		out bool handled)
	{
		handled = true;
		if (argument == name)
		{
			if (!TryTakeValue(args, ref index, out var value))
			{
				options.Error = name + " requires a value";
				handled = false;
			}
			else
			{
				assign(value);
			}

			return true;
		}

		var prefix = name + "=";
		if (!argument.StartsWith(prefix, StringComparison.Ordinal))
		{
			return false;
		}

		var inline = argument[prefix.Length..];
		if (string.IsNullOrWhiteSpace(inline))
		{
			options.Error = name + " requires a value";
			handled = false;
			return true;
		}

		assign(inline);
		return true;
	}

	private static bool IsCommandLocal(string argument)
	{
		var name = argument;
		var equals = argument.IndexOf('=', StringComparison.Ordinal);
		if (equals > 0)
		{
			name = argument[..equals];
		}

		return name is "--help" or "--version" or "--mode" or "--assembly" or "--test" or "--category";
	}

	private static bool TrySetAfter(CliOptions options, string value)
	{
		if (!long.TryParse(value, out var revision) || revision < 0)
		{
			return false;
		}

		options.AfterRevision = revision;
		return true;
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
