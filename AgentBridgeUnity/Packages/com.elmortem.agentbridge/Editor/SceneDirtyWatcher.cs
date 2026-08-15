using System;
using System.Collections.Generic;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine.SceneManagement;

namespace AgentBridge
{
	[InitializeOnLoad]
	public static class SceneDirtyWatcher
	{
		public const string OwnerTaskKey = "AgentBridge_SceneDirtyWatcher";

		private const string LogsKey = OwnerTaskKey + "_Logs";
		private const string TruncatedLine = "scene dirty watcher log truncated";
		private const int MaxLogLines = 50;
		private const int StackSkipFrames = 3;
		private const int StackTakeFrames = 8;

		private static bool _pending;
		private static bool _running;
		private static string _pendingTarget;
		private static string _pendingStack;

		static SceneDirtyWatcher()
		{
			// A PlayMode test run reloads the domain while the watcher must stay live,
			// so the armed state lives in SessionState and is restored on every load.
			if (!string.IsNullOrEmpty(SessionState.GetString(OwnerTaskKey, "")))
			{
				Subscribe();
			}
		}

		public static bool IsArmed
		{
			get { return !string.IsNullOrEmpty(SessionState.GetString(OwnerTaskKey, "")); }
		}

		public static void Arm(string ownerTaskId)
		{
			SessionState.SetString(OwnerTaskKey, ownerTaskId ?? "");
			Subscribe();
		}

		public static void Disarm(string ownerTaskId)
		{
			string owner = SessionState.GetString(OwnerTaskKey, "");
			if (!string.IsNullOrEmpty(owner) && !string.Equals(owner, ownerTaskId ?? "", StringComparison.Ordinal))
			{
				return;
			}

			Unsubscribe();
			SessionState.EraseString(OwnerTaskKey);
			_pending = false;
			_pendingTarget = null;
			_pendingStack = null;
		}

		public static List<string> DrainLogs()
		{
			var result = new List<string>();
			string raw = SessionState.GetString(LogsKey, "");
			SessionState.EraseString(LogsKey);

			if (string.IsNullOrEmpty(raw))
			{
				return result;
			}

			result.AddRange(raw.Split('\n'));
			return result;
		}

		private static void Subscribe()
		{
			Unsubscribe();
			EditorSceneManager.sceneDirtied += OnSceneDirtied;
			EditorApplication.update += OnUpdate;
		}

		private static void Unsubscribe()
		{
			EditorSceneManager.sceneDirtied -= OnSceneDirtied;
			EditorApplication.update -= OnUpdate;
		}

		private static void OnSceneDirtied(Scene scene)
		{
			// Normalizing inside the callback would re-enter Unity's own dirtying path.
			// The next editor tick runs before Test Framework's job runner, which
			// subscribes to EditorApplication.update after the watcher does.
			_pending = true;
			_pendingTarget = string.IsNullOrEmpty(scene.path) ? scene.name + " (untitled)" : scene.path;
			_pendingStack = CaptureStack();
		}

		private static void OnUpdate()
		{
			if (!_pending || _running)
			{
				return;
			}

			if (EditorApplication.isPlayingOrWillChangePlaymode || EditorApplication.isCompiling)
			{
				return;
			}

			_running = true;
			try
			{
				_pending = false;
				string target = string.IsNullOrEmpty(_pendingTarget) ? "<unknown>" : _pendingTarget;
				string stack = _pendingStack;
				_pendingTarget = null;
				_pendingStack = null;

				List<string> actions;
				List<string> blocked;
				SceneSafetyGuard.NormalizeArmed(out actions, out blocked);

				string owner = SessionState.GetString(OwnerTaskKey, "");
				bool first = true;
				foreach (string entry in actions)
				{
					AppendLog(Format(owner, target, entry, first ? stack : null));
					first = false;
				}

				foreach (string entry in blocked)
				{
					AppendLog(Format(owner, target, entry, first ? stack : null));
					first = false;
				}
			}
			finally
			{
				_running = false;
			}
		}

		private static string Format(string owner, string target, string entry, string stack)
		{
			string line = "scene dirtied during task " + owner + ": " + target + "; " + entry;
			if (!string.IsNullOrEmpty(stack))
			{
				line += "; source: " + stack;
			}

			return Flatten(line);
		}

		private static string CaptureStack()
		{
			string[] frames = Environment.StackTrace.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries);
			var parts = new List<string>();
			for (int i = StackSkipFrames; i < frames.Length && parts.Count < StackTakeFrames; i++)
			{
				parts.Add(frames[i].Trim());
			}

			return string.Join(" <- ", parts.ToArray());
		}

		private static void AppendLog(string line)
		{
			string raw = SessionState.GetString(LogsKey, "");
			var lines = new List<string>();
			if (!string.IsNullOrEmpty(raw))
			{
				lines.AddRange(raw.Split('\n'));
			}

			if (lines.Count >= MaxLogLines)
			{
				if (lines[lines.Count - 1] == TruncatedLine)
				{
					return;
				}

				lines[lines.Count - 1] = TruncatedLine;
			}
			else
			{
				lines.Add(line);
			}

			SessionState.SetString(LogsKey, string.Join("\n", lines.ToArray()));
		}

		private static string Flatten(string value)
		{
			return value.Replace("\r\n", " ").Replace('\r', ' ').Replace('\n', ' ');
		}
	}
}
