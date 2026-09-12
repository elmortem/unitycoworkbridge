using System;
using System.Collections.Generic;
using System.IO;
using AgentBridge;
using NUnit.Framework;

// test-cache-v2: several completed sets side by side, an index that is published after its
// entries, and a lookup that refuses everything it cannot fully account for.
public class AgentBridgeTestCacheTests
{
	private readonly List<string> _published = new List<string>();

	[TearDown]
	public void TearDown()
	{
		// Only the entries this fixture created; the project's own cache is left alone.
		foreach (string id in _published)
		{
			TestRunDumpStore.Forget(id);
		}

		_published.Clear();
	}

	[Test]
	public void SeveralSetsCoexistAndAreFoundByTheirOwnInputDigest()
	{
		string first = Publish("digest-one", "EditMode", "Task_one", "Suite.Alpha", "Suite.Beta");
		string second = Publish("digest-two", "EditMode", "Task_two", "Suite.Gamma");

		TestRunDump loadedFirst;
		TestRunDump loadedSecond;
		Assert.IsTrue(TestRunDumpStore.TryLoad(first, out loadedFirst), "the first set survives the second");
		Assert.IsTrue(TestRunDumpStore.TryLoad(second, out loadedSecond), "the second set is stored too");
		Assert.AreEqual("digest-one", loadedFirst.InputDigest, "each set keeps its own input digest");
		Assert.AreEqual("digest-two", loadedSecond.InputDigest, "results from different digests are not merged");

		TestCacheIndex index = TestRunDumpStore.ReadIndex();
		Assert.IsNotNull(index.Find(first), "the index knows the first set");
		Assert.IsNotNull(index.Find(second), "the index knows the second set");
		Assert.AreEqual(2, loadedFirst.Entries.Count, "the whole per-test result is stored");
	}

	[Test]
	public void ForgettingASetRemovesItFromTheIndexAndFromDisk()
	{
		string id = Publish("digest-forget", "EditMode", "Task_forget", "Suite.One");
		TestRunDumpStore.Forget(id);
		_published.Remove(id);

		TestRunDump dump;
		Assert.IsNull(TestRunDumpStore.ReadIndex().Find(id), "a forgotten set leaves the index");
		Assert.IsFalse(TestRunDumpStore.TryLoad(id, out dump), "and its payload is gone");
	}

	[Test]
	public void IndexKeeps32SetsAndEvictsTheLeastRecentlyUsed()
	{
		var index = new TestCacheIndex();
		for (int i = 0; i < 33; i++)
		{
			index.Entries.Add(new TestCacheEntryInfo { Id = "e" + i, LastUsedMs = 1000 + i });
		}

		List<string> evicted = index.Trim();
		Assert.AreEqual(TestCacheIndex.MaxEntries, index.Entries.Count, "the cap is 32 completed sets");
		Assert.AreEqual(1, evicted.Count, "exactly one set is evicted");
		Assert.AreEqual("e0", evicted[0], "the least recently used one goes");
		Assert.IsNotNull(index.Find("e32"), "the newest set stays");
	}

	[Test]
	public void CoverageAcceptsASubsetAndRefusesWhatItDoesNotCover()
	{
		TestRunDump dump = Dump("digest-cover", "EditMode", "Task_cover", "Suite.Alpha", "Suite.Beta");

		var subset = new TaskRequest { Kind = "tests", TestMode = "EditMode", TestNames = new[] { "Suite.Alpha" } };
		Assert.IsTrue(TestFilterCoverage.Covers(dump, subset), "a named subset of a stored set is covered");
		Assert.AreEqual(1, TestFilterCoverage.Select(dump.Entries, subset).Count, "and selects exactly that test");

		var missing = new TaskRequest { Kind = "tests", TestMode = "EditMode", TestNames = new[] { "Suite.Missing" } };
		Assert.IsFalse(TestFilterCoverage.Covers(dump, missing), "a test the set never ran is not covered");

		var empty = new TaskRequest { Kind = "tests", TestMode = "EditMode", TestNames = new[] { "Suite.Nothing" } };
		Assert.AreEqual(0, TestFilterCoverage.Select(dump.Entries, empty).Count, "an empty selection is not a pass");
	}

	[Test]
	public void AMissingMandatoryArtifactRemovesTheSetFromAcceptance()
	{
		TestRunDump dump = Dump("digest-shot", "PlayMode", "Task_shot", "Suite.Visual");
		string artifact = Path.Combine(BridgePaths.ArtifactsFor("AgentBridgeTestCacheTests"), "screen.png");
		File.WriteAllText(artifact, "not really a png");
		dump.Artifacts.Add(artifact);

		Assert.IsTrue(TestCacheQuery.ArtifactsPresent(dump), "a set whose artifacts exist can be served");

		File.Delete(artifact);
		Assert.IsFalse(
			TestCacheQuery.ArtifactsPresent(dump),
			"a screenshot that no longer exists must not be handed out as visual acceptance");
	}

	[Test]
	public void OnlyValidSetsAreEverStored()
	{
		TestRunDump dump = Dump("digest-stale", "EditMode", "Task_stale", "Suite.One");
		dump.Validity = EvidenceRecord.Stale;
		string id = TestRunDumpStore.Publish(dump, 1000);
		_published.Add(id);

		TestCacheEntryInfo entry = TestRunDumpStore.ReadIndex().Find(id);
		Assert.AreEqual(
			EvidenceRecord.Stale,
			entry.Validity,
			"the index records validity so a lookup can refuse a set the runner should never have promoted");
	}

	private string Publish(string digest, string mode, string taskId, params string[] tests)
	{
		string id = TestRunDumpStore.Publish(Dump(digest, mode, taskId, tests), 1000);
		_published.Add(id);
		return id;
	}

	private static TestRunDump Dump(string digest, string mode, string taskId, params string[] tests)
	{
		var dump = new TestRunDump
		{
			SourceTaskId = taskId,
			InputDigest = digest,
			SourceFingerprint = "source-" + digest,
			Fingerprint = "artifact-" + digest,
			Validity = EvidenceRecord.Valid,
			FinishedAtUtc = DateTime.UtcNow.ToString("o")
		};
		dump.Filter.TestMode = mode;
		dump.Filter.TestNames = tests;

		foreach (string test in tests)
		{
			dump.Entries.Add(new TestCaseResult
			{
				FullName = test,
				Assembly = "AgentBridge.ProbeTests",
				Status = "Passed",
				DurationSeconds = 0.01
			});
		}

		return dump;
	}
}
