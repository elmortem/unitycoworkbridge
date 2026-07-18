using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using UnityEngine;
using UnityEditor;

namespace CoworkBridge
{
	public static class AsyncTaskWatcher
	{
		public const string AsyncTaskKey = "CoworkBridge_AsyncTask";

		private static string _taskId;
		private static string _coworkPath;
		private static Task<string> _task;
		private static List<string> _logs;
		private static Application.LogCallback _logHandler;
		private static double _startTime;

		public static bool IsRunning
		{
			get { return _task != null; }
		}

		public static void Begin(string taskId, string coworkPath, Task<string> task, List<string> logs, Application.LogCallback logHandler)
		{
			_taskId = taskId;
			_coworkPath = coworkPath;
			_task = task;
			_logs = logs;
			_logHandler = logHandler;
			_startTime = EditorApplication.timeSinceStartup;

			SessionState.SetString(AsyncTaskKey, taskId);

			EditorApplication.update -= OnUpdate;
			EditorApplication.update += OnUpdate;
		}

		public static void Cancel()
		{
			if (!IsRunning)
			{
				return;
			}

			_logs.Add("Canceled by user");
			Finish("canceled", null);
			EditorUtility.RequestScriptReload();
		}

		private static void OnUpdate()
		{
			if (_task == null)
			{
				EditorApplication.update -= OnUpdate;
				return;
			}

			if (!_task.IsCompleted)
			{
				int timeoutSeconds = CoworkBridgeSettingsStore.GetAsyncTimeoutSeconds();
				double elapsed = EditorApplication.timeSinceStartup - _startTime;
				if (elapsed > timeoutSeconds)
				{
					_logs.Add("Async task exceeded timeout: " + timeoutSeconds + "s");
					Finish("timeout", null);
				}

				return;
			}

			if (_task.IsFaulted)
			{
				Exception inner = _task.Exception != null ? _task.Exception.GetBaseException() : null;
				_logs.Add("Runtime error: " + inner?.Message);
				_logs.Add(inner?.StackTrace);
				Finish("runtime_error", null);
				return;
			}

			if (_task.IsCanceled)
			{
				_logs.Add("Task was canceled");
				Finish("runtime_error", null);
				return;
			}

			Finish("success", _task.Result);
		}

		private static void Finish(string status, string returnValue)
		{
			EditorApplication.update -= OnUpdate;
			Application.logMessageReceived -= _logHandler;
			SessionState.EraseString(AsyncTaskKey);

			var result = new TaskResult
			{
				id = _taskId,
				status = status,
				logs = _logs,
				return_value = returnValue
			};
			ResultWriter.Write(result, _coworkPath);

			_taskId = null;
			_coworkPath = null;
			_task = null;
			_logs = null;
			_logHandler = null;
		}
	}
}
