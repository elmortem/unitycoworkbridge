using AgentBridge;
using static AgentBridge.Coordination.Tests.Harness;

namespace AgentBridge.Coordination.Tests;

// C23 — the stat manifest. The observer sleeps through a domain reload and, being a polling
// watcher, through the seconds before it too. The manifest is the witness that needs no thread:
// path, length and write time of the same inputs the digest hashes, taken before the run, written
// to a file that outlives the reload, compared after. It has to catch exactly what the digest
// cannot — an edit that was put back — and it has to say so when it is missing instead of staying
// quiet.
internal static class StatManifestScenarios
{
	public static void Run(string root, List<string> covered)
	{
		QuietTreeIsUnchanged(root);
		AnEditThatWasRevertedIsSeen(root);
		AddedAndRemovedCountOnce(root);
		ExcludedAndIgnoredDoNotCount(root);
		SurvivesTheRoundTripToDisk(root);
		ALostManifestIsNotAnEmptyOne(root);
		ManyChangesAreCountedAndSampled(root);
		AnUnreachableRootIsUnknown(root);
		covered.Add("C23_stat_manifest");
	}

	// The baseline: nothing touched the inputs, so the two witnesses agree on silence.
	private static void QuietTreeIsUnchanged(string project)
	{
		var assets = NewTree(project, "quiet");
		var before = Capture(assets);
		var after = Capture(assets);

		var verdict = InputStatManifest.Compare(before, after);
		Expect(verdict.Complete, "a manifest of a readable tree is a complete witness");
		Expect(verdict.Changed == 0, "an untouched tree has nothing to report");
		Expect(verdict.Paths.Count == 0, "and names nobody");
	}

	// The reason the manifest exists: the same bytes written again leave the digest equal, and only
	// the write time says the project did not hold still.
	private static void AnEditThatWasRevertedIsSeen(string project)
	{
		var assets = NewTree(project, "reverted");
		var script = Path.Combine(assets, "Thing.cs");
		var text = File.ReadAllText(script);

		var digestBefore = ValidationInputSnapshot.Capture(new[] { assets }, Array.Empty<string>(), "ctx");
		var before = Capture(assets);

		Thread.Sleep(50);
		File.WriteAllText(script, text);

		var after = Capture(assets);
		var digestAfter = ValidationInputSnapshot.Capture(new[] { assets }, Array.Empty<string>(), "ctx");

		Expect(digestBefore.Digest == digestAfter.Digest, "rewriting the same content leaves the digest equal");

		var verdict = InputStatManifest.Compare(before, after);
		Expect(verdict.Complete, "the comparison is a complete witness");
		Expect(verdict.Changed == 1, "and it is the one that noticed the rewrite");
		Expect(verdict.Paths.Count == 1 && verdict.Paths[0] == ValidationInputSnapshot.Normalize(script),
			"a change is reported with the path that changed");
	}

	// Appearing and disappearing are changes like any other, and each is counted once.
	private static void AddedAndRemovedCountOnce(string project)
	{
		var assets = NewTree(project, "moved");
		var before = Capture(assets);

		File.WriteAllText(Path.Combine(assets, "Added.cs"), "class Added {}");
		File.Delete(Path.Combine(assets, "Thing.cs"));

		var verdict = InputStatManifest.Compare(before, Capture(assets));
		Expect(verdict.Complete, "the comparison is a complete witness");
		Expect(verdict.Changed == 2, "an added file and a removed file are two changes");
		Expect(verdict.Paths.Contains(ValidationInputSnapshot.Normalize(Path.Combine(assets, "Added.cs")))
			&& verdict.Paths.Contains(ValidationInputSnapshot.Normalize(Path.Combine(assets, "Thing.cs"))),
			"and both are named");
	}

	// The manifest must walk exactly what the digest walks: editor output and the bridge's own
	// scratch are not inputs, so touching them is not a change.
	private static void ExcludedAndIgnoredDoNotCount(string project)
	{
		var assets = NewTree(project, "excluded");
		var temp = Path.Combine(assets, "Temp");
		Directory.CreateDirectory(temp);
		var artifact = Path.Combine(temp, "artifact.bin");
		File.WriteAllText(artifact, "editor output");
		var scratch = Path.Combine(assets, "InitTestScene.unity");
		File.WriteAllText(scratch, "temporary bootstrap scene");

		var excluded = new[] { temp };
		Func<string, bool> ignore = path =>
			ValidationInputSnapshot.Normalize(path).EndsWith("/InitTestScene.unity", StringComparison.OrdinalIgnoreCase);

		var before = InputStatManifest.Capture(new[] { assets }, excluded, ignore);
		Expect(before.Complete, "a readable tree produces a complete manifest");

		Thread.Sleep(50);
		File.WriteAllText(artifact, "editor output again");
		File.Delete(scratch);

		var verdict = InputStatManifest.Compare(before, InputStatManifest.Capture(new[] { assets }, excluded, ignore));
		Expect(verdict.Complete, "the comparison is a complete witness");
		Expect(verdict.Changed == 0, "excluded output and declared scratch are not inputs");
	}

	// The whole point of the file: the witness taken before the reload is still the same witness
	// after it.
	private static void SurvivesTheRoundTripToDisk(string project)
	{
		var assets = NewTree(project, "roundtrip");
		var file = Path.Combine(project, "stat", "roundtrip.txt");
		Directory.CreateDirectory(Path.GetDirectoryName(file));

		var before = Capture(assets);
		before.Save(file);
		var reloaded = InputStatManifest.Load(file);
		Expect(reloaded.Complete, "a saved manifest loads back complete");
		Expect(reloaded.Entries.Count == before.Entries.Count, "with every entry it had");

		var verdict = InputStatManifest.Compare(reloaded, Capture(assets));
		Expect(verdict.Complete && verdict.Changed == 0, "a manifest that went through a file reports the same silence");

		// Saving twice is what an attempt that starts over does.
		Capture(assets).Save(file);
		Expect(InputStatManifest.Load(file).Complete, "saving over an existing manifest keeps it readable");
	}

	// A manifest that is gone or unreadable must not be mistaken for a manifest that saw nothing.
	private static void ALostManifestIsNotAnEmptyOne(string project)
	{
		var assets = NewTree(project, "lost");
		var missing = Path.Combine(project, "stat", "never-written.txt");

		var lost = InputStatManifest.Load(missing);
		Expect(!lost.Complete, "a manifest that was never written is not a witness");
		Expect(lost.Reason.Length > 0, "and it says why");

		var lostVerdict = InputStatManifest.Compare(lost, Capture(assets));
		Expect(!lostVerdict.Complete, "comparing against a lost manifest cannot conclude anything");
		Expect(lostVerdict.Reason == lost.Reason, "and the verdict carries the reason the manifest gave");
		Expect(lostVerdict.Changed == 0, "an unknown verdict does not invent changes");

		var broken = Path.Combine(project, "stat", "broken.txt");
		Directory.CreateDirectory(Path.GetDirectoryName(broken));
		File.WriteAllLines(broken, new[] { "637000000000000000|12|C:/Project/Assets/Thing.cs", "not a manifest line" });
		var corrupt = InputStatManifest.Load(broken);
		Expect(!corrupt.Complete, "a manifest with a line nobody can parse is not a witness");
		Expect(corrupt.Reason.Length > 0, "and it says why");

		var endVerdict = InputStatManifest.Compare(Capture(assets), corrupt);
		Expect(!endVerdict.Complete && endVerdict.Reason == corrupt.Reason,
			"an unreadable closing manifest is reported the same way");
	}

	// A noisy project must still produce a verdict a human can read: the count is exact, the sample
	// of paths is bounded.
	private static void ManyChangesAreCountedAndSampled(string project)
	{
		var assets = Path.Combine(project, "many", "Assets");
		Directory.CreateDirectory(assets);
		for (var index = 0; index < 40; index++)
		{
			File.WriteAllText(Path.Combine(assets, "File" + index.ToString("D2") + ".cs"), "class C" + index + " {}");
		}

		var before = Capture(assets);
		Thread.Sleep(50);
		for (var index = 0; index < 40; index++)
		{
			var path = Path.Combine(assets, "File" + index.ToString("D2") + ".cs");
			File.WriteAllText(path, File.ReadAllText(path));
		}

		var verdict = InputStatManifest.Compare(before, Capture(assets));
		Expect(verdict.Complete, "the comparison is a complete witness");
		Expect(verdict.Changed == 40, "every changed input is counted");
		Expect(verdict.Paths.Count == InputStatManifest.MaxRecordedPaths, "and the reported sample stays bounded");
	}

	// An input root that cannot be walked makes the manifest unknown, exactly like the digest.
	private static void AnUnreachableRootIsUnknown(string project)
	{
		var manifest = InputStatManifest.Capture(new[] { Path.Combine(project, "never-created") },
			Array.Empty<string>(), null);
		Expect(!manifest.Complete, "an unreachable input root makes the manifest unknown");
		Expect(manifest.Reason.Length > 0, "and it says which root");
	}

	private static InputStatManifest Capture(string assets)
	{
		var manifest = InputStatManifest.Capture(new[] { assets }, Array.Empty<string>(), null);
		Expect(manifest.Complete, "a readable tree produces a complete manifest");
		return manifest;
	}

	private static string NewTree(string project, string name)
	{
		var assets = Path.Combine(project, "stat", name, "Assets");
		Directory.CreateDirectory(Path.Combine(assets, "Scripts"));
		File.WriteAllText(Path.Combine(assets, "Thing.cs"), "class Thing {}");
		File.WriteAllText(Path.Combine(assets, "Thing.cs.meta"), "guid: 1111");
		File.WriteAllText(Path.Combine(assets, "Scripts", "Player.cs"), "class Player {}");
		return assets;
	}
}
