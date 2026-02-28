using System;
using System.Collections.Generic;
using System.IO;
using UnityEngine;
using UnityEditor;
using UnityEditor.Compilation;

namespace CoworkBridge
{
	[InitializeOnLoad]
	public static class CoworkBridge
	{
		private const string PendingTaskKey = "CoworkBridge_PendingTask";
		private const string EnabledKey = "CoworkBridge_Enabled";
		private const float ScanInterval = 1f;
		private const float PendingTimeout = 5f;

		private static string _coworkPath;
		private static double _lastScanTime;
		private static double _pendingSetTime;
		private static readonly List<CompilerError> _collectedErrors = new List<CompilerError>();

		static CoworkBridge()
		{
			_coworkPath = Path.Combine(Application.dataPath, "Editor", "CoworkBridge");

			if (!IsEnabled())
			{
				return;
			}

			Initialize();
			Debug.Log("[CoworkBridge] Enabled. Watching: " + _coworkPath);
		}

		[MenuItem("Tools/Cowork Bridge/Start")]
		public static void Start()
		{
			SetEnabled(true);
			Initialize();
			Debug.Log("[CoworkBridge] Started. Watching: " + _coworkPath);
		}

		[MenuItem("Tools/Cowork Bridge/Start", true)]
		private static bool StartValidate()
		{
			return !IsEnabled();
		}

		[MenuItem("Tools/Cowork Bridge/Stop")]
		public static void Stop()
		{
			SetEnabled(false);
			EditorPrefs.DeleteKey(PendingTaskKey);
			EditorApplication.update -= OnEditorUpdate;
			CompilationPipeline.assemblyCompilationFinished -= OnAssemblyCompilationFinished;
			CompilationPipeline.compilationFinished -= OnCompilationFinished;
			Debug.Log("[CoworkBridge] Stopped.");
		}

		[MenuItem("Tools/Cowork Bridge/Stop", true)]
		private static bool StopValidate()
		{
			return IsEnabled();
		}

		[MenuItem("Tools/Cowork Bridge/Run Task...")]
		public static void RunTaskManual()
		{
			var taskPath = EditorUtility.OpenFilePanel("Select Task Script", _coworkPath, "cs");

			if (string.IsNullOrEmpty(taskPath))
			{
				return;
			}

			string taskId = Path.GetFileNameWithoutExtension(taskPath);
			TaskRunner.ExecuteTask(taskId, _coworkPath);
		}

		[MenuItem("Tools/Cowork Bridge/Clean Completed")]
		public static void CleanCompleted()
		{
			string coworkPath = Path.Combine(Application.dataPath, "Editor", "CoworkBridge");

			if (!Directory.Exists(coworkPath))
			{
				return;
			}

			int count = 0;
			foreach (string csFile in Directory.GetFiles(coworkPath, "*.cs"))
			{
				string taskId = Path.GetFileNameWithoutExtension(csFile);
				string donePath = Path.Combine(coworkPath, "result_" + taskId + ".done");

				if (File.Exists(donePath))
				{
					DeleteTaskFiles(coworkPath, taskId);
					count++;
				}
			}

			AssetDatabase.Refresh();
			Debug.Log("[CoworkBridge] Cleaned " + count + " completed tasks.");
		}

		[MenuItem("Tools/Cowork Bridge/Clean All")]
		public static void CleanAll()
		{
			string coworkPath = Path.Combine(Application.dataPath, "Editor", "CoworkBridge");

			if (!Directory.Exists(coworkPath))
			{
				return;
			}

			int count = 0;
			foreach (string csFile in Directory.GetFiles(coworkPath, "*.cs"))
			{
				string taskId = Path.GetFileNameWithoutExtension(csFile);
				DeleteTaskFiles(coworkPath, taskId);
				count++;
			}

			AssetDatabase.Refresh();
			Debug.Log("[CoworkBridge] Cleaned " + count + " tasks.");
		}

		private static void Initialize()
		{
			_coworkPath = Path.Combine(Application.dataPath, "Editor", "CoworkBridge");

			if (!Directory.Exists(_coworkPath))
			{
				Directory.CreateDirectory(_coworkPath);
			}

			EditorApplication.update -= OnEditorUpdate;
			EditorApplication.update += OnEditorUpdate;

			CompilationPipeline.assemblyCompilationFinished -= OnAssemblyCompilationFinished;
			CompilationPipeline.assemblyCompilationFinished += OnAssemblyCompilationFinished;

			CompilationPipeline.compilationFinished -= OnCompilationFinished;
			CompilationPipeline.compilationFinished += OnCompilationFinished;

			string pendingTaskId = EditorPrefs.GetString(PendingTaskKey, "");
			if (!string.IsNullOrEmpty(pendingTaskId))
			{
				EditorApplication.delayCall += () => ProcessAfterReload(pendingTaskId);
			}
		}

		private static void ProcessAfterReload(string taskId)
		{
			EditorPrefs.DeleteKey(PendingTaskKey);

			if (!IsEnabled())
			{
				return;
			}

			string errorsPath = Path.Combine(_coworkPath, "pending_errors_" + taskId + ".json");
			if (File.Exists(errorsPath))
			{
				string json = File.ReadAllText(errorsPath);
				File.Delete(errorsPath);
				TaskRunner.HandlePendingErrors(taskId, json, _coworkPath);
				return;
			}

			TaskRunner.ExecuteTask(taskId, _coworkPath);
		}

		private static void OnAssemblyCompilationFinished(string assemblyPath, CompilerMessage[] messages)
		{
			string pendingTaskId = EditorPrefs.GetString(PendingTaskKey, "");
			if (string.IsNullOrEmpty(pendingTaskId))
			{
				return;
			}

			if (messages == null)
			{
				return;
			}

			foreach (var msg in messages)
			{
				if (msg.type == CompilerMessageType.Error)
				{
					_collectedErrors.Add(new CompilerError
					{
						type = "Error",
						message = msg.message,
						file = msg.file,
						line = msg.line
					});
				}
			}
		}

		private static void OnCompilationFinished(object obj)
		{
			string pendingTaskId = EditorPrefs.GetString(PendingTaskKey, "");
			if (string.IsNullOrEmpty(pendingTaskId))
			{
				_collectedErrors.Clear();
				return;
			}

			if (_collectedErrors.Count > 0)
			{
				EditorPrefs.DeleteKey(PendingTaskKey);
				TaskRunner.HandleCompilerErrors(pendingTaskId, _collectedErrors, _coworkPath);
				_collectedErrors.Clear();
				return;
			}

			_collectedErrors.Clear();
		}

		private static void OnEditorUpdate()
		{
			if (EditorApplication.timeSinceStartup - _lastScanTime < ScanInterval)
			{
				return;
			}

			_lastScanTime = EditorApplication.timeSinceStartup;

			if (EditorApplication.isCompiling)
			{
				return;
			}

			string pendingTaskId = EditorPrefs.GetString(PendingTaskKey, "");
			if (!string.IsNullOrEmpty(pendingTaskId))
			{
				if (EditorApplication.timeSinceStartup - _pendingSetTime > PendingTimeout)
				{
					ProcessAfterReload(pendingTaskId);
				}
				return;
			}

			if (!Directory.Exists(_coworkPath))
			{
				return;
			}

			string nextScript = FindNextTask();
			if (nextScript == null)
			{
				return;
			}

			string taskId = Path.GetFileNameWithoutExtension(nextScript);

			Type existingType = TaskRunner.FindType(taskId);
			if (existingType != null)
			{
				Debug.Log("[CoworkBridge] Processing task (already compiled): " + taskId);
				TaskRunner.ExecuteTask(taskId, _coworkPath);
				return;
			}

			Debug.Log("[CoworkBridge] New task detected, triggering compilation: " + taskId);
			EditorPrefs.SetString(PendingTaskKey, taskId);
			_pendingSetTime = EditorApplication.timeSinceStartup;

			CleanResultFiles(taskId);

			AssetDatabase.Refresh();
			CompilationPipeline.RequestScriptCompilation();
		}

		private static string FindNextTask()
		{
			var files = Directory.GetFiles(_coworkPath, "*.cs");
			if (files.Length == 0)
			{
				return null;
			}

			var pending = new List<string>();
			foreach (string file in files)
			{
				string taskId = Path.GetFileNameWithoutExtension(file);
				string donePath = Path.Combine(_coworkPath, "result_" + taskId + ".done");

				if (!File.Exists(donePath))
				{
					pending.Add(file);
				}
			}

			if (pending.Count == 0)
			{
				return null;
			}

			pending.Sort((a, b) => File.GetCreationTimeUtc(a).CompareTo(File.GetCreationTimeUtc(b)));
			return pending[0];
		}

		private static void CleanResultFiles(string taskId)
		{
			string resultPath = Path.Combine(_coworkPath, "result_" + taskId + ".json");
			string donePath = Path.Combine(_coworkPath, "result_" + taskId + ".done");

			if (File.Exists(resultPath))
			{
				File.Delete(resultPath);
			}

			if (File.Exists(donePath))
			{
				File.Delete(donePath);
			}
		}

		private static void DeleteTaskFiles(string coworkPath, string taskId)
		{
			string csPath = Path.Combine(coworkPath, taskId + ".cs");
			string resultPath = Path.Combine(coworkPath, "result_" + taskId + ".json");
			string donePath = Path.Combine(coworkPath, "result_" + taskId + ".done");
			string errorsPath = Path.Combine(coworkPath, "pending_errors_" + taskId + ".json");

			if (File.Exists(csPath))
			{
				File.Delete(csPath);
			}

			if (File.Exists(resultPath))
			{
				File.Delete(resultPath);
			}

			if (File.Exists(donePath))
			{
				File.Delete(donePath);
			}

			if (File.Exists(errorsPath))
			{
				File.Delete(errorsPath);
			}
		}

		private static bool IsEnabled()
		{
			return EditorPrefs.GetBool(EnabledKey, false);
		}

		private static void SetEnabled(bool value)
		{
			EditorPrefs.SetBool(EnabledKey, value);
		}
	}
}
