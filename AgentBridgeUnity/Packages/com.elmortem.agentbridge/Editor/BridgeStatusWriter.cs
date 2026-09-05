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
			// The play session outlives the domain reload that entering play mode triggers, so
			// reading it back here keeps the agent's own play mode from looking manual to the CLI
			// for the whole window between the reload and the next PlaySessionManager write.
			PlaySessionState playSession = PlaySessionStore.Read();
			Current.IsPlaying = EditorApplication.isPlayingOrWillChangePlaymode;
			Current.PlaySessionAgentId = playSession != null && !string.IsNullOrEmpty(playSession.OwnerAgentSessionId)
				? playSession.OwnerAgentSessionId
				: null;
			Current.PlaySessionDeadlineUtc = playSession != null ? playSession.DeadlineUtc : null;
			Current.QueuedTasks = new QueuedTaskStatus[0];
			Current.Capabilities = new[] { "csharp", "ui", "sceneshot", "compile", "tests", "release", "play", "stopplay" };

			RoslynLocation location = RoslynResolver.ResolveConfigured();
			Current.RoslynReady = location.Available;
			Current.RoslynSource = location.Available ? location.Kind.ToString() : AgentBridgeSettingsStore.GetRoslynSource();

			Current.WakeTimerInstalled = AgentEditorWakeTimer.Installed;
			Current.WakeTimerKind = AgentEditorWakeTimer.Kind ?? "none";
			Current.InteractionMode = InteractionModeProbe.Read();
			Current.TelemetryEnabled = AgentBridgeSettingsStore.GetTelemetryEnabled();

			Write();
		}

		// Emit after EditorTickPump has published its initialized wake backend. Static
		// initialization order alone does not guarantee that WriteOnLoad sees it.
		public static void WriteStartTelemetry()
		{
			if (Suspended)
			{
				return;
			}

			TelemetryLog.Write("bridge_start", "", "", new[]
			{
				TelemetryField.Text("Package", Current.PackageVersion),
				TelemetryField.Text("Unity", Current.UnityVersion),
				TelemetryField.Text("Wake", Current.WakeTimerKind ?? "none"),
				TelemetryField.Text("Interaction", Current.InteractionMode ?? "unknown"),
				TelemetryField.Number("Pid", Current.EditorPid)
			});
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
			SyncPlayingFlag();
		}

		// With Enter Play Mode Options disabling the domain reload, a manual play toggle never
		// reaches WriteOnLoad, and status.json keeps claiming the editor is idle. Only a changed
		// flag writes, so this never races the transient writes PlaySessionManager makes.
		private static void SyncPlayingFlag()
		{
			bool playing = EditorApplication.isPlayingOrWillChangePlaymode;
			if (Current.IsPlaying == playing)
			{
				return;
			}

			Current.IsPlaying = playing;
			Write();
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
