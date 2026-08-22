using System;
using System.Collections.Generic;
using System.IO;
using UnityEditor;
using UnityEngine;

namespace AgentBridge.Ui
{
	// Executes a declarative <TaskId>.ui.json task: mutations (apply/delete) run
	// over the loaded prefab contents and are saved once, then read actions
	// (dump/shot) run over the saved asset. No C# compilation or domain reload.
	public static class UiTaskRunner
	{
		public static TaskResultData Execute(string payloadPath, TaskContext context)
		{
			Debug.Log("[AgentBridge] Executing UI task: " + context.Id);

			var logs = new List<string>();
			string status = "success";
			string returnValue = null;

			GameObject root = null;
			bool loadedContents = false;

			try
			{
				if (!File.Exists(payloadPath))
					throw new Exception("Task file not found: " + payloadPath);

				object parsed = UiJson.Parse(File.ReadAllText(payloadPath));
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

					var refs = new UiRefQueue();

					foreach (object actionObj in actions)
					{
						var action = (Dictionary<string, object>)actionObj;
						string kind = (string)action["action"];
						if (kind == "apply")
						{
							string target = action.TryGetValue("target", out object tv) ? (string)tv : string.Empty;
							var node = (Dictionary<string, object>)action["node"];
							UiNodeApplier.Apply(root, target, node, refs, logs);
							summary.Add("apply " + (string.IsNullOrEmpty(target) ? "<root>" : target));
						}
						else if (kind == "delete")
						{
							string path = (string)action["path"];
							UiNodeApplier.Delete(root, path, logs);
							summary.Add("delete " + path);
						}
					}

					refs.Resolve(root, logs);

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
						string output = UiTaskArtifacts.GetDumpPath(context);
						UiDumper.Dump(prefabPath, output);
						context.AddArtifact(output);
						summary.Add("dump -> " + output);
					}
					// "shot" is the pre-0.11 name, kept so an older skill still
					// works against a newer package.
					else if (kind == "uishot" || kind == "shot")
					{
						int width = action.TryGetValue("width", out object w) ? UiValue.I(w) : 1920;
						int height = action.TryGetValue("height", out object h) ? UiValue.I(h) : 1080;
						List<string> outline = ReadStringList(action, "outline");

						string outputName = action.TryGetValue("output", out object outputObj) && outputObj is string requestedOutput
							? requestedOutput
							: null;
						string output = UiTaskArtifacts.GetScreenshotPath(context, outputName);
						UiScreenshot.Shot(prefabPath, output, width, height, outline);
						context.AddArtifact(output);
						wrotePng = true;
						summary.Add("uishot -> " + output + " (" + width + "x" + height + ")");
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
			}

			return new TaskResultData
			{
				Status = status,
				ReturnValue = returnValue,
				Logs = logs
			};
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
