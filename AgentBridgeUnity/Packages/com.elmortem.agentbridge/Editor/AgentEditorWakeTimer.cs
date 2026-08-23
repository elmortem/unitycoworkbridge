using System;
using System.Runtime.InteropServices;
using UnityEngine;

namespace AgentBridge
{
	public static class AgentEditorWakeTimer
	{
		private const int MinimumIntervalMs = 15;
		private const double RetryIntervalSeconds = 1d;

		private static UIntPtr _timerId;
		private static int _intervalMs;
		private static double _nextAttemptTime;

		public static bool Installed { get; private set; }

		public static string Kind { get; private set; }

		[DllImport("user32.dll", SetLastError = true)]
		private static extern UIntPtr SetTimer(IntPtr hWnd, UIntPtr nIDEvent, uint uElapse, IntPtr lpTimerFunc);

		[DllImport("user32.dll", SetLastError = true)]
		private static extern bool KillTimer(IntPtr hWnd, UIntPtr uIDEvent);

		public static void Ensure(int intervalMs, double nowSeconds)
		{
			if (Application.platform != RuntimePlatform.WindowsEditor)
			{
				Kind = "unsupported";
				return;
			}

			int clamped = intervalMs < MinimumIntervalMs ? MinimumIntervalMs : intervalMs;
			if (Installed && _intervalMs == clamped)
			{
				return;
			}

			if (!Installed && nowSeconds < _nextAttemptTime)
			{
				return;
			}

			Stop();
			_nextAttemptTime = nowSeconds + RetryIntervalSeconds;

			UIntPtr id = SetTimer(IntPtr.Zero, UIntPtr.Zero, (uint)clamped, IntPtr.Zero);
			if (id == UIntPtr.Zero)
			{
				Kind = "none";
				return;
			}

			_timerId = id;
			_intervalMs = clamped;
			Installed = true;
			Kind = "thread";
		}

		public static void Stop()
		{
			if (!Installed)
			{
				return;
			}

			KillTimer(IntPtr.Zero, _timerId);
			_timerId = UIntPtr.Zero;
			_intervalMs = 0;
			Installed = false;
			Kind = "none";
		}
	}
}
