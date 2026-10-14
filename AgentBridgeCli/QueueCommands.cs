using System.Text.Json;
using AgentBridge.Queueing;

namespace AgentBridge.Cli;

internal static class QueueCommands
{
	internal static async Task<int> RunAsync(string project, string[] args, CliOptions options)
	{
		string op = args.Length == 0 ? "list" : args[0];
		if (op == "status") op = "get";
		if (op is not ("list" or "get" or "move" or "cancel" or "shutdown")
			|| (op is "get" or "cancel" && args.Length != 2) || (op == "move" && args.Length is not (2 or 3)) || (op is "list" or "shutdown" && args.Length > 1))
		{
			Console.WriteLine("usage: agentbridge queue list|status <id>|cancel <id>|move <id> [before-id]|shutdown");
			return 3;
		}
		var reply = await new QueueClient(project).CallAsync(new QueueMessage { Op = op, TaskId = args.Length > 1 ? args[1] : "", BeforeId = args.Length > 2 ? args[2] : "", Session = options.Session ?? "" });
		if (options.Format == "human")
		{
			if (!reply.Ok) Console.WriteLine(reply.Code + ": " + reply.Message);
			else if (op == "list") foreach (var task in reply.Tasks) Console.WriteLine(task.Id + " " + task.Status + " " + task.AgentSessionId + (task.Position > 0 ? " position=" + task.Position : ""));
			else Console.WriteLine(reply.Code + (reply.Task == null ? "" : ": " + reply.Task.Id + " " + reply.Task.Status));
		}
		else Console.WriteLine(JsonSerializer.Serialize(reply, QueueClient.Json));
		return reply.Ok ? 0 : 3;
	}
}
