using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using AgentBridge;

namespace AgentBridge.Coordination.Tests;

internal static class HashingScenarios
{
	public static void Run(string root, List<string> covered)
	{
		var data = new byte[1048579];
		new Random(42).NextBytes(data);
		using (var expected = SHA256.Create())
		using (var actual = ContentHash.Create())
		{
			foreach (int length in new[] { 0, 1, 63, 64, 65537, 1048576 })
			{
				if (!expected.ComputeHash(data, 3, length).SequenceEqual(actual.ComputeHash(data, 3, length)))
					throw new Exception("SHA256 differs for length " + length);
			}
			actual.TransformBlock(data, 3, 63, null, 0);
			actual.TransformFinalBlock(data, 66, 512);
			if (!expected.ComputeHash(data, 3, 575).SequenceEqual(actual.Hash))
				throw new Exception("Incremental SHA256 differs");
			Console.WriteLine("SHA256 provider: " + actual.GetType().FullName);
		}
#if !UNITY_EDITOR_WIN
		if (typeof(ContentHash).GetNestedType("WindowsSha256", BindingFlags.NonPublic) != null)
			throw new Exception("Portable builds must not contain the Windows native implementation");
#endif
		string fixture = Path.Combine(root, "hash-format");
		Directory.CreateDirectory(fixture);
		File.WriteAllBytes(Path.Combine(fixture, "data.bin"), data);
		var snapshot = ValidationInputSnapshot.Capture(new[] { fixture }, Array.Empty<string>(), "test");
		using (var sha = SHA256.Create())
		{
			string fileHash = Convert.ToHexString(sha.ComputeHash(data)).ToLowerInvariant();
			byte[] stream = Encoding.UTF8.GetBytes("context\ntest\nroot\nr0\nfile\nr0/data.bin\n" + data.Length + "\n" + fileHash + "\n");
			if (!snapshot.Complete || snapshot.Digest != Convert.ToHexString(sha.ComputeHash(stream)).ToLowerInvariant())
				throw new Exception("Snapshot digest does not hash each file's content hash");
		}
		covered.Add("C21_hash_platform_and_format");
		SaveDuringCapture(root, covered);
#if UNITY_EDITOR_WIN
		DigestRereadsOnlyChangedFiles(root, covered);
#endif
	}

#if UNITY_EDITOR_WIN
	// A digest of an unchanged project reads nothing; an edit that keeps length and write time
	// is still read, because it moves the change time.
	private static void DigestRereadsOnlyChangedFiles(string root, List<string> covered)
	{
		string fixture = Path.Combine(root, "hash-cache");
		Directory.CreateDirectory(fixture);
		var big = new byte[1024 * 1024];
		new Random(27).NextBytes(big);
		string a = Path.Combine(fixture, "a.bin");
		string b = Path.Combine(fixture, "b.asset");
		File.WriteAllBytes(a, big);
		File.WriteAllText(b, "value: 1");
		Thread.Sleep(TimeSpan.FromTicks(InputHashCache.RacyWindowTicks) + TimeSpan.FromMilliseconds(300));
		string cachePath = Path.Combine(root, "hash-cache.txt");

		ValidationInputSnapshot Step(InputHashCache cache, string name)
		{
			var cached = ValidationInputSnapshot.Capture(new[] { fixture }, Array.Empty<string>(), "c27", null, cache);
			var plain = ValidationInputSnapshot.Capture(new[] { fixture }, Array.Empty<string>(), "c27");
			if (!cached.Complete || !plain.Complete || cached.Digest != plain.Digest)
				throw new Exception("C27 " + name + ": cached digest differs from a full read");
			return cached;
		}

		void Expect(ValidationInputSnapshot snapshot, long readBytes, string name)
		{
			if (snapshot.ReadBytes != readBytes)
				throw new Exception("C27 " + name + ": ReadBytes " + snapshot.ReadBytes + ", expected " + readBytes);
		}

		var cache = new InputHashCache(cachePath);
		var first = Step(cache, "first");
		Expect(first, big.Length + new FileInfo(b).Length, "first");
		Expect(Step(cache, "second"), 0, "second");
		Expect(Step(new InputHashCache(cachePath), "reloaded"), 0, "reloaded");

		DateTime written = File.GetLastWriteTimeUtc(b);
		File.WriteAllText(b, "value: 2");
		File.SetLastWriteTimeUtc(b, written);
		var edited = Step(cache, "edited");
		if (edited.Digest == first.Digest)
			throw new Exception("C27 edited: digest did not move after a same-length edit");
		Expect(edited, new FileInfo(b).Length, "edited");
		Expect(Step(cache, "racy"), new FileInfo(b).Length, "racy");
		covered.Add("C27_digest_rereads_only_changed_files");
	}
#endif

	// Unity saves by renaming a temporary file over the target, and Windows refuses that rename
	// while any handle to the target is open. A save that holds the gate must never meet a digest read.
	private static void SaveDuringCapture(string root, List<string> covered)
	{
		string fixture = Path.Combine(root, "save-gate");
		string scratch = Path.Combine(root, "save-gate-temp");
		Directory.CreateDirectory(fixture);
		Directory.CreateDirectory(scratch);
		string target = Path.Combine(fixture, "scene.unity");
		var data = new byte[32 * 1024 * 1024];
		new Random(7).NextBytes(data);
		File.WriteAllBytes(target, data);

		int stop = 0;
		int captures = 0;
		string readerError = null;
		var reader = new Thread(() =>
		{
			while (Volatile.Read(ref stop) == 0)
			{
				var snapshot = ValidationInputSnapshot.Capture(new[] { fixture }, Array.Empty<string>(), "save-gate");
				if (!snapshot.Complete)
				{
					readerError = snapshot.Reason;
					return;
				}
				Interlocked.Increment(ref captures);
			}
		});
		reader.Start();

		var refused = new List<string>();
		for (int save = 0; save < 8; save++)
		{
			bool owner = InputReadGate.Close();
			string temporary = Path.Combine(scratch, "UnityTempFile-" + save);
			data[0] = (byte)save;
			File.WriteAllBytes(temporary, data);
			try
			{
				File.Move(temporary, target, true);
			}
			catch (Exception error)
			{
				refused.Add(error.Message);
				File.Delete(temporary);
			}
			if (owner)
				InputReadGate.Open();
			Thread.Sleep(60);
		}

		Volatile.Write(ref stop, 1);
		reader.Join();
		if (readerError != null)
			throw new Exception("Digest failed beside a save: " + readerError);
		if (refused.Count > 0)
			throw new Exception(refused.Count + " of 8 saves met an open digest read: " + refused[0]);
		if (Volatile.Read(ref captures) == 0)
			throw new Exception("No digest ran beside the saves");
		covered.Add("C26_save_waits_for_digest_reads");
	}
}
