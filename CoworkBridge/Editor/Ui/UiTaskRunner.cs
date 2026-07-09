using System;
using System.Collections.Generic;
using System.IO;
using UnityEditor;
using UnityEngine;

namespace CoworkBridge.Ui
{
	// Executes a declarative Task_*.ui.json task: mutations (apply/delete) run
	// over the loaded prefab contents and are saved once, then read actions
	// (dump/shot) run over the saved asset. No C# compilation or domain reload.
	public static class UiTaskRunner
	{
		public static void Execute(string taskId, string coworkPath)
		{
			Debug.Log("[CoworkBridge] Executing UI task: " + taskId);

			var logs = new List<string>();
			string status = "success";
			string returnValue = null;

			Application.LogCallback logHandler = (message, stackTrace, type) => logs.Add(message);
			Application.logMessageReceived += logHandler;

			GameObject root = null;
			bool loadedContents = false;

			try
			{
				string taskPath = Path.Combine(coworkPath, taskId + ".ui.json");
				if (!File.Exists(taskPath))
					throw new Exception("Task file not found: " + taskPath);

				object parsed = UiJson.Parse(File.ReadAllText(taskPath));
				if (!(parsed is Dictionary<string, object> doc))
					throw new Exception("Task root is not a JSON object");

				if (!(doc.TryGetValue("prefab", out object prefabObj) && prefabObj is string prefabPath) || string.IsNullOrEmpty(prefabPath))
					throw new Exception("Task is missing 'prefab'");

				if (!(doc.TryGetValue("actions", out object actionsObj) && actionsObj is IList<object> actions))
					throw new Exception("Task is missing 'actions'");

				string projectRoot = Path.GetDirectoryName(Application.dataPath);
				var summary = new List<string>();

				bool hasApply = HasAction(actions, "apply");
				bool hasMutation = hasApply || HasAction(actions, "delete");
				bool prefabExists = AssetDatabase.LoadAssetAtPath<GameObject>(prefabPath) != null;

				if (hasMutation)
				{
					if (prefabExists)
					{
						root = PrefabUtility.LoadPrefabContents(prefabPath);
						loadedContents = true;
					}
					else if (hasApply)
					{
						root = CreateNewRoot(prefabPath);
						summary.Add("created prefab " + prefabPath);
					}
					else
					{
						throw new Exception("Prefab not found: " + prefabPath);
					}

					foreach (object actionObj in actions)
					{
						var action = (Dictionary<string, object>)actionObj;
						string kind = (string)action["action"];
						if (kind == "apply")
						{
							string target = action.TryGetValue("target", out object tv) ? (string)tv : string.Empty;
							var node = (Dictionary<string, object>)action["node"];
							UiNodeApplier.Apply(root, target, node, logs);
							summary.Add("apply " + (string.IsNullOrEmpty(target) ? "<root>" : target));
						}
						else if (kind == "delete")
						{
							string path = (string)action["path"];
							UiNodeApplier.Delete(root, path, logs);
							summary.Add("delete " + path);
						}
					}

					EnsureAssetFolder(projectRoot, prefabPath);
					PrefabUtility.SaveAsPrefabAsset(root, prefabPath);
					summary.Add("saved " + prefabPath);

					if (loadedContents)
						PrefabUtility.UnloadPrefabContents(root);
					else
						UnityEngine.Object.DestroyImmediate(root);
					root = null;
					loadedContents = false;

					AssetDatabase.SaveAssets();
				}
				else if (!prefabExists)
				{
					throw new Exception("Prefab not found: " + prefabPath);
				}

				bool wrotePng = false;
				foreach (object actionObj in actions)
				{
					var action = (Dictionary<string, object>)actionObj;
					string kind = (string)action["action"];
					if (kind == "dump")
					{
						string output = Path.Combine(coworkPath, "uidump_" + taskId + ".json");
						UiDumper.Dump(prefabPath, output);
						summary.Add("dump -> " + output);
					}
					else if (kind == "shot")
					{
						int width = action.TryGetValue("width", out object w) ? UiValue.I(w) : 1920;
						int height = action.TryGetValue("height", out object h) ? UiValue.I(h) : 1080;
						List<string> outline = ReadStringList(action, "outline");

						string output = ResolveShotOutput(action, taskId, coworkPath, projectRoot);
						UiScreenshot.Shot(prefabPath, output, width, height, outline);
						wrotePng = true;
						summary.Add("shot -> " + output + " (" + width + "x" + height + ")");
					}
				}

				if (wrotePng)
					AssetDatabase.Refresh();

				returnValue = string.Join("; ", summary);
			}
			catch (Exception ex)
			{
				status = "runtime_error";
				logs.Add("Runtime error: " + ex.Message);
				logs.Add(ex.StackTrace);
			}
			finally
			{
				if (root != null)
				{
					if (loadedContents)
						PrefabUtility.UnloadPrefabContents(root);
					else
						UnityEngine.Object.DestroyImmediate(root);
				}

				Application.logMessageReceived -= logHandler;
			}

			var result = new TaskResult
			{
				id = taskId,
				status = status,
				logs = logs,
				return_value = returnValue
			};
			ResultWriter.Write(result, coworkPath);
		}

		private static bool HasAction(IList<object> actions, string kind)
		{
			foreach (object actionObj in actions)
			{
				if (actionObj is Dictionary<string, object> action && action.TryGetValue("action", out object k) && (string)k == kind)
					return true;
			}

			return false;
		}

		private static GameObject CreateNewRoot(string prefabPath)
		{
			string name = Path.GetFileNameWithoutExtension(prefabPath);
			var go = new GameObject(name, typeof(RectTransform));
			var rt = (RectTransform)go.transform;
			rt.anchorMin = Vector2.zero;
			rt.anchorMax = Vector2.one;
			rt.offsetMin = Vector2.zero;
			rt.offsetMax = Vector2.zero;
			return go;
		}

		private static string ResolveShotOutput(Dictionary<string, object> action, string taskId, string coworkPath, string projectRoot)
		{
			if (!action.TryGetValue("output", out object outputObj) || !(outputObj is string output) || output.Length == 0)
				return Path.Combine(coworkPath, "shot_" + taskId + ".png");

			if (Path.IsPathRooted(output))
				return output;

			return Path.Combine(projectRoot, output);
		}

		private static List<string> ReadStringList(Dictionary<string, object> action, string key)
		{
			var result = new List<string>();
			if (action.TryGetValue(key, out object value) && value is IList<object> list)
			{
				foreach (object item in list)
					result.Add((string)item);
			}

			return result;
		}

		private static void EnsureAssetFolder(string projectRoot, string prefabPath)
		{
			string dir = Path.GetDirectoryName(Path.Combine(projectRoot, prefabPath));
			if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
			{
				Directory.CreateDirectory(dir);
				AssetDatabase.Refresh();
			}
		}
	}
}
