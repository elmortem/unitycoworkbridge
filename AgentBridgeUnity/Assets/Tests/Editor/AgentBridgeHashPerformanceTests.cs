using System;
using System.Collections;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using AgentBridge;
using NUnit.Framework;
using UnityEngine.TestTools;

public class AgentBridgeHashPerformanceTests
{
	[TestCase(0)]
	[TestCase(1)]
	[TestCase(63)]
	[TestCase(65537)]
	[TestCase(1048576)]
	public void NativeAndPortableSha256AreIdenticalAndReusable(int length)
	{
		var bytes = new byte[length + 7];
		new Random(42).NextBytes(bytes);
		using (var expected = SHA256.Create())
		using (var actual = ContentHash.Create())
		{
			CollectionAssert.AreEqual(expected.ComputeHash(bytes, 7, length), actual.ComputeHash(bytes, 7, length));
			CollectionAssert.AreEqual(expected.ComputeHash(bytes), actual.ComputeHash(bytes));
			actual.Initialize();
			int split = bytes.Length / 2;
			actual.TransformBlock(bytes, 0, split, null, 0);
			actual.TransformFinalBlock(bytes, split, bytes.Length - split);
			CollectionAssert.AreEqual(expected.ComputeHash(bytes), actual.Hash);
		}
	}

	[Test]
	public void StreamingSnapshotPreservesExistingDigestFormat()
	{
		string root = NewRoot();
		try
		{
			var bytes = new byte[150001];
			new Random(8).NextBytes(bytes);
			File.WriteAllBytes(Path.Combine(root, "asset.bin"), bytes);
			var snapshot = ValidationInputSnapshot.Capture(new[] { root }, new string[0], "test");
			using (var sha = SHA256.Create())
			using (var memory = new MemoryStream())
			{
				byte[] header = Encoding.UTF8.GetBytes("context\ntest\nroot\nr0\nfile\nr0/asset.bin\n150001\n");
				memory.Write(header, 0, header.Length);
				memory.Write(bytes, 0, bytes.Length);
				memory.Position = 0;
				Assert.AreEqual(BitConverter.ToString(sha.ComputeHash(memory)).Replace("-", "").ToLowerInvariant(), snapshot.Digest);
				Assert.IsTrue(snapshot.Complete);
				Assert.AreEqual(bytes.Length, snapshot.TotalBytes);
			}
		}
		finally { Directory.Delete(root, true); }
	}

	[UnityTest]
	public IEnumerator BackgroundSnapshotYieldsAndOwnsImmutableInputs()
	{
		string root = NewRoot();
		using (var entered = new ManualResetEventSlim())
		using (var release = new ManualResetEventSlim())
		{
			try
			{
				File.WriteAllText(Path.Combine(root, "asset.bin"), "before");
				int mainThread = Thread.CurrentThread.ManagedThreadId;
				int workerThread = mainThread;
				string[] roots = { root };
				var job = new InputHashJob(roots, new string[0], "test", path => {
					workerThread = Thread.CurrentThread.ManagedThreadId;
					entered.Set();
					if (!release.Wait(5000)) throw new TimeoutException("test worker gate");
					return false;
				});
				roots[0] = root + "_does_not_exist";
				var work = job.Start();
				var deadline = DateTime.UtcNow.AddSeconds(5);
				while (!entered.IsSet && DateTime.UtcNow < deadline) yield return null;
				Assert.IsTrue(entered.IsSet);
				Assert.IsFalse(work.IsCompleted, "the editor got a turn while hashing was blocked");
				Assert.AreNotEqual(mainThread, workerThread);
				release.Set();
				while (!work.IsCompleted && DateTime.UtcNow < deadline) yield return null;
				Assert.IsTrue(work.IsCompleted);
				Assert.IsTrue(work.Result.Complete, "mutating the caller's roots cannot redirect the worker");
			}
			finally { release.Set(); Directory.Delete(root, true); }
		}
	}

	[UnityTest]
	public IEnumerator DirectoryTimestampIsIgnoredButRenamesAndFileWritesAreObserved()
	{
		string root = NewRoot();
		string child = Path.Combine(root, "child");
		Directory.CreateDirectory(child);
		File.WriteAllText(Path.Combine(child, "input.bin"), "original");
		try
		{
			using (var monitor = new ValidationInputMonitor(new[] { root }, new string[0]))
			{
				Directory.SetLastWriteTimeUtc(child, DateTime.UtcNow.AddSeconds(-5));
				var until = DateTime.UtcNow.AddMilliseconds(250);
				while (DateTime.UtcNow < until) yield return null;
				Assert.IsTrue(monitor.Observed);
				Assert.AreEqual(0, monitor.EventCount, "directory timestamp alone does not change any input bytes");
				File.WriteAllText(Path.Combine(child, "input.bin"), "modified");
				until = DateTime.UtcNow.AddSeconds(5);
				while (monitor.EventCount == 0 && DateTime.UtcNow < until) yield return null;
				Assert.Greater(monitor.EventCount, 0, "file writes are still observed");
			}
			using (var monitor = new ValidationInputMonitor(new[] { root }, new string[0]))
			{
				Directory.Move(child, Path.Combine(root, "renamed"));
				var until = DateTime.UtcNow.AddSeconds(5);
				while (monitor.EventCount == 0 && DateTime.UtcNow < until) yield return null;
				Assert.Greater(monitor.EventCount, 0, "moving a directory changes the paths of its inputs");
			}
		}
		finally { Directory.Delete(root, true); }
	}

	private static string NewRoot()
	{
		string root = Path.Combine(Path.GetTempPath(), "AgentBridgeHash_" + Guid.NewGuid().ToString("N"));
		Directory.CreateDirectory(root);
		return root;
	}
}
