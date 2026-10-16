using System.Text.Json;
using AgentBridge.Cli;
using AgentBridge.Queueing;

internal static class QueueReplyLifetimeTests
{
	internal static async Task RunAsync(string root)
	{
		string project = CreateProject(root, "reply-lifetime");
		var client = new QueueClient(project);
		var replies = Path.Combine(QueueFiles.Root(project), "Replies");
		for (int i = 0; i < 5; i++)
		{
			var message = new QueueMessage { Op = "list" };
			Check((await client.CallAsync(message)).Ok, "client_removes_reply: list succeeds");
			Check(!File.Exists(QueueFiles.Reply(project, message.Id)), "client_removes_reply: reply of " + message.Id + " was released");
		}
		Check(Directory.GetFiles(replies, "*.json").Length == 0, "client_removes_reply: no reply is left behind");

		string held = CreateProject(root, "reply-held");
		QueueFiles.Write(QueueFiles.Request(held, "held"), "{}");
		QueueFiles.Write(QueueFiles.Reply(held, "held"), "{}");
		QueueFiles.ReleaseReply(held, "held");
		Check(File.Exists(QueueFiles.Reply(held, "held")), "reply_kept_while_request_exists: reply guards an unfinished request");
		File.Delete(QueueFiles.Request(held, "held"));
		QueueFiles.ReleaseReply(held, "held");
		Check(!File.Exists(QueueFiles.Reply(held, "held")), "reply_kept_while_request_exists: finished reply is released");

		var now = DateTime.UtcNow;
		string sweepDirectory = Path.Combine(root, "reply-sweep");
		Directory.CreateDirectory(sweepDirectory);
		foreach (string name in new[] { "old.json", "old.json." + Guid.NewGuid().ToString("N") + ".tmp", "fresh.json" })
			File.WriteAllText(Path.Combine(sweepDirectory, name), "{}");
		foreach (string path in Directory.GetFiles(sweepDirectory, "old.json*"))
			File.SetLastWriteTimeUtc(path, now.AddDays(-2));
		var sweep = QueueServer.SweepReplies(sweepDirectory, now.AddDays(-1));
		Check(sweep.Deleted == 2 && sweep.Failed == 0 && sweep.Kept == 1, "sweep_deletes_only_expired: " + sweep);
		Check(File.Exists(Path.Combine(sweepDirectory, "fresh.json")), "sweep_deletes_only_expired: fresh reply kept");

		if (OperatingSystem.IsWindows())
		{
			string locked = Path.Combine(sweepDirectory, "locked.json");
			File.WriteAllText(locked, "{}");
			File.SetLastWriteTimeUtc(locked, now.AddDays(-2));
			using (new FileStream(locked, FileMode.Open, FileAccess.Read, FileShare.None))
			{
				var blocked = QueueServer.SweepReplies(sweepDirectory, now.AddDays(-1));
				Check(blocked.Failed == 1 && blocked.Deleted == 0, "sweep_survives_locked_file: " + blocked);
			}
			var retried = QueueServer.SweepReplies(sweepDirectory, now.AddDays(-1));
			Check(retried.Deleted == 1 && !File.Exists(locked), "sweep_survives_locked_file: released file is swept");
		}

		var missing = QueueServer.SweepReplies(Path.Combine(root, "reply-missing"), now);
		Check(missing.Deleted == 0 && missing.Failed == 0 && missing.Kept == 0, "sweep_missing_directory: " + missing);

		string orphan = CreateProject(root, "reply-unacknowledged");
		Directory.CreateDirectory(QueueFiles.Root(orphan));
		using (new FileStream(Path.Combine(QueueFiles.Root(orphan), "server.lock"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None))
		{
			var reply = await new QueueClient(orphan) { AcknowledgeTimeoutMs = 500 }.CallAsync(new QueueMessage { Op = "list" });
			Check(reply.Code == "dispatcher_unavailable", "unacknowledged_is_logged: " + reply.Code);
		}
		bool logged = Directory.GetFiles(Path.Combine(orphan, "Logs"), "AgentBridge-client-*.jsonl")
			.SelectMany(File.ReadAllLines)
			.Select(line => JsonDocument.Parse(line).RootElement)
			.Any(e => e.GetProperty("E").GetString() == "queue_unacknowledged" && e.GetProperty("Op").GetString() == "list");
		Check(logged, "unacknowledged_is_logged: queue_unacknowledged event with Op");
		Console.WriteLine("Queue reply lifetime: PASS");
	}

	private static string CreateProject(string root, string name)
	{
		string project = Path.Combine(root, name);
		Directory.CreateDirectory(Path.Combine(project, "Assets"));
		Directory.CreateDirectory(Path.Combine(project, "ProjectSettings"));
		Directory.CreateDirectory(Path.Combine(project, "Packages"));
		File.WriteAllText(Path.Combine(project, "Packages", "manifest.json"), "{}");
		File.WriteAllText(Path.Combine(project, "ProjectSettings", "ProjectVersion.txt"), "m_EditorVersion: 2022.3.62f2");
		return TestPaths.PhysicalDirectory(project);
	}

	private static void Check(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }
}
