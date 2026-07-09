using System;
using System.Collections.Generic;
using System.Globalization;
using UnityEditor;
using UnityEngine;

namespace CoworkBridge.Ui
{
	// Converts dynamic JSON values (from UiJson) into Unity types and applies
	// them to SerializedProperties / resolves object references.
	public static class UiValue
	{
		public static Color Hex(string hex)
		{
			if (!ColorUtility.TryParseHtmlString(hex, out Color color))
				throw new Exception("Bad color '" + hex + "' (expected #RRGGBB or #RRGGBBAA)");
			return color;
		}

		public static Vector2 V2(object value)
		{
			var list = AsList(value, 2);
			return new Vector2(F(list[0]), F(list[1]));
		}

		public static Vector3 V3(object value)
		{
			var list = AsList(value, 3);
			return new Vector3(F(list[0]), F(list[1]), F(list[2]));
		}

		public static float F(object value)
		{
			return Convert.ToSingle(value, CultureInfo.InvariantCulture);
		}

		public static int I(object value)
		{
			return (int)Math.Round(Convert.ToDouble(value, CultureInfo.InvariantCulture));
		}

		public static bool B(object value)
		{
			return Convert.ToBoolean(value);
		}

		public static UnityEngine.Object Sprite(string path)
		{
			SplitAsset(path, out string assetPath, out string subName);
			if (subName == null)
			{
				var sprite = AssetDatabase.LoadAssetAtPath<Sprite>(assetPath);
				if (sprite == null)
					throw new Exception("Sprite not found: " + assetPath);
				return sprite;
			}

			foreach (var obj in AssetDatabase.LoadAllAssetsAtPath(assetPath))
			{
				if (obj is Sprite && obj.name == subName)
					return obj;
			}

			throw new Exception("Sub-sprite '" + subName + "' not found in " + assetPath);
		}

		public static void SetProperty(SerializedObject so, string name, object value)
		{
			SerializedProperty prop = so.FindProperty(name) ?? so.FindProperty("m_" + name);
			if (prop == null)
				throw new Exception("Serialized property not found: " + name);

			switch (prop.propertyType)
			{
				case SerializedPropertyType.Float:
					prop.floatValue = F(value);
					break;
				case SerializedPropertyType.Integer:
					prop.intValue = I(value);
					break;
				case SerializedPropertyType.Boolean:
					prop.boolValue = B(value);
					break;
				case SerializedPropertyType.String:
					prop.stringValue = value == null ? null : value.ToString();
					break;
				case SerializedPropertyType.Color:
					prop.colorValue = Hex((string)value);
					break;
				case SerializedPropertyType.Vector2:
					prop.vector2Value = V2(value);
					break;
				case SerializedPropertyType.Vector3:
					prop.vector3Value = V3(value);
					break;
				case SerializedPropertyType.Enum:
					prop.enumValueIndex = EnumIndex(prop, value);
					break;
				default:
					throw new Exception("Unsupported property type " + prop.propertyType + " for '" + name + "'");
			}
		}

		public static object RefValue(GameObject root, string spec)
		{
			if (spec == null)
				return null;

			if (spec.Length == 0)
				return root;

			if (spec.StartsWith("asset:"))
			{
				string raw = spec.Substring("asset:".Length);
				SplitAsset(raw, out string assetPath, out string subName);
				if (subName == null)
				{
					var asset = AssetDatabase.LoadAssetAtPath<UnityEngine.Object>(assetPath);
					if (asset == null)
						throw new Exception("Asset not found: " + assetPath);
					return asset;
				}

				foreach (var obj in AssetDatabase.LoadAllAssetsAtPath(assetPath))
				{
					if (obj != null && obj.name == subName)
						return obj;
				}

				throw new Exception("Sub-asset '" + subName + "' not found in " + assetPath);
			}

			int hash = spec.IndexOf('#');
			string nodePath = hash < 0 ? spec : spec.Substring(0, hash);
			string typeName = hash < 0 ? null : spec.Substring(hash + 1);

			Transform node = nodePath.Length == 0 ? root.transform : UiPath.Resolve(root.transform, nodePath);
			if (node == null)
				throw new Exception("Ref node not found: '" + nodePath + "'");

			if (typeName == null)
				return node.gameObject;

			Type type = TaskRunner.FindType(typeName);
			if (type == null)
				throw new Exception("Ref type not found: " + typeName);

			Component component = node.GetComponent(type);
			if (component == null)
				throw new Exception("Ref component '" + typeName + "' not found on '" + nodePath + "'");

			return component;
		}

		private static int EnumIndex(SerializedProperty prop, object value)
		{
			if (value is string name)
			{
				string[] names = prop.enumNames;
				for (int i = 0; i < names.Length; i++)
				{
					if (string.Equals(names[i], name, StringComparison.OrdinalIgnoreCase))
						return i;
				}

				throw new Exception("Enum value '" + name + "' not found for '" + prop.name + "'");
			}

			return I(value);
		}

		private static List<object> AsList(object value, int count)
		{
			if (!(value is IList<object> list) || list.Count < count)
				throw new Exception("Expected array of " + count + " numbers");
			return new List<object>(list);
		}

		private static void SplitAsset(string path, out string assetPath, out string subName)
		{
			int hash = path.IndexOf('#');
			if (hash < 0)
			{
				assetPath = path;
				subName = null;
				return;
			}

			assetPath = path.Substring(0, hash);
			subName = path.Substring(hash + 1);
		}
	}
}
