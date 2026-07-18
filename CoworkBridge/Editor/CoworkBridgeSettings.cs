using System;

namespace CoworkBridge
{
	[Serializable]
	public class CoworkBridgeSettings
	{
		public bool Enabled;
		public int KeepCompletedCount = 10;
		public int AsyncTimeoutSeconds = 300;
	}
}
