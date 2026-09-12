using System.Diagnostics;
using AgentBridge.Coordination;
using static AgentBridge.Coordination.Tests.Harness;

namespace AgentBridge.Coordination.Tests;

// The store tests need real processes: a lock that is only honoured inside one process proves
// nothing. Only children this runner started are ever waited on or stopped.
internal static class Child
{
	public static Process Start(string role, string project, string session)
	{
		var start = new ProcessStartInfo
		{
			FileName = Environment.ProcessPath ?? "dotnet",
			RedirectStandardOutput = true,
			RedirectStandardError = true,
			UseShellExecute = false
		};

		if (Environment.ProcessPath == null)
		{
			start.ArgumentList.Add(Environment.GetCommandLineArgs()[0]);
		}

		start.ArgumentList.Add("--child");
		start.ArgumentList.Add(role);
		start.ArgumentList.Add(project);
		start.ArgumentList.Add(session);

		var process = Process.Start(start);
		if (process == null)
		{
			throw new InvalidOperationException("could not start the child process for role " + role);
		}

		return process;
	}

	public static int Run(string[] args)
	{
		var role = args[0];
		var project = args[1];
		var session = args.Length > 2 ? args[2] : "";

		switch (role)
		{
			case "window":
				return Window(project, session);
			case "crash":
				return Crash(project);
			default:
				Console.Error.WriteLine("unknown child role: " + role);
				return 3;
		}
	}

	// Registers, queues a window and tries to confirm it. Two of these racing must produce exactly
	// one grant between them.
	private static int Window(string project, string session)
	{
		var store = Store(project);
		var engine = new CoordinationEngine();
		var now = CoordinationSystemClock.Instance.UtcNowMs;

		Commit(store, Register(session, "Game/" + session + "/"), now);
		Commit(store, WindowRequest(session, "uuid-" + session), now);

		using var transaction = store.BeginWithRetry(20_000);
		var reply = engine.Apply(transaction.State, Command(CoordinationEngine.OpWindowConfirm), CoordinationSystemClock.Instance.UtcNowMs);
		if (reply.Changed)
		{
			transaction.Commit();
		}

		Console.Out.WriteLine(reply.Code + "|" + reply.Session + "|" + reply.Revision);
		return 0;
	}

	// Leaves a temporary file behind exactly where an interrupted publish would, then dies without
	// replacing the published state.
	private static int Crash(string project)
	{
		var root = CoordinationPathPolicy.CoordinationRoot(project);
		var temporary = Path.Combine(root, "state.json." + Guid.NewGuid().ToString("N") + ".tmp");

		using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
		{
			var bytes = System.Text.Encoding.UTF8.GetBytes("{\"SchemaVersion\":1,\"ProjectId\":\"half\"");
			stream.Write(bytes, 0, bytes.Length);
			stream.Flush(true);
		}

		Environment.Exit(9);
		return 9;
	}
}
