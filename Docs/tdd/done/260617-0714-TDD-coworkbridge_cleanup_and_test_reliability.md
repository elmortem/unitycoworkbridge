Status: Выполнено

# TDD: Очистка завершённых тасков и надёжный прогон тестов в Cowork Bridge

## Проблема

Три связанные проблемы в `CoworkBridge`.

1. Очистка завершённых тасков есть только руками — через меню `Tools/Cowork Bridge/Clean Completed` и `Clean All`. Программного механизма (авто-очистки старых тасков и команды от агента) нет. Папка `Assets/Editor/CoworkBridge` бесконтрольно растёт.

2. Файлы результатов тестов `testresult_<TaskName>.json` и `testresult_<TaskName>.done` не удаляются вообще никогда. Метод удаления (`DeleteTaskFiles` в `CoworkBridge.cs`) чистит `.cs`, `result_*.json`, `result_*.done` и `pending_errors_*.json`, но про `testresult_*` не знает. Даже ручной `Clean All` оставляет их в папке.

3. Прогон PlayMode-тестов идёт криво и падает с ошибками:

```
InvalidOperationException: This cannot be used during play mode.
... EditorSceneManager.SaveCurrentModifiedScenesIfUserWantsTo ...
... SaveModifiedSceneTask ...
Too many instant steps in test execution mode: Error. Current task ExitPlayModeTask.
Exception: Playmode tests were aborted because the player was stopped.
```

Причина: по текущему шаблону (`UNITYCOWORK.md`) каждая тестовая задача регистрирует **временный** `ICallbacks` внутри своего `Run()`. PlayMode-прогон входит в Play Mode, домен перезагружается, разовый таск и его колбэк уничтожаются — прогон рвётся на полпути (плеер останавливается, шаги зацикливаются). Дополнительно, если на старте прогона редактор уже находится в Play Mode (остаток от предыдущего сломанного прогона), `SaveModifiedSceneTask` падает с «This cannot be used during play mode».

### Как накопление файлов ломает вотчер

Таск-скрипты лежат в `Assets/Editor/CoworkBridge` и компилируются в предопределённую сборку `Assembly-CSharp-Editor`. Чем больше файлов в папке, тем хуже работает мост, потому что:

- каждый новый таск вызывает `RequestScriptCompilation()` и перекомпилирует **все** накопленные `.cs` — время компиляции растёт линейно;
- каждый `AssetDatabase.Refresh()` импортирует все накопленные `result_*`/`testresult_*` как ассеты — импорт замедляется;
- скан `OnEditorUpdate` раз в секунду делает `Directory.GetFiles` и сортировку по времени создания по всем файлам.

Отдельной перестройки вотчера не требуется: проблема — в накоплении. Ограничение размера папки авто-очисткой (механизм 1) убирает все три эффекта. asmdef-изоляция и quarantine битых тасков в этой задаче **не используются** — они добавляют больше сложности, чем решают.

## Решение

### Очистка (механизмы 1 и 2)

- Вся логика удаления выносится в новый статический класс `TaskCleaner`. Метод удаления файлов таска расширяется на `testresult_*` (закрывает проблему 2).
- Авто-очистка: Bridge на простое (когда нет ожидающих тасков) держит последние `N` успешно завершённых тасков (по умолчанию `N = 10`), удаляя более старые успешные вместе со всеми их файлами. `N` хранится в настройках проекта (`ProjectSettings/CoworkBridge.json`).
- Команда от агента: агент кладёт пустой файл `Assets/Editor/CoworkBridge/clean.command`. Bridge на следующем скане удаляет **все** успешно завершённые таски (игнорируя `N`) и удаляет сам командный файл.
- Чистятся только **успешные** таски (`status == "success"`). Упавшие (`compiler_error` / `runtime_error`) авто-очистка не трогает — их чинит или удаляет агент (правило в скилле). Если агент не справился, вмешивается человек.
- Меню `Clean Completed` / `Clean All` остаются, их тела переключаются на `TaskCleaner`.

### Надёжный прогон тестов (механизм 3)

- Оркестрация прогона выносится из таска в Bridge. Новый статический класс `CoworkTestRunner` с `[InitializeOnLoad]` регистрирует **персистентный** `ICallbacks` на каждый domain reload — колбэк переживает вход/выход из Play Mode, и результат прогона записывается даже после перезагрузки домена.
- Таск больше не регистрирует колбэки и не ссылается на тест-фреймворк. Он лишь вызывает `CoworkBridge.CoworkTestRunner.RequestRun(...)` со строковыми параметрами. Файлы `testresult_*` пишет сам Bridge.
- Перед стартом: если редактор уже в Play Mode — прогон не стартует, пишется `testresult` с `aborted: true` и сообщением выйти из Play Mode. Перед PlayMode-прогоном открытые сцены сохраняются (`EditorSceneManager.SaveOpenScenes()`), чтобы `SaveModifiedSceneTask` не падал и не показывал интерактивный диалог.
- Решение работает при любой настройке Enter Play Mode (Reload Domain включён или выключен), т.к. колбэки персистентные.

## Изменения по файлам

### Новый файл `CoworkBridge/Editor/TaskCleaner.cs`

Вся логика очистки. `DeleteTaskFiles` теперь удаляет и `testresult_*`.

```csharp
using System.Collections.Generic;
using System.IO;
using UnityEngine;
using UnityEditor;

namespace CoworkBridge
{
	public static class TaskCleaner
	{
		public static void TrimCompleted(string coworkPath, int keepCount)
		{
			string[] csFiles = Directory.GetFiles(coworkPath, "*.cs");
			if (csFiles.Length <= keepCount)
			{
				return;
			}

			List<string> successful = GetSuccessfulTaskIds(coworkPath);
			if (successful.Count <= keepCount)
			{
				return;
			}

			int removeCount = successful.Count - keepCount;
			for (int i = 0; i < removeCount; i++)
			{
				DeleteTaskFiles(coworkPath, successful[i]);
			}

			AssetDatabase.Refresh();
			Debug.Log("[CoworkBridge] Trimmed " + removeCount + " completed tasks.");
		}

		public static void CleanAllSuccessful(string coworkPath)
		{
			List<string> successful = GetSuccessfulTaskIds(coworkPath);
			foreach (string taskId in successful)
			{
				DeleteTaskFiles(coworkPath, taskId);
			}

			AssetDatabase.Refresh();
			Debug.Log("[CoworkBridge] Cleaned " + successful.Count + " successful tasks.");
		}

		public static void CleanCompleted(string coworkPath)
		{
			int count = 0;
			foreach (string csFile in Directory.GetFiles(coworkPath, "*.cs"))
			{
				string taskId = Path.GetFileNameWithoutExtension(csFile);
				string donePath = Path.Combine(coworkPath, "result_" + taskId + ".done");

				if (File.Exists(donePath))
				{
					DeleteTaskFiles(coworkPath, taskId);
					count++;
				}
			}

			AssetDatabase.Refresh();
			Debug.Log("[CoworkBridge] Cleaned " + count + " completed tasks.");
		}

		public static void CleanAll(string coworkPath)
		{
			int count = 0;
			foreach (string csFile in Directory.GetFiles(coworkPath, "*.cs"))
			{
				string taskId = Path.GetFileNameWithoutExtension(csFile);
				DeleteTaskFiles(coworkPath, taskId);
				count++;
			}

			AssetDatabase.Refresh();
			Debug.Log("[CoworkBridge] Cleaned " + count + " tasks.");
		}

		public static void DeleteTaskFiles(string coworkPath, string taskId)
		{
			DeleteFile(Path.Combine(coworkPath, taskId + ".cs"));
			DeleteFile(Path.Combine(coworkPath, "result_" + taskId + ".json"));
			DeleteFile(Path.Combine(coworkPath, "result_" + taskId + ".done"));
			DeleteFile(Path.Combine(coworkPath, "pending_errors_" + taskId + ".json"));
			DeleteFile(Path.Combine(coworkPath, "testresult_" + taskId + ".json"));
			DeleteFile(Path.Combine(coworkPath, "testresult_" + taskId + ".done"));
		}

		private static List<string> GetSuccessfulTaskIds(string coworkPath)
		{
			List<string> files = new List<string>();
			foreach (string csFile in Directory.GetFiles(coworkPath, "*.cs"))
			{
				string taskId = Path.GetFileNameWithoutExtension(csFile);
				if (IsSuccessful(coworkPath, taskId))
				{
					files.Add(csFile);
				}
			}

			files.Sort((a, b) => File.GetCreationTimeUtc(a).CompareTo(File.GetCreationTimeUtc(b)));

			List<string> ids = new List<string>();
			foreach (string csFile in files)
			{
				ids.Add(Path.GetFileNameWithoutExtension(csFile));
			}

			return ids;
		}

		private static bool IsSuccessful(string coworkPath, string taskId)
		{
			string donePath = Path.Combine(coworkPath, "result_" + taskId + ".done");
			if (!File.Exists(donePath))
			{
				return false;
			}

			string resultPath = Path.Combine(coworkPath, "result_" + taskId + ".json");
			if (!File.Exists(resultPath))
			{
				return false;
			}

			string json = File.ReadAllText(resultPath);
			TaskResult result = JsonUtility.FromJson<TaskResult>(json);
			if (result == null)
			{
				return false;
			}

			return result.status == "success";
		}

		private static void DeleteFile(string path)
		{
			if (File.Exists(path))
			{
				File.Delete(path);
			}
		}
	}
}
```

### Новые файлы DTO результата теста

Типы выносятся в отдельные файлы. Поля — в нижнем регистре, как у существующего `TaskResult` (это контракт JSON, который читает агент).

#### `CoworkBridge/Editor/TestRunResult.cs`

```csharp
using System;
using System.Collections.Generic;

namespace CoworkBridge
{
	[Serializable]
	public class TestRunResult
	{
		public int passed;
		public int failed;
		public int skipped;
		public int inconclusive;
		public int total;
		public double duration;
		public bool aborted;
		public string message;
		public List<TestFailure> failures = new List<TestFailure>();
	}
}
```

#### `CoworkBridge/Editor/TestFailure.cs`

```csharp
using System;

namespace CoworkBridge
{
	[Serializable]
	public class TestFailure
	{
		public string name;
		public string message;
		public string stacktrace;
	}
}
```

### Новый файл `CoworkBridge/Editor/CoworkTestRunner.cs`

Персистентная оркестрация тестового прогона. `[InitializeOnLoad]` гарантирует перерегистрацию колбэков после каждого domain reload. `id` текущего прогона хранится в `SessionState` и переживает reload.

```csharp
using System.Collections.Generic;
using System.IO;
using UnityEngine;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEditor.TestTools.TestRunner.Api;

namespace CoworkBridge
{
	[InitializeOnLoad]
	public static class CoworkTestRunner
	{
		private const string TestTaskKey = "CoworkBridge_TestTask";
		private static TestRunnerApi _api;

		static CoworkTestRunner()
		{
			_api = ScriptableObject.CreateInstance<TestRunnerApi>();
			_api.RegisterCallbacks(new TestCallbacks());
		}

		public static string RequestRun(string taskId, string testMode, string[] assemblyNames, string[] testNames, string[] categoryNames)
		{
			if (EditorApplication.isPlaying)
			{
				WriteAborted(taskId, "Editor is in play mode. Exit play mode and re-run the test task.");
				return "Test run aborted: editor in play mode";
			}

			TestMode mode = ParseMode(testMode);

			if (mode == TestMode.PlayMode)
			{
				EditorSceneManager.SaveOpenScenes();
			}

			Filter filter = new Filter { testMode = mode };
			if (assemblyNames != null && assemblyNames.Length > 0)
			{
				filter.assemblyNames = assemblyNames;
			}
			if (testNames != null && testNames.Length > 0)
			{
				filter.testNames = testNames;
			}
			if (categoryNames != null && categoryNames.Length > 0)
			{
				filter.categoryNames = categoryNames;
			}

			SessionState.SetString(TestTaskKey, taskId);

			TestRunnerApi api = ScriptableObject.CreateInstance<TestRunnerApi>();
			api.Execute(new ExecutionSettings(filter));
			return "Test run started";
		}

		private static TestMode ParseMode(string testMode)
		{
			if (testMode == "PlayMode")
			{
				return TestMode.PlayMode;
			}

			return TestMode.EditMode;
		}

		private static void WriteAborted(string taskId, string message)
		{
			TestRunResult run = new TestRunResult
			{
				aborted = true,
				message = message
			};
			WriteResult(taskId, run);
		}

		private static void WriteResult(string taskId, TestRunResult run)
		{
			string dir = Path.Combine(Application.dataPath, "Editor", "CoworkBridge");
			string jsonPath = Path.Combine(dir, "testresult_" + taskId + ".json");
			string donePath = Path.Combine(dir, "testresult_" + taskId + ".done");

			File.WriteAllText(jsonPath, JsonUtility.ToJson(run, true));
			File.WriteAllText(donePath, "");
		}

		private class TestCallbacks : ICallbacks
		{
			public void RunStarted(ITestAdaptor testsToRun)
			{
			}

			public void TestStarted(ITestAdaptor test)
			{
			}

			public void TestFinished(ITestResultAdaptor result)
			{
			}

			public void RunFinished(ITestResultAdaptor result)
			{
				string taskId = SessionState.GetString(TestTaskKey, "");
				if (string.IsNullOrEmpty(taskId))
				{
					return;
				}

				SessionState.EraseString(TestTaskKey);

				TestRunResult run = new TestRunResult
				{
					passed = result.PassCount,
					failed = result.FailCount,
					skipped = result.SkipCount,
					inconclusive = result.InconclusiveCount,
					total = result.PassCount + result.FailCount + result.SkipCount + result.InconclusiveCount,
					duration = result.Duration
				};
				CollectFailures(result, run.failures);
				WriteResult(taskId, run);
				Debug.Log("[CoworkBridge] Tests " + taskId + ": passed " + run.passed + ", failed " + run.failed);
			}

			private static void CollectFailures(ITestResultAdaptor node, List<TestFailure> failures)
			{
				if (node.HasChildren)
				{
					foreach (ITestResultAdaptor child in node.Children)
					{
						CollectFailures(child, failures);
					}

					return;
				}

				if (node.TestStatus == TestStatus.Failed || node.TestStatus == TestStatus.Inconclusive)
				{
					failures.Add(new TestFailure
					{
						name = node.FullName,
						message = node.Message,
						stacktrace = node.StackTrace
					});
				}
			}
		}
	}
}
```

`WriteResult` обращается к приватному статическому полю снаружи вложенного класса — это допустимо: вложенный `TestCallbacks` имеет доступ к приватным членам внешнего `CoworkTestRunner`.

### Правки в `CoworkBridge/Editor/CoworkBridgeSettings.cs`

Добавить поле количества хранимых тасков. Поле сериализуемое, public, с большой буквы — как `Enabled`.

Было:

```csharp
[Serializable]
public class CoworkBridgeSettings
{
	public bool Enabled;
}
```

Стало:

```csharp
[Serializable]
public class CoworkBridgeSettings
{
	public bool Enabled;
	public int KeepCompletedCount = 10;
}
```

### Правки в `CoworkBridge/Editor/CoworkBridgeSettingsStore.cs`

Добавить геттер `KeepCompletedCount` с защитой от нуля/отрицательного значения. Вставить новый метод после `SetEnabled`.

```csharp
public static int GetKeepCompletedCount()
{
	CoworkBridgeSettings settings = Load();
	if (settings.KeepCompletedCount <= 0)
	{
		return 10;
	}

	return settings.KeepCompletedCount;
}
```

### Правки в `CoworkBridge/Editor/CoworkBridge.cs`

#### Тела пунктов меню `Clean Completed` / `Clean All` — делегировать в `TaskCleaner`

Заменить тела двух методов (сигнатуры и атрибуты `[MenuItem]` не трогать).

`CleanCompleted`:

```csharp
[MenuItem("Tools/Cowork Bridge/Clean Completed")]
public static void CleanCompleted()
{
	string coworkPath = Path.Combine(Application.dataPath, "Editor", "CoworkBridge");

	if (!Directory.Exists(coworkPath))
	{
		return;
	}

	TaskCleaner.CleanCompleted(coworkPath);
}
```

`CleanAll`:

```csharp
[MenuItem("Tools/Cowork Bridge/Clean All")]
public static void CleanAll()
{
	string coworkPath = Path.Combine(Application.dataPath, "Editor", "CoworkBridge");

	if (!Directory.Exists(coworkPath))
	{
		return;
	}

	TaskCleaner.CleanAll(coworkPath);
}
```

#### Удалить приватный метод `DeleteTaskFiles`

Логика переехала в `TaskCleaner`. Удалить из `CoworkBridge.cs` весь метод:

```csharp
private static void DeleteTaskFiles(string coworkPath, string taskId)
{
	...
}
```

Метод `CleanResultFiles(string taskId)` оставить без изменений — он используется в `OnEditorUpdate` перед перекомпиляцией и к очистке завершённых тасков отношения не имеет.

#### Добавить обработку командного файла и авто-очистку в `OnEditorUpdate`

В методе `OnEditorUpdate`, в блоке после проверки `if (!Directory.Exists(_coworkPath)) { return; }` и перед `string nextScript = FindNextTask();`, вставить проверку командного файла. А в ветку «нет ожидающих тасков» (`nextScript == null`) добавить вызов авто-очистки.

Было:

```csharp
if (!Directory.Exists(_coworkPath))
{
	return;
}

string nextScript = FindNextTask();
if (nextScript == null)
{
	return;
}
```

Стало:

```csharp
if (!Directory.Exists(_coworkPath))
{
	return;
}

if (TryProcessCleanCommand())
{
	return;
}

string nextScript = FindNextTask();
if (nextScript == null)
{
	TaskCleaner.TrimCompleted(_coworkPath, CoworkBridgeSettingsStore.GetKeepCompletedCount());
	return;
}
```

#### Добавить приватный метод `TryProcessCleanCommand`

Добавить в класс `CoworkBridge` (например, рядом с `FindNextTask`).

```csharp
private static bool TryProcessCleanCommand()
{
	string commandPath = Path.Combine(_coworkPath, "clean.command");
	if (!File.Exists(commandPath))
	{
		return false;
	}

	File.Delete(commandPath);
	TaskCleaner.CleanAllSuccessful(_coworkPath);
	return true;
}
```

Командный файл `clean.command` не имеет расширения `.cs`, поэтому `FindNextTask` его не подхватывает как задачу. `TrimCompleted` запускается только на простое (нет ожидающих тасков) и за счёт ранней проверки `csFiles.Length <= keepCount` ничего не читает и не перекомпилирует, пока тасков не больше `N`. При превышении `N` происходит один батч-удаление до `N` и один `AssetDatabase.Refresh()`.

### Правки в `CoworkBridge/Editor/CoworkBridge.asmdef`

Сборка `CoworkBridge` теперь использует Test Framework API, нужно добавить ссылки на его сборки.

Было:

```json
{
    "name": "CoworkBridge",
    "references": [],
    "includePlatforms": ["Editor"],
    "excludePlatforms": []
}
```

Стало:

```json
{
    "name": "CoworkBridge",
    "references": [
        "UnityEditor.TestRunner",
        "UnityEngine.TestRunner"
    ],
    "includePlatforms": ["Editor"],
    "excludePlatforms": []
}
```

Поле `autoReferenced` не задаётся (по умолчанию `true`), поэтому предопределённая `Assembly-CSharp-Editor` по-прежнему автоматически ссылается на `CoworkBridge`, и таск-скрипты видят `CoworkTestRunner` без дополнительной настройки.

### Правки в `CoworkBridge/package.json`

Зафиксировать зависимость от Test Framework, чтобы ссылки в asmdef всегда резолвились. Добавить блок `dependencies` (значение версии — минимально совместимое с Unity 2022.3; при необходимости поднять под версию проекта).

```json
"dependencies": {
    "com.unity.test-framework": "1.1.33"
}
```

### Правки в `unity-bridge-plugin/skills/unity-bridge/SKILL.md`

#### Правило 6 в разделе «Шаблон C# скрипта»

Заменить текущее правило 6 (запрет удалять файлы) на новое:

> 6. **Не оставляй упавшие таски висеть.** Если таск дал `compiler_error` (свой) или `runtime_error` — почини его (до 3 итераций). Если починить не удаётся — удали файлы этого таска (`<TaskName>.cs` и `result_<TaskName>.*`), не оставляй их в папке: битый `.cs` блокирует компиляцию всей редакторной сборки, и мост залипает. Успешные таски удалять вручную не нужно — их чистит Bridge сам (см. раздел «Очистка задач»).

#### Раздел «Очистка задач»

Заменить раздел целиком:

> ## Очистка задач
>
> Успешно завершённые таски Bridge чистит **сам**:
>
> - Авто-очистка: на простое держит последние N успешных тасков (по умолчанию 10), удаляя более старые вместе с их `result_*` и `testresult_*`. N настраивается в `ProjectSettings/CoworkBridge.json` (`KeepCompletedCount`).
> - Немедленная очистка по команде: чтобы удалить **все** успешные таски сразу (например, после большого батча), создай пустой файл `Assets/Editor/CoworkBridge/clean.command`. Bridge удалит все успешно завершённые таски и сам командный файл. **Не создавай `clean.command`, пока идёт прогон тестов** — это может удалить файлы выполняющейся задачи.
>
> Упавшие таски (`compiler_error` / `runtime_error`) авто-очистка не трогает — чини или удаляй их сам (правило 6). Человек может почистить папку вручную через `Tools → Cowork Bridge → Clean Completed / Clean All`.

### Правки в `CoworkBridge/UNITYCOWORK.md`

Документ описывает запуск тестов. Переписать так, чтобы таск вызывал `CoworkTestRunner`, а не регистрировал колбэки сам.

#### Раздел «Как это работает» и «Шаблон задачи»

Заменить на:

> ## Как это работает
>
> Таск не запускает тесты напрямую и не регистрирует колбэки. Он вызывает API моста `CoworkBridge.CoworkTestRunner.RequestRun(...)`, который владеет персистентным колбэком (переживает domain reload при входе/выходе из Play Mode) и сам пишет результат в:
>
> ```
> Assets/Editor/CoworkBridge/testresult_<TaskName>.json
> Assets/Editor/CoworkBridge/testresult_<TaskName>.done
> ```
>
> Ожиданий два:
> 1. Дождись обычного `result_<TaskName>.done` (`status == "success"`) — скрипт скомпилировался и прогон стартовал.
> 2. Дождись `testresult_<TaskName>.done` и прочитай `testresult_<TaskName>.json` — это результат тестов.
>
> ## Шаблон задачи
>
> Подставь имя задачи в двух местах. Для PlayMode поменяй `"EditMode"` → `"PlayMode"`. Фильтры передаются массивами строк (или `null`, если не нужны).
>
> ```csharp
> using UnityEngine;
> using UnityEditor;
>
> public static class Task_XXX
> {
>     public static string Run()
>     {
>         return CoworkBridge.CoworkTestRunner.RequestRun(
>             "Task_XXX",
>             "EditMode",
>             null,   // assemblyNames, например new[] { "MyGame.Tests" }
>             null,   // testNames,     например new[] { "MyGame.Tests.MathTests.Adds_Two" }
>             null);  // categoryNames, например new[] { "Smoke" }
>     }
> }
> ```

#### Раздел «Логика проверки» и нюанс PlayMode

Заменить «PlayMode: важный нюанс» и логику проверки на:

> Логика проверки `testresult_<TaskName>.json`:
> - `aborted == true` → прогон не стартовал; покажи `message` пользователю (как правило — «выйди из Play Mode и перезапусти»).
> - иначе `failed == 0 && inconclusive == 0` → тесты зелёные; иначе покажи `failures`.
>
> ## PlayMode
>
> PlayMode-прогон надёжен при любой настройке Enter Play Mode (Reload Domain включён или выключен) — колбэк моста персистентный. Перед PlayMode-прогоном Bridge сохраняет открытые сцены (`SaveOpenScenes`), чтобы тест-фреймворк не падал на сохранении. Если на момент запроса редактор уже в Play Mode, прогон не стартует — в `testresult` будет `aborted: true`.

#### Примечание про удаление

В разделе «Примечания» заменить пункт про ручную очистку `testresult_*` на:

> - Файлы `testresult_*` чистит Bridge автоматически вместе с таском (авто-очистка последних N успешных и команда `clean.command`). Отдельно их не трогай.

## Проверка

- Очистка: запустить более `N` (по умолчанию >10) успешных тасков; убедиться, что на простое в папке остаётся ровно последние `N` тасков, а у удалённых пропали и `result_*`, и `testresult_*`.
- testresult: прогнать тест-таск, дождаться `testresult_*`, затем убедиться, что после авто-очистки/`clean.command` файлы `testresult_*` удалены (проблема 2 закрыта).
- Команда: создать пустой `Assets/Editor/CoworkBridge/clean.command`; убедиться, что все успешные таски удалены, командный файл исчез, упавшие таски остались.
- Упавший таск: создать таск с ошибкой компиляции; убедиться, что авто-очистка его не удаляет и он остаётся для починки.
- EditMode-тесты: прогнать через новый шаблон; `testresult_*.json` содержит корректные `passed/failed`, `failures` заполнен при падении.
- PlayMode-тесты: прогнать через новый шаблон при включённом Reload Domain; убедиться, что прежние ошибки (`This cannot be used during play mode`, `Playmode tests were aborted because the player was stopped`) не воспроизводятся и `testresult_*` пишется после выхода из Play Mode.
- PlayMode при уже запущенном Play Mode: войти в Play Mode вручную, запустить тест-таск; убедиться, что в `testresult` приходит `aborted: true` с сообщением, а редактор не падает.
- Вотчер: после внедрения авто-очистки папка не растёт; перекомпиляция при добавлении новых тасков остаётся быстрой.
- Совместимость: убедиться, что `CoworkBridge.asmdef` со ссылками на Test Framework компилируется, а таск-скрипты в `Assembly-CSharp-Editor` видят `CoworkBridge.CoworkTestRunner`.

---

После выполнения:
- Поменяй статус вверху документа на `Выполнено`.
- Уточни у заказчика, нужно ли обновить проектную документацию (README, шаблоны `UNITYCOWORK-template.md` и т.п.), чтобы отразить программную очистку и новый способ запуска тестов.
