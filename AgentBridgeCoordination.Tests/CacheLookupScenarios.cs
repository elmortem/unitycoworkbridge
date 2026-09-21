using AgentBridge;
using static AgentBridge.Coordination.Tests.Harness;

namespace AgentBridge.Coordination.Tests;

// C24 — what a cache hit is allowed to claim, and how often it is allowed to ask.
//
// Serving a result from the cache makes the same claim a real run makes: the project held still while
// the decision was made. Both witnesses of that claim open together here, and they must cover the
// reads the decision stands on — including an input that was edited and put straight back, which the
// digest alone cannot see.
//
// The other half is restraint. A digest that missed against the same sources and the same candidate
// entries will miss again a second later, so the memo answers for it until one of those two facts
// changes or the backstop is due.
internal static class CacheLookupScenarios
{
	public static void Run(string root, List<string> covered)
	{
		InputWatchHub.Shutdown();
		try
		{
			AQuietLookupHasBothWitnesses(root);
			AnEditThatWasRevertedIsCaught(root);
			ExcludedAndIgnoredAreNotInputs(root);
			AnUnreachableRootIsUnknown(root);
			AClosedWitnessCountsNothing(root);
			AMissIsRememberedUntilSomethingChanges();
			covered.Add("C24_cache_lookup");
		}
		finally
		{
			InputWatchHub.Shutdown();
		}
	}

	// The baseline: the window opens over a tree nobody touches, and both witnesses say so.
	private static void AQuietLookupHasBothWitnesses(string project)
	{
		var assets = NewTree(project, "quiet");
		using var witness = Open(assets, Array.Empty<string>(), null);

		Expect(witness.Monitor.Observed, "a witness that opened observes its inputs");
		Expect(witness.Start.Complete, "and holds a complete manifest of them");
		Expect(witness.Start.Entries.Count == 3, "which covers every input of the tree");

		var verdict = witness.VerifyAsync().GetAwaiter().GetResult();
		Expect(verdict.Complete, "verifying a readable tree concludes");
		Expect(verdict.Changed == 0, "an untouched tree has nothing to report");
		Expect(witness.Monitor.EventCount == 0, "and the observer agrees");
	}

	// The reason the manifest is part of the lookup at all: the digest before and the digest after are
	// equal here, so only the write time says the project did not hold still.
	private static void AnEditThatWasRevertedIsCaught(string project)
	{
		var assets = NewTree(project, "reverted");
		var script = Path.Combine(assets, "Thing.cs");
		var text = File.ReadAllText(script);
		using var witness = Open(assets, Array.Empty<string>(), null);

		Thread.Sleep(50);
		File.WriteAllText(script, text);

		var verdict = witness.VerifyAsync().GetAwaiter().GetResult();
		Expect(verdict.Complete, "verifying a readable tree concludes");
		Expect(verdict.Changed == 1, "an input that was rewritten with its own bytes is a change");
		Expect(verdict.Paths.Count == 1 && verdict.Paths[0] == ValidationInputSnapshot.Normalize(script),
			"and it is named by path");
	}

	// The witness has to describe exactly the inputs the digest describes, or the two would disagree
	// about what a change even is.
	private static void ExcludedAndIgnoredAreNotInputs(string project)
	{
		var assets = NewTree(project, "excluded");
		var output = Path.Combine(assets, "Temp");
		Directory.CreateDirectory(output);
		var artifact = Path.Combine(output, "artifact.bin");
		File.WriteAllText(artifact, "editor output");
		var scratch = Path.Combine(assets, "InitTestScene.unity");
		File.WriteAllText(scratch, "temporary bootstrap scene");

		Func<string, bool> ignore = path =>
			ValidationInputSnapshot.Normalize(path).EndsWith("/InitTestScene.unity", StringComparison.OrdinalIgnoreCase);
		using var witness = Open(assets, new[] { output }, ignore);
		Expect(witness.Start.Entries.Count == 3, "neither editor output nor declared scratch is an input");

		Thread.Sleep(50);
		File.WriteAllText(artifact, "editor output again");
		File.Delete(scratch);

		var verdict = witness.VerifyAsync().GetAwaiter().GetResult();
		Expect(verdict.Complete, "verifying a readable tree concludes");
		Expect(verdict.Changed == 0, "and touching what is not an input changes nothing");
		Thread.Sleep(200);
		Expect(witness.Monitor.EventCount == 0, "the observer window applies the same rules");
	}

	// An input root nobody can walk makes the witness missing, not silent.
	private static void AnUnreachableRootIsUnknown(string project)
	{
		var missing = Path.Combine(project, "lookup", "never-created", "Assets");
		using var witness = CacheLookupWitness
			.OpenAsync(new[] { missing }, Array.Empty<string>(), null).GetAwaiter().GetResult();

		Expect(!witness.Start.Complete, "an unreachable root cannot be captured");
		Expect(witness.Start.Reason.Length > 0, "and it says why");
		Expect(!witness.Monitor.Observed, "a root that cannot be observed is not claimed as observed");

		var verdict = witness.VerifyAsync().GetAwaiter().GetResult();
		Expect(!verdict.Complete, "so the verdict concludes nothing");
		Expect(verdict.Reason == witness.Start.Reason, "and carries the reason the manifest gave");
		Expect(verdict.Changed == 0, "an unknown verdict does not invent changes");
	}

	// Disposing the witness closes its window: a lookup that gave up must not keep counting events for
	// the scans that come after it.
	private static void AClosedWitnessCountsNothing(string project)
	{
		var assets = NewTree(project, "closed");
		var script = Path.Combine(assets, "Thing.cs");
		using var holder = new ValidationInputMonitor(new[] { assets }, Array.Empty<string>());

		var witness = Open(assets, Array.Empty<string>(), null);
		File.WriteAllText(script, "changed once");
		Expect(WaitUntil(() => witness.Monitor.EventCount > 0, 5000), "an open witness counts changes");
		var counted = witness.Monitor.EventCount;
		witness.Dispose();

		var holderBefore = holder.EventCount;
		File.WriteAllText(script, "changed twice");
		Expect(WaitUntil(() => holder.EventCount > holderBefore, 5000), "the shared watcher still serves the open window");
		Thread.Sleep(200);
		Expect(witness.Monitor.EventCount == counted, "a closed witness counts nothing more");
	}

	// The memo never answers for the digest — it answers for the miss, and only while the two cheap
	// facts the miss was decided under still hold.
	private static void AMissIsRememberedUntilSomethingChanges()
	{
		const string task = "Task_A";
		var memo = new CacheMissMemo();

		Expect(!memo.ShouldSkip(task, "sources", "e1,e2", 1000), "a task nobody missed for is always asked");
		memo.Record(task, "sources", "e1,e2", 1000);
		Expect(memo.ShouldSkip(task, "sources", "e1,e2", 1000 + CacheMissMemo.RetryMs - 1),
			"the same sources and the same candidates would miss again");
		Expect(!memo.ShouldSkip(task, "edited", "e1,e2", 1000 + 1),
			"but an edit to the sources is asked immediately");

		memo.Record(task, "sources", "e1,e2", 2000);
		Expect(!memo.ShouldSkip(task, "sources", "e1,e2,e3", 2001),
			"and so is a candidate set that grew");

		memo.Record(task, "sources", "e1,e2", 3000);
		Expect(memo.ShouldSkip(task, "sources", "e1,e2", 3001), "the miss holds inside the backstop");
		Expect(!memo.ShouldSkip(task, "sources", "e1,e2", 3000 + CacheMissMemo.RetryMs),
			"an input the fingerprint does not track is what the backstop is for");
		Expect(!memo.ShouldSkip(task, "sources", "e1,e2", 3001), "and a lapsed miss is forgotten, not renewed");

		memo.Record(task, "sources", "e1,e2", 4000);
		memo.Forget(task);
		Expect(!memo.ShouldSkip(task, "sources", "e1,e2", 4001), "a served task starts with a clean slate");

		memo.Record("Task_A", "sources", "e1", 5000);
		memo.Record("Task_B", "sources", "e1", 5000);
		memo.Retain(new HashSet<string> { "Task_B" });
		Expect(!memo.ShouldSkip("Task_A", "sources", "e1", 5001), "a task that left the queue leaves its miss behind");
		Expect(memo.ShouldSkip("Task_B", "sources", "e1", 5001), "and the task still waiting keeps its own");
	}

	private static CacheLookupWitness Open(string assets, string[] excluded, Func<string, bool> ignore)
	{
		var witness = CacheLookupWitness.OpenAsync(new[] { assets }, excluded, ignore).GetAwaiter().GetResult();
		Expect(witness.Monitor.Observed && witness.Start.Complete, "a readable tree opens both witnesses");
		return witness;
	}

	private static string NewTree(string project, string name)
	{
		var assets = Path.Combine(project, "lookup", name, "Assets");
		Directory.CreateDirectory(Path.Combine(assets, "Scripts"));
		File.WriteAllText(Path.Combine(assets, "Thing.cs"), "class Thing {}");
		File.WriteAllText(Path.Combine(assets, "Thing.cs.meta"), "guid: 2222");
		File.WriteAllText(Path.Combine(assets, "Scripts", "Player.cs"), "class Player {}");
		return assets;
	}
}
