using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using AgentBridge;
using NUnit.Framework;
using UnityEditor;
using UnityEngine;

// Live probes for dialog-detection-v1, driven by scripts/verify-modal-dialog.ps1 from outside the
// editor. Each test deliberately holds the main thread and releases it by itself, so a person never
// has to answer anything; they are Explicit and never run with the rest of the suite.
public class ModalDialogProbeTests
{
	public const string DialogTitle = "AgentBridge modal probe";
	public const string ProgressTitle = "AgentBridge progress probe";
	private const int DialogHoldSeconds = 20;
	private const int ProgressHoldSeconds = 10;

	private static volatile bool _closedByProbe;

	[Test, Explicit("Holds the editor in a modal dialog for 20 s; run through scripts/verify-modal-dialog.ps1")]
	public void NativeDialogIsReportedWhileOpen()
	{
		if (Application.platform != RuntimePlatform.WindowsEditor)
		{
			Assert.Ignore("The self-closing dialog uses Win32.");
		}

		string file = BridgePaths.DialogsFile;
		_closedByProbe = false;
		var closer = new Thread(() => CloseAfter(DialogTitle, DialogHoldSeconds)) { IsBackground = true };
		closer.Start();

		bool answered = EditorUtility.DisplayDialog(
			DialogTitle,
			"Agent Bridge verification: this dialog closes itself in " + DialogHoldSeconds + " seconds.",
			"OK",
			"Cancel");

		closer.Join(5000);
		Debug.Log("[AgentBridge] modal probe answered: " + answered);
		Assert.IsTrue(_closedByProbe, "the dialog must have been closed by the probe, not by a person");

		// Still inside the test, so the editor does not tick: the monitor's own scan must notice that
		// the dialog is gone.
		string title = null;
		DateTime deadline = DateTime.UtcNow.AddSeconds(5);
		do
		{
			title = OpenDialogTitle(file);
			if (title == null)
			{
				break;
			}

			Thread.Sleep(100);
		}
		while (DateTime.UtcNow < deadline);

		Assert.IsNull(title, "dialogs.json must be cleared once the dialog closes");
	}

	[Test, Explicit("Keeps a progress bar on a busy main thread for 10 s; run through scripts/verify-modal-dialog.ps1")]
	public void ProgressBarIsNotReportedAsDialog()
	{
		string file = BridgePaths.DialogsFile;
		string reported = null;
		bool stop = false;
		var watcher = new Thread(() =>
		{
			while (!Volatile.Read(ref stop))
			{
				string title = OpenDialogTitle(file);
				if (title != null && reported == null)
				{
					reported = title;
				}

				Thread.Sleep(200);
			}
		}) { IsBackground = true };
		watcher.Start();

		try
		{
			DateTime started = DateTime.UtcNow;
			DateTime until = started.AddSeconds(ProgressHoldSeconds);
			while (DateTime.UtcNow < until)
			{
				float progress = (float)((DateTime.UtcNow - started).TotalSeconds / ProgressHoldSeconds);
				EditorUtility.DisplayProgressBar(ProgressTitle, "Agent Bridge verification: busy main thread", progress);
				Thread.Sleep(50);
			}
		}
		finally
		{
			EditorUtility.ClearProgressBar();
			Volatile.Write(ref stop, true);
			watcher.Join(2000);
		}

		Assert.IsNull(reported, "a progress bar on a busy main thread is not a modal dialog: " + reported);
	}

	// Title of the first dialog in dialogs.json, null when nothing is published. Read by hand: the
	// watcher runs off the main thread.
	private static string OpenDialogTitle(string file)
	{
		string json;
		try
		{
			using (var stream = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
			using (var reader = new StreamReader(stream, Encoding.UTF8))
			{
				json = reader.ReadToEnd();
			}
		}
		catch (Exception)
		{
			return null;
		}

		Match match = Regex.Match(json, "\"Title\":\\s*\"((?:[^\"\\\\]|\\\\.)*)\"");
		return match.Success ? match.Groups[1].Value : null;
	}

	private static void CloseAfter(string title, int seconds)
	{
		Thread.Sleep(seconds * 1000);
		bool posted = false;
		for (int attempt = 0; attempt < 80; attempt++)
		{
			List<IntPtr> windows = FindDialogs(title);
			if (windows.Count == 0)
			{
				if (posted)
				{
					_closedByProbe = true;
					return;
				}

				Thread.Sleep(250);
				continue;
			}

			foreach (IntPtr window in windows)
			{
				PostMessage(window, WmClose, IntPtr.Zero, IntPtr.Zero);
				if (attempt >= 4)
				{
					PostMessage(window, WmCommand, new IntPtr(IdCancel), IntPtr.Zero);
				}
			}

			posted = true;
			Thread.Sleep(250);
		}
	}

	// The titled window, or after a few seconds any visible native dialog of this editor: a probe that
	// cannot find its dialog must still never leave the editor waiting for a person.
	private static List<IntPtr> FindDialogs(string title)
	{
		var result = new List<IntPtr>();
		IntPtr titled = FindWindow(null, title);
		if (titled != IntPtr.Zero)
		{
			result.Add(titled);
			return result;
		}

		uint self = (uint)System.Diagnostics.Process.GetCurrentProcess().Id;
		EnumWindows((window, parameter) =>
		{
			uint owner;
			GetWindowThreadProcessId(window, out owner);
			if (owner == self && IsWindowVisible(window))
			{
				var name = new StringBuilder(64);
				GetClassName(window, name, name.Capacity);
				if (name.ToString() == "#32770")
				{
					result.Add(window);
				}
			}

			return true;
		}, IntPtr.Zero);
		return result;
	}

	private const uint WmClose = 0x0010;
	private const uint WmCommand = 0x0111;
	private const int IdCancel = 2;

	private delegate bool EnumWindowsProc(IntPtr window, IntPtr parameter);

	[DllImport("user32.dll", EntryPoint = "FindWindowW", CharSet = CharSet.Unicode, ExactSpelling = true)]
	private static extern IntPtr FindWindow(string className, string windowName);

	[DllImport("user32.dll", EntryPoint = "PostMessageW", ExactSpelling = true)]
	[return: MarshalAs(UnmanagedType.Bool)]
	private static extern bool PostMessage(IntPtr window, uint message, IntPtr wParam, IntPtr lParam);

	[DllImport("user32.dll")]
	[return: MarshalAs(UnmanagedType.Bool)]
	private static extern bool EnumWindows(EnumWindowsProc callback, IntPtr parameter);

	[DllImport("user32.dll")]
	private static extern uint GetWindowThreadProcessId(IntPtr window, out uint processId);

	[DllImport("user32.dll")]
	[return: MarshalAs(UnmanagedType.Bool)]
	private static extern bool IsWindowVisible(IntPtr window);

	[DllImport("user32.dll", EntryPoint = "GetClassNameW", CharSet = CharSet.Unicode, ExactSpelling = true)]
	private static extern int GetClassName(IntPtr window, StringBuilder name, int capacity);
}
