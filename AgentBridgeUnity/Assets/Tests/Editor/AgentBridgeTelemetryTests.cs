using AgentBridge;
using NUnit.Framework;

public class AgentBridgeTelemetryTests
{
	[Test]
	public void EscapesControlCharactersAndQuotes()
	{
		Assert.AreEqual("a\\\"b", TelemetryJson.Escape("a\"b"));
		Assert.AreEqual("a\\nb", TelemetryJson.Escape("a\nb"));
		Assert.AreEqual("a\\\\b", TelemetryJson.Escape("a\\b"));
	}

	[Test]
	public void BuildsEnvelopeInFixedOrder()
	{
		string line = TelemetryJson.BuildLine(
			new System.DateTime(2026, 8, 23, 12, 0, 0, System.DateTimeKind.Utc),
			"editor",
			"task_start",
			"s1",
			"t1",
			new[] { TelemetryField.Number("QueueDepth", 3), TelemetryField.Flag("Rotated", true) });

		Assert.AreEqual(
			"{\"T\":1787486400000,\"W\":\"editor\",\"E\":\"task_start\",\"S\":\"s1\",\"Id\":\"t1\",\"QueueDepth\":3,\"Rotated\":true}",
			line);
	}

	[Test]
	public void TruncatesLongText()
	{
		Assert.AreEqual(200, TelemetryJson.Escape(new string('x', 500)).Length);
	}
}
