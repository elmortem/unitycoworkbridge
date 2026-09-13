using AgentBridge.Coordination;
using AgentBridge.Coordination.Tests;

// Console regression for coordination-v1, evidence-v1 and the test-cache index. No xUnit, exactly
// like AgentBridgeCli.Tests. Every check asserts observable behaviour of the shipped sources.
//
//   --group state   pure state machine, codecs, input digests and observers
//   --group store   cross-process transactions, crash safety and waiting
//   --group all     both (default)
//   --child <role>  internal: the child process half of the store tests

if (args.Length >= 2 && args[0] == "--child")
{
	return Child.Run(args.Skip(1).ToArray());
}

var group = "all";
for (var index = 0; index < args.Length; index++)
{
	if (args[index] == "--group" && index + 1 < args.Length)
	{
		group = args[++index];
	}
}

if (group is not ("all" or "state" or "store"))
{
	Console.Error.WriteLine("usage: --group state|store|all");
	return 3;
}

var covered = new List<string>();
var root = Path.Combine(Path.GetTempPath(), "AgentBridgeCoordTests_" + Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(root);

try
{
	if (group is "all" or "state")
	{
		Scenarios.C01_ScopeReservation(covered);
		Scenarios.C02_WindowWaitsForWriters(covered);
		Scenarios.C03_Idempotency(covered);
		Scenarios.C06_OrphanedWriterAndAbandon(covered);
		Scenarios.C07_DrainingWindow(covered);
		Scenarios.C19_Codecs(covered);
		Scenarios.C20_RunningTaskOutlivesTheClient(covered);
		Scenarios.C12_InputDigest(root, covered);
		Scenarios.C13_ObserverVerdict(root, covered);
		Scenarios.C18_CacheEviction(covered);
		HashingScenarios.Run(root, covered);
		BatchScenarios.Run(root, covered);
	}

	if (group is "all" or "store")
	{
		Scenarios.C04_TwoProcessesOneWindow(root, covered);
		Scenarios.C05_CrashAndCorruption(root, covered);
		Scenarios.C08_Waiting(root, covered);
	}

	Console.WriteLine("scenarios: " + string.Join(", ", covered));
	Console.WriteLine("Coordination: PASS");
	return 0;
}
catch (Exception error)
{
	Console.WriteLine("scenarios: " + string.Join(", ", covered));
	Console.Error.WriteLine("Coordination: FAIL");
	Console.Error.WriteLine(error.Message);
	Console.Error.WriteLine(error.StackTrace);
	return 1;
}
finally
{
	Harness.TryDelete(root);
}
