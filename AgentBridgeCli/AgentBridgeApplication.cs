using System.Reflection;

namespace AgentBridge.Cli;

internal static class AgentBridgeApplication
{
	public static async Task<int> RunAsync(string[] args)
	{
		Console.OutputEncoding = System.Text.Encoding.UTF8;
		var options = CliOptions.Parse(args);
		if (options.Error != null)
		{
			return WriteError("bad_usage", options.Error);
		}

		if (options.Arguments.Count == 0 || options.Arguments[0] is "help" or "--help" or "-h")
		{
			WriteHelp();
			return options.Arguments.Count == 0 ? 3 : 0;
		}

		if (options.Arguments[0] is "--version" or "version")
		{
			Console.Out.WriteLine(GetVersion());
			return 0;
		}

		var projectRoot = ProjectLocator.Resolve(options.ProjectPath, Environment.CurrentDirectory);
		if (projectRoot == null)
		{
			return WriteError(
				"project_not_found",
				options.ProjectPath == null
					? "No Unity project found at or above the current directory. Use --project <path>."
					: "The --project path is not a Unity project.");
		}

		var command = options.Arguments[0];
		var commandArguments = options.Arguments.Skip(1).ToArray();
		var health = BridgeInspector.Inspect(projectRoot);

		if (command == "status")
		{
			WriteHealth(health, options.Format);
			return health.BridgeReady ? 0 : 3;
		}

		if (command == "doctor")
		{
			WriteDoctor(health, options.Format);
			return health.BridgeReady && health.CSharpReady ? 0 : 1;
		}

		if (!health.BridgeReady)
		{
			WriteHealth(health, options.Format);
			return 3;
		}

		var client = new BridgeClient(projectRoot);
		switch (command)
		{
			case "csharp":
				if (commandArguments.Length != 1)
				{
					return WriteError("bad_usage", "usage: agentbridge csharp <file> [--project <path>] [--wait <seconds>]");
				}

				if (!health.CSharpReady)
				{
					return WriteError("roslyn_not_ready", "Roslyn is not ready. Run agentbridge doctor.");
				}

				return await client.SubmitPayloadAsync("csharp", commandArguments[0], options.WaitSeconds);

			case "ui":
				if (commandArguments.Length != 1)
				{
					return WriteError("bad_usage", "usage: agentbridge ui <file.ui.json> [--project <path>] [--wait <seconds>]");
				}

				return await client.SubmitPayloadAsync("ui", commandArguments[0], options.WaitSeconds);

			case "compile":
				if (commandArguments.Length != 0)
				{
					return WriteError("bad_usage", "usage: agentbridge compile [--project <path>] [--wait <seconds>]");
				}

				return await client.SubmitCompileAsync(options.WaitSeconds);

			case "tests":
				if (!TryParseTests(commandArguments, out var mode, out var assemblies, out var tests, out var categories, out var error))
				{
					return WriteError("bad_usage", error);
				}

				return await client.SubmitTestsAsync(mode, assemblies, tests, categories, options.WaitSeconds);

			case "wait":
				if (commandArguments.Length != 1)
				{
					return WriteError("bad_usage", "usage: agentbridge wait <TaskId> [--project <path>] [--wait <seconds>]");
				}

				return await client.WaitForTaskAsync(commandArguments[0], options.WaitSeconds);

			default:
				return WriteError("bad_usage", "Unknown command: " + command);
		}
	}

	private static bool TryParseTests(
		string[] args,
		out string mode,
		out string[] assemblies,
		out string[] tests,
		out string[] categories,
		out string error)
	{
		mode = "EditMode";
		var assemblyList = new List<string>();
		var testList = new List<string>();
		var categoryList = new List<string>();

		for (var index = 0; index < args.Length; index++)
		{
			var argument = args[index];
			if (argument is not ("--mode" or "--assembly" or "--test" or "--category") || index + 1 >= args.Length)
			{
				assemblies = Array.Empty<string>();
				tests = Array.Empty<string>();
				categories = Array.Empty<string>();
				error = "usage: agentbridge tests [--mode EditMode|PlayMode] [--assembly A] [--test T] [--category C]";
				return false;
			}

			var value = args[++index];
			switch (argument)
			{
				case "--mode":
					if (value is not ("EditMode" or "PlayMode"))
					{
						assemblies = Array.Empty<string>();
						tests = Array.Empty<string>();
						categories = Array.Empty<string>();
						error = "--mode must be EditMode or PlayMode";
						return false;
					}

					mode = value;
					break;
				case "--assembly":
					assemblyList.Add(value);
					break;
				case "--test":
					testList.Add(value);
					break;
				case "--category":
					categoryList.Add(value);
					break;
			}
		}

		assemblies = assemblyList.ToArray();
		tests = testList.ToArray();
		categories = categoryList.ToArray();
		error = "";
		return true;
	}

	private static void WriteHealth(BridgeHealth health, string format)
	{
		if (format == "human")
		{
			Console.Out.WriteLine(health.BridgeReady ? "Agent Bridge is ready." : "Agent Bridge is unavailable: " + health.Code);
			Console.Out.WriteLine("Project: " + health.ProjectPath);
			if (health.Bridge != null)
			{
				Console.Out.WriteLine("Package: " + health.Bridge.PackageVersion);
				Console.Out.WriteLine("Unity: " + health.Bridge.UnityVersion);
				Console.Out.WriteLine("Roslyn: " + (health.Bridge.RoslynReady ? "ready" : "not ready"));
				Console.Out.WriteLine("Active task: " + (health.Bridge.ActiveTaskId ?? "none"));
			}

			return;
		}

		JsonSupport.Write(health);
	}

	private static void WriteDoctor(BridgeHealth health, string format)
	{
		if (format == "human")
		{
			WriteHealth(health, format);
			Console.Out.WriteLine("CLI version: " + GetVersion());
			Console.Out.WriteLine("CLI path: " + (Environment.ProcessPath ?? "unknown"));
			foreach (var problem in health.Problems)
			{
				Console.Out.WriteLine("- " + problem);
			}

			return;
		}

		JsonSupport.Write(new
		{
			Ok = health.BridgeReady && health.CSharpReady,
			CliVersion = GetVersion(),
			CliPath = Environment.ProcessPath,
			ExpectedProtocolVersion = BridgeConstants.ProtocolVersion,
			Health = health
		});
	}

	private static string GetVersion()
	{
		return Assembly.GetExecutingAssembly().GetName().Version?.ToString(3) ?? "0.0.0";
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

	private static void WriteHelp()
	{
		Console.Out.WriteLine(
			"""
			Unity Agent Bridge CLI

			usage: agentbridge <command> [arguments] [--project <path>] [--wait <seconds>]

			commands:
			  status
			  doctor
			  csharp <file.cs>
			  ui <file.ui.json>
			  compile
			  tests [--mode EditMode|PlayMode] [--assembly A] [--test T] [--category C]
			  wait <TaskId>

			global options:
			  --project <path>   Unity project root; otherwise discovered from cwd
			  --wait <seconds>   client wait timeout, default 110
			  --format <value>   json or human for status and doctor
			  --version
			""");
	}
}
