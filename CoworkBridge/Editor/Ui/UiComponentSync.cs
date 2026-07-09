using System;
using System.Collections.Generic;
using TMPro;
using UnityEditor;
using UnityEditor.Events;
using UnityEngine;
using UnityEngine.Events;
using UnityEngine.UI;

namespace CoworkBridge.Ui
{
	// Applies a single component fragment onto a node: matches by type
	// (existing component of that type is updated, otherwise added), then sets
	// native fields plus the generic set / ref blocks.
	public static class UiComponentSync
	{
		public static void Sync(GameObject go, Dictionary<string, object> comp, GameObject root, List<string> log)
		{
			string typeName = (string)comp["type"];
			Type type = ResolveComponentType(typeName);
			if (type == null)
				throw new Exception("Component type not found: " + typeName);

			Component component = go.GetComponent(type);
			bool added = component == null;
			if (added)
			{
				component = go.AddComponent(type);
				if (log != null)
					log.Add("Added component " + typeName + " to " + go.name);
			}

			if (component is Image image)
				ApplyImage(image, comp);
			else if (component is TextMeshProUGUI text)
				ApplyText(text, comp, added);
			else if (component is Button button)
				ApplyButton(button, comp, root, added);

			ApplySetAndRef(component, comp, root);
		}

		private static void ApplyImage(Image image, Dictionary<string, object> comp)
		{
			if (comp.TryGetValue("sprite", out object sprite))
				image.sprite = sprite == null ? null : (Sprite)UiValue.Sprite((string)sprite);
			if (comp.TryGetValue("color", out object color))
				image.color = UiValue.Hex((string)color);
			if (comp.TryGetValue("imageType", out object imageType))
				image.type = (Image.Type)Enum.Parse(typeof(Image.Type), (string)imageType, true);
			if (comp.TryGetValue("raycast", out object raycast))
				image.raycastTarget = UiValue.B(raycast);
			if (comp.TryGetValue("fillCenter", out object fillCenter))
				image.fillCenter = UiValue.B(fillCenter);
			if (comp.TryGetValue("ppuMultiplier", out object ppu))
				image.pixelsPerUnitMultiplier = UiValue.F(ppu);
		}

		private static void ApplyText(TextMeshProUGUI text, Dictionary<string, object> comp, bool added)
		{
			if (added)
				text.raycastTarget = false;

			if (comp.TryGetValue("text", out object value))
				text.text = value == null ? string.Empty : value.ToString();
			if (comp.TryGetValue("size", out object size))
				text.fontSize = UiValue.F(size);
			if (comp.TryGetValue("color", out object color))
				text.color = UiValue.Hex((string)color);
			if (comp.TryGetValue("align", out object align))
				text.alignment = (TextAlignmentOptions)Enum.Parse(typeof(TextAlignmentOptions), (string)align, true);
			if (comp.TryGetValue("wrap", out object wrap))
				text.enableWordWrapping = UiValue.B(wrap);

			if (comp.TryGetValue("font", out object font))
			{
				if (font == null)
				{
					text.font = TMP_Settings.defaultFontAsset;
				}
				else
				{
					var asset = AssetDatabase.LoadAssetAtPath<TMP_FontAsset>((string)font);
					if (asset == null)
						throw new Exception("TMP font asset not found: " + font);
					text.font = asset;
				}
			}
			else if (added && text.font == null)
			{
				text.font = TMP_Settings.defaultFontAsset;
			}
		}

		private static void ApplyButton(Button button, Dictionary<string, object> comp, GameObject root, bool added)
		{
			if (added)
				button.transition = Selectable.Transition.None;

			if (comp.TryGetValue("targetGraphic", out object target))
				button.targetGraphic = target == null ? null : (Graphic)UiValue.RefValue(button.gameObject, (string)target);

			if (comp.TryGetValue("wire", out object wireValue) && wireValue is IList<object> wires)
				Wire(button.onClick, wires, root);
		}

		private static void Wire(UnityEvent onClick, IList<object> wires, GameObject root)
		{
			while (onClick.GetPersistentEventCount() > 0)
				UnityEventTools.RemovePersistentListener(onClick, 0);

			foreach (object entry in wires)
			{
				var wire = (Dictionary<string, object>)entry;
				string targetPath = wire.TryGetValue("target", out object t) ? (string)t : string.Empty;
				string typeName = (string)wire["type"];
				string method = (string)wire["method"];

				Transform node = string.IsNullOrEmpty(targetPath) ? root.transform : UiPath.Resolve(root.transform, targetPath);
				if (node == null)
					throw new Exception("Wire target node not found: '" + targetPath + "'");

				Type type = TaskRunner.FindType(typeName);
				if (type == null)
					throw new Exception("Wire type not found: " + typeName);

				Component listener = node.GetComponent(type);
				if (listener == null)
					throw new Exception("Wire component '" + typeName + "' not found on '" + targetPath + "'");

				var action = (UnityAction)Delegate.CreateDelegate(typeof(UnityAction), listener, method);
				UnityEventTools.AddVoidPersistentListener(onClick, action);
			}
		}

		private static void ApplySetAndRef(Component component, Dictionary<string, object> comp, GameObject root)
		{
			bool hasSet = comp.TryGetValue("set", out object setValue) && setValue is Dictionary<string, object>;
			bool hasRef = comp.TryGetValue("ref", out object refValue) && refValue is Dictionary<string, object>;
			if (!hasSet && !hasRef)
				return;

			var so = new SerializedObject(component);

			if (hasSet)
			{
				foreach (var kv in (Dictionary<string, object>)setValue)
					UiValue.SetProperty(so, kv.Key, kv.Value);
			}

			if (hasRef)
			{
				foreach (var kv in (Dictionary<string, object>)refValue)
				{
					SerializedProperty prop = so.FindProperty(kv.Key) ?? so.FindProperty("m_" + kv.Key);
					if (prop == null)
						throw new Exception("Serialized property not found: " + kv.Key);
					prop.objectReferenceValue = (UnityEngine.Object)UiValue.RefValue(root, (string)kv.Value);
				}
			}

			so.ApplyModifiedPropertiesWithoutUndo();
		}

		private static Type ResolveComponentType(string typeName)
		{
			switch (typeName)
			{
				case "Image": return typeof(Image);
				case "Text": return typeof(TextMeshProUGUI);
				case "Button": return typeof(Button);
			}

			return TaskRunner.FindType(typeName);
		}
	}
}
