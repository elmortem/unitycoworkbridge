using AgentBridge.Coordination;

namespace AgentBridge.Cli;

// `agentbridge dialog answer <DialogId> --button LABEL --reason TEXT` (dialog-answer-v1). The
// editor's main thread is stuck in the dialog, so the request goes to the package's background
// monitor as Library/AgentBridge/dialog-answer.txt; the monitor clicks the button and reports the
// outcome in dialogs.json. The command then waits for the dialog to disappear from the snapshot.
internal static class DialogCommands
{
	// Internal so the tests can shorten them.
	internal static int AcknowledgeMs = 5000;
	internal static int ForeignAcknowledgeMs = 15000;
	internal static int CloseMs = 5000;
	private const int PollMs = 200;

	public const string Answered = "answered";
	public const string Unsupported = "dialog_answer_unsupported";
	public const string Unacknowledged = "answer_unacknowledged";
	public const string StillOpen = "dialog_still_open";

	private const string Usage = "usage: agentbridge dialog answer <DialogId> --button LABEL --reason TEXT [--session S]";

	private sealed class Outcome
	{
		public string Code = "";
		public string Title = "";
		public string Button = "";
		public string Detail = "";
	}

	public static async Task<int> RunAsync(string projectRoot, BridgePaths paths, BridgeHealth health,
		string[] arguments, CliOptions options, TelemetryLog telemetry)
	{
		if (arguments.Length != 2 || arguments[0] != "answer" || string.IsNullOrWhiteSpace(arguments[1])
			|| string.IsNullOrWhiteSpace(options.Button) || string.IsNullOrWhiteSpace(options.Reason))
		{
			return AgentBridgeApplication.WriteError("bad_usage", Usage, options.Format);
		}

		if (DialogAnswerRequest.OneLine(options.Reason).Length > DialogAnswerRequest.ReasonLimit)
		{
			return AgentBridgeApplication.WriteError("bad_usage",
				"--reason must be at most " + DialogAnswerRequest.ReasonLimit + " characters", options.Format);
		}

		var dialogId = arguments[1];
		var outcome = new Outcome { Button = options.Button! };
		try
		{
			outcome.Code = await AnswerAsync(paths, health, dialogId, options, outcome);
		}
		finally
		{
			try
			{
				File.Delete(paths.DialogAnswerFile);
			}
			catch (Exception error) when (error is IOException or UnauthorizedAccessException)
			{
			}
		}

		telemetry.Write("cli_dialog_answer", options.Session, "", new Dictionary<string, object?>
		{
			["Dialog"] = outcome.Title,
			["Button"] = outcome.Button,
			["Code"] = outcome.Code
		});

		var blockedBy = BridgeInspector.Inspect(projectRoot).BlockedBy;
		if (options.Format == "human")
		{
			Console.Out.WriteLine(outcome.Code == Answered
				? "dialog: answered \"" + outcome.Title + "\" with \"" + outcome.Button + "\""
				: "dialog: " + outcome.Code + " — " + outcome.Detail);
			foreach (var dialog in blockedBy)
			{
				Console.Out.WriteLine("Blocked by dialog: " + EditorDialogReader.Describe(dialog));
			}
		}
		else
		{
			JsonSupport.Write(new
			{
				Ok = outcome.Code == Answered,
				outcome.Code,
				DialogId = dialogId,
				outcome.Title,
				outcome.Button,
				outcome.Detail,
				BlockedBy = blockedBy
			});
		}

		return ExitCode(outcome.Code);
	}

	internal static int ExitCode(string code)
	{
		return code switch
		{
			Answered => 0,
			Unacknowledged => 2,
			"bad_usage" or Unsupported => 3,
			_ => 1
		};
	}

	private static async Task<string> AnswerAsync(BridgePaths paths, BridgeHealth health, string dialogId,
		CliOptions options, Outcome outcome)
	{
		if (health.Bridge == null || !health.Bridge.Capabilities.Contains(DialogAnswerRequest.Capability))
		{
			outcome.Detail = "the editor does not report " + DialogAnswerRequest.Capability
				+ " (it needs a Windows editor with a current Agent Bridge package)";
			return Unsupported;
		}

		var target = health.BlockedBy.FirstOrDefault(dialog => dialog.Id == dialogId);
		if (target == null)
		{
			outcome.Detail = health.BlockedBy.Length == 0
				? "no modal dialog holds the editor"
				: "open dialogs: " + string.Join(", ", health.BlockedBy.Select(dialog => dialog.Id + " \"" + dialog.Title + "\""));
			return DialogAnswerRequest.DialogNotOpen;
		}

		outcome.Title = target.Title;
		var index = DialogAnswerRequest.MatchButton(target.Buttons, outcome.Button);
		if (index < 0)
		{
			outcome.Detail = target.Buttons.Length == 0 ? "the bridge cannot read this dialog's buttons" : string.Join(" | ", target.Buttons);
			return index == DialogAnswerRequest.Ambiguous ? DialogAnswerRequest.AmbiguousButton : DialogAnswerRequest.ButtonNotFound;
		}

		var request = new DialogAnswerRequest
		{
			RequestId = Guid.NewGuid().ToString("N"),
			EditorPid = health.Bridge.EditorPid,
			DialogId = dialogId,
			Title = target.Title,
			Button = outcome.Button,
			Session = options.Session ?? "",
			Reason = options.Reason ?? ""
		};
		outcome.Button = target.Buttons[index];
		SharedFile.WriteAtomic(paths.DialogAnswerFile, request.Format());

		var acknowledgeMs = health.ForeignHost ? ForeignAcknowledgeMs : AcknowledgeMs;
		var clock = System.Diagnostics.Stopwatch.StartNew();
		EditorDialogReader.AnswerResult? answer;
		while (true)
		{
			answer = EditorDialogReader.ReadSnapshot(paths.DialogsFile)?.Answer;
			if (answer != null && answer.RequestId == request.RequestId)
			{
				break;
			}

			if (clock.ElapsedMilliseconds >= acknowledgeMs)
			{
				outcome.Detail = "the editor did not pick up the request within " + acknowledgeMs / 1000.0 + " s; ask a person";
				return Unacknowledged;
			}

			await Task.Delay(PollMs);
		}

		outcome.Detail = answer.Detail ?? "";
		if (answer.Outcome != DialogAnswerRequest.Sent)
		{
			return string.IsNullOrEmpty(answer.Outcome) ? DialogAnswerRequest.SendFailed : answer.Outcome;
		}

		clock.Restart();
		while (true)
		{
			var snapshot = EditorDialogReader.ReadSnapshot(paths.DialogsFile);
			if (snapshot != null && !(snapshot.Dialogs ?? Array.Empty<EditorDialogInfo>()).Any(dialog => dialog?.Id == dialogId))
			{
				return Answered;
			}

			if (clock.ElapsedMilliseconds >= CloseMs)
			{
				outcome.Detail = "the button was clicked but the dialog is still open after " + CloseMs / 1000.0 + " s";
				return StillOpen;
			}

			await Task.Delay(PollMs);
		}
	}
}
