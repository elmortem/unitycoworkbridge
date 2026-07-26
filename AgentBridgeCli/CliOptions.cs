namespace AgentBridge.Cli;

internal sealed class CliOptions
{
	public string? ProjectPath { get; private set; }
	public int WaitSeconds { get; private set; } = 110;
	public string Format { get; private set; } = "json";
	public List<string> Arguments { get; } = new();
	public string? Error { get; private set; }

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
