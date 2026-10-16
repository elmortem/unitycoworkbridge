using System;
using System.IO;
using System.Text;
using AgentBridge.Coordination;

namespace AgentBridge.Queueing
{
	// The executor never locks the queue. Only the external server owns server.lock;
	// clients publish immutable messages and read atomic replies through shared handles.
	public static class QueueFiles
	{
		public const string Capability = "independent-queue-v1";
		// The live editor keeps the dispatcher running on its own machine; foreign clients never start one.
		public const string HostCapability = "dispatcher-host-v1";
		public static string Root(string project) { return Path.Combine(project, "Library", "AgentBridge", "Queue"); }
		public static string Request(string project, string id) { return Path.Combine(Root(project), "Requests", Safe(id) + ".json"); }
		public static string Reply(string project, string id) { return Path.Combine(Root(project), "Replies", Safe(id) + ".json"); }
		// The reader owns a reply once the server has finished the transaction: the server deletes
		// the request right after publishing the reply. While the request still exists the reply
		// guards it against re-handling after a server crash, so it stays for the server's sweep.
		public static void ReleaseReply(string project, string id)
		{
			try
			{
				if (File.Exists(Request(project, id))) return;
				File.Delete(Reply(project, id));
			}
			catch (IOException) { }
			catch (UnauthorizedAccessException) { }
		}
		public static string Task(string project, string id) { return Path.Combine(Root(project), "Tasks", Safe(id) + ".json"); }
		public static string Safe(string id)
		{
			if (string.IsNullOrWhiteSpace(id) || id.Length > 200 || id == "." || id == "..") throw new ArgumentException("invalid task id");
			foreach (char c in id) if (!char.IsLetterOrDigit(c) && c != '_' && c != '-' && c != '.') throw new ArgumentException("invalid task id");
			return id;
		}
		public static void Write(string path, string json)
		{
			Directory.CreateDirectory(Path.GetDirectoryName(path));
			string temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
			try
			{
				byte[] bytes = new UTF8Encoding(false).GetBytes(json);
				using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
				{
					stream.Write(bytes, 0, bytes.Length);
					stream.Flush(true);
				}
				for (int attempt = 0; ; attempt++)
				{
					try
					{
						if (File.Exists(path)) File.Replace(temporary, path, null, true);
						else File.Move(temporary, path);
						break;
					}
					catch (IOException) when (attempt < 4) { System.Threading.Thread.Sleep(10); }
				}
			}
			finally { if (File.Exists(temporary)) File.Delete(temporary); }
		}
		public static bool Alive(string project)
		{
			string text;
			long stamp;
			return SharedFile.TryReadAllText(Path.Combine(Root(project), "heartbeat"), out text)
				&& long.TryParse(text, out stamp) && Math.Abs(DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() - stamp) < 5000;
		}
		public static bool Terminal(string status)
		{
			return status == "success" || status == "test_failure" || status == "no_tests_matched"
				|| status == "ambiguous_test_filter" || status == "compiler_error" || status == "runtime_error"
				|| status == "timeout" || status == "canceled" || status == "interrupted_by_domain_reload"
				|| status == "rejected" || status == "stale_input" || status == "evidence_unavailable";
		}
	}
}
