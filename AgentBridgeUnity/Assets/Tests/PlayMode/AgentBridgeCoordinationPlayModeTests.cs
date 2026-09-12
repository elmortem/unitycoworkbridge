using System.Collections;
using System.IO;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.TestTools;

// PlayMode is where the bridge reloads its own domain in the middle of a validation. The package
// assembly is editor-only and cannot be referenced from a PlayMode assembly, so this fixture
// asserts the same invariants from the outside: through the files both halves of the bridge share.
//
// What it proves: entering play mode does not lose the coordination state, does not change the
// epoch that every live token is bound to, and does not publish anybody's token into the status
// file that every agent in the project can read.
public class AgentBridgeCoordinationPlayModeTests
{
	private static string WorkingRoot
	{
		get { return Path.Combine(Path.GetDirectoryName(Application.dataPath), "Library", "AgentBridge"); }
	}

	private static string CoordinationState
	{
		get { return Path.Combine(WorkingRoot, "Coordination", "state.json"); }
	}

	[Test]
	public void BridgeStatusIsReadableFromInsidePlayMode()
	{
		string status = Path.Combine(WorkingRoot, "status.json");
		Assert.IsTrue(File.Exists(status), "the editor must keep publishing its status while the game runs");

		string text = File.ReadAllText(status);
		Assert.IsNotEmpty(text, "the status file must never be published empty");
		StringAssert.Contains("\"ProtocolVersion\"", text, "the status file must carry the protocol version");
	}

	[Test]
	public void StatusNeverCarriesSomebodyElsesToken()
	{
		string status = Path.Combine(WorkingRoot, "status.json");
		string text = File.ReadAllText(status);

		// The coordination summary in the status file is deliberately token free: it is world
		// readable to every agent working in this project.
		Assert.IsFalse(text.Contains("\"Token\""), "the shared status must not expose any grant token");
		Assert.IsFalse(text.Contains("CoordinationToken"), "the shared status must not expose any grant token");
	}

	[UnityTest]
	public IEnumerator CoordinationStateSurvivesThePlayModeDomainReload()
	{
		string before = ReadStateOrEmpty();

		// A frame in play mode is on the far side of the domain reload that entering it caused.
		yield return null;
		yield return null;

		string after = ReadStateOrEmpty();

		if (before.Length == 0 && after.Length == 0)
		{
			// No coordinator in this project: the legacy path is what has to keep working, and the
			// absence of the file is itself the assertion.
			Assert.IsFalse(File.Exists(CoordinationState), "an uncoordinated project must stay uncoordinated");
			yield break;
		}

		Assert.IsNotEmpty(after, "entering play mode must not lose the coordination state");
		Assert.AreEqual(
			FieldOf(before, "Epoch"),
			FieldOf(after, "Epoch"),
			"a domain reload the bridge caused is not a restart: the epoch every token is bound to must hold");
		Assert.AreEqual(
			FieldOf(before, "ProjectId"),
			FieldOf(after, "ProjectId"),
			"the state must still describe this project");
	}

	private static string ReadStateOrEmpty()
	{
		try
		{
			return File.Exists(CoordinationState) ? File.ReadAllText(CoordinationState) : "";
		}
		catch (IOException)
		{
			return "";
		}
	}

	private static string FieldOf(string json, string name)
	{
		string key = "\"" + name + "\"";
		int start = json.IndexOf(key);
		if (start < 0)
		{
			return "";
		}

		int colon = json.IndexOf(':', start);
		int open = json.IndexOf('"', colon + 1);
		int close = json.IndexOf('"', open + 1);
		return open < 0 || close < 0 ? "" : json.Substring(open + 1, close - open - 1);
	}
}
