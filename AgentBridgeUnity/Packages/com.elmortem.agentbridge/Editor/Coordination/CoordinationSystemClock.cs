using System;

namespace AgentBridge.Coordination
{
	public sealed class CoordinationSystemClock : ICoordinationClock
	{
		public static readonly CoordinationSystemClock Instance = new CoordinationSystemClock();

		public long UtcNowMs
		{
			get { return new DateTimeOffset(DateTime.UtcNow).ToUnixTimeMilliseconds(); }
		}
	}
}
