using AgentBridge.Coordination;
using UnityEngine;

namespace AgentBridge
{
	// JsonUtility lives outside the shared folder on purpose: the coordination engine must stay
	// free of UnityEngine so the CLI can compile exactly the same sources.
	public sealed class CoordinationUnityCodec : ICoordinationCodec
	{
		public static readonly CoordinationUnityCodec Instance = new CoordinationUnityCodec();

		public string Serialize(CoordinationState state)
		{
			state.Normalize();
			return JsonUtility.ToJson(state, true);
		}

		public CoordinationState Deserialize(string json)
		{
			CoordinationState state = JsonUtility.FromJson<CoordinationState>(json);
			if (state != null)
			{
				state.Normalize();
			}

			return state;
		}
	}
}
