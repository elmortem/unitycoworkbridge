using System.Text.Json;
using AgentBridge.Queueing;

namespace AgentBridge.Cli;

internal static class QueueCommands
{
	internal static async Task<int> RunAsync(string project, string[] args, CliOptions options)
	{
		string op = args.Length == 0 ? "list" : args[0];
		if (op == "status") op = "get";
		if (op is not ("list" or "get" or "move" or "cancel" or "shutdown" or "reserve" or "request-stop" or "respond" or "inbox")
			|| (op is "get" or "cancel" or "request-stop" or "respond" && args.Length != 2) || (op == "move" && args.Length is not (2 or 3))
			|| (op is "list" or "shutdown" or "reserve" or "inbox" && args.Length > 1))
		{
			Console.WriteLine("usage: agentbridge queue list|status <id>|cancel <id>|move <id> [before-id]|shutdown|reserve|request-stop <task>|respond <discussion>|inbox");
			return 3;
		}
		if (op == "inbox" && string.IsNullOrEmpty(options.Session))
		{
			Write(new QueueReply { Code = "bad_usage", Message = "inbox requires --session S" }, options.Format);
			return 3;
		}
		var reply = await new QueueClient(project).CallAsync(new QueueMessage { Op = op, TaskId = args.Length > 1 ? args[1] : "",
			BeforeId = args.Length > 2 ? args[2] : "", Session = options.Session ?? "", DiscussionId = op == "respond" ? args[1] : options.RequestUuid ?? "",
			Reason = options.Reason ?? "", Decision = options.Kind ?? "", Seconds = options.Seconds, AfterRevision = options.AfterRevision });
		Write(reply, options.Format);
		return reply.Ok ? (reply.Code == "pending" ? 2 : 0) : 3;
	}
	internal static void Write(QueueReply reply, string format)
	{
		if (format == "human")
		{
			Console.WriteLine(reply.Code + ": " + reply.Message);
			foreach (var task in reply.Tasks) Console.WriteLine(task.Id + " " + task.Status + " " + task.AgentSessionId + (task.Position > 0 ? " position=" + task.Position : ""));
			if (reply.Task != null) Console.WriteLine(reply.Task.Id + " " + reply.Task.Status + " " + reply.Task.AgentSessionId
				+ (reply.Task.CanceledBy.Length > 0 ? " canceled by " + reply.Task.CanceledBy + ": " + reply.Task.CancellationReason : ""));
			if (reply.Discussion != null) WriteDiscussion(reply.Discussion);
			foreach (var item in reply.Discussions) WriteDiscussion(item);
			Console.WriteLine("DiscussionRevision: " + reply.DiscussionRevision);
		}
		else Console.WriteLine(JsonSerializer.Serialize(reply, QueueClient.Json));
	}
	private static void WriteDiscussion(QueueDiscussion d)
	{
		Console.WriteLine(d.Id + " " + d.Kind + " " + d.State + " revision=" + d.Revision + " requester=" + d.Session + " owner=" + d.Owner + " task=" + d.TaskId);
		Console.WriteLine("Reason: " + d.Reason + "; expected/remaining seconds=" + d.Seconds);
		if (d.ReplyByMs > 0) Console.WriteLine("Reply by: " + DateTimeOffset.FromUnixTimeMilliseconds(d.ReplyByMs).ToString("o"));
		if (d.Blockers.Length > 0) Console.WriteLine("Blockers: " + string.Join(", ", d.Blockers));
		foreach (var answer in d.Answers) Console.WriteLine(answer.Session + ": " + answer.Decision + "; " + answer.Reason + "; seconds=" + answer.Seconds);
	}
}
