using System.Diagnostics;

namespace AgentBridge.Cli;

// `agentbridge editor open [--editor-path PATH]` and `agentbridge editor close --reason TEXT [--force]`.
// open launches the project's Unity and returns its PID at once: readiness is the ordinary status/wait
// path, and the queue keeps tasks while no editor runs. close asks the main window to close like its
// close button, or kills the process with --force. Both work only on the editor's machine.
internal static class EditorCommands
{
	// Internal so the tests can shorten them.
	internal static int CloseWaitMs = 30000;
	internal static int KillWaitMs = 10000;
	private const int PollMs = 250;
	public const int ReasonLimit = 200;
	// The tests replace the launch; returns the PID.
	internal static Func<ProcessStartInfo, int> Launch = info =>
	{
		using var process = Process.Start(info)!;
		return process.Id;
	};

	public const string Launched = "launched";
	public const string AlreadyRunning = "already_running";
	public const string Closed = "closed";
	public const string Killed = "killed";
	public const string NotRunning = "not_running";
	public const string ProjectLocked = "project_locked";
	public const string NotFound = "editor_not_found";
	public const string TaskRunning = "task_running";
	public const string Playing = "editor_playing";
	public const string BlockedByDialog = "blocked_by_dialog";
	public const string PidMismatch = "pid_mismatch";
	public const string CloseRefused = "close_refused";
	public const string KillFailed = "kill_failed";
	public const string CloseBlockedByDialog = "close_blocked_by_dialog";
	public const string ClosePending = "close_pending";
	public const string ForeignHost = "editor_control_foreign_host";

	private const string Usage = "usage: agentbridge editor open [--editor-path PATH] | agentbridge editor close --reason TEXT [--force]";

	private sealed class Outcome
	{
		public string Code = "";
		public int Pid;
		public string EditorPath = "";
		public string UnityVersion = "";
		public string Detail = "";
		public EditorDialogInfo[] BlockedBy = Array.Empty<EditorDialogInfo>();
	}

	public static async Task<int> RunAsync(string projectRoot, BridgePaths paths, BridgeHealth health,
		string[] arguments, CliOptions options, TelemetryLog telemetry)
	{
		var open = arguments.Length == 1 && arguments[0] == "open";
		var close = arguments.Length == 1 && arguments[0] == "close";
		if (!open && !close)
		{
			return AgentBridgeApplication.WriteError("bad_usage", Usage, options.Format);
		}

		if (close && string.IsNullOrWhiteSpace(options.Reason))
		{
			return AgentBridgeApplication.WriteError("bad_usage", "editor close requires --reason with why the editor is closed", options.Format);
		}

		if (close && options.Reason!.Length > ReasonLimit)
		{
			return AgentBridgeApplication.WriteError("bad_usage", "--reason must be at most " + ReasonLimit + " characters", options.Format);
		}

		if (health.ForeignHost)
		{
			return AgentBridgeApplication.WriteError(ForeignHost,
				"the editor runs on " + health.HostOs + " and this client on " + health.ClientOs
				+ "; open and close the editor from its own machine", options.Format);
		}

		var outcome = new Outcome
		{
			Pid = health.Bridge?.EditorPid ?? 0,
			EditorPath = health.Bridge?.EditorPath ?? "",
			UnityVersion = health.Bridge?.UnityVersion ?? ""
		};
		var running = health.Bridge != null && health.ProjectMatches && health.EditorProcessAlive == true;
		if (open)
		{
			outcome.Code = Open(projectRoot, health, options, running, outcome);
			telemetry.Write("cli_editor_open", options.Session, "", new Dictionary<string, object?>
			{
				["Code"] = outcome.Code,
				["Pid"] = outcome.Pid,
				["Path"] = outcome.EditorPath
			});
		}
		else
		{
			outcome.Code = await CloseAsync(projectRoot, health, options, running, outcome);
			telemetry.Write("cli_editor_close", options.Session, "", new Dictionary<string, object?>
			{
				["Code"] = outcome.Code,
				["Pid"] = outcome.Pid,
				["Force"] = options.Force,
				["Reason"] = options.Reason
			});
		}

		Write(outcome, options.Format);
		return ExitCode(outcome.Code);
	}

	internal static int ExitCode(string code)
	{
		return code switch
		{
			Launched or AlreadyRunning or Closed or Killed or NotRunning => 0,
			CloseBlockedByDialog or ClosePending => 2,
			"bad_usage" or ForeignHost => 3,
			_ => 1
		};
	}

	private static string Open(string projectRoot, BridgeHealth health, CliOptions options, bool running, Outcome outcome)
	{
		if (running)
		{
			return AlreadyRunning;
		}

		if (IsProjectLocked(projectRoot))
		{
			outcome.Pid = 0;
			outcome.Detail = "Temp/UnityLockfile is held: a Unity process has the project open without a live bridge";
			return ProjectLocked;
		}

		var version = EditorLocator.ProjectVersion(projectRoot);
		outcome.UnityVersion = version;
		var path = EditorLocator.Resolve(version, options.EditorPath, health.Bridge);
		if (path == null)
		{
			outcome.Pid = 0;
			outcome.EditorPath = "";
			var candidates = EditorLocator.CandidatesHere(version, options.EditorPath, health.Bridge);
			outcome.Detail = "version " + (version.Length == 0 ? "unknown" : version) + "; tried: "
				+ (candidates.Count == 0 ? "nothing" : string.Join(", ", candidates)) + "; pass --editor-path";
			return NotFound;
		}

		var start = new ProcessStartInfo(path)
		{
			UseShellExecute = OperatingSystem.IsWindows(),
			RedirectStandardInput = !OperatingSystem.IsWindows(),
			RedirectStandardOutput = !OperatingSystem.IsWindows(),
			RedirectStandardError = !OperatingSystem.IsWindows(),
			WorkingDirectory = projectRoot
		};
		start.ArgumentList.Add("-projectPath");
		start.ArgumentList.Add(projectRoot);
		outcome.EditorPath = path;
		outcome.Pid = Launch(start);
		return Launched;
	}

	// Unity holds Temp/UnityLockfile exclusively while the project is open.
	private static bool IsProjectLocked(string projectRoot)
	{
		var lockfile = Path.Combine(projectRoot, "Temp", "UnityLockfile");
		if (!File.Exists(lockfile))
		{
			return false;
		}

		try
		{
			using var stream = new FileStream(lockfile, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
			return false;
		}
		catch (IOException)
		{
			return true;
		}
		catch (UnauthorizedAccessException)
		{
			return false;
		}
	}

	private static async Task<string> CloseAsync(string projectRoot, BridgeHealth health, CliOptions options, bool running, Outcome outcome)
	{
		if (!running)
		{
			return NotRunning;
		}

		var bridge = health.Bridge!;
		if (!options.Force)
		{
			if (!string.IsNullOrEmpty(bridge.ActiveTaskId))
			{
				outcome.Detail = bridge.ActiveTaskId + " of session " + (string.IsNullOrEmpty(bridge.HolderAgentSessionId) ? "unknown" : bridge.HolderAgentSessionId);
				return TaskRunning;
			}

			if (bridge.IsPlaying)
			{
				outcome.Detail = string.IsNullOrEmpty(bridge.PlaySessionAgentId) ? "manual" : bridge.PlaySessionAgentId;
				return Playing;
			}

			if (health.BlockedBy.Length > 0)
			{
				outcome.BlockedBy = health.BlockedBy;
				outcome.Detail = "answer the dialog first";
				return BlockedByDialog;
			}
		}

		Process process;
		try
		{
			process = Process.GetProcessById(bridge.EditorPid);
		}
		catch (ArgumentException)
		{
			return NotRunning;
		}

		using (process)
		{
			string name;
			try
			{
				name = process.ProcessName;
			}
			catch (InvalidOperationException)
			{
				return NotRunning;
			}

			if (!name.StartsWith("Unity", StringComparison.OrdinalIgnoreCase))
			{
				outcome.Detail = "process " + bridge.EditorPid + " is " + name + ", not Unity";
				return PidMismatch;
			}

			if (options.Force)
			{
				process.Kill();
				if (process.WaitForExit(KillWaitMs))
				{
					return Killed;
				}

				outcome.Detail = "the process is still alive " + KillWaitMs / 1000.0 + " s after kill";
				return KillFailed;
			}

			if (!process.CloseMainWindow())
			{
				outcome.Detail = "the editor has no main window to close; use --force if it does not respond";
				return CloseRefused;
			}

			var clock = Stopwatch.StartNew();
			while (true)
			{
				if (process.HasExited)
				{
					return Closed;
				}

				var blockedBy = BridgeInspector.Inspect(projectRoot).BlockedBy;
				if (blockedBy.Length > 0)
				{
					outcome.BlockedBy = blockedBy;
					outcome.Detail = "Unity asks before closing; answer with 'agentbridge dialog answer' and repeat editor close";
					return CloseBlockedByDialog;
				}

				if (clock.ElapsedMilliseconds >= CloseWaitMs)
				{
					outcome.Detail = "the editor is still running " + CloseWaitMs / 1000.0 + " s after the close request";
					return ClosePending;
				}

				await Task.Delay(PollMs);
			}
		}
	}

	private static void Write(Outcome outcome, string format)
	{
		if (format == "human")
		{
			var line = "editor: " + outcome.Code;
			if (outcome.Pid > 0)
			{
				line += " (pid " + outcome.Pid + (outcome.EditorPath.Length > 0 ? ", " + outcome.EditorPath : "") + ")";
			}

			if (outcome.Detail.Length > 0)
			{
				line += " — " + outcome.Detail;
			}

			Console.Out.WriteLine(line);
			foreach (var dialog in outcome.BlockedBy)
			{
				Console.Out.WriteLine("Blocked by dialog: " + EditorDialogReader.Describe(dialog));
			}

			return;
		}

		JsonSupport.Write(new
		{
			Ok = ExitCode(outcome.Code) == 0,
			outcome.Code,
			outcome.Pid,
			outcome.EditorPath,
			outcome.UnityVersion,
			outcome.Detail,
			outcome.BlockedBy
		});
	}
}
