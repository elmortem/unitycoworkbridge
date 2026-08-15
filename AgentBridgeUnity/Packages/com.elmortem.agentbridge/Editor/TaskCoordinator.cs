using System;
using System.Collections.Generic;
using System.IO;
using System.Security.Cryptography;
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

		private static double _lastScanTime;
		private static double _lastTrimTime;
		private static readonly Dictionary<string, CachedHash> _hashCache = new Dictionary<string, CachedHash>();
		private static bool _pendingTimeoutReload;

		private static string _activeTaskId;
		private static CancellationTokenSource _activeCancellation;
		private static TaskLogScope _activeLogScope;
		private static double _activeStartTime;
		private static TaskRecord _activeRecord;
		private static CSharpTaskExecutor _activeCSharpExecutor;
		private static SceneShot.SceneShotTaskExecutor _activeShotExecutor;
		private static TaskContext _activeShotContext;

		public static void Start()
		{
			EditorApplication.update -= OnUpdate;
			EditorApplication.update += OnUpdate;

			AssemblyReloadEvents.beforeAssemblyReload -= OnBeforeAssemblyReload;
			AssemblyReloadEvents.beforeAssemblyReload += OnBeforeAssemblyReload;

			PlayModeSceneRecovery.Start();
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

				if (record.Id == compileTaskId || record.Id == testTaskId)
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
		}

		public static void Stop()
		{
			EditorApplication.update -= OnUpdate;
			AssemblyReloadEvents.beforeAssemblyReload -= OnBeforeAssemblyReload;
			PlayModeSceneRecovery.Stop();
		}

		public static bool HasActiveTask
		{
			get { return _activeTaskId != null; }
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

			FinishTask("canceled", null, new List<string> { "Canceled by user" }, false);
		}

		private static void OnUpdate()
		{
			PlayModeSceneRecovery.Tick();

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
				else if (_activeRecord != null && _activeRecord.Kind == "tests")
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

			if (EditorApplication.isPlayingOrWillChangePlaymode || PlayModeSceneRecovery.IsPending)
			{
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

		private static void TryStartNextTask()
		{
			if (!Directory.Exists(BridgePaths.Inbox))
			{
				return;
			}

			string[] taskFiles = Directory.GetFiles(BridgePaths.Inbox, "*.task.json");
			if (taskFiles.Length == 0)
			{
				return;
			}

			string oldest = null;
			DateTime oldestTime = DateTime.MaxValue;

			foreach (string file in taskFiles)
			{
				string id = IdOf(file);

				TaskRecord existing;
				if (TaskJournal.TryRead(id, out existing) && IsTerminal(existing.Status))
				{
					string payloadPath = PayloadPathOf(file);
					if (existing.Hash == HashOf(file, payloadPath))
					{
						continue;
					}
				}

				DateTime created = File.GetCreationTimeUtc(file);
				if (created < oldestTime)
				{
					oldestTime = created;
					oldest = file;
				}
			}

			if (oldest == null)
			{
				return;
			}

			StartTask(oldest);
		}

		private static void StartTask(string taskFilePath)
		{
			string id = IdOf(taskFilePath);
			TaskRequest request;

			try
			{
				string requestJson = File.ReadAllText(taskFilePath);
				request = JsonUtility.FromJson<TaskRequest>(requestJson);
			}
			catch (Exception ex)
			{
				WriteTerminal(id, "unknown", "rejected", "invalid task.json: " + ex.Message);
				return;
			}

			if (request == null || string.IsNullOrEmpty(request.Id))
			{
				WriteTerminal(id, "unknown", "rejected", "invalid task.json");
				return;
			}

			string payloadPath = null;
			if (!string.IsNullOrEmpty(request.PayloadFile))
			{
				payloadPath = Path.Combine(BridgePaths.Inbox, request.PayloadFile);
			}

			string hash = HashOf(taskFilePath, payloadPath);

			TaskRecord existing;
			bool hasExisting = TaskJournal.TryRead(id, out existing);

			if (hasExisting)
			{
				if (existing.Hash != hash)
				{
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
				StartedAtUtc = DateTime.UtcNow.ToString("o")
			};
			TaskJournal.Write(_activeRecord);

			// Every kind runs the preflight: compile forces a domain reload and sceneshot
			// ticks the editor, and both can reach Unity code that opens the save dialog.
			string sceneError;
			if (!SceneSafetyGuard.TryPrepareForTask(out sceneError))
			{
				FinishTask("runtime_error", null, new List<string> { sceneError }, false);
				return;
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
				default:
					FinishTask("rejected", null, new List<string> { "unknown kind" }, false);
					break;
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

			_activeRecord.Status = status;
			_activeRecord.ReturnValue = returnValue;
			_activeRecord.Logs = logs;
			_activeRecord.ForeignErrors = foreignErrors;
			_activeRecord.FinishedAtUtc = DateTime.UtcNow.ToString("o");
			_activeRecord.Timing.TotalMs = (int)((EditorApplication.timeSinceStartup - _activeStartTime) * 1000);

			TaskJournal.Write(_activeRecord);

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

			if (_activeRecord.Kind == "tests")
			{
				CleanupActive();
				return;
			}

			_activeRecord.Status = "interrupted_by_domain_reload";
			_activeRecord.FinishedAtUtc = DateTime.UtcNow.ToString("o");
			TaskJournal.Write(_activeRecord);

			CleanupActive();
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

		private static string HashOf(string taskFilePath, string payloadPath)
		{
			long taskFileLength = new FileInfo(taskFilePath).Length;
			long payloadLength = !string.IsNullOrEmpty(payloadPath) && File.Exists(payloadPath)
				? new FileInfo(payloadPath).Length
				: 0;
			string taskFileWriteUtc = File.GetLastWriteTimeUtc(taskFilePath).ToString("o");
			string payloadWriteUtc = !string.IsNullOrEmpty(payloadPath) && File.Exists(payloadPath)
				? File.GetLastWriteTimeUtc(payloadPath).ToString("o")
				: "";
			string cacheKey = taskFilePath + "|" + (payloadPath ?? "");

			CachedHash cached;
			if (_hashCache.TryGetValue(cacheKey, out cached)
				&& cached.TaskFileLength == taskFileLength
				&& cached.PayloadLength == payloadLength
				&& cached.TaskFileWriteUtc == taskFileWriteUtc
				&& cached.PayloadWriteUtc == payloadWriteUtc)
			{
				return cached.Hash;
			}

			string hash = ComputeHash(taskFilePath, payloadPath);
			_hashCache[cacheKey] = new CachedHash
			{
				TaskFileLength = taskFileLength,
				PayloadLength = payloadLength,
				TaskFileWriteUtc = taskFileWriteUtc,
				PayloadWriteUtc = payloadWriteUtc,
				Hash = hash
			};
			return hash;
		}

		private static string ComputeHash(string taskFilePath, string payloadPath)
		{
			using (SHA256 sha = SHA256.Create())
			{
				byte[] taskBytes = File.ReadAllBytes(taskFilePath);
				byte[] combined = taskBytes;

				if (!string.IsNullOrEmpty(payloadPath) && File.Exists(payloadPath))
				{
					byte[] payloadBytes = File.ReadAllBytes(payloadPath);
					combined = new byte[taskBytes.Length + payloadBytes.Length];
					Buffer.BlockCopy(taskBytes, 0, combined, 0, taskBytes.Length);
					Buffer.BlockCopy(payloadBytes, 0, combined, taskBytes.Length, payloadBytes.Length);
				}

				byte[] hashBytes = sha.ComputeHash(combined);
				var builder = new StringBuilder(hashBytes.Length * 2);
				foreach (byte b in hashBytes)
				{
					builder.Append(b.ToString("x2"));
				}

				return builder.ToString();
			}
		}
	}
}
