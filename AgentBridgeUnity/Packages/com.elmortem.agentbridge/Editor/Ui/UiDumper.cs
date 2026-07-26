using System;
using System.Collections.Generic;
using System.IO;
using TMPro;
using UnityEditor;
using UnityEngine;
using UnityEngine.UI;

namespace AgentBridge.Ui
{
	// Instantiates a prefab in a temporary stage (no render) and walks the tree,
	// emitting geometry, native component fields and object references of custom
	// components as JSON.
	public static class UiDumper
	{
		public static string Dump(string prefabPath, string outputPath)
		{
			UiStage stage = UiPrefabStage.Open(prefabPath, 1920, 1080, false);
			try
			{
				var root = DumpNode(stage, stage.Instance.transform, stage.Instance.transform);
				var doc = new Dictionary<string, object>
				{
					{ "prefab", prefabPath },
					{ "reference", new List<object> { 1920.0, 1080.0 } },
					{ "root", root }
				};

				File.WriteAllText(outputPath, UiJson.Write(doc));
				return outputPath;
			}
			finally
			{
				UiPrefabStage.Close(stage);
			}
		}

		private static Dictionary<string, object> DumpNode(UiStage stage, Transform t, Transform instanceRoot)
		{
			var node = new Dictionary<string, object>
			{
				{ "name", t.name },
				{ "path", UiPath.PathOf(t, instanceRoot) },
				{ "active", t.gameObject.activeSelf }
			};

			if (t is RectTransform rt)
			{
				UnityEngine.Rect sr = UiPrefabStage.ScreenRect(stage, rt);
				node["screenRect"] = new List<object> { sr.x, sr.y, sr.width, sr.height };
				node["rect"] = RectInfo(rt);
			}

			if (t != instanceRoot && PrefabUtility.IsAnyPrefabInstanceRoot(t.gameObject))
			{
				string assetPath = PrefabUtility.GetPrefabAssetPathOfNearestInstanceRoot(t.gameObject);
				if (!string.IsNullOrEmpty(assetPath))
					node["prefab"] = assetPath;
			}

			var components = new List<object>();
			foreach (var component in t.GetComponents<Component>())
			{
				var dumped = DumpComponent(component, instanceRoot);
				if (dumped != null)
					components.Add(dumped);
			}

			if (components.Count > 0)
				node["components"] = components;

			var children = new List<object>();
			for (int i = 0; i < t.childCount; i++)
				children.Add(DumpNode(stage, t.GetChild(i), instanceRoot));

			node["children"] = children;
			return node;
		}

		private static Dictionary<string, object> RectInfo(RectTransform rt)
		{
			return new Dictionary<string, object>
			{
				{ "anchorMin", new List<object> { rt.anchorMin.x, rt.anchorMin.y } },
				{ "anchorMax", new List<object> { rt.anchorMax.x, rt.anchorMax.y } },
				{ "pivot", new List<object> { rt.pivot.x, rt.pivot.y } },
				{ "pos", new List<object> { rt.anchoredPosition.x, rt.anchoredPosition.y } },
				{ "size", new List<object> { rt.sizeDelta.x, rt.sizeDelta.y } }
			};
		}

		private static Dictionary<string, object> DumpComponent(Component component, Transform instanceRoot)
		{
			if (component == null)
				return null;
			if (component is Transform || component is CanvasRenderer)
				return null;

			if (component is Image image)
			{
				return new Dictionary<string, object>
				{
					{ "type", "Image" },
					{ "sprite", AssetPathOf(image.sprite) },
					{ "color", "#" + ColorUtility.ToHtmlStringRGBA(image.color) },
					{ "imageType", image.type.ToString() },
					{ "raycast", image.raycastTarget }
				};
			}

			if (component is TextMeshProUGUI text)
			{
				return new Dictionary<string, object>
				{
					{ "type", "Text" },
					{ "text", text.text },
					{ "size", text.fontSize },
					{ "color", "#" + ColorUtility.ToHtmlStringRGBA(text.color) },
					{ "align", text.alignment.ToString() },
					{ "font", AssetPathOf(text.font) }
				};
			}

			if (component is Button button)
			{
				var wire = new List<object>();
				int count = button.onClick.GetPersistentEventCount();
				for (int i = 0; i < count; i++)
				{
					UnityEngine.Object target = button.onClick.GetPersistentTarget(i);
					wire.Add(new Dictionary<string, object>
					{
						{ "target", RefOf(target, instanceRoot) },
						{ "type", target != null ? target.GetType().Name : null },
						{ "method", button.onClick.GetPersistentMethodName(i) }
					});
				}

				return new Dictionary<string, object>
				{
					{ "type", "Button" },
					{ "targetGraphic", RefOf(button.targetGraphic, instanceRoot) },
					{ "wire", wire }
				};
			}

			return DumpGenericComponent(component, instanceRoot);
		}

		private static Dictionary<string, object> DumpGenericComponent(Component component, Transform instanceRoot)
		{
			var refs = new Dictionary<string, object>();
			var so = new SerializedObject(component);
			SerializedProperty prop = so.GetIterator();
			bool visible = prop.NextVisible(true);
			while (visible)
			{
				if (prop.propertyType == SerializedPropertyType.ObjectReference && prop.name != "m_Script")
				{
					string key = prop.name.StartsWith("m_") ? prop.name.Substring(2) : prop.name;
					refs[key] = RefOf(prop.objectReferenceValue, instanceRoot);
				}

				visible = prop.NextVisible(false);
			}

			return new Dictionary<string, object>
			{
				{ "type", component.GetType().Name },
				{ "refs", refs }
			};
		}

		private static object RefOf(UnityEngine.Object obj, Transform instanceRoot)
		{
			if (obj == null)
				return null;

			Transform tr = obj is GameObject go ? go.transform : (obj is Component c ? c.transform : null);
			if (tr != null && (tr == instanceRoot || tr.IsChildOf(instanceRoot)))
			{
				string path = UiPath.PathOf(tr, instanceRoot);
				return obj is Component ? path + "#" + obj.GetType().Name : path;
			}

			string assetRef = AssetPathOf(obj);
			if (assetRef != null)
				return "asset:" + assetRef;

			return "~external";
		}

		private static string AssetPathOf(UnityEngine.Object obj)
		{
			if (obj == null)
				return null;

			string path = AssetDatabase.GetAssetPath(obj);
			if (string.IsNullOrEmpty(path))
				return null;

			UnityEngine.Object main = AssetDatabase.LoadMainAssetAtPath(path);
			if (main != obj && !string.IsNullOrEmpty(obj.name))
				return path + "#" + obj.name;

			return path;
		}
	}
}
