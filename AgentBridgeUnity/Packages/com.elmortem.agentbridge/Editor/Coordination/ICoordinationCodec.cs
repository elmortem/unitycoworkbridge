namespace AgentBridge.Coordination
{
	// JSON belongs to the host: Unity has JsonUtility, the CLI has System.Text.Json, and the
	// shared folder must not depend on either. Both adapters must produce the same bytes.
	public interface ICoordinationCodec
	{
		string Serialize(CoordinationState state);
		CoordinationState Deserialize(string json);
	}
}
