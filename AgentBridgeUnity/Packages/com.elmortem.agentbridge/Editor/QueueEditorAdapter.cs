using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using AgentBridge.Coordination;
using AgentBridge.Queueing;
using UnityEngine;

namespace AgentBridge
{
	// A tick may publish a request or collect its reply; it never waits for a process or lock.
	public static class QueueEditorAdapter
	{
		private static readonly Dictionary<string, string> Pending = new Dictionary<string, string>();
		private static DateTime _lastLaunch;
		private static bool _introduced;
		public static void Tick()
		{
			if (_introduced) return;
			QueueReply reply;
			if (TryCall("hello", new QueueMessage { Op = "hello", Incarnation = CoordinationEditorAdapter.Incarnation, EditorPid = Process.GetCurrentProcess().Id, EditorStartedTicks = Process.GetCurrentProcess().StartTime.ToUniversalTime().Ticks }, out reply) && reply.Ok) _introduced = true;
		}
		public static bool TryCall(string key, QueueMessage message, out QueueReply reply)
		{
			reply = null;
			try
			{
				EnsureServer();
				string id;
				if (!Pending.TryGetValue(key, out id))
				{
					id = Guid.NewGuid().ToString("N");
					message.Id = id;
					QueueFiles.Write(QueueFiles.Request(BridgePaths.ProjectRoot, id), JsonUtility.ToJson(message));
					Pending[key] = id;
				}
				string json;
				if (!SharedFile.TryReadAllText(QueueFiles.Reply(BridgePaths.ProjectRoot, id), out json)) return false;
				QueueFiles.ReleaseReply(BridgePaths.ProjectRoot, id);
				reply = JsonUtility.FromJson<QueueReply>(json);
				Pending.Remove(key);
				return reply != null;
			}
			catch (Exception error)
			{
				UnityEngine.Debug.LogWarning("[AgentBridge] Queue handshake deferred: " + error.Message);
				return false;
			}
		}
		public static bool TryClaim(TaskRequest request, CoordinationCommand command, out string reason)
		{
			reason = CoordinationCodes.Busy;
			string path = Path.Combine(BridgePaths.Inbox, request.Id + ".task.json");
			string payload = string.IsNullOrEmpty(request.PayloadFile) ? null : Path.Combine(BridgePaths.Inbox, request.PayloadFile);
			string hash = TaskFileHash.HashOf(path, payload);
			QueueReply reply;
			if (!TryCall("claim:" + request.Id, new QueueMessage { Op = "claim", TaskId = request.Id,
				RequestHash = hash, Session = request.AgentSessionId ?? "", Incarnation = CoordinationEditorAdapter.Incarnation,
				CoordinationJson = command == null ? "" : JsonUtility.ToJson(command) }, out reply)) return false;
			if (reply.Ok && reply.RequestHash != hash) { reason = "id_conflict: input changed during claim"; return false; }
			reason = reply.Ok ? "" : reply.Code + ": " + reply.Message;
			if (!reply.Ok && reply.Code == CoordinationCodes.Busy) reason = CoordinationCodes.Busy;
			return reply.Ok;
		}
		public static bool TryCoordinate(CoordinationCommand command, out CoordinationReply reply)
		{
			string nonce = command.Nonce;
			if (command.Op != CoordinationEngine.OpIncarnation) command.Nonce = "";
			string key = "coord:" + JsonUtility.ToJson(command);
			command.Nonce = nonce;
			QueueReply answer;
			reply = null;
			if (!TryCall(key, new QueueMessage { Op = "coord", CoordinationJson = JsonUtility.ToJson(command) }, out answer)) return false;
			reply = string.IsNullOrEmpty(answer.CoordinationJson) ? CoordinationReply.Fail(answer.Code, answer.Message) : JsonUtility.FromJson<CoordinationReply>(answer.CoordinationJson);
			return true;
		}
		// One-shot controls always mint their own message. A later identical action cannot
		// consume an old acknowledgment left by a previous user action or attachment.
		public static void Post(QueueMessage message)
		{
			EnsureServer();
			message.Id = Guid.NewGuid().ToString("N");
			QueueFiles.Write(QueueFiles.Request(BridgePaths.ProjectRoot, message.Id), JsonUtility.ToJson(message));
		}
		public static bool Cancel(string id) { Post(new QueueMessage { Op = "cancel", TaskId = id, Human = true }); return true; }
		public static bool Move(string id, string beforeId) { Post(new QueueMessage { Op = "move", TaskId = id, BeforeId = beforeId ?? "" }); return true; }
		public static void Requeue(string id) { Post(new QueueMessage { Op = "requeue", TaskId = id, Incarnation = CoordinationEditorAdapter.Incarnation }); }
		public static bool IsCanceled(string id)
		{
			string json;
			if (!SharedFile.TryReadAllText(QueueFiles.Task(BridgePaths.ProjectRoot, id), out json)) return false;
			var task = JsonUtility.FromJson<TaskEnvelope>(json);
			return task != null && task.Info != null && task.Info.Status == "canceled";
		}
		public static bool IsAuthorizedCancellation(string id, string target)
		{
			string json;
			if (!SharedFile.TryReadAllText(QueueFiles.Task(BridgePaths.ProjectRoot, id), out json)) return false;
			var task = JsonUtility.FromJson<TaskEnvelope>(json);
			return task != null && task.AuthorizedCancelTarget == target;
		}
		[Serializable] private sealed class TaskEnvelope { public QueueTaskInfo Info; public string AuthorizedCancelTarget; }
		private static void EnsureServer()
		{
			string project = BridgePaths.ProjectRoot;
			if (QueueFiles.Alive(project) || DateTime.UtcNow - _lastLaunch < TimeSpan.FromSeconds(3)) return;
			_lastLaunch = DateTime.UtcNow;
			QueueLaunch launch = null;
			string json;
			if (SharedFile.TryReadAllText(Path.Combine(QueueFiles.Root(project), "launch.json"), out json)) launch = JsonUtility.FromJson<QueueLaunch>(json);
			if (launch == null || !File.Exists(launch.Executable))
			{
				string native = Environment.OSVersion.Platform == PlatformID.Win32NT
					? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "AgentBridge", "bin", "agentbridge.exe")
					: Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".local", "bin", "agentbridge");
				if (!File.Exists(native)) return;
				launch = new QueueLaunch { Executable = native, Arguments = new[] { "queue-server", "--project", project } };
			}
			using (var process = Process.Start(new ProcessStartInfo(launch.Executable, string.Join(" ", launch.Arguments.Select(Quote)))
			{ UseShellExecute = Environment.OSVersion.Platform == PlatformID.Win32NT, CreateNoWindow = true, WindowStyle = ProcessWindowStyle.Hidden, WorkingDirectory = project })) { }
		}
		private static string Quote(string argument)
		{
			// CommandLineToArgvW-compatible quoting, also accepted by Mono's Process parser.
			var result = new System.Text.StringBuilder("\"");
			int slashes = 0;
			foreach (char c in argument)
			{
				if (c == '\\') { slashes++; continue; }
				result.Append('\\', c == '"' ? slashes * 2 + 1 : slashes).Append(c); slashes = 0;
			}
			return result.Append('\\', slashes * 2).Append('"').ToString();
		}
	}
}
