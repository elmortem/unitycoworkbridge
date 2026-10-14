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
	public QueueReply Call(QueueMessage message) => CallAsync(message).GetAwaiter().GetResult();
	public async Task<QueueReply> CallAsync(QueueMessage message)
	{
		_launch ??= PrepareLaunch();
		message.Id = string.IsNullOrEmpty(message.Id) ? Guid.NewGuid().ToString("N") : message.Id;
		string path = QueueFiles.Request(_project, message.Id);
		QueueFiles.Write(path, JsonSerializer.Serialize(message, Json));
		var deadline = Stopwatch.StartNew();
		long nextStart = 0;
		while (deadline.ElapsedMilliseconds < 15000)
		{
			if (SharedFile.TryReadAllText(QueueFiles.Reply(_project, message.Id), out var json))
				return JsonSerializer.Deserialize<QueueReply>(json, Json) ?? new QueueReply { Code = "dispatcher_invalid_reply" };
			if (deadline.ElapsedMilliseconds >= nextStart && !QueueFiles.Alive(_project))
			{
				Start();
				nextStart = deadline.ElapsedMilliseconds + 3000;
			}
			await Task.Delay(20);
		}
		return new QueueReply { Code = "dispatcher_unavailable", Message = "The queue server did not acknowledge request " + message.Id + ". Task: " + message.TaskId + ". The durable request remains; inspect the existing task before retrying.", Task = new QueueTaskInfo { Id = message.TaskId } };
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
