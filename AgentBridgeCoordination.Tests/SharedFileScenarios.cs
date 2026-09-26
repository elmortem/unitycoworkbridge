using System.Diagnostics;
using static AgentBridge.Coordination.Tests.Harness;

namespace AgentBridge.Coordination.Tests;

// C25 — the editor republishes status.json, heartbeat and journal records with an atomic replace
// while every waiting CLI polls them. On Windows File.Replace fails with a sharing violation when
// a reader holds the destination without FILE_SHARE_DELETE; that surfaced as IOException from
// BridgeStatusWriter and runtime_error tasks when two agents shared one editor.
internal static class SharedFileScenarios
{
	private const string Tail = "|end";

	public static void Run(string root, List<string> covered)
	{
		var directory = Path.Combine(root, "shared-file");
		Directory.CreateDirectory(directory);
		var path = Path.Combine(directory, "status.json");

		// A reader holding the file the way SharedFile opens it never blocks the replace.
		SharedFile.WriteAtomic(path, Payload(0));
		using (new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
		{
			SharedFile.WriteAtomic(path, Payload(1));
		}

		Expect(SharedFile.ReadAllText(path) == Payload(1), "the replace under a shared reader publishes the new content");

		// The negative control proves the scenario is real where it matters: a plain
		// File.ReadAllText-style handle still defeats the replace on Windows, retries included.
		if (OperatingSystem.IsWindows())
		{
			var failed = false;
			using (new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read))
			{
				try
				{
					SharedFile.WriteAtomic(path, Payload(2));
				}
				catch (IOException)
				{
					failed = true;
				}
				catch (UnauthorizedAccessException)
				{
					failed = true;
				}
			}

			Expect(failed, "a reader without FILE_SHARE_DELETE must still block File.Replace on Windows");
		}

		Expect(!SharedFile.TryReadAllText(Path.Combine(directory, "missing.json"), out var missing) && missing == "",
			"a missing file is a failed read, not an exception");

		covered.Add("C25");
	}

	// Two child readers poll as fast as they can while this process replaces the file in a loop:
	// no write may fail and no read may observe a torn file.
	public static void CrossProcess(string root, List<string> covered)
	{
		var directory = Path.Combine(root, "shared-file-race");
		Directory.CreateDirectory(directory);
		var path = Path.Combine(directory, "status.json");
		SharedFile.WriteAtomic(path, Payload(0));

		var readers = new[] { StartReader(path), StartReader(path) };
		var writes = 0;
		var clock = Stopwatch.StartNew();
		while (clock.ElapsedMilliseconds < 3_000)
		{
			SharedFile.WriteAtomic(path, Payload(++writes));
		}

		foreach (var reader in readers)
		{
			Expect(reader.WaitForExit(30_000), "a shared reader must finish");
			var output = reader.StandardOutput.ReadToEnd().Trim();
			Expect(reader.ExitCode == 0, "shared reader failed: " + output + " " + reader.StandardError.ReadToEnd());
			var parts = output.Split('|');
			Expect(parts.Length == 2 && int.Parse(parts[0]) > 0 && parts[1] == "0",
				"reads must happen and none may be torn: " + output);
		}

		Expect(writes > 50, "the writer must actually race the readers: " + writes);
		covered.Add("C25x");
	}

	// Child role: reads for a little longer than the writer runs and reports "reads|torn".
	public static int Read(string path)
	{
		var reads = 0;
		var torn = 0;
		var clock = Stopwatch.StartNew();
		while (clock.ElapsedMilliseconds < 3_500)
		{
			var text = SharedFile.ReadAllText(path);
			reads++;
			if (!text.StartsWith("seq:", StringComparison.Ordinal) || !text.EndsWith(Tail, StringComparison.Ordinal))
			{
				torn++;
			}
		}

		Console.Out.WriteLine(reads + "|" + torn);
		return 0;
	}

	private static Process StartReader(string path)
	{
		return Child.Start("shared-reader", path, "");
	}

	// Large enough that a non-atomic publish would be visible as a torn read.
	private static string Payload(int sequence)
	{
		return "seq:" + sequence + "|" + new string('x', 16 * 1024) + Tail;
	}
}
