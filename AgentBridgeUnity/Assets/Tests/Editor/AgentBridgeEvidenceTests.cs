using System;
using System.IO;
using AgentBridge;
using NUnit.Framework;
using UnityEditor;
using UnityEngine;

// evidence-v1 against the real project: which roots count as inputs, what moves the digest, and
// what the observer has to be able to say before a result may be called valid.
public class AgentBridgeEvidenceTests
{
	private string _root;

	[SetUp]
	public void SetUp()
	{
		_root = Path.Combine(Path.GetTempPath(), "AgentBridgeEvidence_" + Guid.NewGuid().ToString("N"));
		Directory.CreateDirectory(_root);
	}

	[TearDown]
	public void TearDown()
	{
		try
		{
			if (Directory.Exists(_root))
			{
				Directory.Delete(_root, true);
			}
		}
		catch (IOException)
		{
		}
	}

	[Test]
	public void InputRootsCoverTheImportedTreeAndExcludeEditorOutput()
	{
		string projectRoot = BridgePaths.ProjectRoot.Replace('\\', '/');
		string[] roots = ValidationEvidence.CollectRoots();
		string[] excluded = ValidationEvidence.CollectExcludedRoots();

		Assert.IsTrue(Contains(roots, projectRoot + "/Assets"), "Assets is an input root");
		Assert.IsTrue(Contains(roots, projectRoot + "/Packages"), "Packages is an input root");
		Assert.IsTrue(Contains(roots, projectRoot + "/ProjectSettings"), "ProjectSettings is an input root");

		Assert.IsTrue(Contains(excluded, projectRoot + "/Library"), "Library is editor output");
		Assert.IsTrue(Contains(excluded, projectRoot + "/Temp"), "Temp is editor output");
		Assert.IsTrue(Contains(excluded, projectRoot + "/Logs"), "Logs is editor output");

		// Never exclude whole trees the acceptance depends on.
		Assert.IsFalse(Contains(excluded, projectRoot + "/Assets/Tests"), "the tests themselves are inputs");
		Assert.IsFalse(Contains(excluded, projectRoot + "/ProjectSettings"), "project settings are inputs");
	}

	[Test]
	public void ContextCarriesUnityPackageAndPlatform()
	{
		string context = ValidationEvidence.ContextOf("EditMode", "AgentBridgeEvidenceTests");
		StringAssert.Contains("unity=" + Application.unityVersion, context);
		StringAssert.Contains("platform=" + EditorUserBuildSettings.activeBuildTarget, context);
		StringAssert.Contains("mode=EditMode", context);
		Assert.AreEqual(context, ValidationEvidence.ContextOf("EditMode", "OtherSubset"),
			"selection is checked by coverage; subsets share the same input digest");
		Assert.AreNotEqual(
			context,
			ValidationEvidence.ContextOf("PlayMode", "AgentBridgeEvidenceTests"),
			"the mode is part of what the evidence claims");
	}

	[Test]
	public void DigestFollowsContentNotTimestamps()
	{
		string assets = Path.Combine(_root, "Assets");
		string temp = Path.Combine(_root, "Temp");
		Directory.CreateDirectory(assets);
		Directory.CreateDirectory(temp);

		string asset = Path.Combine(assets, "Thing.asset");
		File.WriteAllText(asset, "value: 1");
		File.WriteAllText(asset + ".meta", "guid: aaaa");

		string[] roots = { assets };
		string[] excluded = { temp };
		ValidationInputSnapshot before = ValidationInputSnapshot.Capture(roots, excluded, "ctx");
		Assert.IsTrue(before.Complete);
		Assert.AreEqual(2, before.FileCount, ".meta files are inputs");

		DateTime stamp = File.GetLastWriteTimeUtc(asset);
		File.WriteAllText(asset, "value: 2");
		File.SetLastWriteTimeUtc(asset, stamp);
		Assert.AreNotEqual(
			before.Digest,
			ValidationInputSnapshot.Capture(roots, excluded, "ctx").Digest,
			"an .asset edited back to the same size and time must still move the digest");

		ValidationInputSnapshot current = ValidationInputSnapshot.Capture(roots, excluded, "ctx");
		File.WriteAllText(asset + ".meta", "guid: bbbb");
		Assert.AreNotEqual(
			current.Digest,
			ValidationInputSnapshot.Capture(roots, excluded, "ctx").Digest,
			"a .meta change must move the digest");

		current = ValidationInputSnapshot.Capture(roots, excluded, "ctx");
		File.WriteAllText(Path.Combine(temp, "AgentBridge.log"), "editor output");
		Assert.AreEqual(
			current.Digest,
			ValidationInputSnapshot.Capture(roots, excluded, "ctx").Digest,
			"an ordinary Temp artifact must not invalidate the evidence");

		ValidationInputSnapshot unreachable = ValidationInputSnapshot.Capture(
			new[] { Path.Combine(_root, "not-here") }, excluded, "ctx");
		Assert.IsFalse(unreachable.Complete, "an unreachable root is unknown, not a partial hash");
	}

	[Test]
	public void ObserverSeesAChangeThatWasReverted()
	{
		string assets = Path.Combine(_root, "Assets");
		Directory.CreateDirectory(assets);
		string script = Path.Combine(assets, "Thing.cs");
		File.WriteAllText(script, "original");

		string[] roots = { assets };
		ValidationInputSnapshot before = ValidationInputSnapshot.Capture(roots, new string[0], "ctx");

		using (var monitor = new ValidationInputMonitor(roots, new string[0]))
		{
			Assert.IsTrue(monitor.Observed, "a real directory can be observed");

			File.WriteAllText(script, "changed");
			Assert.IsTrue(WaitUntil(() => monitor.EventCount > 0, 5000), "the observer must see the change");
			File.WriteAllText(script, "original");

			ValidationInputSnapshot after = ValidationInputSnapshot.Capture(roots, new string[0], "ctx");
			Assert.AreEqual(before.Digest, after.Digest, "the revert makes both digests equal");
			Assert.Greater(monitor.EventCount, 0, "and only the observer can turn that into a stale verdict");
		}
	}

	[Test]
	public void StatManifestSeesAChangeThatWasReverted()
	{
		// Under Mono the observer polls, so it can miss both the seconds before a domain reload and
		// the gap across it. The manifest has no thread to lose: it is taken here on the editor's
		// own runtime, over a tree inside the project's Temp so the run's inputs stay untouched.
		string assets = Path.Combine(BridgePaths.ProjectRoot, "Temp", "AgentBridgeStatManifest_" + Guid.NewGuid().ToString("N"));
		Directory.CreateDirectory(assets);
		try
		{
			string script = Path.Combine(assets, "Thing.cs");
			File.WriteAllText(script, "class Thing {}");

			string[] roots = { assets };
			InputStatManifest before = InputStatManifest.Capture(roots, new string[0], null);
			Assert.IsTrue(before.Complete, "a readable tree produces a complete manifest");

			System.Threading.Thread.Sleep(50);
			File.WriteAllText(script, "class Thing {}");

			ValidationInputSnapshot digestBefore = ValidationInputSnapshot.Capture(roots, new string[0], "ctx");
			InputStatManifest after = InputStatManifest.Capture(roots, new string[0], null);
			Assert.IsTrue(after.Complete);

			InputStatVerdict verdict = InputStatManifest.Compare(before, after);
			Assert.IsTrue(verdict.Complete, "the comparison is a complete witness");
			Assert.AreEqual(1, verdict.Changed, "rewriting the same bytes is a change only the manifest can see");
			Assert.AreEqual(ValidationInputSnapshot.Normalize(script), verdict.Paths[0], "and it names the path");

			Assert.AreEqual(
				digestBefore.Digest,
				ValidationInputSnapshot.Capture(roots, new string[0], "ctx").Digest,
				"while the digest sees the same project it saw before");
		}
		finally
		{
			try
			{
				Directory.Delete(assets, true);
			}
			catch (IOException)
			{
			}
		}
	}

	[Test]
	public void TheBridgesOwnTemporaryPlayModeSceneIsNotAForeignChange()
	{
		// The Unity Test Framework creates Assets/InitTestScene*.unity for a PlayMode run and the
		// bridge deletes it afterwards. Blaming that create-then-delete on somebody else made
		// every PlayMode validation randomly stale.
		Assert.IsTrue(
			SceneSafetyGuard.IsTestScenePath("Assets/InitTestScene.unity", null),
			"the test framework's bootstrap scene is the bridge's own scratch");
		Assert.IsTrue(
			SceneSafetyGuard.IsTestScenePath("Assets/InitTestScene2.unity", null),
			"so are its numbered variants");
		Assert.IsTrue(
			SceneSafetyGuard.IsTestScenePath("Assets/Recorded.unity", "Assets/Recorded.unity"),
			"and the recorded bootstrap scene of the run in flight");

		Assert.IsFalse(
			SceneSafetyGuard.IsTestScenePath("Assets/InitTestSceneHelper.cs", null),
			"a script that merely shares the prefix is an ordinary input");
		Assert.IsFalse(
			SceneSafetyGuard.IsTestScenePath("Assets/Scenes/InitTestScene.unity", null),
			"only the scene directly under Assets is the framework's scratch");
		Assert.IsFalse(
			SceneSafetyGuard.IsTestScenePath("Assets/Game/Main.unity", null),
			"a product scene is never ignored");

		string projectRoot = BridgePaths.ProjectRoot.Replace('\\', '/');
		System.Func<string, bool> ignore = ValidationEvidence.BuildIgnore("");
		Assert.IsTrue(ignore(projectRoot + "/Assets/InitTestScene.unity"), "the predicate takes absolute paths");
		Assert.IsTrue(ignore(projectRoot + "/Assets/InitTestScene.unity.meta"), "and the .meta beside it");
		Assert.IsFalse(ignore(projectRoot + "/Assets/Game/Main.unity"), "and leaves product assets alone");
		Assert.IsFalse(ignore("D:/elsewhere/Assets/InitTestScene.unity"), "a path outside this project is not ours to ignore");
	}

	[Test]
	public void EvidenceRecordDefaultsToUnknown()
	{
		var record = new EvidenceRecord();
		Assert.AreEqual(EvidenceRecord.Unknown, record.Validity, "a record with no verdict is unknown");
		Assert.AreEqual(EvidenceRecord.Unknown, EvidenceRecord.UnknownBecause("no snapshot").Validity);
		Assert.AreEqual("no snapshot", EvidenceRecord.UnknownBecause("no snapshot").Reason);
	}

	[Test]
	public void EvidenceIsAdvisoryOutsideAValidationWindow()
	{
		// No coordination window is open while this fixture runs, so an unknown snapshot must not
		// turn a green run red: only a coordinated validation window makes evidence the acceptance.
		Assert.IsFalse(EvidenceClassification.RequiresEvidence(), "no validation window is open here");
		Assert.AreEqual("", EvidenceClassification.WindowId(), "and there is no window id to report");
	}

	private static bool Contains(string[] values, string expected)
	{
		foreach (string value in values)
		{
			if (string.Equals(value.Replace('\\', '/'), expected, StringComparison.OrdinalIgnoreCase))
			{
				return true;
			}
		}

		return false;
	}

	private static bool WaitUntil(Func<bool> condition, int timeoutMs)
	{
		DateTime deadline = DateTime.UtcNow.AddMilliseconds(timeoutMs);
		while (DateTime.UtcNow < deadline)
		{
			if (condition())
			{
				return true;
			}

			System.Threading.Thread.Sleep(20);
		}

		return condition();
	}
}
