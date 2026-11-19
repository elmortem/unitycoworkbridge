using System.Text.Json;
using AgentBridge;
using AgentBridge.Cli;
using AgentBridge.Queueing;

// dialog-answer-v1: `agentbridge dialog answer` against a fake editor that plays the package's
// background monitor through dialog-answer.txt and dialogs.json.
internal static class DialogAnswerTests
{
	private const string DialogId = "win32:1d24a8";
	private const string Title = "AgentBridge answer probe";
	private static readonly string[] Buttons = { "Alpha", "Beta", "Gamma" };

	internal static async Task RunAsync(string root)
	{
		RunRequestTests();
		RunMatchTests();
		await RunCommandTests(root);
		Console.WriteLine("Dialog answer: PASS (request round trip, button matching, answered/still open/unacknowledged/unknown button/unsupported, request cleanup)");
	}

	private static void RunRequestTests()
	{
		var request = new DialogAnswerRequest
		{
			RequestId = "r1", EditorPid = 42, DialogId = DialogId, Title = Title, Button = "Gamma",
			Session = "agent-1", Reason = "a = b\nc"
		};
		var parsed = DialogAnswerRequest.Parse(request.Format());
		Check(parsed != null && parsed.RequestId == "r1" && parsed.EditorPid == 42 && parsed.DialogId == DialogId
			&& parsed.Title == Title && parsed.Button == "Gamma" && parsed.Session == "agent-1",
			"Format/Parse must keep every field");
		Check(parsed!.Reason == "a = b c", "a reason keeps '=' and loses its line break: " + parsed.Reason);
		Check(DialogAnswerRequest.Parse("garbage\nmore") == null, "garbage parses to null");
		Check(DialogAnswerRequest.Parse("") == null, "empty text parses to null");
		Check(DialogAnswerRequest.Parse("RequestId=r\nEditorPid=x\nDialogId=d\nButton=b\n") == null, "a non-numeric pid parses to null");
		Check(DialogAnswerRequest.Parse("RequestId=r\nEditorPid=1\nDialogId=d\n") == null, "a missing button parses to null");
	}

	private static void RunMatchTests()
	{
		Check(DialogAnswerRequest.MatchButton(Buttons, "  gamma ") == 2, "matching ignores case and surrounding spaces");
		Check(DialogAnswerRequest.MatchButton(new[] { "Save", "Don't Save" }, "Don’t Save") == 1, "a typographic apostrophe matches a straight one");
		Check(DialogAnswerRequest.MatchButton(Buttons, "Delta") == DialogAnswerRequest.NotFound, "an unknown label is NotFound");
		Check(DialogAnswerRequest.MatchButton(new[] { "OK", "ok" }, "OK") == DialogAnswerRequest.Ambiguous, "two equal labels are Ambiguous");
		Check(DialogAnswerRequest.MatchButton(null!, "OK") == DialogAnswerRequest.NotFound, "no labels is NotFound");
	}

	private static async Task RunCommandTests(string root)
	{
		string project = CreateProject(root, "dialog-answer-project");
		var paths = new BridgePaths(project);
		int acknowledge = DialogCommands.AcknowledgeMs;
		int close = DialogCommands.CloseMs;
		DialogCommands.AcknowledgeMs = 800;
		DialogCommands.CloseMs = 800;
		try
		{
			WriteStatus(paths, true);
			WriteDialogs(paths, null);

			var editor = FakeEditor(paths, closes: true);
			var (code, output) = await Run(project, "--button", "gamma", "--reason", "verification", "--session", "agent-1");
			var request = await editor;
			Check(code == 0 && output.GetProperty("code").GetString() == "answered" && output.GetProperty("ok").GetBoolean(),
				"a clicked dialog that closes is answered with exit 0: " + code + " " + output);
			Check(output.GetProperty("button").GetString() == "Gamma" && output.GetProperty("title").GetString() == Title,
				"the result names the dialog and the label it clicked: " + output);
			Check(request != null && request.Button == "gamma" && request.Title == Title && request.Session == "agent-1"
				&& request.Reason == "verification" && request.EditorPid == Environment.ProcessId,
				"the request carries the dialog, the button, the session and the reason");
			Check(!File.Exists(paths.DialogAnswerFile), "answered: the request file is removed");

			WriteDialogs(paths, null);
			editor = FakeEditor(paths, closes: false);
			(code, output) = await Run(project, "--button", "Beta", "--reason", "r");
			await editor;
			Check(code == 1 && output.GetProperty("code").GetString() == DialogCommands.StillOpen,
				"a click the dialog ignores is dialog_still_open with exit 1: " + code + " " + output);
			Check(!File.Exists(paths.DialogAnswerFile), "still open: the request file is removed");

			WriteDialogs(paths, null);
			(code, output) = await Run(project, "--button", "Alpha", "--reason", "r");
			Check(code == 2 && output.GetProperty("code").GetString() == DialogCommands.Unacknowledged,
				"a silent editor is answer_unacknowledged with exit 2: " + code + " " + output);
			Check(!File.Exists(paths.DialogAnswerFile), "unacknowledged: the request file is removed");

			// A directory where the request file belongs: writing a request would fail the command.
			Directory.CreateDirectory(paths.DialogAnswerFile);
			try
			{
				(code, output) = await Run(project, "--button", "Delta", "--reason", "r");
				Check(code == 1 && output.GetProperty("code").GetString() == DialogAnswerRequest.ButtonNotFound
					&& output.GetProperty("detail").GetString() == "Alpha | Beta | Gamma",
					"an unknown button is button_not_found with exit 1 and the labels: " + code + " " + output);
			}
			finally
			{
				Directory.Delete(paths.DialogAnswerFile);
			}

			Check(!File.Exists(paths.DialogAnswerFile), "unknown button: no request file is written");

			WriteStatus(paths, false);
			(code, output) = await Run(project, "--button", "Alpha", "--reason", "r");
			Check(code == 3 && output.GetProperty("code").GetString() == DialogCommands.Unsupported,
				"an editor without dialog-answer-v1 is dialog_answer_unsupported with exit 3: " + code + " " + output);
			Check(!File.Exists(paths.DialogAnswerFile), "unsupported: no request file is left");

			Check(DialogCommands.ExitCode("not_blocked") == 1 && DialogCommands.ExitCode("dialog_changed") == 1
				&& DialogCommands.ExitCode("send_failed") == 1 && DialogCommands.ExitCode("dialog_not_open") == 1,
				"editor refusals exit with 1");
		}
		finally
		{
			DialogCommands.AcknowledgeMs = acknowledge;
			DialogCommands.CloseMs = close;
		}
	}

	private static async Task<(int Code, JsonElement Output)> Run(string project, params string[] extra)
	{
		var stdout = Console.Out;
		using var output = new StringWriter();
		Console.SetOut(output);
		int code;
		try
		{
			var args = new List<string> { "dialog", "answer", DialogId, "--project", project };
			args.AddRange(extra);
			code = await AgentBridgeApplication.RunAsync(args.ToArray()).WaitAsync(TimeSpan.FromSeconds(20));
		}
		finally
		{
			Console.SetOut(stdout);
		}

		using var document = JsonDocument.Parse(output.ToString());
		return (code, document.RootElement.Clone());
	}

	// Plays the package's monitor: picks up the request and reports it sent; closes the dialog or not.
	private static Task<DialogAnswerRequest?> FakeEditor(BridgePaths paths, bool closes)
	{
		return Task.Run(async () =>
		{
			var deadline = DateTime.UtcNow.AddSeconds(10);
			while (DateTime.UtcNow < deadline)
			{
				if (File.Exists(paths.DialogAnswerFile))
				{
					var request = DialogAnswerRequest.Parse(File.ReadAllText(paths.DialogAnswerFile));
					if (request != null)
					{
						WriteDialogs(paths, request.RequestId, closes);
						return request;
					}
				}

				await Task.Delay(20);
			}

			return null;
		});
	}

	private static void WriteDialogs(BridgePaths paths, string? answerRequestId, bool closed = false)
	{
		var dialogs = closed
			? Array.Empty<object>()
			: new object[]
			{
				new
				{
					Id = DialogId, Source = "win32", Kind = "native_dialog", Level = "", Title, Message = "",
					Buttons, OpenedAtUtc = DateTime.UtcNow.ToString("o"), OpenMs = 5000, TaskId = ""
				}
			};
		var snapshot = new Dictionary<string, object?>
		{
			["EditorPid"] = Environment.ProcessId,
			["UpdatedUtcMs"] = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
			["MainThreadIdleMs"] = 5000,
			["Dialogs"] = dialogs
		};
		if (answerRequestId != null)
		{
			snapshot["Answer"] = new { RequestId = answerRequestId, Outcome = DialogAnswerRequest.Sent, Detail = "" };
		}

		AgentBridge.Coordination.SharedFile.WriteAtomic(paths.DialogsFile, JsonSerializer.Serialize(snapshot));
	}

	private static void WriteStatus(BridgePaths paths, bool answerCapability)
	{
		Directory.CreateDirectory(paths.WorkingRoot);
		var capabilities = new List<string> { QueueFiles.Capability, "dialog-detection-v1" };
		if (answerCapability)
		{
			capabilities.Add(DialogAnswerRequest.Capability);
		}

		File.WriteAllText(paths.StatusFile, JsonSerializer.Serialize(new BridgeStatus
		{
			ProtocolVersion = 1, ProjectPath = paths.ProjectRoot, EditorPid = Environment.ProcessId, Enabled = true, RoslynReady = true,
			HostOs = HostPlatform.Current, DialogDetection = "win32", Capabilities = capabilities.ToArray()
		}));
		File.WriteAllText(paths.HeartbeatFile, DateTimeOffset.UtcNow.ToUnixTimeMilliseconds().ToString());
	}

	private static string CreateProject(string root, string name)
	{
		string project = Path.Combine(root, name);
		Directory.CreateDirectory(Path.Combine(project, "Assets"));
		Directory.CreateDirectory(Path.Combine(project, "ProjectSettings"));
		Directory.CreateDirectory(Path.Combine(project, "Packages", "com.elmortem.agentbridge"));
		File.WriteAllText(Path.Combine(project, "Packages", "manifest.json"), """{"dependencies":{}}""");
		File.WriteAllText(Path.Combine(project, "Packages", "com.elmortem.agentbridge", "package.json"), """{"name":"com.elmortem.agentbridge"}""");
		File.WriteAllText(Path.Combine(project, "ProjectSettings", "ProjectVersion.txt"), "m_EditorVersion: 2022.3.62f2");
		return TestPaths.PhysicalDirectory(project);
	}

	private static void Check(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }
}
