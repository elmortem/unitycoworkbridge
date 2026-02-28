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
				var result = new TaskResult
				{
					id = taskId,
					status = "runtime_error",
					logs = new List<string> { "Class not found: " + taskId }
				};
				ResultWriter.Write(result, coworkPath);
				return;
			}

			MethodInfo method = taskType.GetMethod("Run", BindingFlags.Public | BindingFlags.Static);
			if (method == null)
			{
				var result = new TaskResult
				{
					id = taskId,
					status = "runtime_error",
					logs = new List<string> { "Method Run not found in class " + taskId }
				};
				ResultWriter.Write(result, coworkPath);
				return;
			}

			var logs = new List<string>();
			string returnValue = null;
			string status = "success";

			Application.LogCallback logHandler = (message, stackTrace, type) =>
			{
				logs.Add(message);
			};

			Application.logMessageReceived += logHandler;
			try
			{
				object resultObj = method.Invoke(null, null);
				if (resultObj != null)
				{
					returnValue = resultObj.ToString();
				}
			}
			catch (TargetInvocationException ex)
			{
				status = "runtime_error";
				logs.Add("Runtime error: " + ex.InnerException?.Message);
				logs.Add(ex.InnerException?.StackTrace);
			}
			catch (Exception ex)
			{
				status = "runtime_error";
				logs.Add("Unexpected error: " + ex.Message);
			}
			finally
			{
				Application.logMessageReceived -= logHandler;
			}

			var taskResult = new TaskResult
			{
				id = taskId,
				status = status,
				logs = logs,
				return_value = returnValue
			};

			ResultWriter.Write(taskResult, coworkPath);
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
