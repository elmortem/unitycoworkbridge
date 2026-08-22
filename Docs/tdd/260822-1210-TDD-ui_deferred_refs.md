Status: Выполнено

# Отложенный резолв ссылок в ui-тасках и резолв коротких имён типов

Проблема: в `ui`-таске `ref`/`wire`/`targetGraphic` резолвятся жадно в момент синка компонента, а `UiNodeApplier.SyncNode` синкает компоненты до создания детей — поэтому ссылка по абсолютному пути на узел, создаваемый позже в том же apply (или в следующем apply того же таска), падает с «Ref node not found». Вторая часть: короткое имя типа в `"путь#Тип"` и `wire.type` идёт через `DomainTypeResolver.FindType`, который берёт первый попавшийся тип с таким именем из любой сборки (например `UnityEngine.UIElements.Button` вместо `UnityEngine.UI.Button`), и резолв ломается. Решение: все ссылки собираются в очередь при обходе и применяются одним проходом после всех mutation-экшенов, перед сохранением префаба; резолв коротких имён для компонентов фильтруется по наследованию от `Component` с алиасами `Image`/`Text`/`Button`.

## References (not inlined)

- Конвенции кода: CLAUDE.md проекта (табы, типы в отдельных файлах, без комментариев).
- Текущее поведение: `Editor/Ui/UiTaskRunner.cs`, `Editor/Ui/UiNodeApplier.cs`, `Editor/Ui/UiComponentSync.cs`, `Editor/Ui/UiValue.cs`, `Editor/DomainTypeResolver.cs` — все в `AgentBridgeUnity/Packages/com.elmortem.agentbridge/`.
- Скилл: `unity-bridge-plugin/skills/unity-ui/SKILL.md`.

## Новый файл Editor/Ui/UiRefEntry.cs

```csharp
using UnityEngine;

namespace AgentBridge.Ui
{
	public class UiRefEntry
	{
		public Component Component;
		public string Property;
		public string Spec;
		public GameObject RelativeRoot;
	}
}
```

## Новый файл Editor/Ui/UiWireEntry.cs

```csharp
using System.Collections.Generic;
using UnityEngine.UI;

namespace AgentBridge.Ui
{
	public class UiWireEntry
	{
		public Button Button;
		public IList<object> Wires;
	}
}
```

## Новый файл Editor/Ui/UiRefQueue.cs

```csharp
using System;
using System.Collections.Generic;
using UnityEditor;
using UnityEditor.Events;
using UnityEngine;
using UnityEngine.Events;
using UnityEngine.UI;

namespace AgentBridge.Ui
{
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
```

## Новый файл Editor/Ui/UiComponentTypes.cs

```csharp
using System;

namespace AgentBridge.Ui
{
	public static class UiComponentTypes
	{
		public static Type Resolve(string typeName)
		{
			switch (typeName)
			{
				case "Image": return typeof(UnityEngine.UI.Image);
				case "Text": return typeof(TMPro.TextMeshProUGUI);
				case "Button": return typeof(UnityEngine.UI.Button);
			}

			return DomainTypeResolver.FindComponentType(typeName);
		}
	}
}
```

## Editor/DomainTypeResolver.cs

Добавить метод `FindComponentType` (существующий `FindType` не менять — его используют csharp-таски):

```csharp
		public static Type FindComponentType(string className)
		{
			foreach (Assembly assembly in AppDomain.CurrentDomain.GetAssemblies())
			{
				Type type = assembly.GetType(className);
				if (type != null && typeof(UnityEngine.Component).IsAssignableFrom(type))
				{
					return type;
				}
			}

			var matches = new List<Type>();
			foreach (Assembly assembly in AppDomain.CurrentDomain.GetAssemblies())
			{
				Type[] types;
				try
				{
					types = assembly.GetTypes();
				}
				catch
				{
					continue;
				}

				foreach (Type type in types)
				{
					if (type.Name == className && typeof(UnityEngine.Component).IsAssignableFrom(type) && !matches.Contains(type))
					{
						matches.Add(type);
					}
				}
			}

			if (matches.Count == 0)
			{
				return null;
			}

			if (matches.Count > 1)
			{
				var names = new List<string>();
				foreach (Type match in matches)
				{
					names.Add(match.FullName);
				}

				throw new Exception("Ambiguous component type '" + className + "': " + string.Join(", ", names) + ". Use the full type name.");
			}

			return matches[0];
		}
```

В начало файла добавить `using System.Collections.Generic;`.

## Editor/Ui/UiValue.cs

В `RefValue` заменить резолв типа:

```csharp
			Type type = UiComponentTypes.Resolve(typeName);
```

Остальное в методе без изменений.

## Editor/Ui/UiComponentSync.cs

- Сигнатура: `public static void Sync(GameObject go, Dictionary<string, object> comp, UiRefQueue refs, List<string> log)` — параметр `root` удаляется, после переноса `wire`/`ref` в очередь он внутри не нужен.
- `ApplyButton` получает `refs` вместо немедленного резолва:

```csharp
		private static void ApplyButton(Button button, Dictionary<string, object> comp, UiRefQueue refs, bool added)
		{
			if (added)
				button.transition = Selectable.Transition.None;

			if (comp.TryGetValue("targetGraphic", out object target))
				refs.AddRef(button, "TargetGraphic", target == null ? null : (string)target, button.gameObject);

			if (comp.TryGetValue("wire", out object wireValue) && wireValue is IList<object> wires)
				refs.AddWire(button, wires);
		}
```

- Вызов в `Sync`: `ApplyButton(button, comp, refs, added);`.
- Приватный метод `Wire` удалить (логика переехала в `UiRefQueue`); using `UnityEditor.Events`, `UnityEngine.Events` убрать, если больше не используются.
- `ApplySetAndRef` разделяется: `set` применяется на месте, `ref` уходит в очередь:

```csharp
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
```

- Вызов в `Sync`: `ApplySetAndRef(component, comp, refs);`.
- `ResolveComponentType` заменить на делегирование: `return UiComponentTypes.Resolve(typeName);` (switch с алиасами удалить — он теперь в `UiComponentTypes`).

## Editor/Ui/UiNodeApplier.cs

- Сигнатуры: `Apply(GameObject root, string target, Dictionary<string, object> node, UiRefQueue refs, List<string> log)` и `SyncNode(GameObject go, Dictionary<string, object> node, GameObject root, UiRefQueue refs, List<string> log)`.
- В `SyncNode` вызов компонентов: `UiComponentSync.Sync(go, (Dictionary<string, object>)comp, refs, log);`, рекурсия детей: `SyncNode(childT.gameObject, child, root, refs, log);`.
- `Delete` без изменений.

## Editor/Ui/UiTaskRunner.cs

В mutation-ветке создать очередь перед циклом экшенов и разрешить её после цикла, до сохранения:

```csharp
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
```

Любое исключение из `Resolve` попадает в существующий catch: таск завершается `runtime_error`, префаб не сохраняется.

## unity-bridge-plugin/skills/unity-ui/SKILL.md

В разделе «Компоненты» дополнить пункт про `ref` и `wire`:

- после описания `ref` добавить предложение: `Ссылки резолвятся после применения всех apply/delete таска — можно ссылаться на узлы, которые создаются позже в этом же таске.`
- в этот же раздел добавить пункт: `Имя типа в "#Тип" и wire.type можно писать коротким ("Button", "Text", "UI.MyView") — резолв ищет только наследников Component; при неоднозначности таск падает с перечислением полных имён, тогда укажи полное имя.`

## Проверка

Один ui-таск на несуществующий префаб: создаёт `Root/Panel` с кастомным view-компонентом, у которого `ref: { "Icon": "Panel/Icon#Image", "CloseButton": "Panel/Close#Button" }`, а узлы `Panel/Icon` (с Image) и `Panel/Close` (с Button) объявлены **после** — в children ниже по документу и во втором apply-экшене того же таска. Ожидание: `success`, в dump обе ссылки заполнены. Второй прогон: `wire` на компонент, объявленный следующим apply-экшеном → `success`. Третий: `ref` на несуществующий узел → `runtime_error`, префаб не создан/не изменён.

## После выполнения

- Смени статус в начале документа на `Выполнено`.
- Уточни у заказчика, нужно ли обновить документацию проекта под эти изменения.
