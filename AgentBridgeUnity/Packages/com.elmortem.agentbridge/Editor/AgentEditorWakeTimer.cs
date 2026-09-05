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
		private static BackgroundTickTimer _backgroundTimer;
		private static Action _signalTick;
		private static bool _signalFailed;

		public static bool Installed { get; private set; }

		public static string Kind { get; private set; }
		public static long SignalCount { get { return _backgroundTimer != null ? _backgroundTimer.SignalCount : 0; } }

		internal static void ConfigureSignal(Action signalTick)
		{
			Stop();
			_signalTick = signalTick;
			_signalFailed = false;
		}

		[DllImport("user32.dll", SetLastError = true)]
		private static extern UIntPtr SetTimer(IntPtr hWnd, UIntPtr nIDEvent, uint uElapse, IntPtr lpTimerFunc);

		[DllImport("user32.dll", SetLastError = true)]
		private static extern bool KillTimer(IntPtr hWnd, UIntPtr uIDEvent);

		public static void Ensure(int intervalMs, double nowSeconds)
		{
			if (_backgroundTimer != null && _backgroundTimer.Error != null)
			{
				Debug.LogWarning("[AgentBridge] Background SignalTick failed: " + _backgroundTimer.Error);
				Stop();
				_signalFailed = true;
			}

			bool useSignal = _signalTick != null && !_signalFailed;
			if (!useSignal && Application.platform != RuntimePlatform.WindowsEditor)
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
			if (useSignal)
			{
				_backgroundTimer = new BackgroundTickTimer(_signalTick, clamped);
				_intervalMs = clamped;
				Installed = true;
				Kind = "background_signal";
				return;
			}

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
			_nextAttemptTime = 0d;
			if (!Installed)
			{
				return;
			}

			if (_backgroundTimer != null)
			{
				_backgroundTimer.Dispose();
				_backgroundTimer = null;
			}
			if (_timerId != UIntPtr.Zero) KillTimer(IntPtr.Zero, _timerId);
			_timerId = UIntPtr.Zero;
			_intervalMs = 0;
			Installed = false;
			Kind = "none";
		}
	}
}
