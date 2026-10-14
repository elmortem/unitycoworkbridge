using System.Diagnostics;
using System.Text.Json;
using AgentBridge.Cli;
using AgentBridge.Coordination;
using AgentBridge.Queueing;

internal static class QueueRuntimeTests
{
	internal static async Task RunAsync(string root)
	{
		string fixture = Path.Combine(root, "runtime-paths");
		Directory.CreateDirectory(fixture);
		fixture = TestPaths.PhysicalDirectory(fixture);
		string project = Path.Combine(fixture, "project");
		Directory.CreateDirectory(Path.Combine(project, "Assets"));
		Directory.CreateDirectory(Path.Combine(project, "Packages"));
		File.WriteAllText(Path.Combine(project, "Packages", "manifest.json"), "{}");
		Directory.CreateDirectory(Path.Combine(project, "ProjectSettings"));
		File.WriteAllText(Path.Combine(project, "ProjectSettings", "ProjectVersion.txt"), "m_EditorVersion: 2022.3.62f2");
		string physical = Path.Combine(fixture, "physical");
		string runtime = Path.Combine(physical, "nested", "runtime");
		Directory.CreateDirectory(runtime);
		string executableName = OperatingSystem.IsWindows() ? "agentbridge.exe" : "agentbridge";
		foreach (string name in new[] { executableName, "agentbridge.dll", "agentbridge.deps.json", "agentbridge.runtimeconfig.json" })
			File.Copy(Path.Combine(AppContext.BaseDirectory, name), Path.Combine(runtime, name));
		string alias = Path.Combine(fixture, "alias");
		await TestPaths.CreateDirectoryAlias(alias, physical);
		try
		{
			string aliasExecutable = Path.Combine(alias, "nested", "runtime", executableName);
			string executable = Path.Combine(runtime, executableName);
			var launch = new QueueLaunch { Executable = aliasExecutable, Arguments = new[] { "queue-server", "--project", project } };
			string launchFile = Path.Combine(QueueFiles.Root(project), "launch.json");
			QueueFiles.Write(launchFile, JsonSerializer.Serialize(launch, QueueClient.Json));
			using var server = Process.Start(new ProcessStartInfo(executable) { UseShellExecute = false, CreateNoWindow = true,
				RedirectStandardOutput = true, RedirectStandardError = true, ArgumentList = { "queue-server", "--project", project } })!;
			try
			{
				// /var and /private/var on macOS name the same runtime. Publish the alias but
				// start the physical path so this also reproduces the mismatch on Windows.
				await Request(server, project, "aliased-launch");
				launch.Executable = executable;
				QueueFiles.Write(launchFile, JsonSerializer.Serialize(launch, QueueClient.Json));
				await Request(server, project, "physical-launch");
				launch.Executable = aliasExecutable;
				QueueFiles.Write(launchFile, JsonSerializer.Serialize(launch, QueueClient.Json));
				await Request(server, project, "aliased-again");

				// A genuinely different runtime must still yield ownership for an upgrade.
				launch.Executable = Path.Combine(AppContext.BaseDirectory, executableName);
				QueueFiles.Write(launchFile, JsonSerializer.Serialize(launch, QueueClient.Json));
				await server.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(10));
				Check(server.ExitCode == 0, "runtime replacement must exit cleanly");
				Check(!QueueFiles.Alive(project), "runtime replacement must release the heartbeat");
			}
			finally
			{
				if (!server.HasExited) { server.Kill(); await server.WaitForExitAsync(); }
			}
			await ManagedRuntime(project, runtime, Path.Combine(alias, "nested", "runtime"));
			await AutomaticLaunch(project, Path.Combine(alias, "nested"));
		}
		finally { Directory.Delete(alias); }
		Console.WriteLine("Queue runtime: PASS (ancestor alias, equivalent paths, dotnet host, handover, automatic launch from aliased temp)");
	}

	private static async Task ManagedRuntime(string project, string runtime, string alias)
	{
		var launch = new QueueLaunch { Executable = "dotnet", Arguments = new[] { Path.Combine(alias, "agentbridge.dll"), "queue-server", "--project", project } };
		string launchFile = Path.Combine(QueueFiles.Root(project), "launch.json");
		QueueFiles.Write(launchFile, JsonSerializer.Serialize(launch, QueueClient.Json));
		using var server = Process.Start(new ProcessStartInfo("dotnet") { UseShellExecute = false, CreateNoWindow = true,
			RedirectStandardOutput = true, RedirectStandardError = true,
			ArgumentList = { Path.Combine(runtime, "agentbridge.dll"), "queue-server", "--project", project } })!;
		try
		{
			await Request(server, project, "dotnet-launch");
			launch.Arguments[0] = Path.Combine(AppContext.BaseDirectory, "agentbridge.dll");
			QueueFiles.Write(launchFile, JsonSerializer.Serialize(launch, QueueClient.Json));
			await server.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(10));
			Check(server.ExitCode == 0 && !QueueFiles.Alive(project), "changing the assembly must yield ownership even with the same dotnet host");
		}
		finally { if (!server.HasExited) { server.Kill(); await server.WaitForExitAsync(); } }
	}

	private static async Task AutomaticLaunch(string project, string temp)
	{
		string cli = Path.Combine(AppContext.BaseDirectory, OperatingSystem.IsWindows() ? "agentbridge.exe" : "agentbridge");
		async Task Invoke(string operation)
		{
			var start = new ProcessStartInfo(cli) { UseShellExecute = false, CreateNoWindow = true,
				RedirectStandardOutput = true, RedirectStandardError = true, ArgumentList = { "queue", operation, "--project", project } };
			foreach (string variable in new[] { "TEMP", "TMP", "TMPDIR" }) start.Environment[variable] = temp;
			using var client = Process.Start(start)!;
			var output = client.StandardOutput.ReadToEndAsync();
			var errors = client.StandardError.ReadToEndAsync();
			try
			{
				await client.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(20));
				string text = await output.WaitAsync(TimeSpan.FromSeconds(3));
				string stderr = await errors.WaitAsync(TimeSpan.FromSeconds(3));
				Check(client.ExitCode == 0, $"automatic launch through aliased temp: exit={client.ExitCode}, stdout={text}, stderr={stderr}");
			}
			finally { if (!client.HasExited) { client.Kill(); await client.WaitForExitAsync(); } }
		}
		try
		{
			await Invoke("list");
			var launch = JsonSerializer.Deserialize<QueueLaunch>(SharedFile.ReadAllText(Path.Combine(QueueFiles.Root(project), "launch.json")), QueueClient.Json)!;
			Check(launch.Executable.StartsWith(TestPaths.PhysicalDirectory(temp) + Path.DirectorySeparatorChar, StringComparison.Ordinal),
				"automatic launch must publish the physical snapshot path");
			Check(QueueFiles.Alive(project), "server must stay alive after its launching CLI exits");
		}
		finally
		{
			await Invoke("shutdown");
			await IndependentQueueTests.Until(() => !QueueFiles.Alive(project));
			// Release the executable before deleting its fixture on Windows.
			using var metadata = JsonDocument.Parse(SharedFile.ReadAllText(Path.Combine(QueueFiles.Root(project), "server.json")));
			try
			{
				using var server = Process.GetProcessById(metadata.RootElement.GetProperty("Pid").GetInt32());
				await server.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(10));
			}
			catch (ArgumentException) { } // Already exited.
		}
	}

	private static async Task Request(Process server, string project, string id)
	{
		QueueFiles.Write(QueueFiles.Request(project, id), JsonSerializer.Serialize(new QueueMessage { Id = id, Op = "list" }, QueueClient.Json));
		var timer = Stopwatch.StartNew();
		while (!File.Exists(QueueFiles.Reply(project, id)))
		{
			if (server.HasExited)
				throw new InvalidOperationException($"Queue server exited before acknowledging {id}: exit={server.ExitCode}, stdout={await server.StandardOutput.ReadToEndAsync()}, stderr={await server.StandardError.ReadToEndAsync()}");
			Check(timer.Elapsed.TotalSeconds < 10, "queue server did not acknowledge " + id);
			await Task.Delay(20);
		}
		var reply = JsonSerializer.Deserialize<QueueReply>(SharedFile.ReadAllText(QueueFiles.Reply(project, id)), QueueClient.Json)!;
		Check(reply.Ok, "queue request failed: " + reply.Code);
	}

	private static void Check(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }
}
