Status: Выполнено

# ТДД: асинхронный контракт Run() в Cowork Bridge

## Тип

Новая фича в существующем коде с ломающим изменением контракта задач. Фокус — интеграция с текущим скан-циклом и reload-механикой, единый async-путь выполнения.

## Контракт

- Единственная поддерживаемая сигнатура задачи: `public static Task<string> Run()` (в шаблоне скилла — `async Task<string>`). Старая сигнатура `string Run()` больше не выполняется — мост пишет `runtime_error` с сообщением о требуемой сигнатуре.
- Мост вызывает `Run()`, не блокируя editor-поток, и пишет `result_<TaskId>.json` только после завершения возвращённого `Task`.
- Наблюдение завершения — поллинг `Task.IsCompleted` в `EditorApplication.update` отдельным наблюдателем `AsyncTaskWatcher`.
- Пока задача летит (in-flight), скан моста не подхватывает ни её повторно, ни следующие задачи. Очередь последовательная.
- Таймаут на стороне моста: настройка `AsyncTimeoutSeconds` в `ProjectSettings/CoworkBridge.json`, по умолчанию 300. По истечении мост пишет result со `status: "timeout"`, снимает in-flight и продолжает обрабатывать очередь. Незавершённый `Task` остаётся жить до ближайшего domain reload, мост его не трогает.
- Отмена человеком: пункт меню `Tools/Cowork Bridge/Cancel Running Task` — пишет result со `status: "canceled"` и вызывает `EditorUtility.RequestScriptReload()`, чтобы domain reload убил незавершённый `Task`.
- Domain reload во время полёта: задача авто-перезапускается после reload, если её `.cs` ещё лежит в папке моста. Если файл удалён — перезапуска нет.
- Новые значения `status` в `TaskResult`: `timeout`, `canceled`. Поля класса `TaskResult` не меняются.

## Область изменений

- `CoworkBridge/Editor/TaskRunner.cs` — переписывается `ExecuteTask`, добавляется хелпер.
- `CoworkBridge/Editor/AsyncTaskWatcher.cs` — новый файл.
- `CoworkBridge/Editor/CoworkBridge.cs` — in-flight guard в `OnEditorUpdate`, рестарт async-задачи в `Initialize`, очистка ключа в `Stop`, пункт меню Cancel.
- `CoworkBridge/Editor/CoworkBridgeSettings.cs` — поле `AsyncTimeoutSeconds`.
- `CoworkBridge/Editor/CoworkBridgeSettingsStore.cs` — метод `GetAsyncTimeoutSeconds`.
- `unity-bridge-plugin/skills/unity-bridge/SKILL.md` — шаблон и правила под новый контракт.
- `unity-bridge-plugin/unity-bridge-plugin.zip` — пересборка с обновлённым SKILL.md.

Не меняются: `ResultWriter`, `TaskResult`, `TaskCleaner`, `CoworkTestRunner`, `CoworkEditorWakeTimer`, весь UI-пайплайн (`Ui/*`, задачи `.ui.json` остаются синхронными), `wait-for-result.sh`.

## TaskRunner.cs

В `ExecuteTask` заменяется всё тело после поиска метода. Итоговый вид `ExecuteTask` и новый хелпер (`HandleCompilerErrors`, `HandlePendingErrors`, `FindType` не меняются):

```csharp
public static void ExecuteTask(string taskId, string coworkPath)
{
	Debug.Log("[CoworkBridge] Executing task: " + taskId);

	Type taskType = FindType(taskId);
	if (taskType == null)
	{
		WriteRuntimeError(taskId, coworkPath, new List<string> { "Class not found: " + taskId });
		return;
	}

	MethodInfo method = taskType.GetMethod("Run", BindingFlags.Public | BindingFlags.Static);
	if (method == null)
	{
		WriteRuntimeError(taskId, coworkPath, new List<string> { "Method Run not found in class " + taskId });
		return;
	}

	if (method.ReturnType != typeof(System.Threading.Tasks.Task<string>))
	{
		WriteRuntimeError(taskId, coworkPath, new List<string> { "Run must have signature: public static Task<string> Run()" });
		return;
	}

	var logs = new List<string>();

	Application.LogCallback logHandler = (message, stackTrace, type) =>
	{
		logs.Add(message);
	};

	Application.logMessageReceived += logHandler;

	System.Threading.Tasks.Task<string> task;
	try
	{
		task = (System.Threading.Tasks.Task<string>)method.Invoke(null, null);
	}
	catch (TargetInvocationException ex)
	{
		Application.logMessageReceived -= logHandler;
		logs.Add("Runtime error: " + ex.InnerException?.Message);
		logs.Add(ex.InnerException?.StackTrace);
		WriteRuntimeError(taskId, coworkPath, logs);
		return;
	}
	catch (Exception ex)
	{
		Application.logMessageReceived -= logHandler;
		logs.Add("Unexpected error: " + ex.Message);
		WriteRuntimeError(taskId, coworkPath, logs);
		return;
	}

	if (task == null)
	{
		Application.logMessageReceived -= logHandler;
		logs.Add("Run returned null Task");
		WriteRuntimeError(taskId, coworkPath, logs);
		return;
	}

	AsyncTaskWatcher.Begin(taskId, coworkPath, task, logs, logHandler);
}

private static void WriteRuntimeError(string taskId, string coworkPath, List<string> logs)
{
	var result = new TaskResult
	{
		id = taskId,
		status = "runtime_error",
		logs = logs
	};
	ResultWriter.Write(result, coworkPath);
}
```

## AsyncTaskWatcher.cs (новый файл)

```csharp
using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using UnityEngine;
using UnityEditor;

namespace CoworkBridge
{
	public static class AsyncTaskWatcher
	{
		public const string AsyncTaskKey = "CoworkBridge_AsyncTask";

		private static string _taskId;
		private static string _coworkPath;
		private static Task<string> _task;
		private static List<string> _logs;
		private static Application.LogCallback _logHandler;
		private static double _startTime;

		public static bool IsRunning
		{
			get { return _task != null; }
		}

		public static void Begin(string taskId, string coworkPath, Task<string> task, List<string> logs, Application.LogCallback logHandler)
		{
			_taskId = taskId;
			_coworkPath = coworkPath;
			_task = task;
			_logs = logs;
			_logHandler = logHandler;
			_startTime = EditorApplication.timeSinceStartup;

			SessionState.SetString(AsyncTaskKey, taskId);

			EditorApplication.update -= OnUpdate;
			EditorApplication.update += OnUpdate;
		}

		public static void Cancel()
		{
			if (!IsRunning)
			{
				return;
			}

			_logs.Add("Canceled by user");
			Finish("canceled", null);
			EditorUtility.RequestScriptReload();
		}

		private static void OnUpdate()
		{
			if (_task == null)
			{
				EditorApplication.update -= OnUpdate;
				return;
			}

			if (!_task.IsCompleted)
			{
				int timeoutSeconds = CoworkBridgeSettingsStore.GetAsyncTimeoutSeconds();
				double elapsed = EditorApplication.timeSinceStartup - _startTime;
				if (elapsed > timeoutSeconds)
				{
					_logs.Add("Async task exceeded timeout: " + timeoutSeconds + "s");
					Finish("timeout", null);
				}

				return;
			}

			if (_task.IsFaulted)
			{
				Exception inner = _task.Exception != null ? _task.Exception.GetBaseException() : null;
				_logs.Add("Runtime error: " + inner?.Message);
				_logs.Add(inner?.StackTrace);
				Finish("runtime_error", null);
				return;
			}

			if (_task.IsCanceled)
			{
				_logs.Add("Task was canceled");
				Finish("runtime_error", null);
				return;
			}

			Finish("success", _task.Result);
		}

		private static void Finish(string status, string returnValue)
		{
			EditorApplication.update -= OnUpdate;
			Application.logMessageReceived -= _logHandler;
			SessionState.EraseString(AsyncTaskKey);

			var result = new TaskResult
			{
				id = _taskId,
				status = status,
				logs = _logs,
				return_value = returnValue
			};
			ResultWriter.Write(result, _coworkPath);

			_taskId = null;
			_coworkPath = null;
			_task = null;
			_logs = null;
			_logHandler = null;
		}
	}
}
```

## CoworkBridge.cs

- В `OnEditorUpdate` сразу после строки `CoworkEditorWakeTimer.Start();` добавить:

```csharp
if (AsyncTaskWatcher.IsRunning)
{
	return;
}
```

- В `Initialize` после существующего блока с `pendingTaskId` добавить рестарт async-задачи:

```csharp
string asyncTaskId = SessionState.GetString(AsyncTaskWatcher.AsyncTaskKey, "");
if (!string.IsNullOrEmpty(asyncTaskId))
{
	SessionState.EraseString(AsyncTaskWatcher.AsyncTaskKey);
	string asyncScriptPath = Path.Combine(_coworkPath, asyncTaskId + ".cs");
	if (File.Exists(asyncScriptPath))
	{
		EditorApplication.delayCall += () => TaskRunner.ExecuteTask(asyncTaskId, _coworkPath);
	}
}
```

- В `Stop` рядом со строкой `SessionState.EraseString(PendingTaskKey);` добавить:

```csharp
SessionState.EraseString(AsyncTaskWatcher.AsyncTaskKey);
```

- Добавить пункт меню рядом с остальными:

```csharp
[MenuItem("Tools/Cowork Bridge/Cancel Running Task")]
public static void CancelRunningTask()
{
	AsyncTaskWatcher.Cancel();
}

[MenuItem("Tools/Cowork Bridge/Cancel Running Task", true)]
private static bool CancelRunningTaskValidate()
{
	return AsyncTaskWatcher.IsRunning;
}
```

## CoworkBridgeSettings.cs

```csharp
using System;

namespace CoworkBridge
{
	[Serializable]
	public class CoworkBridgeSettings
	{
		public bool Enabled;
		public int KeepCompletedCount = 10;
		public int AsyncTimeoutSeconds = 300;
	}
}
```

## CoworkBridgeSettingsStore.cs

Добавить метод по образцу `GetKeepCompletedCount`:

```csharp
public static int GetAsyncTimeoutSeconds()
{
	CoworkBridgeSettings settings = Load();
	if (settings.AsyncTimeoutSeconds <= 0)
	{
		return 300;
	}

	return settings.AsyncTimeoutSeconds;
}
```

## SKILL.md (unity-bridge-plugin/skills/unity-bridge/SKILL.md)

- Шаблон C# скрипта заменить на:

```csharp
using System.Threading.Tasks;
using UnityEngine;
using UnityEditor;

public static class Task_XXX
{
    public static async Task<string> Run()
    {
        // сгенерированный код
        return "описание результата";
    }
}
```

- Правило 1 заменить на: `public static class` с `public static async Task<string> Run()`. Метод обязан возвращать `Task<string>` — другие сигнатуры мост отклоняет с `runtime_error`.
- Добавить правило: внутри `Run()` разрешён `await` любых async API (пул потоков, `Task.Delay`, Unity async-операции) — мост пишет результат только после завершения задачи. Если `await` не нужен, метод всё равно объявляется `async Task<string>` (предупреждение CS1998 компиляцию не ломает).
- В раздел «Логика обработки ошибок» добавить два статуса:
  - `status == "timeout"` (в `result_<TaskName>.json`): задача превысила лимит моста `AsyncTimeoutSeconds` (по умолчанию 300 секунд, настройка в `ProjectSettings/CoworkBridge.json`). Очередь моста уже разблокирована. Для заведомо долгих задач заранее увеличить `AsyncTimeoutSeconds` и клиентский таймаут `wait-for-result.sh`. Файл упавшей задачи почини или удали, как при `runtime_error`.
  - `status == "canceled"`: задачу отменил человек через меню редактора. Сообщить пользователю и не перезапускать без его явного решения. Файл задачи удалить.
- В существующем разделе `status == "timeout"` (клиентском) уточнить, что таймаут может прийти двумя путями: JSON от `wait-for-result.sh` (мост не ответил вовсе) и `result_<TaskName>.json` от моста (задача выполнялась, но превысила `AsyncTimeoutSeconds`).
- Правило 6 дополнить: таски со статусами `timeout` и `canceled` — тоже «упавшие», их файлы нельзя оставлять висеть.

## Пересборка плагина

- Распаковать `unity-bridge-plugin/unity-bridge-plugin.zip` во временную папку (в архиве: `.claude-plugin/plugin.json`, `skills/unity-bridge/SKILL.md`, `skills/unity-ui/SKILL.md`).
- Заменить `skills/unity-bridge/SKILL.md` обновлённым файлом из `unity-bridge-plugin/skills/unity-bridge/SKILL.md`.
- Собрать архив заново с тем же составом и путями от корня архива, перезаписать `unity-bridge-plugin/unity-bridge-plugin.zip`.

## Критерии приёмки

- Задача со старой сигнатурой `string Run()` получает `runtime_error` с текстом `Run must have signature: public static Task<string> Run()`.
- Задача `async Task<string> Run()` с `await Task.Delay(3000)` и возвратом строки: result появляется только после завершения, `status == "success"`, `return_value` заполнен, логи из async-фазы присутствуют в `logs`.
- Задача, уходящая через `await Task.Run(...)` в пул потоков и затем обращающаяся к Unity API на editor-потоке, завершается `success` — сценарий async-генерации из проблемы воспроизводится и проходит.
- Исключение, брошенное после `await`, даёт `runtime_error` с сообщением и стектрейсом в `logs`.
- Задача, летящая дольше `AsyncTimeoutSeconds`, получает result со `status == "timeout"`; следующий таск из папки после этого выполняется без ручного вмешательства.
- Ручная рекомпиляция во время полёта задачи: после domain reload задача перезапускается, если её `.cs` на месте; при удалённом `.cs` перезапуска нет и мост продолжает обрабатывать очередь.
- `Tools → Cowork Bridge → Cancel Running Task` недоступен без летящей задачи; при летящей — по нажатию появляется result со `status == "canceled"` и происходит script reload.
- Пока async-задача летит, скан не запускает ни её повторно, ни новые таски; `.ui.json`-задачи и тест-раннер после завершения работают как раньше.
- Повторный `Cancel` и `Stop` моста при летящей задаче не бросают исключений.

## После выполнения

- Смени статус в начале документа на `Выполнено`.
- Уточни у заказчика, нужно ли обновить проектную документацию (README.md, Docs/UNITYCOWORK-template.md — там остался старый шаблон `string Run()`) под новый контракт.
