using System;
using System.Collections.Generic;
using TMPro;
using UnityEditor;
using UnityEngine;
using UnityEngine.UI;

namespace AgentBridge.Ui
{
	// Applies a single component fragment onto a node: matches by type
	// (existing component of that type is updated, otherwise added), then sets
	// native fields plus the generic set / ref blocks. Object references are not
	// resolved here — they go into the queue and are applied after the whole task.
	public static class UiComponentSync
	{
		// TMP moved from a bool to an enum independently of the editor version.
		// Resolve the public setter once, without a compile-time reference to either API:
		// recent TMP marks the old property Obsolete(error: true).
		private static readonly Action<TMP_Text, bool> SetWordWrapping = CreateWordWrappingSetter();

		private static Action<TMP_Text, bool> CreateWordWrappingSetter()
		{
			var mode = typeof(TMP_Text).GetProperty("textWrappingMode");
			if (mode != null)
			{
				object normal = Enum.Parse(mode.PropertyType, "Normal");
				object noWrap = Enum.Parse(mode.PropertyType, "NoWrap");
				return (text, wrap) => mode.SetValue(text, wrap ? normal : noWrap);
			}

			var legacy = typeof(TMP_Text).GetProperty("enableWordWrapping");
			if (legacy == null)
				throw new MissingMemberException(typeof(TMP_Text).FullName, "textWrappingMode / enableWordWrapping");
			return (text, wrap) => legacy.SetValue(text, wrap);
		}

		public static void Sync(GameObject go, Dictionary<string, object> comp, UiRefQueue refs, List<string> log)
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
				ApplyButton(button, comp, refs, added);

			ApplySetAndRef(component, comp, refs);
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
				SetWordWrapping(text, UiValue.B(wrap));

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

		private static void ApplyButton(Button button, Dictionary<string, object> comp, UiRefQueue refs, bool added)
		{
			if (added)
				button.transition = Selectable.Transition.None;

			if (comp.TryGetValue("targetGraphic", out object target))
				refs.AddRef(button, "TargetGraphic", target == null ? null : (string)target, button.gameObject);

			if (comp.TryGetValue("wire", out object wireValue) && wireValue is IList<object> wires)
				refs.AddWire(button, wires);
		}

		private static void ApplySetAndRef(Component component, Dictionary<string, object> comp, UiRefQueue refs)
		{
			bool hasSet = comp.TryGetValue("set", out object setValue) && setValue is Dictionary<string, object>;
			bool hasRef = comp.TryGetValue("ref", out object refValue) && refValue is Dictionary<string, object>;
			if (!hasSet && !hasRef)
				return;

			if (hasSet)
			{
				var so = new SerializedObject(component);
				foreach (var kv in (Dictionary<string, object>)setValue)
					UiValue.SetProperty(so, kv.Key, kv.Value);
				so.ApplyModifiedPropertiesWithoutUndo();
			}

			if (hasRef)
			{
				foreach (var kv in (Dictionary<string, object>)refValue)
					refs.AddRef(component, kv.Key, (string)kv.Value, null);
			}
		}

		private static Type ResolveComponentType(string typeName)
		{
			return UiComponentTypes.Resolve(typeName);
		}
	}
}
