using System.Globalization;

namespace AgentBridge
{
	// One already-serialized field of a telemetry line. The value is carried as raw JSON so
	// the writer never has to guess how to render it.
	public struct TelemetryField
	{
		public string Name;
		public string RawValue;

		public static TelemetryField Text(string name, string value)
		{
			return new TelemetryField
			{
				Name = name,
				RawValue = "\"" + TelemetryJson.Escape(value) + "\""
			};
		}

		public static TelemetryField Number(string name, long value)
		{
			return new TelemetryField
			{
				Name = name,
				RawValue = value.ToString(CultureInfo.InvariantCulture)
			};
		}

		public static TelemetryField Flag(string name, bool value)
		{
			return new TelemetryField
			{
				Name = name,
				RawValue = value ? "true" : "false"
			};
		}
	}
}
