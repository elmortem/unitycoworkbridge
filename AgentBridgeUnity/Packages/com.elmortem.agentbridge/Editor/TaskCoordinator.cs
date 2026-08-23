using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Threading;
using UnityEditor;
using UnityEngine;

namespace AgentBridge
{
	public static class TaskCoordinator
	{
		private const float ScanIntervalSeconds = 0.25f;
		private const float TrimIntervalSeconds = 30f;
		private const float QueueRefreshIntervalSeconds = 2f;
		private const float ServeIntervalSeconds = 1f;

		private static double _lastScanTime;
		private static double _lastTrimTime;
		private static double _lastQueueRefreshTime;
		private static double _lastServeTime;
		private static readonly Dictionary<string, string> _rejectedTaskHashes = new Dictionary<string, string>();
		private static bool _pendingTimeoutReload;

		private static string _activeTaskId;
		private static CancellationTokenSource _activeCancellation;
		private static TaskLogScope _activeLogScope;
		private static double _activeStartTime;
		private static TaskRecord _activeRecord;
		private static CSharpTaskExecutor _activeCSharpExecutor;
		private static SceneShot.SceneShotTaskExecutor _activeShotExecutor;
		private static TaskContext _activeShotContext;
		private static List<string> _activeRestoreLogs;
		private static string _queueSignature = "";

		public static void Start()
		{
			EditorApplication.update -= OnUpdate;
			EditorApplication.update += OnUpdate;

			AssemblyReloadEvents.beforeAssemblyReload -= OnBeforeAssemblyReload;
			AssemblyReloadEvents.beforeAssemblyReload += OnBeforeAssemblyReload;

			PlayModeSceneRecovery.Start();
			UnsanctionedPlayGuard.Start();
			TryFinalizePendingCompileTask();
			FinalizeOrphanRecords();
			SceneShot.SceneShotTaskExecutor.CloseOrphanWindows();
		}

		private static void FinalizeOrphanRecords()
		{
			if (!Directory.Exists(BridgePaths.Journal))
			{
				return;
			}

			string compileTaskId = SessionState.GetString(CompileTaskExecutor.PendingCompileTaskKey, "");
			string testTaskId = SessionState.GetString(AgentTestRunner.CoordinatorTestTaskKey, "");
			string sessionId = BridgeStatusWriter.Current.SessionId;

			// A play session survives the domain reload it caused, so the task that opened it
			// and the stopplay task waiting for it are still legitimately running.
			PlaySessionState playSession = PlaySessionStore.Read();
			string playTaskId = playSession != null ? playSession.TaskId ?? "" : "";
			string stopTaskId = playSession != null ? playSession.PendingStopTaskId ?? "" : "";

			foreach (string file in Directory.GetFiles(BridgePaths.Journal, "*.json"))
			{
				TaskRecord record;
				string id = Path.GetFileNameWithoutExtension(file);
				if (!TaskJournal.TryRead(id, out record))
				{
					continue;
				}

				if (IsTerminal(record.Status))
				{
					continue;
				}

				// An attachment only means something while its run is alive. If the run did not
				// survive the reload, the record goes away and the task returns to the queue.
				if (record.Status == "attached")
				{
					if (!string.IsNullOrEmpty(testTaskId) && record.AttachedToTaskId == testTaskId)
					{
						continue;
					}

					TaskJournal.Delete(record.Id);
					continue;
				}

				if (record.Id == compileTaskId || record.Id == testTaskId)
				{
					continue;
				}

				if ((!string.IsNullOrEmpty(playTaskId) && record.Id == playTaskId)
					|| (!string.IsNullOrEmpty(stopTaskId) && record.Id == stopTaskId))
				{
					continue;
				}

				if (record.SessionId == sessionId)
				{
					continue;
				}

				record.Status = "interrupted_by_domain_reload";
				record.FinishedAtUtc = DateTime.UtcNow.ToString("o");
				if (record.Logs == null)
				{
					record.Logs = new List<string>();
				}

				record.Logs.Add("orphaned record finalized on domain load");
				TaskJournal.Write(record);
			}
		}

		private static void TryFinalizePendingCompileTask()
		{
			string taskId;
			if (!CompileTaskExecutor.HasPendingTask(out taskId))
			{
				return;
			}

			TaskRecordOutcome outcome = CompileTaskExecutor.ConsumePending(taskId);

			// A compile task finalizes after its domain reload, past any CleanupActive call,
			// so the watcher it armed is released here.
			SceneDirtyWatcher.Disarm(taskId);
			List<string> watcherLogs = SceneDirtyWatcher.DrainLogs();

			TaskRecord record;
			if (!TaskJournal.TryRead(taskId, out record))
			{
				return;
			}

			if (record.Logs == null)
			{
				record.Logs = new List<string>();
			}

			record.Logs.AddRange(watcherLogs);

			record.Status = outcome.Status;
			record.Diagnostics = outcome.Diagnostics;
			record.ForeignErrors = outcome.ForeignErrors;
			record.FinishedAtUtc = DateTime.UtcNow.ToString("o");
			TaskJournal.Write(record);
			AgentSessionScheduler.OnTaskFinished(record.AgentSessionId, DateTime.UtcNow);

			// The fingerprint taken before the refresh proves nothing changed while the project
			// compiled; a mismatch means the result already describes older sources.
			string startFingerprint = SessionState.GetString(CompileTaskExecutor.PendingCompileFingerprintKey, "");
			SessionState.EraseString(CompileTaskExecutor.PendingCompileFingerprintKey);
			if ((record.Status == "success" || record.Status == "compiler_error")
				&& !string.IsNullOrEmpty(startFingerprint)
				&& startFingerprint == CompileFingerprint.Current())
			{
				CompileCacheStore.Write(new CompileCacheEntry
				{
					Fingerprint = startFingerprint,
					SourceTaskId = record.Id,
					Status = record.Status,
					Diagnostics = record.Diagnostics,
					FinishedAtUtc = record.FinishedAtUtc
				});
			}
		}

		public static void Stop()
		{
			EditorApplication.update -= OnUpdate;
			AssemblyReloadEvents.beforeAssemblyReload -= OnBeforeAssemblyReload;
			PlayModeSceneRecovery.Stop();
			UnsanctionedPlayGuard.Stop();
		}

		public static bool HasActiveTask
		{
			get { return _activeTaskId != null; }
		}

		public static string ActiveTaskId
		{
			get { return _activeTaskId; }
		}

		public static void CancelActive()
		{
			if (_activeRecord == null)
			{
				return;
			}

			if (_activeCancellation != null)
			{
				_activeCancellation.Cancel();
			}

			// Cancelling the task that owns the play mode must not leave the editor playing with
			// nothing left to stop it.
			if (_activeRecord.Kind == "play" || _activeRecord.Kind == "stopplay")
			{
				PlaySessionManager.BeginStop("", "manual");
			}

			FinishTask("canceled", null, new List<string> { "Canceled by user" }, false);
		}

		private static void OnUpdate()
		{
			PlayModeSceneRecovery.Tick();
			UnsanctionedPlayGuard.Tick();
			PlaySessionManager.Reconcile();

			if (_pendingTimeoutReload && _activeTaskId == null)
			{
				_pendingTimeoutReload = false;
				EditorUtility.RequestScriptReload();
				return;
			}

			double now = EditorApplication.timeSinceStartup;

			if (_activeTaskId != null)
			{
				CheckTimeout(now);
				RefreshQueueStatus(now);
				TryServeThrottled(now);

				if (_activeCSharpExecutor != null)
				{
					PollCSharpExecutor();
				}
				else if (_activeShotExecutor != null)
				{
					PollShotExecutor();
				}
				else if (_activeRecord != null && _activeRecord.Kind == "compile")
				{
					PollCompileTask();
				}
				else if (_activeRecord != null
					&& (_activeRecord.Kind == "tests" || _activeRecord.Kind == "play" || _activeRecord.Kind == "stopplay"))
				{
					PollExternallyFinalizedTask();
				}

				return;
			}

			if (now - _lastScanTime < ScanIntervalSeconds)
			{
				return;
			}

			_lastScanTime = now;

			if (!AgentBridgeSettingsStore.IsEnabled())
			{
				return;
			}

			if (EditorApplication.isCompiling)
			{
				return;
			}

			if (PlayModeSceneRecovery.IsPending)
			{
				TryServeThrottled(now);
				return;
			}

			if (EditorApplication.isPlayingOrWillChangePlaymode)
			{
				TryServeThrottled(now);

				if (PlaySessionManager.IsSessionActive)
				{
					TryStartPlaySessionTask();
				}
				else
				{
					TryStartStopplayTask();
				}

				return;
			}

			TryStartNextTask();
			TryTrim(now);
		}

		private static void TryTrim(double now)
		{
			if (_activeTaskId != null)
			{
				return;
			}

			if (now - _lastTrimTime < TrimIntervalSeconds)
			{
				return;
			}

			_lastTrimTime = now;
			TaskJournal.Trim(AgentBridgeSettingsStore.GetKeepCompletedCount());
		}

		// A cache hit owes nothing to the scheduler: no lease, no scene context, no editor time.
		// Serving it from the scan keeps the answer instant even while another task holds the
		// editor, a play session is open, or a PlayMode run waits for its scenes back.
		private static void TryServeThrottled(double now)
		{
			if (now - _lastServeTime < ServeIntervalSeconds)
			{
				return;
			}

			_lastServeTime = now;
			List<PendingTaskInfo> pending = BuildPendingList(_activeTaskId);
			CachedResultServer.TryServePending(pending);
			TestRunCoalescer.TryAttachPending(pending);
		}

		private static void TryStartNextTask()
		{
			List<PendingTaskInfo> pending = BuildPendingList(null);
			CachedResultServer.TryServePending(pending);

			DateTime nowUtc = DateTime.UtcNow;
			string idleHolder = SchedulerStateStore.State.HolderAgentSessionId;
			AgentSessionScheduler.TickIdle(nowUtc, pending.Count > 0);
			if (!string.IsNullOrEmpty(idleHolder) && string.IsNullOrEmpty(SchedulerStateStore.State.HolderAgentSessionId))
			{
				// The lease expired with an empty queue, so the editor is idle and the scenes on
				// screen still belong to the session that just lost it.
				string idleError;
				if (!SessionContextSwitcher.TrySaveContext(idleHolder, out idleError))
				{
					Debug.LogWarning("[AgentBridge] Failed to save the scene context of an idle agent session: " + idleError);
				}
			}

			UpdateQueueStatus(pending);

			PendingTaskInfo next;
			bool holderChanged;
			string previousHolder;
			if (!AgentSessionScheduler.TryPick(pending, nowUtc, out next, out holderChanged, out previousHolder))
			{
				return;
			}

			StartTask(next, holderChanged, previousHolder);
		}

		// The regular scheduler is off while the editor plays, so the session that owns the play
		// mode gets its own narrow pick: only the kinds that make sense inside play mode, and
		// only from the owner. Everything else is answered here or left in the queue.
		private static void TryStartPlaySessionTask()
		{
			PlaySessionState state = PlaySessionStore.Read();
			if (state == null)
			{
				return;
			}

			string owner = state.OwnerAgentSessionId ?? "";
			List<PendingTaskInfo> pending = BuildPendingList(null);
			PendingTaskInfo next = null;

			foreach (PendingTaskInfo task in pending)
			{
				bool isOwner = string.Equals(task.EffectiveSessionId, owner, StringComparison.Ordinal);

				if (!isOwner)
				{
					if (task.Kind == "stopplay")
					{
						RejectTaskFile(task.TaskFilePath, task.Id, "stopplay", "play_session_held_by:" + owner);
					}
					else if (task.Kind == "release")
					{
						// The ordinary release path is unreachable with the scheduler asleep, and a
						// foreign session must not wait out the whole play session for an answer.
						_rejectedTaskHashes[task.TaskFilePath] = TaskFileHash.HashOf(task.TaskFilePath, PayloadPathOf(task.TaskFilePath));
						WriteTerminal(task.Id, "release", "success", "not_holder");
					}

					continue;
				}

				if (task.Kind != "csharp" && task.Kind != "sceneshot" && task.Kind != "stopplay")
				{
					RejectTaskFile(task.TaskFilePath, task.Id, task.Kind, "kind not allowed during play session");
					continue;
				}

				if (next == null || task.CreatedUtc < next.CreatedUtc)
				{
					next = task;
				}
			}

			// The owner is working even though the scheduler never sees these picks, so the lease
			// has to be kept warm by hand or it expires in the middle of the session.
			SchedulerStateStore.State.HolderLastActivityUtc = DateTime.UtcNow.ToString("o");
			SchedulerStateStore.Save();

			if (next == null)
			{
				return;
			}

			StartTask(next, false, "");
		}

		// Play mode without a session is a stuck editor: nothing but stopplay may run, and it
		// may come from any session.
		private static void TryStartStopplayTask()
		{
			List<PendingTaskInfo> pending = BuildPendingList(null);
			PendingTaskInfo next = null;

			foreach (PendingTaskInfo task in pending)
			{
				if (task.Kind != "stopplay")
				{
					continue;
				}

				if (next == null || task.CreatedUtc < next.CreatedUtc)
				{
					next = task;
				}
			}

			if (next == null)
			{
				return;
			}

			StartTask(next, false, "");
		}

		private static List<PendingTaskInfo> BuildPendingList(string excludeTaskId)
		{
			var pending = new List<PendingTaskInfo>();
			if (!Directory.Exists(BridgePaths.Inbox))
			{
				return pending;
			}

			foreach (string file in Directory.GetFiles(BridgePaths.Inbox, "*.task.json"))
			{
				string id = IdOf(file);
				if (id == excludeTaskId)
				{
					continue;
				}

				TaskRecord existing;
				if (TaskJournal.TryRead(id, out existing))
				{
					// An attached task is alive: it waits for the run it joined, and putting it
					// back in the queue would start a second run of the same tests.
					if (existing.Status == "attached")
					{
						continue;
					}

					if (IsTerminal(existing.Status))
					{
						string payloadPath = PayloadPathOf(file);
						if (existing.Hash == TaskFileHash.HashOf(file, payloadPath))
						{
							continue;
						}
					}
				}

				// A task file that StartTask already refused cannot become runnable on its own.
				// Keeping it in the queue would hand it the lease again on every scan and starve
				// every other session behind it.
				string rejectedHash;
				if (_rejectedTaskHashes.TryGetValue(file, out rejectedHash)
					&& rejectedHash == TaskFileHash.HashOf(file, PayloadPathOf(file)))
				{
					continue;
				}

				var info = new PendingTaskInfo
				{
					Id = id,
					TaskFilePath = file,
					CreatedUtc = File.GetCreationTimeUtc(file),
					EffectiveSessionId = AgentSessionScheduler.EffectiveSessionId("", id),
					Note = "",
					Kind = ""
				};

				try
				{
					TaskRequest request = JsonUtility.FromJson<TaskRequest>(File.ReadAllText(file));
					if (request != null)
					{
						info.EffectiveSessionId = AgentSessionScheduler.EffectiveSessionId(request.AgentSessionId, id);
						info.Note = request.Note ?? "";
						info.Kind = request.Kind ?? "";
					}
				}
				catch
				{
					// An unreadable request stays in the queue as its own anonymous session;
					// StartTask rejects it with the same message as before.
				}

				pending.Add(info);
			}

			return pending;
		}

		private static void RefreshQueueStatus(double now)
		{
			// The scan loop is asleep while a task runs, and that is exactly when other sessions
			// pile up behind it: without this refresh a queued client never learns its position.
			if (now - _lastQueueRefreshTime < QueueRefreshIntervalSeconds)
			{
				return;
			}

			_lastQueueRefreshTime = now;
			UpdateQueueStatus(BuildPendingList(null));
		}

		private static void UpdateQueueStatus(List<PendingTaskInfo> pending)
		{
			string holder = SchedulerStateStore.State.HolderAgentSessionId;
			QueuedTaskStatus[] queue = AgentSessionScheduler.BuildQueue(pending);

			var signature = new StringBuilder(holder ?? "");
			foreach (QueuedTaskStatus item in queue)
			{
				signature.Append('|').Append(item.Id).Append(':').Append(item.Position);
			}

			string current = signature.ToString();
			if (current == _queueSignature)
			{
				return;
			}

			_queueSignature = current;
			BridgeStatusWriter.Current.HolderAgentSessionId = string.IsNullOrEmpty(holder) ? null : holder;
			BridgeStatusWriter.Current.QueuedTasks = queue;
			BridgeStatusWriter.Write();
		}

		private static void StartTask(PendingTaskInfo task, bool holderChanged, string previousHolder)
		{
			string taskFilePath = task.TaskFilePath;
			string id = task.Id;
			TaskRequest request;

			try
			{
				string requestJson = File.ReadAllText(taskFilePath);
				request = JsonUtility.FromJson<TaskRequest>(requestJson);
			}
			catch (Exception ex)
			{
				RejectTaskFile(taskFilePath, id, "unknown", "invalid task.json: " + ex.Message);
				return;
			}

			if (request == null || string.IsNullOrEmpty(request.Id))
			{
				RejectTaskFile(taskFilePath, id, "unknown", "invalid task.json");
				return;
			}

			string payloadPath = null;
			if (!string.IsNullOrEmpty(request.PayloadFile))
			{
				payloadPath = Path.Combine(BridgePaths.Inbox, request.PayloadFile);
			}

			string hash = TaskFileHash.HashOf(taskFilePath, payloadPath);

			TaskRecord existing;
			bool hasExisting = TaskJournal.TryRead(id, out existing);

			if (hasExisting)
			{
				if (existing.Hash != hash)
				{
					_rejectedTaskHashes[taskFilePath] = hash;
					WriteTerminal(id, request.Kind, "rejected", "id_conflict");
				}

				return;
			}

			_activeTaskId = id;
			EditorTickPump.HasActiveTask = true;
			BridgeStatusWriter.Current.ActiveTaskId = id;
			BridgeStatusWriter.Write();
			_activeCancellation = new CancellationTokenSource();
			_activeStartTime = EditorApplication.timeSinceStartup;
			_activeLogScope = TaskLogScope.Begin();

			_activeRecord = new TaskRecord
			{
				Id = id,
				Kind = request.Kind,
				Status = "queued",
				Hash = hash,
				SessionId = BridgeStatusWriter.Current.SessionId,
				AgentSessionId = task.EffectiveSessionId,
				StartedAtUtc = DateTime.UtcNow.ToString("o")
			};
			TaskJournal.Write(_activeRecord);

			if (holderChanged && !string.IsNullOrEmpty(previousHolder) && !AgentSessionScheduler.IsAnonymous(previousHolder))
			{
				// The scheduler was not mutated yet: a failed save leaves the old holder in place
				// and the next scan retries the rotation instead of losing its scenes.
				string contextError;
				if (!SessionContextSwitcher.TrySaveContext(previousHolder, out contextError))
				{
					FinishTask("runtime_error", null, new List<string> { contextError }, false);
					return;
				}
			}

			AgentSessionScheduler.CommitStart(task, holderChanged);

			// Every kind but release runs the preflight: compile forces a domain reload and
			// sceneshot ticks the editor, and both can reach Unity code that opens the save dialog.
			// Inside a play session the scenes on screen are the running game, and touching their
			// dirtiness there neither protects anything nor survives the exit.
			bool playSessionActive = PlaySessionManager.IsSessionActive;
			if (request.Kind != "release" && request.Kind != "stopplay" && !playSessionActive)
			{
				string sceneError;
				if (!SceneSafetyGuard.TryPrepareForTask(out sceneError))
				{
					FinishTask("runtime_error", null, new List<string> { sceneError }, false);
					return;
				}
			}

			if (NeedsScenes(request.Kind) && !SchedulerStateStore.State.HolderContextRestored && !playSessionActive)
			{
				_activeRestoreLogs = new List<string>();
				SessionContextSwitcher.RestoreContext(task.EffectiveSessionId, _activeRestoreLogs);
				SchedulerStateStore.State.HolderContextRestored = true;
				SchedulerStateStore.Save();
			}

			SceneDirtyWatcher.Arm(_activeRecord.Id);

			try
			{
				RunTask(request);
			}
			catch (Exception ex)
			{
				FinishTask("runtime_error", null, new List<string> { ex.Message }, false);
			}
		}

		private static void RunTask(TaskRequest request)
		{
			_activeRecord.Status = "running";
			TaskJournal.Write(_activeRecord);

			switch (request.Kind)
			{
				case "csharp":
					StartCSharpTask(request);
					break;
				case "ui":
					RunUiTask(request);
					break;
				case "compile":
					CompileTaskExecutor.Begin(request.Id);
					break;
				case "tests":
					StartTestsTask(request);
					break;
				case "sceneshot":
					StartShotTask(request);
					break;
				case "release":
					RunReleaseTask(request);
					break;
				case "play":
					RunPlayTask(request);
					break;
				case "stopplay":
					RunStopplayTask(request);
					break;
				default:
					FinishTask("rejected", null, new List<string> { "unknown kind" }, false);
					break;
			}
		}

		private static void RunReleaseTask(TaskRequest request)
		{
			string effective = AgentSessionScheduler.EffectiveSessionId(request.AgentSessionId, request.Id);
			string holder = SchedulerStateStore.State.HolderAgentSessionId ?? "";

			if (!string.Equals(holder, effective, StringComparison.Ordinal))
			{
				FinishTask("success", "not_holder", null, false);
				return;
			}

			var logs = new List<string>();
			string error;
			if (!SessionContextSwitcher.TrySaveContext(effective, out error))
			{
				logs.Add(error);
			}

			AgentSessionScheduler.Release(effective);
			FinishTask("success", "released", logs, false);
		}

		// A play task opens the session on whatever scenes its own agent session was working on,
		// so the record stays running until PlaySessionManager finalizes it from the other side
		// of the domain reload that entering play mode triggers.
		private static void RunPlayTask(TaskRequest request)
		{
			string error;
			if (!PlaySessionManager.BeginPlay(request, _activeRecord, out error))
			{
				FinishTask("rejected", null, new List<string> { error }, false);
			}
		}

		private static void RunStopplayTask(TaskRequest request)
		{
			string effective = AgentSessionScheduler.EffectiveSessionId(request.AgentSessionId, request.Id);
			PlaySessionState state = PlaySessionStore.Read();
			StopVerdict verdict = PlaySessionArbiter.Judge(
				state, effective, EditorApplication.isPlaying, PlayModeSceneRecovery.IsPending);

			switch (verdict)
			{
				case StopVerdict.NotPlaying:
					FinishTask("success", "not_playing", null, false);
					return;
				case StopVerdict.RejectTests:
					FinishTask("rejected", null, new List<string> { "tests are running" }, false);
					return;
				case StopVerdict.RejectForeign:
					FinishTask("rejected", null,
						new List<string> { "play_session_held_by:" + (state.OwnerAgentSessionId ?? "") }, false);
					return;
			}

			UnsanctionedPlayGuard.ClearMark();
			PlaySessionManager.BeginStop(request.Id, verdict == StopVerdict.StopOwn ? "stopplay" : "manual");
		}

		private static bool NeedsScenes(string kind)
		{
			switch (kind)
			{
				case "csharp":
				case "ui":
				case "tests":
				case "sceneshot":
				case "play":
					return true;
				default:
					return false;
			}
		}

		private static void StartCSharpTask(TaskRequest request)
		{
			string sourcePath = Path.Combine(BridgePaths.Inbox, request.Id + ".cs");
			if (!File.Exists(sourcePath))
			{
				FinishTask("rejected", null, new List<string> { "source file not found: " + request.Id + ".cs" }, false);
				return;
			}

			string source = File.ReadAllText(sourcePath);
			_activeCSharpExecutor = CSharpTaskExecutor.Begin(source, sourcePath, request.Id, _activeCancellation.Token);
		}

		private static void RunUiTask(TaskRequest request)
		{
			string payloadPath = Path.Combine(BridgePaths.Inbox, request.Id + ".ui.json");
			if (!File.Exists(payloadPath))
			{
				FinishTask("rejected", null, new List<string> { "payload file not found: " + request.Id + ".ui.json" }, false);
				return;
			}

			var context = new TaskContext
			{
				Id = request.Id,
				Kind = request.Kind,
				CancellationToken = _activeCancellation.Token
			};

			TaskResultData result = Ui.UiTaskRunner.Execute(payloadPath, context);

			foreach (string artifact in context.Artifacts)
			{
				_activeRecord.Artifacts.Add(artifact);
			}

			FinishTask(result.Status, result.ReturnValue, result.Logs, false);
		}

		private static void StartShotTask(TaskRequest request)
		{
			string payloadPath = Path.Combine(BridgePaths.Inbox, request.Id + ".sceneshot.json");
			if (!File.Exists(payloadPath))
			{
				FinishTask("rejected", null, new List<string> { "payload file not found: " + request.Id + ".sceneshot.json" }, false);
				return;
			}

			_activeShotContext = new TaskContext
			{
				Id = request.Id,
				Kind = request.Kind,
				CancellationToken = _activeCancellation.Token
			};

			try
			{
				_activeShotExecutor = SceneShot.SceneShotTaskExecutor.Begin(payloadPath, _activeShotContext);
			}
			catch (Exception ex)
			{
				_activeShotContext = null;
				FinishTask("rejected", null, new List<string> { ex.GetBaseException().Message }, false);
			}
		}

		private static void PollShotExecutor()
		{
			if (_activeRecord == null)
			{
				return;
			}

			_activeShotExecutor.Tick();

			if (!_activeShotExecutor.IsCompleted)
			{
				return;
			}

			TaskResultData result = _activeShotExecutor.GetResult();
			foreach (string artifact in _activeShotContext.Artifacts)
			{
				_activeRecord.Artifacts.Add(artifact);
			}

			_activeShotExecutor = null;
			_activeShotContext = null;
			FinishTask(result.Status, result.ReturnValue, result.Logs, false);
		}

		private static void StartTestsTask(TaskRequest request)
		{
			TestRunResult abortedResult;
			bool started = AgentTestRunner.TryRequestRunForCoordinator(
				request.Id, request.TestMode, request.AssemblyNames, request.TestNames, request.CategoryNames, out abortedResult);

			if (!started)
			{
				// No run was handed to Test Framework, so nothing will disarm the watcher later.
				SceneDirtyWatcher.Disarm(request.Id);
				_activeRecord.Tests = abortedResult;
				FinishTask("runtime_error", null, new List<string> { abortedResult.message }, false);
			}
		}

		private static void PollExternallyFinalizedTask()
		{
			TaskRecord latest;
			if (!TaskJournal.TryRead(_activeRecord.Id, out latest))
			{
				return;
			}

			if (!IsTerminal(latest.Status))
			{
				return;
			}

			// PlaySessionManager already told the scheduler when it finalized the record;
			// a second call would refresh the lease of a session that is no longer working.
			if (_activeRecord.Kind == "tests")
			{
				AgentSessionScheduler.OnTaskFinished(_activeRecord.AgentSessionId, DateTime.UtcNow);
			}

			CleanupActive();
		}

		private static void PollCompileTask()
		{
			if (!CompileTaskExecutor.IsTimedOut())
			{
				return;
			}

			string taskId;
			CompileTaskExecutor.HasPendingTask(out taskId);
			TaskRecordOutcome outcome = CompileTaskExecutor.ConsumePending(taskId);

			List<string> extraLogs = null;
			if (outcome.Diagnostics.Count > 0)
			{
				extraLogs = new List<string>();
				foreach (TaskDiagnostic diagnostic in outcome.Diagnostics)
				{
					extraLogs.Add(diagnostic.Code + ": " + diagnostic.Message);
				}
			}

			_activeRecord.Diagnostics = outcome.Diagnostics;
			SessionState.EraseString(CompileTaskExecutor.PendingCompileFingerprintKey);
			FinishTask(outcome.Status, null, extraLogs, outcome.ForeignErrors);
		}

		private static void PollCSharpExecutor()
		{
			if (_activeRecord == null || _activeCSharpExecutor == null)
			{
				return;
			}

			string statusHint = _activeCSharpExecutor.StatusHint;
			if (_activeRecord.Status != statusHint && !IsTerminal(_activeRecord.Status))
			{
				_activeRecord.Status = statusHint;
				TaskJournal.Write(_activeRecord);
			}

			if (!_activeCSharpExecutor.IsCompleted)
			{
				return;
			}

			CSharpTaskOutcome outcome = _activeCSharpExecutor.GetResult();
			_activeCSharpExecutor = null;

			var extraLogs = new List<string>();

			if (outcome.CompileResult != null)
			{
				_activeRecord.Diagnostics = outcome.CompileResult.Diagnostics;

				if (outcome.CompileResult.GuardrailRejected)
				{
					foreach (TaskDiagnostic diagnostic in outcome.CompileResult.Diagnostics)
					{
						extraLogs.Add("guardrail: " + diagnostic.Message);
					}
				}
			}

			if (!string.IsNullOrEmpty(outcome.ErrorMessage))
			{
				extraLogs.Add(outcome.ErrorMessage);
			}

			if (outcome.Status == "success")
			{
				BridgeStatusWriter.Current.LoadedTaskAssemblies++;
				BridgeStatusWriter.Current.ExecutedTasks++;
				BridgeStatusWriter.Write();
			}

			FinishTask(outcome.Status, outcome.ReturnValue, extraLogs, false);
		}

		private static void FinishTask(string status, string returnValue, List<string> extraLogs, bool foreignErrors)
		{
			if (_activeRecord == null)
			{
				return;
			}

			List<string> logs = _activeLogScope != null ? _activeLogScope.Drain() : new List<string>();
			if (extraLogs != null)
			{
				logs.AddRange(extraLogs);
			}

			logs.AddRange(SceneDirtyWatcher.DrainLogs());

			if (_activeRestoreLogs != null && _activeRestoreLogs.Count > 0)
			{
				logs.InsertRange(0, _activeRestoreLogs);
			}

			DateTime finishedUtc = DateTime.UtcNow;
			_activeRecord.Status = status;
			_activeRecord.ReturnValue = returnValue;
			_activeRecord.Logs = logs;
			_activeRecord.ForeignErrors = foreignErrors;
			_activeRecord.FinishedAtUtc = finishedUtc.ToString("o");
			_activeRecord.Timing.TotalMs = (int)((EditorApplication.timeSinceStartup - _activeStartTime) * 1000);
			_activeRecord.Contention = AgentSessionScheduler.BuildContention(BuildPendingList(_activeRecord.Id), finishedUtc);

			TaskJournal.Write(_activeRecord);
			UnsanctionedPlayGuard.RecordTaskFinish(_activeRecord.Id);
			AgentSessionScheduler.OnTaskFinished(_activeRecord.AgentSessionId, DateTime.UtcNow);

			CleanupActive();
		}

		private static void CleanupActive()
		{
			// A tests task keeps the watcher armed across the whole Test Framework run,
			// including its domain reloads; AgentTestRunner disarms it on finalization.
			if (_activeRecord == null || _activeRecord.Kind != "tests")
			{
				SceneDirtyWatcher.Disarm(_activeTaskId ?? "");
			}

			if (_activeLogScope != null)
			{
				_activeLogScope.Dispose();
				_activeLogScope = null;
			}

			_activeCancellation = null;
			_activeRecord = null;
			_activeTaskId = null;
			_activeCSharpExecutor = null;
			_activeRestoreLogs = null;

			// Timeout, cancel and domain reload drop the executor without ever
			// ticking it again, so its temporary window has to be closed here.
			if (_activeShotExecutor != null)
			{
				SceneShot.SceneShotTaskExecutor.CloseOrphanWindows();
			}

			_activeShotExecutor = null;
			_activeShotContext = null;
			EditorTickPump.HasActiveTask = false;
			BridgeStatusWriter.Current.ActiveTaskId = null;
			BridgeStatusWriter.Write();
		}

		private static void CheckTimeout(double now)
		{
			if (_activeRecord == null)
			{
				return;
			}

			// A play session has its own deadline and a stopplay task waits for the editor to
			// finish leaving play mode; the task timeout would cut both short.
			if (_activeRecord.Kind == "play" || _activeRecord.Kind == "stopplay")
			{
				return;
			}

			int timeoutSeconds = AgentBridgeSettingsStore.GetTaskTimeoutSeconds();
			if (now - _activeStartTime <= timeoutSeconds)
			{
				return;
			}

			if (_activeCancellation != null)
			{
				_activeCancellation.Cancel();
			}

			if (_activeRecord.Kind == "csharp")
			{
				_pendingTimeoutReload = true;
			}

			FinishTask("timeout", null, new List<string> { "task exceeded timeout: " + timeoutSeconds + "s" }, false);
		}

		private static void OnBeforeAssemblyReload()
		{
			if (_activeRecord == null)
			{
				return;
			}

			if (_activeRecord.Kind == "compile")
			{
				return;
			}

			// Entering and leaving play mode both reload the domain while the record is still
			// running; PlaySessionManager finalizes it on the other side, exactly like tests.
			if (_activeRecord.Kind == "tests" || _activeRecord.Kind == "play" || _activeRecord.Kind == "stopplay")
			{
				CleanupActive();
				return;
			}

			_activeRecord.Status = "interrupted_by_domain_reload";
			_activeRecord.FinishedAtUtc = DateTime.UtcNow.ToString("o");
			TaskJournal.Write(_activeRecord);
			AgentSessionScheduler.OnTaskFinished(_activeRecord.AgentSessionId, DateTime.UtcNow);

			CleanupActive();
		}

		private static void RejectTaskFile(string taskFilePath, string id, string kind, string logLine)
		{
			_rejectedTaskHashes[taskFilePath] = TaskFileHash.HashOf(taskFilePath, PayloadPathOf(taskFilePath));
			WriteTerminal(id, kind, "rejected", logLine);
		}

		private static void WriteTerminal(string id, string kind, string status, string logLine)
		{
			var record = new TaskRecord
			{
				Id = id,
				Kind = kind,
				Status = status,
				SessionId = BridgeStatusWriter.Current.SessionId,
				StartedAtUtc = DateTime.UtcNow.ToString("o"),
				FinishedAtUtc = DateTime.UtcNow.ToString("o")
			};

			if (!string.IsNullOrEmpty(logLine))
			{
				record.Logs.Add(logLine);
			}

			TaskJournal.Write(record);
		}

		private static bool IsTerminal(string status)
		{
			switch (status)
			{
				case "success":
				case "test_failure":
				case "compiler_error":
				case "runtime_error":
				case "timeout":
				case "canceled":
				case "interrupted_by_domain_reload":
				case "rejected":
					return true;
				default:
					return false;
			}
		}

		private static string IdOf(string taskFilePath)
		{
			string fileName = Path.GetFileName(taskFilePath);
			const string suffix = ".task.json";
			return fileName.Substring(0, fileName.Length - suffix.Length);
		}

		private static string PayloadPathOf(string taskFilePath)
		{
			try
			{
				string requestJson = File.ReadAllText(taskFilePath);
				TaskRequest request = JsonUtility.FromJson<TaskRequest>(requestJson);
				if (request == null || string.IsNullOrEmpty(request.PayloadFile))
				{
					return null;
				}

				return Path.Combine(BridgePaths.Inbox, request.PayloadFile);
			}
			catch
			{
				return null;
			}
		}

	}
}
