using System;
using System.Collections.Generic;
using UnityEditor;
using UnityEngine;

namespace AgentBridge.Ui
{
	// Applies a declarative node fragment onto a prefab subtree. Specified
	// properties are set, unspecified are left alone; children are synced by
	// name (created when missing), extra children are never removed.
	public static class UiNodeApplier
	{
		public static void Apply(GameObject root, string target, Dictionary<string, object> node, List<string> log)
		{
			Transform t;
			if (string.IsNullOrEmpty(target))
			{
				t = root.transform;
			}
			else
			{
				Transform existing = UiPath.Resolve(root.transform, target);
				if (existing != null)
				{
					t = existing;
				}
				else if (node.TryGetValue("prefab", out object prefabObj) && prefabObj is string prefabPath)
				{
					SplitLast(target, out string parentPath, out string name);
					Transform parent = UiPath.ResolveOrCreate(root.transform, parentPath, log);
					t = InstantiatePrefabChild(parent, name, prefabPath, log);
				}
				else
				{
					t = UiPath.ResolveOrCreate(root.transform, target, log);
				}
			}

			SyncNode(t.gameObject, node, root, log);
		}

		public static void SyncNode(GameObject go, Dictionary<string, object> node, GameObject root, List<string> log)
		{
			if (node.TryGetValue("prefab", out object prefabObj) && prefabObj is string prefabPath)
				VerifyPrefabInstance(go, prefabPath);

			if (node.TryGetValue("active", out object active))
				go.SetActive(UiValue.B(active));

			var rt = go.transform as RectTransform;

			if (node.TryGetValue("stretch", out object stretchObj) && stretchObj is Dictionary<string, object> stretch)
				ApplyStretch(rt, stretch);
			else if (node.TryGetValue("rect", out object rectObj) && rectObj is Dictionary<string, object> rect)
				ApplyRect(rt, rect);

			if (node.TryGetValue("index", out object index))
				go.transform.SetSiblingIndex(UiValue.I(index));

			if (node.TryGetValue("components", out object componentsObj) && componentsObj is IList<object> components)
			{
				foreach (object comp in components)
					UiComponentSync.Sync(go, (Dictionary<string, object>)comp, root, log);
			}

			if (node.TryGetValue("children", out object childrenObj) && childrenObj is IList<object> children)
			{
				foreach (object childObj in children)
				{
					var child = (Dictionary<string, object>)childObj;
					string name = (string)child["name"];
					Transform childT = FindDirectChild(go.transform, name);
					if (childT == null)
					{
						if (child.TryGetValue("prefab", out object cp) && cp is string childPrefab)
							childT = InstantiatePrefabChild(go.transform, name, childPrefab, log);
						else
							childT = CreateEmpty(go.transform, name, log);
					}

					SyncNode(childT.gameObject, child, root, log);
				}
			}
		}

		public static void Delete(GameObject root, string path, List<string> log)
		{
			Transform t = UiPath.Resolve(root.transform, path);
			if (t == null)
			{
				if (log != null)
					log.Add("Warning: delete target not found: '" + path + "'");
				return;
			}

			UnityEngine.Object.DestroyImmediate(t.gameObject);
			if (log != null)
				log.Add("Deleted node: " + path);
		}

		private static void ApplyStretch(RectTransform rt, Dictionary<string, object> stretch)
		{
			if (rt == null)
				throw new Exception("Node has no RectTransform; cannot apply stretch");

			float left = stretch.TryGetValue("left", out object l) ? UiValue.F(l) : 0f;
			float right = stretch.TryGetValue("right", out object r) ? UiValue.F(r) : 0f;
			float top = stretch.TryGetValue("top", out object t) ? UiValue.F(t) : 0f;
			float bottom = stretch.TryGetValue("bottom", out object b) ? UiValue.F(b) : 0f;

			rt.anchorMin = Vector2.zero;
			rt.anchorMax = Vector2.one;
			rt.offsetMin = new Vector2(left, bottom);
			rt.offsetMax = new Vector2(-right, -top);
		}

		private static void ApplyRect(RectTransform rt, Dictionary<string, object> rect)
		{
			if (rt == null)
				throw new Exception("Node has no RectTransform; cannot apply rect");

			if (rect.TryGetValue("anchorMin", out object anchorMin))
				rt.anchorMin = UiValue.V2(anchorMin);
			if (rect.TryGetValue("anchorMax", out object anchorMax))
				rt.anchorMax = UiValue.V2(anchorMax);
			if (rect.TryGetValue("pivot", out object pivot))
				rt.pivot = UiValue.V2(pivot);
			if (rect.TryGetValue("pos", out object pos))
				rt.anchoredPosition = UiValue.V2(pos);
			if (rect.TryGetValue("size", out object size))
				rt.sizeDelta = UiValue.V2(size);
			if (rect.TryGetValue("rotation", out object rotation))
				rt.localEulerAngles = new Vector3(0f, 0f, UiValue.F(rotation));
			if (rect.TryGetValue("scale", out object scale))
			{
				Vector2 s = UiValue.V2(scale);
				rt.localScale = new Vector3(s.x, s.y, 1f);
			}
		}

		private static void VerifyPrefabInstance(GameObject go, string prefabPath)
		{
			if (!PrefabUtility.IsAnyPrefabInstanceRoot(go))
				throw new Exception("Node '" + go.name + "' has 'prefab' but is not a prefab instance root; renaming into a prefab is not supported");

			string actual = PrefabUtility.GetPrefabAssetPathOfNearestInstanceRoot(go);
			if (!SamePath(actual, prefabPath))
				throw new Exception("Node '" + go.name + "' is an instance of '" + actual + "', not '" + prefabPath + "'");
		}

		private static Transform InstantiatePrefabChild(Transform parent, string name, string prefabPath, List<string> log)
		{
			var asset = AssetDatabase.LoadAssetAtPath<GameObject>(prefabPath);
			if (asset == null)
				throw new Exception("Prefab not found: " + prefabPath);

			var instance = (GameObject)PrefabUtility.InstantiatePrefab(asset);
			instance.transform.SetParent(parent, false);
			instance.name = name;
			if (log != null)
				log.Add("Instantiated prefab " + prefabPath + " as '" + name + "'");
			return instance.transform;
		}

		private static Transform CreateEmpty(Transform parent, string name, List<string> log)
		{
			var go = new GameObject(name, typeof(RectTransform));
			go.transform.SetParent(parent, false);
			if (log != null)
				log.Add("Created node: " + name);
			return go.transform;
		}

		private static Transform FindDirectChild(Transform parent, string name)
		{
			for (int i = 0; i < parent.childCount; i++)
			{
				Transform child = parent.GetChild(i);
				if (child.name == name)
					return child;
			}

			return null;
		}

		private static void SplitLast(string path, out string parentPath, out string name)
		{
			int slash = path.LastIndexOf('/');
			if (slash < 0)
			{
				parentPath = string.Empty;
				name = path;
				return;
			}

			parentPath = path.Substring(0, slash);
			name = path.Substring(slash + 1);
		}

		private static bool SamePath(string a, string b)
		{
			if (a == null || b == null)
				return false;
			return string.Equals(a.Replace('\\', '/'), b.Replace('\\', '/'), StringComparison.OrdinalIgnoreCase);
		}
	}
}
