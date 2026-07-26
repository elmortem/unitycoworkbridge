using System;
using System.Diagnostics;
using System.Runtime.InteropServices;
using UnityEngine;

namespace AgentBridge
{
	public static class AgentEditorWakeTimer
	{
		private const int TimerId = 0xC0B0;
		private const uint IntervalMs = 500;

		private static IntPtr _windowHandle;
		private static bool _installed;

		[DllImport("user32.dll")]
		private static extern UIntPtr SetTimer(IntPtr hWnd, UIntPtr nIdEvent, uint uElapse, IntPtr lpTimerFunc);

		[DllImport("user32.dll")]
		private static extern bool KillTimer(IntPtr hWnd, UIntPtr uIdEvent);

		public static void Start()
		{
			if (Application.platform != RuntimePlatform.WindowsEditor)
			{
				return;
			}

			if (_installed)
			{
				return;
			}

			IntPtr handle = ResolveWindowHandle();
			if (handle == IntPtr.Zero)
			{
				return;
			}

			UIntPtr result = SetTimer(handle, (UIntPtr)TimerId, IntervalMs, IntPtr.Zero);
			_installed = result != UIntPtr.Zero;
		}

		public static void Stop()
		{
			if (!_installed)
			{
				return;
			}

			KillTimer(_windowHandle, (UIntPtr)TimerId);
			_installed = false;
		}

		private static IntPtr ResolveWindowHandle()
		{
			if (_windowHandle != IntPtr.Zero)
			{
				return _windowHandle;
			}

			try
			{
				_windowHandle = Process.GetCurrentProcess().MainWindowHandle;
			}
			catch (Exception)
			{
				_windowHandle = IntPtr.Zero;
			}

			return _windowHandle;
		}
	}
}
