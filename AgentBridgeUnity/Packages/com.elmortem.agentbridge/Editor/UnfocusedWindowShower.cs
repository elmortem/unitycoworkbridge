using System;
using System.Reflection;
using UnityEditor;
using UnityEngine;

namespace AgentBridge
{
	public static class UnfocusedWindowShower
	{
		private const string FallbackWarning =
			"no-focus show is not available in this Unity version, falling back to a focused window";

		public static bool TryShow(EditorWindow window, Rect position, Action<string> warn)
		{
			window.position = position;

			MethodInfo method = typeof(EditorWindow).GetMethod(
				"ShowPopupWithMode",
				BindingFlags.Instance | BindingFlags.NonPublic);
			Type showModeType = typeof(EditorWindow).Assembly.GetType("UnityEditor.ShowMode");

			if (method == null || showModeType == null)
			{
				return ShowFocused(window, warn);
			}

			object tooltipMode;
			try
			{
				tooltipMode = Enum.Parse(showModeType, "Tooltip");
			}
			catch
			{
				return ShowFocused(window, warn);
			}

			method.Invoke(window, new object[] { tooltipMode, false });
			window.position = new Rect(position.x, position.y, position.width, position.height);
			return true;
		}

		private static bool ShowFocused(EditorWindow window, Action<string> warn)
		{
			if (warn != null)
			{
				warn(FallbackWarning);
			}

			window.Show();
			return false;
		}
	}
}
