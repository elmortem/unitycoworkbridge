using System;
using System.Reflection;
using UnityEditor;

namespace AgentBridge
{
	public static class InteractionModeProbe
	{
		public static string Read()
		{
			PropertyInfo property = typeof(EditorApplication).GetProperty(
				"interactionMode",
				BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic);

			if (property != null)
			{
				try
				{
					object value = property.GetValue(null);
					if (value != null)
					{
						return value.ToString();
					}
				}
				catch
				{
				}
			}

			return "unknown";
		}

		public static bool IsThrottled(string mode)
		{
			return string.Equals(mode, "Default", StringComparison.OrdinalIgnoreCase)
				|| string.Equals(mode, "MonitorRefreshRate", StringComparison.OrdinalIgnoreCase)
				|| string.Equals(mode, "Custom", StringComparison.OrdinalIgnoreCase);
		}
	}
}
