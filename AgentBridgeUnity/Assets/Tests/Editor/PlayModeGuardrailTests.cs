using System.Threading;
using AgentBridge;
using NUnit.Framework;

public class PlayModeGuardrailTests
{
	private static CompileResult Compile(string className, string body)
	{
		string source = "public static class " + className + "\n{\n\tpublic static void Run()\n\t{\n\t\t" + body + "\n\t}\n}";
		return RoslynCompiler.Compile(source, className + ".cs", className, CancellationToken.None);
	}

	private static void AssertRejected(string className, string body)
	{
		CompileResult result = Compile(className, body);
		Assert.IsTrue(result.GuardrailRejected, className + " must be rejected by the guardrail.");
		Assert.AreEqual("guardrail", result.Diagnostics[0].Code);
	}

	[Test]
	public void Guardrail_RejectsExecuteMenuItem()
	{
		AssertRejected("PlayModeMenuItem", "UnityEditor.EditorApplication.ExecuteMenuItem(\"Edit/Play\");");
	}

	[Test]
	public void Guardrail_RejectsReflectionOntoEnterPlaymode()
	{
		AssertRejected("PlayModeReflection",
			"var method = typeof(UnityEditor.EditorApplication).GetMethod(\"EnterPlaymode\");");
	}

	[Test]
	public void Guardrail_RejectsIsPlayingLiteral()
	{
		AssertRejected("PlayModeIsPlayingLiteral", "string member = \"isPlaying\";");
	}

	[Test]
	public void Guardrail_AllowsReadingIsPlaying()
	{
		CompileResult result = Compile("PlayModeReadIsPlaying", "bool playing = UnityEditor.EditorApplication.isPlaying;");
		Assert.IsFalse(result.GuardrailRejected, "Reading the play mode flag must stay allowed.");
	}

	[Test]
	public void Guardrail_AllowsOrdinaryStringLiteral()
	{
		CompileResult result = Compile("PlayModeOrdinaryLiteral", "string greeting = \"hello\";");
		Assert.IsFalse(result.GuardrailRejected, "An unrelated string literal must not be a violation.");
	}
}
