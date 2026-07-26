using System;
using System.IO;
using System.Threading;
using UnityEditor;
using UnityEngine;

namespace AgentBridge
{
	public static class AgentBridgeSettingsStore
	{
		private const string FileName = "AgentBridge.json";

		private static AgentBridgeSettings _cached;
		private static DateTime _cachedWriteUtc;
		private static double _lastCheckTime;
		private static int _mainThreadId;

		[InitializeOnLoadMethod]
		private static void Initialize()
		{
			_mainThreadId = Thread.CurrentThread.ManagedThreadId;
		}

		public static bool IsEnabled()
		{
			AgentBridgeSettings settings = Load();
			return settings.Enabled;
		}

		public static void SetEnabled(bool value)
		{
			AgentBridgeSettings settings = Load();
			settings.Enabled = value;
			Save(settings);
		}

		public static void SetRoslynSource(string value)
		{
			AgentBridgeSettings settings = Load();
			settings.RoslynSource = value;
			Save(settings);
		}

		public static int GetKeepCompletedCount()
		{
			AgentBridgeSettings settings = Load();
			if (settings.KeepCompletedCount <= 0)
			{
				return 10;
			}

			return settings.KeepCompletedCount;
		}

		public static int GetTaskTimeoutSeconds()
		{
			AgentBridgeSettings settings = Load();
			if (settings.TaskTimeoutSeconds <= 0)
			{
				return 300;
			}

			return settings.TaskTimeoutSeconds;
		}

		public static int GetIdleTickIntervalMs()
		{
			AgentBridgeSettings settings = Load();
			if (settings.IdleTickIntervalMs <= 0)
			{
				return 500;
			}

			return settings.IdleTickIntervalMs;
		}

		public static int GetActiveTickIntervalMs()
		{
			AgentBridgeSettings settings = Load();
			if (settings.ActiveTickIntervalMs <= 0)
			{
				return 33;
			}

			return settings.ActiveTickIntervalMs;
		}

		public static string GetRoslynSource()
		{
			AgentBridgeSettings settings = Load();
			if (string.IsNullOrEmpty(settings.RoslynSource))
			{
				return "Auto";
			}

			return settings.RoslynSource;
		}

		public static string GetRoslynLocalPath()
		{
			AgentBridgeSettings settings = Load();
			return settings.RoslynLocalPath ?? "";
		}

		public static bool GetEmitPdb()
		{
			AgentBridgeSettings settings = Load();
			return settings.EmitPdb;
		}

		public static int GetClientWaitSeconds()
		{
			AgentBridgeSettings settings = Load();
			if (settings.ClientWaitSeconds <= 0)
			{
				return 110;
			}

			return settings.ClientWaitSeconds;
		}

		private static AgentBridgeSettings Load()
		{
			double now = CurrentTime();
			if (_cached != null && now - _lastCheckTime < 2)
			{
				return _cached;
			}

			_lastCheckTime = now;
			string path = GetSettingsPath();
			DateTime writeUtc = File.Exists(path) ? File.GetLastWriteTimeUtc(path) : DateTime.MinValue;
			if (_cached != null && _cachedWriteUtc == writeUtc)
			{
				return _cached;
			}

			if (!File.Exists(path))
			{
				_cached = new AgentBridgeSettings();
				_cachedWriteUtc = writeUtc;
				return _cached;
			}

			string json = File.ReadAllText(path);
			AgentBridgeSettings settings = JsonUtility.FromJson<AgentBridgeSettings>(json);

			if (settings == null)
			{
				settings = new AgentBridgeSettings();
			}

			_cached = settings;
			_cachedWriteUtc = writeUtc;
			return _cached;
		}

		private static double CurrentTime()
		{
			if (Thread.CurrentThread.ManagedThreadId == _mainThreadId)
			{
				return EditorApplication.timeSinceStartup;
			}

			return _lastCheckTime;
		}

		private static void Save(AgentBridgeSettings settings)
		{
			string path = GetSettingsPath();
			string json = JsonUtility.ToJson(settings, true);
			File.WriteAllText(path, json);
			_cached = null;
		}

		private static string GetSettingsPath()
		{
			string projectRoot = Path.GetDirectoryName(Application.dataPath);
			string projectSettingsPath = Path.Combine(projectRoot, "ProjectSettings");
			return Path.Combine(projectSettingsPath, FileName);
		}
	}
}
