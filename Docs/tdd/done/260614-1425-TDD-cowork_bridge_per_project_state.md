Status: Выполнено

# TDD: Per-project состояние Cowork Bridge

## Проблема

`CoworkBridge` хранит флаг «мост включён» и id отложенной задачи в `EditorPrefs`:

- `CoworkBridge_Enabled` — включён ли мост
- `CoworkBridge_PendingTask` — id задачи, ожидающей обработки после domain reload

`EditorPrefs` — это глобальное хранилище на уровне пользователя и версии Unity (реестр Windows / plist на macOS), общее для всех проектов. Поэтому флаг, выставленный в одном проекте, виден во всех остальных проектах на той же машине. Это не позволяет включать и выключать мост отдельно в каждом проекте.

## Решение

- Флаг `Enabled` хранить в файле `ProjectSettings/CoworkBridge.json`. Файл коммитится в git, состояние общее для команды в рамках одного проекта.
- `PendingTask` хранить в `SessionState` — внутрипамятное хранилище на сессию Editor. Оно автоматически привязано к проекту (у каждого открытого проекта свой инстанс Editor), переживает domain reload (перекомпиляцию) и чистится при закрытии Editor, не оставляя устаревшего состояния.
- Старый глобальный ключ `CoworkBridge_Enabled` не трогать: не читать и не удалять, чтобы не ломать проекты со старой версией пакета на той же машине.

## Изменения по файлам

### Новый файл `CoworkBridge/Editor/CoworkBridgeSettings.cs`

Сериализуемая модель настроек, хранимых в файле.

```csharp
using System;

namespace CoworkBridge
{
	[Serializable]
	public class CoworkBridgeSettings
	{
		public bool Enabled;
	}
}
```

### Новый файл `CoworkBridge/Editor/CoworkBridgeSettingsStore.cs`

Статический класс — загрузка и сохранение настроек в `ProjectSettings/CoworkBridge.json`.

Путь к файлу строится от `Application.dataPath`: корень проекта — это родительская папка от `Assets`, файл лежит в `ProjectSettings`. Папка `ProjectSettings` в проекте Unity всегда существует, создавать её не нужно.

```csharp
using System.IO;
using UnityEngine;

namespace CoworkBridge
{
	public static class CoworkBridgeSettingsStore
	{
		private const string FileName = "CoworkBridge.json";

		public static bool IsEnabled()
		{
			CoworkBridgeSettings settings = Load();
			return settings.Enabled;
		}

		public static void SetEnabled(bool value)
		{
			CoworkBridgeSettings settings = Load();
			settings.Enabled = value;
			Save(settings);
		}

		private static CoworkBridgeSettings Load()
		{
			string path = GetSettingsPath();

			if (!File.Exists(path))
			{
				return new CoworkBridgeSettings();
			}

			string json = File.ReadAllText(path);
			CoworkBridgeSettings settings = JsonUtility.FromJson<CoworkBridgeSettings>(json);

			if (settings == null)
			{
				return new CoworkBridgeSettings();
			}

			return settings;
		}

		private static void Save(CoworkBridgeSettings settings)
		{
			string path = GetSettingsPath();
			string json = JsonUtility.ToJson(settings, true);
			File.WriteAllText(path, json);
		}

		private static string GetSettingsPath()
		{
			string projectRoot = Path.GetDirectoryName(Application.dataPath);
			string projectSettingsPath = Path.Combine(projectRoot, "ProjectSettings");
			return Path.Combine(projectSettingsPath, FileName);
		}
	}
}
```

### Правки в `CoworkBridge/Editor/CoworkBridge.cs`

#### Удалить константу ключа Enabled

Удалить строку:

```csharp
private const string EnabledKey = "CoworkBridge_Enabled";
```

Константу `PendingTaskKey` оставить — она будет использоваться как ключ `SessionState`.

#### Переключить `IsEnabled` / `SetEnabled` на файловое хранилище

Заменить тело двух приватных методов в конце класса. Сигнатуры и все места вызова не меняются.

Было:

```csharp
private static bool IsEnabled()
{
	return EditorPrefs.GetBool(EnabledKey, false);
}

private static void SetEnabled(bool value)
{
	EditorPrefs.SetBool(EnabledKey, value);
}
```

Стало:

```csharp
private static bool IsEnabled()
{
	return CoworkBridgeSettingsStore.IsEnabled();
}

private static void SetEnabled(bool value)
{
	CoworkBridgeSettingsStore.SetEnabled(value);
}
```

#### Перевести PendingTask с `EditorPrefs` на `SessionState`

`SessionState` находится в `UnityEditor`, который уже подключён в файле. Заменить все обращения к `PendingTaskKey` через `EditorPrefs` на `SessionState`:

- `EditorPrefs.GetString(PendingTaskKey, "")` → `SessionState.GetString(PendingTaskKey, "")`
- `EditorPrefs.SetString(PendingTaskKey, taskId)` → `SessionState.SetString(PendingTaskKey, taskId)`
- `EditorPrefs.DeleteKey(PendingTaskKey)` → `SessionState.EraseString(PendingTaskKey)`

Конкретные места:

- `Stop()`: `EditorPrefs.DeleteKey(PendingTaskKey);` → `SessionState.EraseString(PendingTaskKey);`
- `Initialize()`: `string pendingTaskId = EditorPrefs.GetString(PendingTaskKey, "");` → `string pendingTaskId = SessionState.GetString(PendingTaskKey, "");`
- `ProcessAfterReload()`: `EditorPrefs.DeleteKey(PendingTaskKey);` → `SessionState.EraseString(PendingTaskKey);`
- `OnAssemblyCompilationFinished()`: `string pendingTaskId = EditorPrefs.GetString(PendingTaskKey, "");` → `string pendingTaskId = SessionState.GetString(PendingTaskKey, "");`
- `OnCompilationFinished()`: `string pendingTaskId = EditorPrefs.GetString(PendingTaskKey, "");` → `string pendingTaskId = SessionState.GetString(PendingTaskKey, "");` и `EditorPrefs.DeleteKey(PendingTaskKey);` → `SessionState.EraseString(PendingTaskKey);`
- `OnEditorUpdate()`: `string pendingTaskId = EditorPrefs.GetString(PendingTaskKey, "");` → `string pendingTaskId = SessionState.GetString(PendingTaskKey, "");` и `EditorPrefs.SetString(PendingTaskKey, taskId);` → `SessionState.SetString(PendingTaskKey, taskId);`

После правок в `CoworkBridge.cs` не должно остаться обращений к `EditorPrefs`.

## Проверка

- В файле `CoworkBridge.cs` нет ни одного вхождения `EditorPrefs` и нет константы `EnabledKey`.
- Включить мост в проекте A (`Tools/Cowork Bridge/Start`), убедиться, что появился файл `ProjectSettings/CoworkBridge.json` с `"Enabled": true`.
- Открыть отдельный проект B с тем же пакетом: мост выключен, пункт меню `Start` активен, `Stop` неактивен.
- Выключить мост в проекте A (`Stop`), убедиться, что в `ProjectSettings/CoworkBridge.json` стало `"Enabled": false`.
- Запустить задачу через мост и убедиться, что обработка после перекомпиляции (domain reload) по-прежнему работает — `PendingTask` корректно переживает reload через `SessionState`.

---

После выполнения:
- Поменяй статус вверху документа на `Выполнено`.
- Уточни у заказчика, нужно ли обновить проектную документацию (README и т.п.), чтобы отразить переход на per-project хранение состояния.
