using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using UnityEngine;
using UnityEditor;

namespace CoworkBridge
{
	public static class TaskRunner
	{
		public static void ExecuteTask(string taskId, string coworkPath)
		{
			Debug.Log("[CoworkBridge] Executing task: " + taskId);

			Type taskType = FindType(taskId);
			if (taskType == null)
			{
				WriteRuntimeError(taskId, coworkPath, new List<string> { "Class not found: " + taskId });
				return;
			}

			MethodInfo method = taskType.GetMethod("Run", BindingFlags.Public | BindingFlags.Static);
			if (method == null)
			{
				WriteRuntimeError(taskId, coworkPath, new List<string> { "Method Run not found in class " + taskId });
				return;
			}

			if (method.ReturnType != typeof(System.Threading.Tasks.Task<string>))
			{
				WriteRuntimeError(taskId, coworkPath, new List<string> { "Run must have signature: public static Task<string> Run()" });
				return;
			}

			var logs = new List<string>();

			Application.LogCallback logHandler = (message, stackTrace, type) =>
			{
				logs.Add(message);
			};

			Application.logMessageReceived += logHandler;

			System.Threading.Tasks.Task<string> task;
			try
			{
				task = (System.Threading.Tasks.Task<string>)method.Invoke(null, null);
			}
			catch (TargetInvocationException ex)
			{
				Application.logMessageReceived -= logHandler;
				logs.Add("Runtime error: " + ex.InnerException?.Message);
				logs.Add(ex.InnerException?.StackTrace);
				WriteRuntimeError(taskId, coworkPath, logs);
				return;
			}
			catch (Exception ex)
			{
				Application.logMessageReceived -= logHandler;
				logs.Add("Unexpected error: " + ex.Message);
				WriteRuntimeError(taskId, coworkPath, logs);
				return;
			}

			if (task == null)
			{
				Application.logMessageReceived -= logHandler;
				logs.Add("Run returned null Task");
				WriteRuntimeError(taskId, coworkPath, logs);
				return;
			}

			AsyncTaskWatcher.Begin(taskId, coworkPath, task, logs, logHandler);
		}

		private static void WriteRuntimeError(string taskId, string coworkPath, List<string> logs)
		{
			var result = new TaskResult
			{
				id = taskId,
				status = "runtime_error",
				logs = logs
			};
			ResultWriter.Write(result, coworkPath);
		}

		public static void HandleCompilerErrors(string taskId, List<CompilerError> errors, string coworkPath)
		{
			Debug.Log("[CoworkBridge] Compilation failed for task: " + taskId);

			string projectRoot = Path.GetDirectoryName(Application.dataPath);
			string taskScriptFullPath = Path.GetFullPath(Path.Combine(projectRoot, "Assets", "Editor", "CoworkBridge", taskId + ".cs"));
			bool hasForeignErrors = false;

			foreach (var error in errors)
			{
				if (string.IsNullOrEmpty(error.file))
				{
					continue;
				}

				string errorFileFullPath = Path.GetFullPath(error.file);
				if (!string.Equals(errorFileFullPath, taskScriptFullPath, StringComparison.OrdinalIgnoreCase))
				{
					hasForeignErrors = true;
					break;
				}
			}

			var result = new TaskResult
			{
				id = taskId,
				status = "compiler_error",
				compiler_errors = errors,
				foreign_errors = hasForeignErrors
			};

			ResultWriter.Write(result, coworkPath);
		}

		public static void HandlePendingErrors(string taskId, string errorsJson, string coworkPath)
		{
			var errorList = JsonUtility.FromJson<CompilerErrorList>(errorsJson);
			if (errorList != null && errorList.errors != null && errorList.errors.Count > 0)
			{
				HandleCompilerErrors(taskId, errorList.errors, coworkPath);
			}
			else
			{
				ExecuteTask(taskId, coworkPath);
			}
		}

		public static Type FindType(string className)
		{
			foreach (Assembly assembly in AppDomain.CurrentDomain.GetAssemblies())
			{
				Type type = assembly.GetType(className);
				if (type != null)
				{
					return type;
				}
			}

			foreach (Assembly assembly in AppDomain.CurrentDomain.GetAssemblies())
			{
				foreach (Type type in assembly.GetTypes())
				{
					if (type.Name == className)
					{
						return type;
					}
				}
			}

			return null;
		}
	}
}
