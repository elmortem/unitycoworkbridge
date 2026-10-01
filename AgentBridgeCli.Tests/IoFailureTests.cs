using System.Text;
using System.Text.Json;
using AgentBridge.Cli;
using AgentBridge.Coordination;

internal static class IoFailureTests
{
	public static async Task RunAsync(string root)
	{
		var project = Path.Combine(root, "io-failure");
		Directory.CreateDirectory(project);
		project = TestPaths.PhysicalDirectory(project);
		Check(CoordinationPathPolicy.TryResolveProjectRoot(project, out _, out var pathError),
			$"I/O fixture must reach file operations through a supported physical path: {pathError}");
		Directory.CreateDirectory(Path.Combine(project, "Assets"));
		Directory.CreateDirectory(Path.Combine(project, "Packages"));
		Directory.CreateDirectory(Path.Combine(project, "ProjectSettings"));
		File.WriteAllText(Path.Combine(project, "Packages", "manifest.json"), "{}");
		File.WriteAllText(Path.Combine(project, "ProjectSettings", "ProjectVersion.txt"), "m_EditorVersion: 2022.3.62f2");
		var bridge = Path.Combine(project, "Library", "AgentBridge");
		Directory.CreateDirectory(bridge);
		File.WriteAllText(Path.Combine(bridge, "status.json"),
			"""{"Capabilities":["coordination-edit-leases-v1"]}""");
		var scope = Path.Combine(project, "scope.json");
		File.WriteAllText(scope, """{"Paths":["Assets/Shared/"]}""");
		var arguments = new[] { "coord", "register", "--project", project, "--session", "io-test",
			"--spec", "io-test", "--scope", scope };
		var coordination = CoordinationPathPolicy.CoordinationRoot(project);

		// A filesystem error before transaction creation must also cross the CLI boundary safely.
		File.WriteAllText(coordination, "not a directory");
		try
		{
			await ExpectError(arguments, "io_error", human: false);
			await ExpectError(arguments, "io_error", human: true);
		}
		finally { File.Delete(coordination); }

		Check((await Invoke(arguments)).Exit == 0, "first registration must create state");
		var second = arguments.ToArray();
		second[5] = "second-session";
		Check((await Invoke(second)).Exit == 0, "second registration must atomically replace state under sandbox permissions");
		var state = Path.Combine(coordination, CoordinationFileStore.StateFileName);
		var withReader = arguments.ToArray();
		withReader[5] = "shared-reader-session";
		using (new FileStream(state, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
		{
			Check((await Invoke(withReader)).Exit == 0, "an open bridge reader must not prevent atomic state publication");
		}
		var before = File.ReadAllBytes(state);

		if (OperatingSystem.IsWindows())
		{
			var denied = arguments.ToArray();
			denied[5] = "denied-session";
			var attributes = File.GetAttributes(state);
			File.SetAttributes(state, attributes | FileAttributes.ReadOnly);
			try
			{
				await ExpectError(denied, "access_denied", human: false);
				await ExpectError(denied, "access_denied", human: true);
			}
			finally { File.SetAttributes(state, attributes); }
			Check(before.SequenceEqual(File.ReadAllBytes(state)), "a denied publication must preserve the previous state");
			Check(Directory.GetFiles(coordination, "state.json.*.tmp").Length == 0,
				"failed publications must clean only their own temporary files");
			using (new FileStream(state, FileMode.Open, FileAccess.Read, FileShare.Read))
			{
				var locked = await Invoke(denied);
				using var json = JsonDocument.Parse(locked.Output);
				Check(locked.Exit == 3 && json.RootElement.TryGetProperty("code", out var lockedCode)
					&& lockedCode.ValueKind == JsonValueKind.String && lockedCode.GetString() is "io_error" or "access_denied",
					$"an incompatible reader must cause a structured failure; exit={locked.Exit}, reply={locked.Output}");
			}
			Check(before.SequenceEqual(File.ReadAllBytes(state)), "a locked publication must preserve the previous state");
			Check((await Invoke(denied)).Exit == 0, "retry after restoring access must succeed without state recovery");
		}

		// A closed output pipe must not throw again while reporting the first I/O failure.
		var previousOut = Console.Out;
		var previousError = Console.Error;
		try
		{
			Console.SetOut(new ClosedPipeWriter());
			Console.SetError(new ClosedPipeWriter());
			Check(await AgentBridgeApplication.RunAsync(new[] { "--version" }) == 3,
				"closed stdout and stderr must produce a normal failure exit");
		}
		finally { Console.SetOut(previousOut); Console.SetError(previousError); }
		Console.WriteLine("CLI I/O failures: PASS (sandbox replacement, JSON/human errors, state preservation, retry, closed pipes)");
	}

	private static async Task ExpectError(string[] arguments, string code, bool human)
	{
		var result = await Invoke(arguments.Concat(new[] { "--format", human ? "human" : "json" }).ToArray());
		var context = $"expected={code}, exit={result.Exit}, reply={result.Output}";
		Check(result.Exit == 3, "filesystem errors must return exit 3: " + context);
		if (human)
		{
			Check(result.Output.StartsWith($"agentbridge: error ({code})"), "human output must name the failure: " + context);
		}
		else
		{
			using var json = JsonDocument.Parse(result.Output);
			Check(json.RootElement.TryGetProperty("ok", out var ok) && ok.ValueKind == JsonValueKind.False,
				"failed writes must report ok=false: " + context);
			Check(json.RootElement.TryGetProperty("code", out var actualCode)
				&& actualCode.ValueKind == JsonValueKind.String && actualCode.GetString() == code,
				"JSON output must name the failure: " + context);
		}
	}

	private static async Task<(int Exit, string Output)> Invoke(string[] arguments)
	{
		var previous = Console.Out;
		using var output = new StringWriter();
		try
		{
			Console.SetOut(output);
			return (await AgentBridgeApplication.RunAsync(arguments), output.ToString());
		}
		finally { Console.SetOut(previous); }
	}

	private static void Check(bool condition, string message)
	{
		if (!condition) throw new InvalidOperationException(message);
	}

	private sealed class ClosedPipeWriter : TextWriter
	{
		public override Encoding Encoding => Encoding.UTF8;
		public override void WriteLine(string? value) => throw new IOException("The pipe is closed.");
	}
}
