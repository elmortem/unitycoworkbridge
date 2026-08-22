using System;
using System.Collections.Generic;
using UnityEditor;
using UnityEditor.Events;
using UnityEngine;
using UnityEngine.Events;
using UnityEngine.UI;

namespace AgentBridge.Ui
{
	// Collects object references and button wirings while nodes are applied and
	// resolves them in one pass afterwards, so a reference may point at a node
	// that is created later in the same task.
	public class UiRefQueue
	{
		private readonly List<UiRefEntry> _refs = new List<UiRefEntry>();
		private readonly List<UiWireEntry> _wires = new List<UiWireEntry>();

		public void AddRef(Component component, string property, string spec, GameObject relativeRoot)
		{
			_refs.Add(new UiRefEntry
			{
				Component = component,
				Property = property,
				Spec = spec,
				RelativeRoot = relativeRoot
			});
		}

		public void AddWire(Button button, IList<object> wires)
		{
			_wires.Add(new UiWireEntry
			{
				Button = button,
				Wires = wires
			});
		}

		public void Resolve(GameObject root, List<string> log)
		{
			foreach (UiRefEntry entry in _refs)
			{
				if (entry.Component == null)
					throw new Exception("Ref owner was destroyed before resolving '" + entry.Property + "'");

				var so = new SerializedObject(entry.Component);
				SerializedProperty prop = so.FindProperty(entry.Property) ?? so.FindProperty("m_" + entry.Property);
				if (prop == null)
					throw new Exception("Serialized property not found: " + entry.Property);

				object value = UiValue.RefValue(entry.RelativeRoot != null ? entry.RelativeRoot : root, entry.Spec);
				prop.objectReferenceValue = (UnityEngine.Object)value;
				so.ApplyModifiedPropertiesWithoutUndo();
			}

			foreach (UiWireEntry entry in _wires)
			{
				if (entry.Button == null)
					throw new Exception("Wire owner button was destroyed before resolving");

				Wire(entry.Button.onClick, entry.Wires, root);
			}
		}

		private static void Wire(UnityEvent onClick, IList<object> wires, GameObject root)
		{
			while (onClick.GetPersistentEventCount() > 0)
				UnityEventTools.RemovePersistentListener(onClick, 0);

			foreach (object entryObj in wires)
			{
				var wire = (Dictionary<string, object>)entryObj;
				string targetPath = wire.TryGetValue("target", out object t) ? (string)t : string.Empty;
				string typeName = (string)wire["type"];
				string method = (string)wire["method"];

				Transform node = string.IsNullOrEmpty(targetPath) ? root.transform : UiPath.Resolve(root.transform, targetPath);
				if (node == null)
					throw new Exception("Wire target node not found: '" + targetPath + "'");

				Type type = UiComponentTypes.Resolve(typeName);
				if (type == null)
					throw new Exception("Wire type not found: " + typeName);

				Component listener = node.GetComponent(type);
				if (listener == null)
					throw new Exception("Wire component '" + typeName + "' not found on '" + targetPath + "'");

				var action = (UnityAction)Delegate.CreateDelegate(typeof(UnityAction), listener, method);
				UnityEventTools.AddVoidPersistentListener(onClick, action);
			}
		}
	}
}
