using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Text;

namespace AgentBridge
{
	// Win32 view of the editor's own modal windows. It runs on a background thread while the main
	// thread may sit in a modal loop or be plain busy, so nothing here may wait on that thread
	// without a bound: GetWindowText would send WM_GETTEXT to it, InternalGetWindowText only reads
	// the stored text. The one message it does send carries a timeout and doubles as the test that
	// tells a modal loop (it pumps messages) from a long import or script (it does not). Only
	// windows of the editor's main thread count: Unity animates its "Hold on" busy window on a
	// thread of its own, and that thread pumps while the main thread is merely busy.
	internal static class NativeDialogProbe
	{
		public const string NativeDialogClass = "#32770";

		// Only the Windows branch of Scan fills these fields; other editors never create one.
#pragma warning disable 0649
		internal sealed class Window
		{
			public long Handle;
			public string ClassName;
			public string Title;
			public string Message;
			public string[] Buttons;
			public bool NativeDialog;
		}
#pragma warning restore 0649

		public static bool Supported
		{
			get
			{
#if UNITY_EDITOR_WIN
				return true;
#else
				return false;
#endif
			}
		}

		// Classes that are owned by a disabled editor window without being modal themselves.
		public static bool IsIgnoredClass(string className)
		{
			if (string.IsNullOrEmpty(className))
			{
				return true;
			}

			return className == "tooltips_class32"
				|| className == "UnityTooltipWndClass"
				|| className == "#32768"
				|| className == "IME"
				|| className == "MSCTFIME UI"
				|| className == "SysShadow"
				|| className.IndexOf("progress", StringComparison.OrdinalIgnoreCase) >= 0;
		}

		// "&Save" → "Save", "R&&D" → "R&D": button labels carry Win32 accelerator markers.
		public static string StripAccelerators(string label)
		{
			if (string.IsNullOrEmpty(label) || label.IndexOf('&') < 0)
			{
				return label ?? "";
			}

			var builder = new StringBuilder(label.Length);
			for (int index = 0; index < label.Length; index++)
			{
				char symbol = label[index];
				if (symbol == '&')
				{
					if (index + 1 < label.Length && label[index + 1] == '&')
					{
						builder.Append('&');
						index++;
					}

					continue;
				}

				builder.Append(symbol);
			}

			return builder.ToString();
		}

		public static uint CurrentThreadId()
		{
#if UNITY_EDITOR_WIN
			return GetCurrentThreadId();
#else
			return 0;
#endif
		}

		// cancelled is polled between windows: shutdown before a domain reload must not wait for a
		// scan that still has several 200 ms probes ahead of it.
		public static List<Window> Scan(int processId, uint mainThreadId, int pumpTimeoutMs, Func<bool> cancelled)
		{
			var result = new List<Window>();
#if UNITY_EDITOR_WIN
			var windows = new List<IntPtr>();
			GCHandle handle = GCHandle.Alloc(windows);
			try
			{
				EnumWindows(CollectCallback, GCHandle.ToIntPtr(handle));
			}
			finally
			{
				handle.Free();
			}

			foreach (IntPtr window in windows)
			{
				if (cancelled != null && cancelled())
				{
					return new List<Window>();
				}

				if (!IsWindowVisible(window) || !IsWindowEnabled(window) || !BelongsTo(window, processId))
				{
					continue;
				}

				if (mainThreadId != 0 && ThreadOf(window) != mainThreadId)
				{
					continue;
				}

				string className = ClassOf(window);
				if (IsIgnoredClass(className))
				{
					continue;
				}

				// A modal window disables its owner; a native dialog whose owner is still enabled is
				// modeless. Only an ownerless native dialog (a task-modal message box) passes without it.
				bool nativeDialog = className == NativeDialogClass;
				IntPtr owner = GetWindow(window, GwOwner);
				bool ownerBlocked = owner != IntPtr.Zero && BelongsTo(owner, processId) && !IsWindowEnabled(owner);
				if (!ownerBlocked && !(nativeDialog && owner == IntPtr.Zero))
				{
					continue;
				}

				string title = TextOf(window, 512);
				if (!nativeDialog && title.Length == 0)
				{
					continue;
				}

				// A message box disables only its owner: floating editor windows owned by the same
				// window stay enabled and pump inside the modal loop. Of those, the modal one is the
				// popup the owner activated last.
				if (!nativeDialog && GetLastActivePopup(owner) != window)
				{
					continue;
				}

				var children = new List<IntPtr>();
				GCHandle childHandle = GCHandle.Alloc(children);
				try
				{
					EnumChildWindows(window, CollectCallback, GCHandle.ToIntPtr(childHandle));
				}
				finally
				{
					childHandle.Free();
				}

				bool progress = false;
				var statics = new List<string>();
				var buttons = new List<string>();
				foreach (IntPtr child in children)
				{
					string childClass = ClassOf(child);
					if (childClass.IndexOf("progress", StringComparison.OrdinalIgnoreCase) >= 0)
					{
						progress = true;
						break;
					}

					if (!IsWindowVisible(child))
					{
						continue;
					}

					if (childClass == "Button")
					{
						string label = StripAccelerators(TextOf(child, 128)).Trim();
						if (label.Length > 0)
						{
							buttons.Add(label);
						}
					}
					else if (childClass == "Static")
					{
						string text = TextOf(child, 2048).Trim();
						if (text.Length > 0)
						{
							statics.Add(text);
						}
					}
				}

				// A progress window belongs to a busy editor, not to a question somebody must answer.
				if (progress)
				{
					continue;
				}

				if ((cancelled != null && cancelled()) || !Pumps(window, pumpTimeoutMs))
				{
					continue;
				}

				result.Add(new Window
				{
					Handle = window.ToInt64(),
					ClassName = className,
					Title = title,
					Message = string.Join("\n", statics.ToArray()),
					Buttons = buttons.ToArray(),
					NativeDialog = nativeDialog
				});
			}

			// While a native dialog is up, any other candidate is an editor window behind it.
			if (result.Exists(found => found.NativeDialog))
			{
				result.RemoveAll(found => !found.NativeDialog);
			}
#endif
			return result;
		}

#if UNITY_EDITOR_WIN
		private const uint GwOwner = 4;
		private const uint WmNull = 0x0000;
		private const uint SmtoBlock = 0x0001;
		private const uint SmtoAbortIfHung = 0x0002;

		private delegate bool EnumWindowsProc(IntPtr window, IntPtr parameter);

		// Held by a static field: the marshaled callback must outlive every native call that uses it.
		private static readonly EnumWindowsProc CollectCallback = Collect;

		private static bool Collect(IntPtr window, IntPtr parameter)
		{
			try
			{
				var windows = (List<IntPtr>)GCHandle.FromIntPtr(parameter).Target;
				windows.Add(window);
			}
			catch
			{
				// An exception must never unwind through user32.
			}

			return true;
		}

		private static bool BelongsTo(IntPtr window, int processId)
		{
			uint owner;
			GetWindowThreadProcessId(window, out owner);
			return owner == (uint)processId;
		}

		private static uint ThreadOf(IntPtr window)
		{
			uint ignored;
			return GetWindowThreadProcessId(window, out ignored);
		}

		private static string ClassOf(IntPtr window)
		{
			var builder = new StringBuilder(256);
			int length = GetClassName(window, builder, builder.Capacity);
			return length > 0 ? builder.ToString() : "";
		}

		private static string TextOf(IntPtr window, int capacity)
		{
			var builder = new StringBuilder(capacity);
			int length = InternalGetWindowText(window, builder, capacity);
			return length > 0 ? builder.ToString() : "";
		}

		private static bool Pumps(IntPtr window, int timeoutMs)
		{
			IntPtr ignored;
			IntPtr sent = SendMessageTimeout(window, WmNull, IntPtr.Zero, IntPtr.Zero, SmtoBlock | SmtoAbortIfHung, (uint)Math.Max(1, timeoutMs), out ignored);
			return sent != IntPtr.Zero;
		}

		[DllImport("kernel32.dll")]
		private static extern uint GetCurrentThreadId();

		[DllImport("user32.dll")]
		[return: MarshalAs(UnmanagedType.Bool)]
		private static extern bool EnumWindows(EnumWindowsProc callback, IntPtr parameter);

		[DllImport("user32.dll")]
		[return: MarshalAs(UnmanagedType.Bool)]
		private static extern bool EnumChildWindows(IntPtr parent, EnumWindowsProc callback, IntPtr parameter);

		[DllImport("user32.dll")]
		private static extern uint GetWindowThreadProcessId(IntPtr window, out uint processId);

		[DllImport("user32.dll")]
		[return: MarshalAs(UnmanagedType.Bool)]
		private static extern bool IsWindowVisible(IntPtr window);

		[DllImport("user32.dll")]
		[return: MarshalAs(UnmanagedType.Bool)]
		private static extern bool IsWindowEnabled(IntPtr window);

		[DllImport("user32.dll")]
		private static extern IntPtr GetWindow(IntPtr window, uint command);

		[DllImport("user32.dll")]
		private static extern IntPtr GetLastActivePopup(IntPtr window);

		[DllImport("user32.dll", EntryPoint = "GetClassNameW", CharSet = CharSet.Unicode, ExactSpelling = true)]
		private static extern int GetClassName(IntPtr window, StringBuilder name, int capacity);

		[DllImport("user32.dll", CharSet = CharSet.Unicode, ExactSpelling = true)]
		private static extern int InternalGetWindowText(IntPtr window, StringBuilder text, int capacity);

		[DllImport("user32.dll", EntryPoint = "SendMessageTimeoutW", CharSet = CharSet.Unicode, ExactSpelling = true)]
		private static extern IntPtr SendMessageTimeout(IntPtr window, uint message, IntPtr wParam, IntPtr lParam, uint flags, uint timeoutMs, out IntPtr result);
#endif
	}
}
