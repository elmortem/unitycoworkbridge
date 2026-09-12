using System.Text;
using System.Text.Json;
using AgentBridge.Coordination;

namespace AgentBridge.Cli;

// One line of state, then what is blocking and what the caller may legally do next. No heartbeat
// every second, and stdout carries exactly one answer.
internal static class CoordinationResultFormatter
{
	public static void Write(CoordinationReply reply, string format, CoordinationState? snapshot)
	{
		if (format == "human")
		{
			Console.Out.WriteLine(Human(reply, snapshot));
			return;
		}

		Console.Out.WriteLine(Json(reply));
	}

	public static string Json(CoordinationReply reply)
	{
		return JsonSerializer.Serialize(new
		{
			reply.Ok,
			reply.Code,
			reply.ProjectId,
			reply.Epoch,
			reply.Revision,
			reply.Session,
			reply.RequestId,
			reply.State,
			reply.Token,
			reply.Blockers,
			reply.Message
		}, JsonSupport.Task);
	}

	public static string Human(CoordinationReply reply, CoordinationState? snapshot)
	{
		var output = new StringBuilder();
		output.Append("coord: ").Append(reply.Ok ? reply.Code : "refused (" + reply.Code + ")");

		var details = new List<string>();
		if (!string.IsNullOrEmpty(reply.RequestId))
		{
			details.Add(reply.RequestId);
		}

		if (!string.IsNullOrEmpty(reply.State))
		{
			details.Add(reply.State);
		}

		details.Add("rev " + reply.Revision);
		output.Append(" (").Append(string.Join(", ", details)).Append(')');

		if (!string.IsNullOrEmpty(reply.Message))
		{
			output.AppendLine().Append("Message: ").Append(reply.Message);
		}

		if (!string.IsNullOrEmpty(reply.Token))
		{
			output.AppendLine().Append("Token: ").Append(reply.Token);
		}

		foreach (var blocker in reply.Blockers)
		{
			output.AppendLine().Append("Blocked by: ").Append(blocker);
		}

		var next = NextStep(reply);
		if (next != null)
		{
			output.AppendLine().Append("Next: ").Append(next);
		}

		if (snapshot != null)
		{
			AppendSnapshot(output, snapshot);
		}

		return output.ToString();
	}

	private static string? NextStep(CoordinationReply reply)
	{
		return reply.Code switch
		{
			CoordinationCodes.Waiting => "agentbridge coord wait --after " + reply.Revision,
			CoordinationCodes.Granted => "do the bounded package, then edit-end or finish",
			CoordinationCodes.PauseRequested => "finish the current package and run coord edit-end",
			CoordinationCodes.OrphanedWriter => "confirm your writers stopped, then run coord edit-end with the same token",
			CoordinationCodes.Draining => "wait for the running task, then run coord finish again",
			CoordinationCodes.Busy => "retry the same command with the same uuid",
			CoordinationCodes.RecoveryRequired => "register a session, or recover the coordination state explicitly",
			CoordinationCodes.Required => "request a window and resubmit with --coord-window and --coord-step",
			_ => null
		};
	}

	private static void AppendSnapshot(StringBuilder output, CoordinationState state)
	{
		if (state.Participants.Count == 0)
		{
			output.AppendLine().Append("No registered sessions.");
			return;
		}

		output.AppendLine().Append("Sessions:");
		foreach (var participant in state.Participants)
		{
			output.AppendLine()
				.Append("- ").Append(participant.Session)
				.Append(' ').Append(participant.Lifecycle)
				.Append(" spec=").Append(participant.SpecId)
				.Append(" gen=").Append(participant.Generation);
			if (participant.Paths.Length > 0)
			{
				output.Append(" scope=").Append(string.Join(",", participant.Paths));
			}
		}

		var waiting = new List<CoordinationRequest>();
		foreach (var request in state.Requests)
		{
			if (request.State == CoordinationLimits.StateWaiting)
			{
				waiting.Add(request);
			}
		}

		if (waiting.Count > 0)
		{
			waiting.Sort((left, right) => left.Ticket.CompareTo(right.Ticket));
			output.AppendLine().Append("Queue:");
			foreach (var request in waiting)
			{
				output.AppendLine()
					.Append("- #").Append(request.Ticket)
					.Append(' ').Append(request.Session)
					.Append(' ').Append(request.Kind)
					.Append(' ').Append(request.Id);
			}
		}

		foreach (var grant in state.Grants)
		{
			output.AppendLine()
				.Append("Grant: ").Append(grant.Session)
				.Append(' ').Append(grant.Kind)
				.Append(' ').Append(grant.State);
			if (grant.ActiveTaskIds.Length > 0)
			{
				output.Append(" running=").Append(string.Join(",", grant.ActiveTaskIds));
			}
		}
	}
}
