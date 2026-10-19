using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.Threading;
using UnityEditor;
using UnityEngine;

namespace AgentBridge
{
	// Tells the CLI that a modal dialog holds the editor's main thread, the way Unity CLI reports
	// STATUS_BLOCKED_BY_DIALOG. While the dialog is open the main thread neither ticks nor writes
	// status.json, so a timer on a thread-pool thread publishes Library/AgentBridge/dialogs.json and
	// refreshes it every second. Two sources feed it: Unity's EditorDialogEvents where the editor
	// has them (6.7+), and on Windows a scan of the editor's own windows once the main thread has
	// stopped ticking. Dialogs seen during a task end up in that task's logs.
	[InitializeOnLoad]
	public static class EditorDialogMonitor
	{
		public const string Capability = "dialog-detection-v1";

		private const int ScanIntervalMs = 1000;
		// The window scan starts only after the main thread has missed its updates for this long;
		// a ticking editor is not blocked, whatever windows it shows. Idle ticks come every 500 ms.
		private const long StallBeforeScanMs = 1000;
		private const int PumpTimeoutMs = 200;
		private const int ShutdownWaitMs = 2000;
		private const int ClosedLimit = 32;
		private const long DismissedMemoryMs = 3000;
		private const double SettingsRefreshSeconds = 1d;

		private sealed class Tracked
		{
			public EditorDialogInfo Info;
			public long FirstSeenMs;
			public long LastSeenMs;
		}

		private static readonly object Gate = new object();
		private static readonly Dictionary<string, Tracked> Open = new Dictionary<string, Tracked>();
		private static readonly Dictionary<string, EditorDialogInfo> EventDialogs = new Dictionary<string, EditorDialogInfo>();
		// Titles of event dialogs dismissed a moment ago: a window scan that started before the dismiss
		// must not bring them back as win32 duplicates.
		private static readonly Dictionary<string, long> RecentlyDismissed = new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase);
		private static readonly List<Tracked> Closed = new List<Tracked>();
		private static readonly List<Tracked> TelemetryQueue = new List<Tracked>();

		private static System.Threading.Timer _timer;
		private static volatile bool _running;
		private static bool _published;
		private static string _file;
		private static int _processId;
		private static uint _mainThreadId;
		private static long _lastUpdateMs;
		private static int _scanning;
		private static volatile bool _enabled;
		private static volatile string _testTaskId;
		private static double _nextSettingsRefresh;
		private static string _lastError;
		private static bool _errorReported;

		static EditorDialogMonitor()
		{
			if (Application.isBatchMode)
			{
				return;
			}

			// TaskCoordinator.FinishTask and the status writer call into this class: a throwing type
			// initializer would fail every task from then on, so a broken detector only reports itself.
			try
			{
				Start();
			}
			catch (Exception exception)
			{
				_running = false;
				lock (Gate)
				{
					_lastError = exception.GetType().Name + ": " + exception.Message;
				}
			}

			EditorApplication.update -= OnUpdate;
			EditorApplication.update += OnUpdate;
			AssemblyReloadEvents.beforeAssemblyReload -= Shutdown;
			AssemblyReloadEvents.beforeAssemblyReload += Shutdown;
			EditorApplication.quitting -= Shutdown;
			EditorApplication.quitting += Shutdown;
		}

		private static void Start()
		{
			_file = BridgePaths.DialogsFile;
			_processId = Process.GetCurrentProcess().Id;
			_mainThreadId = NativeDialogProbe.CurrentThreadId();
			_enabled = AgentBridgeSettingsStore.IsEnabled();
			Interlocked.Exchange(ref _lastUpdateMs, NowMs());

			EditorDialogEventsHook.TryInstall(OnEventShown, OnEventDismissed);

			lock (Gate)
			{
				_running = true;
				// A file left by a previous domain or a crashed editor describes nothing that is open now.
				_published = System.IO.File.Exists(_file);
				PublishLocked(NowMs());
			}

			_timer = new System.Threading.Timer(OnTimer, null, ScanIntervalMs, ScanIntervalMs);
		}

		// What this editor can detect: written to status.json so a client can tell "no dialog" from
		// "cannot know".
		public static string Detection
		{
			get
			{
				if (!_running)
				{
					return "unavailable";
				}

				bool events = EditorDialogEventsHook.Installed;
				bool windows = NativeDialogProbe.Supported;
				if (events && windows)
				{
					return "editor_dialog_events+win32";
				}

				if (events)
				{
					return "editor_dialog_events";
				}

				return windows ? "win32" : "unsupported";
			}
		}

		// Log lines for dialogs that were open while the given task ran; each dialog is reported once.
		public static List<string> DrainTaskLogs(string taskId)
		{
			var lines = new List<string>();
			if (string.IsNullOrEmpty(taskId))
			{
				return lines;
			}

			lock (Gate)
			{
				for (int index = 0; index < Closed.Count; index++)
				{
					Tracked tracked = Closed[index];
					if (tracked.Info.TaskId != taskId)
					{
						continue;
					}

					lines.Add(Describe(tracked, false));
					Closed.RemoveAt(index);
					index--;
				}

				foreach (Tracked tracked in Open.Values)
				{
					if (tracked.Info.TaskId == taskId)
					{
						lines.Add(Describe(tracked, true));
					}
				}
			}

			return lines;
		}

		private static string Describe(Tracked tracked, bool stillOpen)
		{
			EditorDialogInfo info = tracked.Info;
			string details = info.Kind;
			if (!string.IsNullOrEmpty(info.Level))
			{
				details += ", " + info.Level;
			}

			double seconds = Math.Max(0, tracked.LastSeenMs - tracked.FirstSeenMs) / 1000d;
			string line = "modal dialog: \"" + info.Title + "\" (" + details + ") "
				+ (stillOpen ? "is still open after " : "held the editor for ")
				+ seconds.ToString("0.0", CultureInfo.InvariantCulture) + " s during this task";
			string message = EditorDialogSnapshot.Limit(info.Message, 200).Replace('\n', ' ').Replace('\r', ' ');
			if (message.Length > 0)
			{
				line += ": " + message;
			}

			return line;
		}

		private static void OnUpdate()
		{
			long nowMs = NowMs();
			Interlocked.Exchange(ref _lastUpdateMs, nowMs);

			double now = EditorApplication.timeSinceStartup;
			if (now >= _nextSettingsRefresh)
			{
				_nextSettingsRefresh = now + SettingsRefreshSeconds;
				_enabled = AgentBridgeSettingsStore.IsEnabled();
				// A PlayMode test run outlives the coordinator's record across its domain reloads.
				string testTask = TestRunLifecycle.TaskId;
				_testTaskId = string.IsNullOrEmpty(testTask) ? null : testTask;
			}

			List<Tracked> finished = null;
			string error = null;
			lock (Gate)
			{
				// A native message box runs its own loop: an update proves it has closed even if Unity
				// never raised dialogDismissed for it.
				if (DropNativeEventDialogs())
				{
					Reconcile(null, nowMs);
					PublishLocked(nowMs);
				}

				if (TelemetryQueue.Count > 0)
				{
					finished = new List<Tracked>(TelemetryQueue);
					TelemetryQueue.Clear();
				}

				if (_lastError != null && !_errorReported)
				{
					error = _lastError;
					_errorReported = true;
				}
			}

			if (error != null)
			{
				UnityEngine.Debug.LogWarning("[AgentBridge] Modal dialog detection failed: " + error);
			}

			if (finished == null)
			{
				return;
			}

			foreach (Tracked tracked in finished)
			{
				TelemetryLog.Write("editor_dialog", "", tracked.Info.TaskId ?? "", new[]
				{
					TelemetryField.Text("Title", tracked.Info.Title ?? ""),
					TelemetryField.Text("Kind", tracked.Info.Kind ?? ""),
					TelemetryField.Text("Source", tracked.Info.Source ?? ""),
					TelemetryField.Text("Level", tracked.Info.Level ?? ""),
					TelemetryField.Number("OpenMs", Math.Max(0, tracked.LastSeenMs - tracked.FirstSeenMs))
				});
			}
		}

		private static bool DropNativeEventDialogs()
		{
			if (EventDialogs.Count == 0)
			{
				return false;
			}

			var stale = new List<string>();
			foreach (KeyValuePair<string, EditorDialogInfo> entry in EventDialogs)
			{
				if (entry.Value.Kind == "native_message_box")
				{
					stale.Add(entry.Key);
				}
			}

			foreach (string id in stale)
			{
				EventDialogs.Remove(id);
			}

			return stale.Count > 0;
		}

		// Main thread, right before Unity's modal loop starts: publish now, the next tick is far away.
		private static void OnEventShown(EditorDialogInfo info)
		{
			if (info == null || !_running)
			{
				return;
			}

			lock (Gate)
			{
				EventDialogs[info.Id] = info;
				long now = NowMs();
				Reconcile(null, now);
				PublishLocked(now);
			}
		}

		private static void OnEventDismissed(string id)
		{
			if (!_running)
			{
				return;
			}

			lock (Gate)
			{
				EditorDialogInfo info;
				if (!EventDialogs.TryGetValue(id, out info))
				{
					return;
				}

				EventDialogs.Remove(id);
				long now = NowMs();
				if (!string.IsNullOrEmpty(info.Title))
				{
					RecentlyDismissed[info.Title] = now;
				}

				Reconcile(null, now);
				PublishLocked(now);
			}
		}

		private static void OnTimer(object state)
		{
			if (Interlocked.Exchange(ref _scanning, 1) == 1)
			{
				return;
			}

			try
			{
				// Shutdown clears _running before it waits for _scanning: either it sees this scan, or
				// this scan sees the cleared flag. A callback dequeued just before Timer.Dispose ends here.
				if (!_running)
				{
					return;
				}

				long now = NowMs();
				bool stalled = now - Interlocked.Read(ref _lastUpdateMs) >= StallBeforeScanMs;
				// Outside the lock: the probe may wait up to PumpTimeoutMs on the main thread, which may
				// itself be about to take the lock in OnEventShown. A ticking editor is not blocked by
				// any window it shows, so its scan result is simply empty.
				List<NativeDialogProbe.Window> windows = _enabled && stalled && NativeDialogProbe.Supported
					? NativeDialogProbe.Scan(_processId, _mainThreadId, PumpTimeoutMs, () => !_running)
					: new List<NativeDialogProbe.Window>();

				lock (Gate)
				{
					if (!_running)
					{
						return;
					}

					now = NowMs();
					Reconcile(windows, now);
					PublishLocked(now);
				}
			}
			catch (Exception exception)
			{
				lock (Gate)
				{
					if (_lastError == null)
					{
						_lastError = exception.GetType().Name + ": " + exception.Message;
					}
				}
			}
			finally
			{
				Interlocked.Exchange(ref _scanning, 0);
			}
		}

		// The task executing right now. A plain static read is safe off the main thread, and reading it
		// here rather than once per update catches a dialog raised in the tick that started the task.
		private static string CurrentTaskId()
		{
			string task = TaskCoordinator.ActiveTaskId;
			return string.IsNullOrEmpty(task) ? (_testTaskId ?? "") : task;
		}

		// windows == null keeps the window-scan dialogs as they are: an event arrived between two
		// scans. An empty list means the scan ran (or the editor ticks again) and found nothing.
		private static void Reconcile(List<NativeDialogProbe.Window> windows, long now)
		{
			PruneDismissed(now);
			var current = new List<EditorDialogInfo>(EventDialogs.Values);
			if (windows != null)
			{
				foreach (NativeDialogProbe.Window window in windows)
				{
					if (SameTitleAsEvent(window.Title) || RecentlyDismissed.ContainsKey(window.Title ?? ""))
					{
						continue;
					}

					current.Add(new EditorDialogInfo
					{
						Id = "win32:" + window.Handle.ToString("x", CultureInfo.InvariantCulture),
						Source = "win32",
						Kind = window.NativeDialog ? "native_dialog" : "editor_window",
						Level = "",
						Title = window.Title ?? "",
						Message = window.Message ?? "",
						Buttons = window.Buttons ?? new string[0],
						OpenedAtUtc = DateTime.UtcNow.ToString("o")
					});
				}
			}
			else
			{
				foreach (Tracked tracked in Open.Values)
				{
					if (tracked.Info.Source == "win32" && !RecentlyDismissed.ContainsKey(tracked.Info.Title ?? ""))
					{
						current.Add(tracked.Info);
					}
				}
			}

			var seen = new HashSet<string>();
			foreach (EditorDialogInfo info in current)
			{
				seen.Add(info.Id);
				Tracked tracked;
				if (Open.TryGetValue(info.Id, out tracked))
				{
					tracked.LastSeenMs = now;
					continue;
				}

				info.TaskId = CurrentTaskId();
				Open[info.Id] = new Tracked { Info = info, FirstSeenMs = now, LastSeenMs = now };
			}

			var gone = new List<string>();
			foreach (KeyValuePair<string, Tracked> entry in Open)
			{
				if (!seen.Contains(entry.Key))
				{
					gone.Add(entry.Key);
				}
			}

			foreach (string id in gone)
			{
				Tracked tracked = Open[id];
				Open.Remove(id);
				tracked.LastSeenMs = now;
				TelemetryQueue.Add(tracked);
				if (TelemetryQueue.Count > ClosedLimit)
				{
					TelemetryQueue.RemoveAt(0);
				}

				if (!string.IsNullOrEmpty(tracked.Info.TaskId))
				{
					Closed.Add(tracked);
					if (Closed.Count > ClosedLimit)
					{
						Closed.RemoveAt(0);
					}
				}
			}
		}

		private static void PruneDismissed(long now)
		{
			if (RecentlyDismissed.Count == 0)
			{
				return;
			}

			var expired = new List<string>();
			foreach (KeyValuePair<string, long> entry in RecentlyDismissed)
			{
				if (now - entry.Value > DismissedMemoryMs)
				{
					expired.Add(entry.Key);
				}
			}

			foreach (string title in expired)
			{
				RecentlyDismissed.Remove(title);
			}
		}

		private static bool SameTitleAsEvent(string title)
		{
			foreach (EditorDialogInfo info in EventDialogs.Values)
			{
				if (string.Equals(info.Title, title, StringComparison.OrdinalIgnoreCase))
				{
					return true;
				}
			}

			return false;
		}

		// Unity's own events first (they know level and buttons), then native dialogs, then editor
		// windows: BlockedBy[0] is what an agent shows a person.
		private static int Rank(EditorDialogInfo info)
		{
			if (info.Source == "editor_dialog_events")
			{
				return 0;
			}

			return info.Kind == "native_dialog" ? 1 : 2;
		}

		private static void PublishLocked(long now)
		{
			if (Open.Count == 0 && !_published)
			{
				return;
			}

			var ordered = new List<Tracked>(Open.Values);
			ordered.Sort((left, right) =>
			{
				int rank = Rank(left.Info).CompareTo(Rank(right.Info));
				return rank != 0 ? rank : left.FirstSeenMs.CompareTo(right.FirstSeenMs);
			});

			var dialogs = new List<EditorDialogInfo>();
			foreach (Tracked tracked in ordered)
			{
				tracked.Info.OpenMs = Math.Max(0, now - tracked.FirstSeenMs);
				dialogs.Add(tracked.Info);
			}

			try
			{
				long idleMs = now - Interlocked.Read(ref _lastUpdateMs);
				Coordination.SharedFile.WriteAtomic(_file, EditorDialogSnapshot.Build(_processId, now, idleMs, dialogs));
				_published = dialogs.Count > 0;
			}
			catch (Exception exception)
			{
				// Retried on the next timer tick; a stale file ages out on the client side.
				if (_lastError == null)
				{
					_lastError = "cannot write dialogs.json: " + exception.Message;
				}
			}
		}

		private static void Shutdown()
		{
			EditorApplication.update -= OnUpdate;
			AssemblyReloadEvents.beforeAssemblyReload -= Shutdown;
			EditorApplication.quitting -= Shutdown;

			_running = false;
			if (_timer != null)
			{
				_timer.Dispose();
				_timer = null;
			}

			// Let an in-flight scan leave native code before the domain that owns it goes away. The scan
			// checks _running between windows, so this wait is one probe long at most.
			SpinWait.SpinUntil(() => Interlocked.CompareExchange(ref _scanning, 0, 0) == 0, ShutdownWaitMs);
			EditorDialogEventsHook.Uninstall();

			lock (Gate)
			{
				Open.Clear();
				EventDialogs.Clear();
				if (_published && _file != null)
				{
					try
					{
						Coordination.SharedFile.WriteAtomic(_file, EditorDialogSnapshot.Build(_processId, NowMs(), 0, new List<EditorDialogInfo>()));
						_published = false;
					}
					catch
					{
					}
				}
			}
		}

		private static long NowMs()
		{
			return DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
		}
	}
}
