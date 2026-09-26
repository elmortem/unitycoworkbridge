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

		private const double CoordinationSyncIntervalSeconds = 2d;

		private static double _lastBeatTime = double.MinValue;
		private static double _lastCoordinationSync = double.MinValue;

		// status.json and heartbeat are snapshots of in-memory state, so a missed write is only
		// postponed: the next tick publishes the then-current state. A failure must never reach
		// the task lifecycle that happened to trigger the write.
		private static bool _statusPending;
		private static bool _writeFailureReported;
		private static double _retryAfter = double.MinValue;

		// SharedFile already retries for about 100 ms; during a longer lock the editor waits this
		// long between attempts instead of sleeping on every tick.
		private const double FailedWriteBackoffSeconds = 0.5d;

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
			Current.Capabilities = BuildCapabilities();
			RefreshCoordination();

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
			_statusPending = !TryWriteAtomic(BridgePaths.StatusFile, json);
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

			long unixMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
			// A failed beat leaves the clock alone, so the next tick tries again instead of
			// letting the heartbeat age past the CLI's liveness threshold.
			if (TryWriteAtomic(BridgePaths.HeartbeatFile, unixMs.ToString()))
			{
				_lastBeatTime = now;
			}
		}

		private static void OnUpdate()
		{
			Beat();
			if (_statusPending)
			{
				Write();
			}

			string compilation = CompileTaskExecutor.LastCycleStatus;
			string finished = CompileTaskExecutor.LastCycleFinishedUtc;
			if (Current.CompilationState != compilation || Current.LastCompileFinishedUtc != finished)
			{
				Current.CompilationState = compilation;
				Current.LastCompileId = CompileTaskExecutor.LastCycleId;
				Current.LastCompileFinishedUtc = finished;
				Write();
			}
			SyncPlayingFlag();
			SyncCoordination();
		}

		// Capabilities are additive and independent: a build may serve tasks without being able to
		// coordinate, and a client must be able to tell those apart before it offers a command.
		private static string[] BuildCapabilities()
		{
			var capabilities = new System.Collections.Generic.List<string>
			{
				"csharp", "ui", "sceneshot", "compile", "tests", "release", "play", "stopplay",
				"evidence-v1", "test-cache-v2", "cancel-v1", TaskCancellationPolicy.Capability
			};

			if (CoordinationEditorAdapter.Available)
			{
				capabilities.Add("coordination-v1");
				capabilities.Add(Coordination.CoordinationBatch.Capability);
				capabilities.Add(Coordination.CoordinationLimits.EditLeasesCapability);
			}

			return capabilities.ToArray();
		}

		private static void SyncCoordination()
		{
			double now = EditorApplication.timeSinceStartup;
			if (now - _lastCoordinationSync < CoordinationSyncIntervalSeconds)
			{
				return;
			}

			_lastCoordinationSync = now;
			bool active = Current.CoordinationActive;
			int participants = Current.CoordinationParticipants;
			long revision = Current.CoordinationRevision;
			string window = Current.CoordinationWindowSession;

			RefreshCoordination();

			if (active == Current.CoordinationActive
				&& participants == Current.CoordinationParticipants
				&& revision == Current.CoordinationRevision
				&& window == Current.CoordinationWindowSession)
			{
				return;
			}

			Write();
		}

		private static void RefreshCoordination()
		{
			Current.CoordinationUnavailable = CoordinationEditorAdapter.Available
				? null
				: CoordinationEditorAdapter.UnavailableReason;

			Coordination.CoordinationState state = CoordinationEditorAdapter.Snapshot;
			if (state == null)
			{
				Current.CoordinationActive = false;
				Current.CoordinationParticipants = 0;
				Current.CoordinationRevision = 0;
				Current.CoordinationWindowSession = null;
				return;
			}

			Coordination.CoordinationGrant window = state.FindWindowGrant();
			Current.CoordinationActive = state.Participants.Count > 0;
			Current.CoordinationParticipants = state.Participants.Count;
			Current.CoordinationRevision = state.Revision;
			Current.CoordinationWindowSession = window != null ? window.Session : null;
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

		private static bool TryWriteAtomic(string path, string content)
		{
			double now = EditorApplication.timeSinceStartup;
			if (_writeFailureReported && now < _retryAfter)
			{
				return false;
			}

			try
			{
				Coordination.SharedFile.WriteAtomic(path, content);
				if (_writeFailureReported)
				{
					_writeFailureReported = false;
					UnityEngine.Debug.Log("[AgentBridge] Status files are written again.");
				}

				return true;
			}
			catch (Exception exception)
			{
				// One warning per failure streak: the editor retries every tick, and a locked file
				// would otherwise flood the console.
				_retryAfter = now + FailedWriteBackoffSeconds;
				if (!_writeFailureReported)
				{
					_writeFailureReported = true;
					UnityEngine.Debug.LogWarning("[AgentBridge] Could not write " + Path.GetFileName(path)
						+ ", retrying on the next tick: " + exception.Message);
				}

				return false;
			}
		}
	}
}
