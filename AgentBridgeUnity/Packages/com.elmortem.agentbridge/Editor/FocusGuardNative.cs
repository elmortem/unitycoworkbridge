using System;
#if UNITY_EDITOR_WIN
using System.Runtime.InteropServices;
#endif

namespace AgentBridge
{
	internal static class FocusGuardNative
	{
#if UNITY_EDITOR_WIN
		[DllImport("user32.dll")]
		private static extern IntPtr GetForegroundWindow();

		[DllImport("user32.dll")]
		private static extern bool SetForegroundWindow(IntPtr hWnd);

		[DllImport("user32.dll")]
		private static extern bool IsWindow(IntPtr hWnd);

		[DllImport("user32.dll")]
		private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint processId);

		public static long GetForegroundWindowHandle()
		{
			return GetForegroundWindow().ToInt64();
		}

		public static bool IsWindowAlive(long handle)
		{
			return handle != 0 && IsWindow(new IntPtr(handle));
		}

		public static bool TrySetForegroundWindow(long handle)
		{
			return SetForegroundWindow(new IntPtr(handle));
		}

		public static bool BelongsToCurrentProcess(long handle)
		{
			if (handle == 0)
			{
				return false;
			}

			uint pid;
			GetWindowThreadProcessId(new IntPtr(handle), out pid);
			return pid == (uint)System.Diagnostics.Process.GetCurrentProcess().Id;
		}
#else
		public static long GetForegroundWindowHandle()
		{
			return 0;
		}

		public static bool IsWindowAlive(long handle)
		{
			return false;
		}

		public static bool TrySetForegroundWindow(long handle)
		{
			return false;
		}

		public static bool BelongsToCurrentProcess(long handle)
		{
			return false;
		}
#endif
	}
}
