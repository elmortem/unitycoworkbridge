using System;
using System.Collections.Generic;
using System.IO;
using UnityEditor;
using UnityEditor.Compilation;
using UnityEngine;

namespace AgentBridge
{
	[InitializeOnLoad]
	public static class CompileTaskExecutor
	{
		public const string PendingCompileTaskKey = "AgentBridge_CompileTask";
		public const string PendingCompileFingerprintKey = "AgentBridge_CompileFingerprint";
		public const float NoReloadTimeoutSeconds = 20f;

		private static readonly List<TaskDiagnostic> _collectedErrors = new List<TaskDiagnostic>();
		private static bool _subscribed;
		private static double _startTime;
		private const string FinishedKey = "AgentBridge_CompileFinished";
		private const string CycleSourcesKey = "AgentBridge_CompileCycleSources";
		private const string CycleIdKey = "AgentBridge_CompileCycleId";
		private const string PublishKey = "AgentBridge_CompilePublish";
		private const string CycleErrorsKey = "AgentBridge_CompileCycleErrors";
		private const string CycleFinishedUtcKey = "AgentBridge_CompileCycleFinishedUtc";
		private const string CycleStableKey = "AgentBridge_CompileCycleStable";
		private static ValidationInputMonitor _cycleMonitor;
		public static string LastCycleStatus { get { return EditorApplication.isCompiling ? "compiling" : SessionState.GetString("AgentBridge_LastCompileStatus", "unknown"); } }
		public static string LastCycleId { get { return SessionState.GetString(CycleIdKey, ""); } }
		public static string LastCycleFinishedUtc { get { return SessionState.GetString(CycleFinishedUtcKey, ""); } }

		static CompileTaskExecutor()
		{
			if (!Application.isBatchMode) EnsureSubscribed();
		}

		public static bool HasCompleted()
		{
			return SessionState.GetBool(FinishedKey, false) && !EditorApplication.isCompiling && !EditorApplication.isUpdating;
		}

		// Split in two so evidence can be captured between them. The refresh writes the .meta files
		// the import owes, and a snapshot taken before that would call the bridge's own expected
		// import metadata a foreign change.
		public static void BeginImport(string taskId)
		{
			SessionState.SetString(PendingCompileTaskKey, taskId);
			_startTime = EditorApplication.timeSinceStartup;
			_collectedErrors.Clear();
			SessionState.SetBool(FinishedKey, false);
			SessionState.EraseString(PendingCompileFingerprintKey);

			EnsureSubscribed();

			AssetDatabase.Refresh(ImportAssetOptions.ForceSynchronousImport);
		}

		public static void RequestCompilation()
		{
			// Refresh may already have compiled the imported sources. Never request that cycle twice.
			_startTime = EditorApplication.timeSinceStartup;
			SessionState.SetString(PendingCompileFingerprintKey, ValidationEvidence.PreparedSources);
			if (HasCompleted() && SessionState.GetString(CycleSourcesKey, "") == ValidationEvidence.PreparedSources) return;
			SessionState.SetBool(FinishedKey, false);
			CompilationPipeline.RequestScriptCompilation();
		}

		public static bool HasPendingTask(out string taskId)
		{
			taskId = SessionState.GetString(PendingCompileTaskKey, "");
			return !string.IsNullOrEmpty(taskId);
		}

		public static bool IsTimedOut()
		{
			string taskId;
			if (!HasPendingTask(out taskId))
			{
				return false;
			}

			return EditorApplication.timeSinceStartup - _startTime >= NoReloadTimeoutSeconds;
		}

		public static TaskRecordOutcome ConsumePending(string taskId)
		{
			bool completed = SessionState.GetBool(FinishedKey, false);
			SessionState.EraseString(PendingCompileTaskKey);
			SessionState.EraseBool(FinishedKey);

			string pendingPath = PendingErrorsPath(taskId);
			var diagnostics = new List<TaskDiagnostic>();

			if (File.Exists(pendingPath))
			{
				string json = File.ReadAllText(pendingPath);
				TaskDiagnosticList list = JsonUtility.FromJson<TaskDiagnosticList>(json);
				if (list != null && list.Items != null)
				{
					diagnostics.AddRange(list.Items);
				}

				File.Delete(pendingPath);
			}

			diagnostics.AddRange(SourceImportVerifier.ValidateProjectSources());
			string status = diagnostics.Count > 0 ? "compiler_error" : (completed ? "success" : "runtime_error");
			if (status == "success")
			{
				try
				{
					string sources = SessionState.GetString(CycleSourcesKey, "");
					if (string.IsNullOrEmpty(sources)) status = "evidence_unavailable";
					else if (sources != CompileFingerprint.Current()) status = "stale_input";
				}
				catch { status = "evidence_unavailable"; }
			}

			return new TaskRecordOutcome
			{
				Status = status,
				Diagnostics = diagnostics,
				ForeignErrors = diagnostics.Count > 0
			};
		}

		private static void EnsureSubscribed()
		{
			if (_subscribed)
			{
				return;
			}

			_subscribed = true;
			CompilationPipeline.compilationStarted += OnCompilationStarted;
			CompilationPipeline.compilationFinished += OnCompilationFinished;
			CompilationPipeline.assemblyCompilationFinished += OnAssemblyCompilationFinished;
			EditorApplication.update += PublishCompletedCycle;
			AssemblyReloadEvents.beforeAssemblyReload += CaptureCycleChanges;
		}

		private static void OnCompilationStarted(object context)
		{
			_collectedErrors.Clear();
			SessionState.SetBool(FinishedKey, false);
			SessionState.SetBool(PublishKey, false);
			SessionState.SetString(CycleErrorsKey, "");
			SessionState.SetString(CycleSourcesKey, "");
			SessionState.SetBool(CycleStableKey, false);
			_cycleMonitor?.Dispose();
			_cycleMonitor = null;
			string taskId = SessionState.GetString(PendingCompileTaskKey, "");
			if (!string.IsNullOrEmpty(taskId)) WritePending(taskId, _collectedErrors);
			SessionState.SetString(CycleIdKey, string.IsNullOrEmpty(taskId) ? "UnityCompile_" + Guid.NewGuid().ToString("N") : taskId);
			try
			{
				_cycleMonitor = new ValidationInputMonitor(CompileInputContext.Roots, new string[0]);
				SessionState.SetBool(CycleStableKey, _cycleMonitor.Observed);
				SessionState.SetString(CycleSourcesKey, CompileFingerprint.Current());
			}
			catch { SessionState.SetBool(CycleStableKey, false); }
		}

		private static void OnCompilationFinished(object context)
		{
			SessionState.SetString("AgentBridge_LastCompileStatus", _collectedErrors.Count == 0 ? "success" : "compiler_error");
			SessionState.SetString(CycleErrorsKey, JsonUtility.ToJson(new TaskDiagnosticList { Items = _collectedErrors }));
			SessionState.SetString(CycleFinishedUtcKey, DateTime.UtcNow.ToString("o"));
			SessionState.SetBool(FinishedKey, true);
			SessionState.SetBool(PublishKey, true);
		}

		private static void PublishCompletedCycle()
		{
			if (!SessionState.GetBool(PublishKey, false) || EditorApplication.isCompiling || EditorApplication.isUpdating) return;
			SessionState.SetBool(PublishKey, false);
			CaptureCycleChanges();
			try
			{
				if (!SessionState.GetBool(CycleStableKey, false)) return;
				string start = SessionState.GetString(CycleSourcesKey, "");
				if (string.IsNullOrEmpty(start) || start != CompileFingerprint.Current()) return;
				var list = JsonUtility.FromJson<TaskDiagnosticList>(SessionState.GetString(CycleErrorsKey, ""));
				if (list == null || list.Items == null) return;
				list.Items.AddRange(SourceImportVerifier.ValidateProjectSources());
				CompileCacheStore.Write(new CompileCacheEntry {
					Fingerprint = start, SourceTaskId = SessionState.GetString(CycleIdKey, ""),
					Status = list.Items.Count == 0 ? "success" : "compiler_error", Diagnostics = list.Items,
					FinishedAtUtc = SessionState.GetString(CycleFinishedUtcKey, "")
				});
			}
			catch { /* An unreadable or changing input cannot seed reuse. The task still reports its diagnostics. */ }
		}

		private static void CaptureCycleChanges()
		{
			if (_cycleMonitor == null) return;
			if (!_cycleMonitor.Observed || _cycleMonitor.EventCount != 0) SessionState.SetBool(CycleStableKey, false);
			_cycleMonitor.Dispose();
			_cycleMonitor = null;
		}

		private static void OnAssemblyCompilationFinished(string assemblyPath, CompilerMessage[] messages)
		{
			string taskId = SessionState.GetString(PendingCompileTaskKey, "");
			if (messages == null)
			{
				return;
			}

			foreach (CompilerMessage message in messages)
			{
				if (message.type != CompilerMessageType.Error)
				{
					continue;
				}

				_collectedErrors.Add(new TaskDiagnostic
				{
					Code = ExtractCode(message.message),
					Severity = "Error",
					Message = message.message,
					File = message.file,
					Line = message.line,
					Column = message.column
				});
			}

			if (!string.IsNullOrEmpty(taskId)) WritePending(taskId, _collectedErrors);
		}

		private static string ExtractCode(string message)
		{
			int index = message.IndexOf("CS", StringComparison.Ordinal);
			if (index < 0 || index + 6 > message.Length)
			{
				return "";
			}

			return message.Substring(index, 6);
		}

		private static void WritePending(string taskId, List<TaskDiagnostic> diagnostics)
		{
			var list = new TaskDiagnosticList { Items = diagnostics };
			string json = JsonUtility.ToJson(list);
			File.WriteAllText(PendingErrorsPath(taskId), json);
		}

		private static string PendingErrorsPath(string taskId)
		{
			return Path.Combine(BridgePaths.WorkingRoot, "pending_" + taskId + ".json");
		}
	}
}
