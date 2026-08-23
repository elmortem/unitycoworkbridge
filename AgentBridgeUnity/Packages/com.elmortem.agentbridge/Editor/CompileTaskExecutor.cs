using System;
using System.Collections.Generic;
using System.IO;
using UnityEditor;
using UnityEditor.Compilation;
using UnityEngine;

namespace AgentBridge
{
	public static class CompileTaskExecutor
	{
		public const string PendingCompileTaskKey = "AgentBridge_CompileTask";
		public const string PendingCompileFingerprintKey = "AgentBridge_CompileFingerprint";
		public const float NoReloadTimeoutSeconds = 20f;

		private static readonly List<TaskDiagnostic> _collectedErrors = new List<TaskDiagnostic>();
		private static bool _subscribed;
		private static double _startTime;

		public static void Begin(string taskId)
		{
			SessionState.SetString(PendingCompileTaskKey, taskId);
			_startTime = EditorApplication.timeSinceStartup;
			_collectedErrors.Clear();

			EnsureSubscribed();

			SessionState.SetString(PendingCompileFingerprintKey, CompileFingerprint.Current());

			AssetDatabase.Refresh(ImportAssetOptions.ForceSynchronousImport);
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
			SessionState.EraseString(PendingCompileTaskKey);

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

			return new TaskRecordOutcome
			{
				Status = diagnostics.Count > 0 ? "compiler_error" : "success",
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
			CompilationPipeline.assemblyCompilationFinished += OnAssemblyCompilationFinished;
		}

		private static void OnAssemblyCompilationFinished(string assemblyPath, CompilerMessage[] messages)
		{
			string taskId = SessionState.GetString(PendingCompileTaskKey, "");
			if (string.IsNullOrEmpty(taskId))
			{
				return;
			}

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

			WritePending(taskId, _collectedErrors);
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
