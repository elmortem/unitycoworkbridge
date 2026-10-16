using System.Diagnostics;
using System.Text.Json;
using AgentBridge.Coordination;
using AgentBridge.Queueing;

namespace AgentBridge.Cli;

internal sealed class QueueClient
{
	internal static readonly JsonSerializerOptions Json = new() { IncludeFields = true, PropertyNameCaseInsensitive = true };
	private readonly string _project;
	private QueueLaunch? _launch;
	public QueueClient(string project) { _project = project; }
	internal int AcknowledgeTimeoutMs { get; init; } = 15000;
	public QueueReply Call(QueueMessage message) => CallAsync(message).GetAwaiter().GetResult();
	public async Task<QueueReply> CallAsync(QueueMessage message)
	{
		_launch ??= PrepareLaunch();
		message.Id = string.IsNullOrEmpty(message.Id) ? Guid.NewGuid().ToString("N") : message.Id;
		string path = QueueFiles.Request(_project, message.Id);
		QueueFiles.Write(path, JsonSerializer.Serialize(message, Json));
		var deadline = Stopwatch.StartNew();
		long nextStart = 0;
		int starts = 0;
		while (deadline.ElapsedMilliseconds < AcknowledgeTimeoutMs)
		{
			if (SharedFile.TryReadAllText(QueueFiles.Reply(_project, message.Id), out var json))
			{
				await ReleaseReplyAsync(message.Id);
				return JsonSerializer.Deserialize<QueueReply>(json, Json) ?? new QueueReply { Code = "dispatcher_invalid_reply" };
			}
			if (deadline.ElapsedMilliseconds >= nextStart && !QueueFiles.Alive(_project))
			{
				Start();
				starts++;
				nextStart = deadline.ElapsedMilliseconds + 3000;
			}
			await Task.Delay(20);
		}
		new TelemetryLog(_project, true).Write("queue_unacknowledged", message.Session, message.TaskId, new Dictionary<string, object?>
		{
			["Op"] = message.Op,
			["WaitedMs"] = deadline.ElapsedMilliseconds,
			["ServerAlive"] = QueueFiles.Alive(_project),
			["Starts"] = starts,
			["Host"] = HostPlatform.Current
		});
		return new QueueReply { Code = "dispatcher_unavailable", Message = "The queue server did not acknowledge request " + message.Id + ". Task: " + message.TaskId + ". The durable request remains; inspect the existing task before retrying.", Task = new QueueTaskInfo { Id = message.TaskId } };
	}
	// The server deletes the request microseconds after publishing the reply; give it that
	// moment so the reply is not left behind for the daily sweep.
	private async Task ReleaseReplyAsync(string id)
	{
		for (int attempt = 0; attempt < 10 && File.Exists(QueueFiles.Request(_project, id)); attempt++) await Task.Delay(20);
		QueueFiles.ReleaseReply(_project, id);
	}
	private QueueLaunch PrepareLaunch()
	{
		// Empty Location explicitly selects the self-contained, single-file snapshot path.
#pragma warning disable IL3000
		string assembly = typeof(QueueClient).Assembly.Location;
#pragma warning restore IL3000
		string executable = Environment.ProcessPath ?? "dotnet";
		var args = new List<string>();
		// Tests invoke the CLI assembly directly; never launch the test runner as the server.
		if (!string.IsNullOrEmpty(assembly) && !string.Equals(Path.GetFileNameWithoutExtension(executable), "agentbridge", StringComparison.OrdinalIgnoreCase))
		{
			var apphost = Path.Combine(Path.GetDirectoryName(assembly)!, OperatingSystem.IsWindows() ? "agentbridge.exe" : "agentbridge");
			if (File.Exists(apphost)) executable = apphost;
			else { executable = Path.GetFileNameWithoutExtension(executable) == "dotnet" ? executable : "dotnet"; args.Add(assembly); }
		}
		if (args.Count == 0) executable = QueueRuntime.Snapshot(executable, assembly);
		args.AddRange(new[] { "queue-server", "--project", _project });
		var launch = new QueueLaunch { Executable = executable, Arguments = args.ToArray() };
		// A healthy server reached through a shared filesystem may run on another OS.
		// Do not replace its host launcher with a path that only this client can execute.
		if (!QueueFiles.Alive(_project) || !BridgeInspector.Inspect(_project).ForeignHost)
			QueueFiles.Write(Path.Combine(QueueFiles.Root(_project), "launch.json"), JsonSerializer.Serialize(launch, Json));
		return launch;
	}
	private void Start()
	{
		var launch = _launch!;
		QueueFiles.Write(Path.Combine(QueueFiles.Root(_project), "launch.json"), JsonSerializer.Serialize(launch, Json));
		var start = new ProcessStartInfo(launch.Executable) { UseShellExecute = OperatingSystem.IsWindows(), RedirectStandardInput = !OperatingSystem.IsWindows(), RedirectStandardOutput = !OperatingSystem.IsWindows(), RedirectStandardError = !OperatingSystem.IsWindows(), CreateNoWindow = true, WindowStyle = ProcessWindowStyle.Hidden, WorkingDirectory = _project };
		foreach (string argument in launch.Arguments) start.ArgumentList.Add(argument);
		using var process = Process.Start(start);
	}
}
