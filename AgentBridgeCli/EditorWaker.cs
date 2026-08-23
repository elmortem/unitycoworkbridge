using System.Diagnostics;
using System.Runtime.InteropServices;

namespace AgentBridge.Cli;

internal static class EditorWaker
{
	private const uint WmNull = 0x0000;
	private const int FocusHoldMs = 250;

	[DllImport("user32.dll", SetLastError = true)]
	private static extern bool PostMessage(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);

	[DllImport("user32.dll")]
	private static extern IntPtr GetForegroundWindow();

	[DllImport("user32.dll")]
	private static extern bool SetForegroundWindow(IntPtr hWnd);

	[DllImport("user32.dll", SetLastError = true)]
	private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint processId);

	public static bool IsEditorForeground(int editorPid)
	{
		if (!OperatingSystem.IsWindows())
		{
			return false;
		}

		var foreground = GetForegroundWindow();
		if (foreground == IntPtr.Zero)
		{
			return false;
		}

		GetWindowThreadProcessId(foreground, out var processId);
		return processId == (uint)editorPid;
	}

	public static bool TryPost(int editorPid)
	{
		if (!OperatingSystem.IsWindows())
		{
			return false;
		}

		var window = ResolveMainWindow(editorPid);
		if (window == IntPtr.Zero)
		{
			return false;
		}

		return PostMessage(window, WmNull, IntPtr.Zero, IntPtr.Zero);
	}

	public static bool TryFocus(int editorPid)
	{
		if (!OperatingSystem.IsWindows())
		{
			return false;
		}

		var window = ResolveMainWindow(editorPid);
		if (window == IntPtr.Zero)
		{
			return false;
		}

		var previous = GetForegroundWindow();
		SetForegroundWindow(window);
		Thread.Sleep(FocusHoldMs);

		if (previous != IntPtr.Zero && previous != window)
		{
			SetForegroundWindow(previous);
		}

		return true;
	}

	private static IntPtr ResolveMainWindow(int editorPid)
	{
		try
		{
			using var process = Process.GetProcessById(editorPid);
			return process.MainWindowHandle;
		}
		catch
		{
			return IntPtr.Zero;
		}
	}
}
