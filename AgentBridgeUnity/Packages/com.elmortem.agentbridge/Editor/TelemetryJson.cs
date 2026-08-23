using System;
using System.Globalization;
using System.Text;

namespace AgentBridge
{
	// A telemetry line is built by hand rather than through JsonUtility: the envelope has a
	// fixed field order, mixes types the serializer cannot express, and must stay on one line.
	public static class TelemetryJson
	{
		private const int MaxTextLength = 200;

		public static string Escape(string value)
		{
			if (string.IsNullOrEmpty(value))
			{
				return "";
			}

			string trimmed = value.Length > MaxTextLength ? value.Substring(0, MaxTextLength) : value;
			var builder = new StringBuilder(trimmed.Length + 8);

			foreach (char symbol in trimmed)
			{
				switch (symbol)
				{
					case '"':
						builder.Append("\\\"");
						break;
					case '\\':
						builder.Append("\\\\");
						break;
					case '\n':
						builder.Append("\\n");
						break;
					case '\r':
						builder.Append("\\r");
						break;
					case '\t':
						builder.Append("\\t");
						break;
					default:
						if (symbol < ' ')
						{
							builder.Append("\\u").Append(((int)symbol).ToString("x4", CultureInfo.InvariantCulture));
						}
						else
						{
							builder.Append(symbol);
						}

						break;
				}
			}

			return builder.ToString();
		}

		public static string BuildLine(
			DateTime utc,
			string writer,
			string eventName,
			string agentSessionId,
			string taskId,
			TelemetryField[] fields)
		{
			var builder = new StringBuilder(160);
			builder.Append("{\"T\":").Append(new DateTimeOffset(utc, TimeSpan.Zero).ToUnixTimeMilliseconds());
			builder.Append(",\"W\":\"").Append(writer).Append('"');
			builder.Append(",\"E\":\"").Append(eventName).Append('"');
			builder.Append(",\"S\":\"").Append(Escape(agentSessionId)).Append('"');
			builder.Append(",\"Id\":\"").Append(Escape(taskId)).Append('"');

			if (fields != null)
			{
				foreach (TelemetryField field in fields)
				{
					builder.Append(",\"").Append(field.Name).Append("\":").Append(field.RawValue);
				}
			}

			builder.Append('}');
			return builder.ToString();
		}
	}
}
