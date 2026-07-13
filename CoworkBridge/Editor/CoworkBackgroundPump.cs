using System;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;
using UnityEngine;

namespace CoworkBridge
{
	public static class CoworkBackgroundPump
	{
		private const int PendingIntervalMs = 200;
		private const int IdleIntervalMs = 500;
		private const uint WmNull = 0x0000;

		private static Thread _thread;
		private static volatile bool _running;
		private static string _coworkPath;
		private static bool _isWindows;
		private static IntPtr _windowHandle;

		[DllImport("user32.dll", CharSet = CharSet.Auto)]
		private static extern bool PostMessage(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);

		public static void Start(string coworkPath)
		{
			_coworkPath = coworkPath;

			if (_running)
			{
				return;
			}

			_isWindows = Application.platform == RuntimePlatform.WindowsEditor;
			if (!_isWindows)
			{
				return;
			}

			_windowHandle = IntPtr.Zero;
			_running = true;
			_thread = new Thread(Loop)
			{
				IsBackground = true,
				Name = "CoworkBridgeBackgroundPump"
			};
			_thread.Start();
		}

		public static void Stop()
		{
			_running = false;
			_thread = null;
		}

		private static void Loop()
		{
			while (_running)
			{
				if (HasPendingWork())
				{
					WakeEditor();
					Thread.Sleep(PendingIntervalMs);
				}
				else
				{
					Thread.Sleep(IdleIntervalMs);
				}
			}
		}

		private static bool HasPendingWork()
		{
			try
			{
				if (string.IsNullOrEmpty(_coworkPath) || !Directory.Exists(_coworkPath))
				{
					return false;
				}

				if (File.Exists(Path.Combine(_coworkPath, "clean.command")))
				{
					return true;
				}

				foreach (string path in Directory.GetFiles(_coworkPath))
				{
					if (!IsTaskFile(path))
					{
						continue;
					}

					string taskId = TaskIdOf(path);
					string donePath = Path.Combine(_coworkPath, "result_" + taskId + ".done");
					if (!File.Exists(donePath))
					{
						return true;
					}
				}

				return false;
			}
			catch (Exception)
			{
				return false;
			}
		}

		private static bool IsTaskFile(string path)
		{
			if (path.EndsWith(".ui.json", StringComparison.OrdinalIgnoreCase))
			{
				return true;
			}

			return path.EndsWith(".cs", StringComparison.OrdinalIgnoreCase);
		}

		private static string TaskIdOf(string filePath)
		{
			string name = Path.GetFileName(filePath);
			if (name.EndsWith(".ui.json", StringComparison.OrdinalIgnoreCase))
			{
				return name.Substring(0, name.Length - ".ui.json".Length);
			}

			return Path.GetFileNameWithoutExtension(name);
		}

		private static void WakeEditor()
		{
			IntPtr handle = ResolveWindowHandle();
			if (handle == IntPtr.Zero)
			{
				return;
			}

			PostMessage(handle, WmNull, IntPtr.Zero, IntPtr.Zero);
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
