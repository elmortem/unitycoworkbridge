using AgentBridge.Coordination;
using System.Text.Json;

// Only the Unity serialization/filesystem/transaction boundary is substituted. The pump and
// state machine are the production sources; tests drive editor ticks without an agent client.
namespace UnityEngine
{
	public static class JsonUtility
	{
		static readonly JsonSerializerOptions Options = new() { IncludeFields = true };
		public static string ToJson<T>(T value) => JsonSerializer.Serialize(value, Options);
		public static T FromJson<T>(string value) => JsonSerializer.Deserialize<T>(value, Options);
	}
}
namespace AgentBridge
{
	public static class BridgePaths
	{
		public static string Root;
		public static string Inbox => Path.Combine(Root, "Inbox");
		public static string Journal => Path.Combine(Root, "Journal");
	}
	public class TaskRecord
	{
		public string Status;
		public string FinishedAtUtc;
	}
	public static class CoordinationEditorAdapter
	{
		public static CoordinationState Snapshot;
		public static long Now = 1700000000000;
		public static bool LoseNextCompletion;
		public static CoordinationCommand NewCommand(string op) => new() { Op = op, Nonce = Guid.NewGuid().ToString("N") };
		public static bool TryApply(CoordinationCommand command, out CoordinationReply reply)
		{
			if (LoseNextCompletion && command.Op == CoordinationEngine.OpStepFinish)
			{
				LoseNextCompletion = false; reply = null; return false;
			}
			reply = new CoordinationEngine().Apply(Snapshot, command, Now);
			return true;
		}
	}
}
