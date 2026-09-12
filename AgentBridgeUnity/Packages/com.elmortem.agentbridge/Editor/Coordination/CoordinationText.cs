using System;

namespace AgentBridge.Coordination
{
	// JsonUtility writes an absent string as "" while System.Text.Json writes null. Keeping the
	// in-memory state free of nulls is what makes the two codecs round-trip to the same bytes.
	public static class CoordinationText
	{
		public static readonly string[] Empty = new string[0];

		public static string Safe(string value)
		{
			return value ?? "";
		}

		public static string[] Safe(string[] values)
		{
			if (values == null)
			{
				return new string[0];
			}

			for (int i = 0; i < values.Length; i++)
			{
				if (values[i] == null)
				{
					values[i] = "";
				}
			}

			return values;
		}

		public static bool Contains(string[] values, string value)
		{
			if (values == null)
			{
				return false;
			}

			foreach (string item in values)
			{
				if (string.Equals(item, value, StringComparison.Ordinal))
				{
					return true;
				}
			}

			return false;
		}

		public static string[] Add(string[] values, string value)
		{
			if (Contains(values, value))
			{
				return values;
			}

			string[] source = values ?? new string[0];
			var result = new string[source.Length + 1];
			Array.Copy(source, result, source.Length);
			result[source.Length] = value;
			return result;
		}

		public static string[] Remove(string[] values, string value)
		{
			if (!Contains(values, value))
			{
				return values ?? new string[0];
			}

			var result = new string[values.Length - 1];
			int index = 0;
			foreach (string item in values)
			{
				if (string.Equals(item, value, StringComparison.Ordinal))
				{
					continue;
				}

				result[index++] = item;
			}

			return result;
		}
	}
}
