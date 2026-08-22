using System;
using System.Globalization;
using System.Reflection;
using UnityEditor;
using UnityEngine;

namespace AgentBridge
{
	[InitializeOnLoad]
	public static class FocusGuard
	{
		private const string HwndKey = "AgentBridge_FocusGuard_Hwnd";
		private const string ActiveUntilKey = "AgentBridge_FocusGuard_ActiveUntilUtc";
		private const string RestoreCountKey = "AgentBridge_FocusGuard_RestoreCount";
		private const string PrevPlayBehaviorKey = "AgentBridge_FocusGuard_PrevPlayBehavior";

		private const int MaxRestores = 2;
		private const double PlayEntryGuardSeconds = 120d;
		private const double WindowGuardSeconds = 5d;

		static FocusGuard()
		{
			EditorApplication.update += Tick;
			EditorApplication.playModeStateChanged += OnPlayModeStateChanged;
		}

		public static void BeginPlayEntryGuard()
		{
			Capture();
			SetPlayUnfocused();
			Arm(PlayEntryGuardSeconds);
		}

		public static void BeginWindowGuard()
		{
			Capture();
			Arm(WindowGuardSeconds);
		}

		private static void Arm(double seconds)
		{
			SessionState.SetString(ActiveUntilKey, DateTime.UtcNow.AddSeconds(seconds).ToString("o"));
			SessionState.SetString(RestoreCountKey, "0");
		}

		private static void Capture()
		{
			long hwnd = FocusGuardNative.GetForegroundWindowHandle();
			if (FocusGuardNative.BelongsToCurrentProcess(hwnd))
			{
				SessionState.SetString(HwndKey, "0");
				return;
			}

			SessionState.SetString(HwndKey, hwnd.ToString(CultureInfo.InvariantCulture));
		}

		private static void SetPlayUnfocused()
		{
			try
			{
				Type viewType = Type.GetType("UnityEditor.PlayModeView,UnityEditor");
				if (viewType == null)
				{
					return;
				}

				PropertyInfo prop = viewType.GetProperty(
					"enterPlayModeBehavior",
					BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
				if (prop == null)
				{
					return;
				}

				object unfocused;
				try
				{
					unfocused = Enum.Parse(prop.PropertyType, "PlayUnfocused");
				}
				catch
				{
					return;
				}

				UnityEngine.Object[] views = Resources.FindObjectsOfTypeAll(viewType);
				if (views.Length == 0)
				{
					return;
				}

				int previous = Convert.ToInt32(prop.GetValue(views[0]));
				SessionState.SetString(PrevPlayBehaviorKey, previous.ToString(CultureInfo.InvariantCulture));

				foreach (UnityEngine.Object view in views)
				{
					prop.SetValue(view, unfocused);
				}
			}
			catch
			{
			}
		}

		private static void RestorePlayBehavior()
		{
			string saved = SessionState.GetString(PrevPlayBehaviorKey, "");
			if (string.IsNullOrEmpty(saved))
			{
				return;
			}

			int previous;
			if (!int.TryParse(saved, NumberStyles.Integer, CultureInfo.InvariantCulture, out previous))
			{
				return;
			}

			try
			{
				Type viewType = Type.GetType("UnityEditor.PlayModeView,UnityEditor");
				if (viewType == null)
				{
					return;
				}

				PropertyInfo prop = viewType.GetProperty(
					"enterPlayModeBehavior",
					BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
				if (prop == null)
				{
					return;
				}

				object behavior = Enum.ToObject(prop.PropertyType, previous);
				foreach (UnityEngine.Object view in Resources.FindObjectsOfTypeAll(viewType))
				{
					prop.SetValue(view, behavior);
				}
			}
			catch
			{
			}
		}

		private static void OnPlayModeStateChanged(PlayModeStateChange state)
		{
			if (state != PlayModeStateChange.EnteredPlayMode)
			{
				return;
			}

			if (string.IsNullOrEmpty(SessionState.GetString(ActiveUntilKey, "")))
			{
				return;
			}

			SessionState.SetString(ActiveUntilKey, DateTime.UtcNow.AddSeconds(WindowGuardSeconds).ToString("o"));
		}

		private static void Tick()
		{
			string activeUntil = SessionState.GetString(ActiveUntilKey, "");
			if (string.IsNullOrEmpty(activeUntil))
			{
				return;
			}

			DateTime activeUntilUtc;
			if (!TryParseUtc(activeUntil, out activeUntilUtc) || DateTime.UtcNow > activeUntilUtc)
			{
				Deactivate();
				return;
			}

			long hwnd = ReadHwnd();
			if (hwnd == 0 || !FocusGuardNative.IsWindowAlive(hwnd))
			{
				return;
			}

			if (!FocusGuardNative.BelongsToCurrentProcess(FocusGuardNative.GetForegroundWindowHandle()))
			{
				return;
			}

			int restoreCount = ReadRestoreCount();
			if (restoreCount >= MaxRestores)
			{
				return;
			}

			FocusGuardNative.TrySetForegroundWindow(hwnd);
			SessionState.SetString(RestoreCountKey, (restoreCount + 1).ToString(CultureInfo.InvariantCulture));
		}

		private static void Deactivate()
		{
			RestorePlayBehavior();

			SessionState.EraseString(HwndKey);
			SessionState.EraseString(ActiveUntilKey);
			SessionState.EraseString(RestoreCountKey);
			SessionState.EraseString(PrevPlayBehaviorKey);
		}

		private static long ReadHwnd()
		{
			long hwnd;
			if (!long.TryParse(SessionState.GetString(HwndKey, "0"), NumberStyles.Integer, CultureInfo.InvariantCulture, out hwnd))
			{
				return 0;
			}

			return hwnd;
		}

		private static int ReadRestoreCount()
		{
			int count;
			if (!int.TryParse(SessionState.GetString(RestoreCountKey, "0"), NumberStyles.Integer, CultureInfo.InvariantCulture, out count))
			{
				return 0;
			}

			return count;
		}

		private static bool TryParseUtc(string value, out DateTime result)
		{
			return DateTime.TryParse(value, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out result);
		}
	}
}
