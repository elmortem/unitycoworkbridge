using System.Text.Json;
using System.Text.Json.Serialization;
using AgentBridge.Coordination;

namespace AgentBridge.Cli;

// The client half of the codec. System.Text.Json here, JsonUtility in the package; the shared
// engine never sees either. Both must preserve the same data, which is what the round-trip test
// in AgentBridgeCoordination.Tests holds them to.
internal sealed class CoordinationJsonCodec : ICoordinationCodec
{
	public static readonly CoordinationJsonCodec Instance = new();

	// Unknown additive fields are read and dropped rather than refused: a newer editor must not
	// make an older client unable to read the state at all.
	internal static readonly JsonSerializerOptions State = new()
	{
		IncludeFields = true,
		WriteIndented = true,
		DefaultIgnoreCondition = JsonIgnoreCondition.Never
	};

	// Command input the agent wrote by hand is strict: a misspelled field is a usage error, not a
	// silently empty scope.
	internal static readonly JsonSerializerOptions Strict = new()
	{
		IncludeFields = true,
		UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow
	};

	public string Serialize(CoordinationState state)
	{
		state.Normalize();
		return JsonSerializer.Serialize(state, State);
	}

	public CoordinationState Deserialize(string json)
	{
		var state = JsonSerializer.Deserialize<CoordinationState>(json, State);
		state?.Normalize();
		return state!;
	}
}
