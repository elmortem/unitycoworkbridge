using System;
using System.IO;
using System.Diagnostics;
using UnityEditor;
using UnityEngine;

namespace AgentBridge
{
	[InitializeOnLoad]
	public static class BridgeStatusWriter
	{
		private const double BeatIntervalSeconds = 2d;
		private const int ProtocolVersion = 1;

		public static readonly BridgeStatus Current = new BridgeStatus();

		private static readonly bool Suspended = Application.isBatchMode;

		private static double _lastBeatTime = double.MinValue;

		static BridgeStatusWriter()
		{
			if (Suspended)
			{
				return;
			}

			WriteOnLoad();
			EditorApplication.update -= OnUpdate;
			EditorApplication.update += OnUpdate;
		}

		public static void WriteOnLoad()
		{
			UnityEditor.PackageManager.PackageInfo package =
				UnityEditor.PackageManager.PackageInfo.FindForAssembly(typeof(BridgeStatusWriter).Assembly);
			Current.ProtocolVersion = ProtocolVersion;
			Current.PackageVersion = package != null ? package.version : "unknown";
			Current.ProjectPath = BridgePaths.ProjectRoot;
			Current.ProjectId = ProjectIdentity.Ensure();
			Current.HostOs = HostPlatform.Name;
			Current.UnityVersion = Application.unityVersion;
			Current.EditorPid = Process.GetCurrentProcess().Id;
			Current.SessionId = Guid.NewGuid().ToString("N");
			Current.AssemblyBuildTimeUtc = File.GetLastWriteTimeUtc(typeof(BridgeStatusWriter).Assembly.Location).ToString("o");
			Current.Enabled = AgentBridgeSettingsStore.IsEnabled();
			Current.ActiveTaskId = null;
			Current.HolderAgentSessionId = null;
			Current.IsPlaying = EditorApplication.isPlayingOrWillChangePlaymode;
			Current.PlaySessionAgentId = null;
			Current.PlaySessionDeadlineUtc = null;
			Current.QueuedTasks = new QueuedTaskStatus[0];
			Current.Capabilities = new[] { "csharp", "ui", "sceneshot", "compile", "tests", "release", "play", "stopplay" };

			RoslynLocation location = RoslynResolver.ResolveConfigured();
			Current.RoslynReady = location.Available;
			Current.RoslynSource = location.Available ? location.Kind.ToString() : AgentBridgeSettingsStore.GetRoslynSource();

			Current.WakeTimerInstalled = AgentEditorWakeTimer.Installed;
			Current.WakeTimerKind = AgentEditorWakeTimer.Kind ?? "none";
			Current.InteractionMode = InteractionModeProbe.Read();

			Write();
		}

		public static void Write()
		{
			if (Suspended)
			{
				return;
			}

			string json = UnityEngine.JsonUtility.ToJson(Current, true);
			WriteAtomic(BridgePaths.StatusFile, json);
		}

		public static void Beat()
		{
			if (Suspended)
			{
				return;
			}

			double now = EditorApplication.timeSinceStartup;
			if (now - _lastBeatTime < BeatIntervalSeconds)
			{
				return;
			}

			_lastBeatTime = now;
			long unixMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
			WriteAtomic(BridgePaths.HeartbeatFile, unixMs.ToString());
		}

		private static void OnUpdate()
		{
			Beat();
		}

		private static void WriteAtomic(string path, string content)
		{
			string tempPath = path + ".tmp";
			File.WriteAllText(tempPath, content);

			if (File.Exists(path))
			{
				File.Replace(tempPath, path, null);
			}
			else
			{
				File.Move(tempPath, path);
			}
		}
	}
}
