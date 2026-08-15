Status: Не готов

# Unity Agent Bridge — сессионный планировщик задач для нескольких агентов

## Тип изменения

Новая функциональность в существующем коде: CLI и Editor-пакет получают понятие агентской сессии, lease с ротацией по слайсам, контекст-свитч scene setup между сессиями, канал контентации (`--note` / `Contention`) и команду `release`. Протокол задач остаётся файловым, `ProtocolVersion` не меняется — все новые поля аддитивны и игнорируются старыми парсерами.

## Контракт поведения

- CLI передаёт в каждой задаче идентификатор агентской сессии (`--session`). Задачи одной сессии выполняются в порядке подачи. Задачи без `--session` работают ровно как сегодня: каждая — отдельная одноразовая сессия.
- В редакторе одновременно выполняется не более одной задачи (как сейчас). Планировщик решает, чья задача стартует следующей.
- Сессия-держатель lease выполняет свои задачи без переключений, пока никто не ждёт. При появлении задач другой сессии держатель дорабатывает не дольше `ContentionSliceSeconds`, затем очередь ротируется на границе задач.
- Если у держателя нет ожидающих задач, а у другой сессии есть — ротация происходит сразу.
- При ротации сохраняется контекст уходящей сессии (scene setup + открытый prefab stage) и восстанавливается сохранённый контекст приходящей. Сессия, вернувшая lease, продолжает в тех же сценах, что и до ротации.
- Держатель без активности дольше `LeaseIdleTimeoutSeconds` теряет lease автоматически.
- Клиентский бюджет `--wait` расходуется только со старта задачи в Unity. Время в очереди CLI ждёт отдельно, с прогрессом «queued, position N, holder X», при живом heartbeat — до жёсткого потолка 3600 секунд.
- В результат каждой задачи добавляется блок `Contention`: сколько чужих сессий ждёт, как давно, и их `--note`-тексты. Команда `agentbridge release` досрочно отдаёт lease.
- Рестарт редактора сбрасывает lease, но сохраняет накопленные контексты сессий. Domain reload не сбрасывает ничего.

## Протокол задач

### `AgentBridgeCli/TaskRequest.cs` и `Editor/TaskRequest.cs`

Добавить поля (CLI — свойства с дефолтом `""`, Unity — public-поля):

```csharp
public string AgentSessionId;
public string Note;
```

Хэш задачи считается по байтам `task.json` и не требует изменений: новые поля попадают в него автоматически, replay той же задачи с теми же аргументами остаётся идемпотентным.

### `Editor/TaskRecord.cs`

Добавить поля:

```csharp
public string AgentSessionId;
public ContentionInfo Contention = new ContentionInfo();
```

### Новый файл `Editor/ContentionInfo.cs`

```csharp
using System;
using System.Collections.Generic;

namespace AgentBridge
{
	[Serializable]
	public class ContentionInfo
	{
		public int WaitingSessions;
		public int OldestWaitSeconds;
		public List<string> Notes = new List<string>();
	}
}
```

### `Editor/BridgeStatus.cs` и `AgentBridgeCli/BridgeStatus.cs`

Добавить поля (CLI — nullable-свойства, массив с дефолтом `Array.Empty`):

```csharp
public string HolderAgentSessionId;
public QueuedTaskStatus[] QueuedTasks;
```

### Новый файл `Editor/QueuedTaskStatus.cs` и зеркальный `AgentBridgeCli/QueuedTaskStatus.cs`

```csharp
using System;

namespace AgentBridge
{
	[Serializable]
	public class QueuedTaskStatus
	{
		public string Id;
		public string AgentSessionId;
		public int Position;
	}
}
```

В `BridgeStatusWriter.WriteOnLoad` дополнить `Capabilities` значением `"release"`.

## Настройки

### `Editor/AgentBridgeSettings.cs`

Добавить поля:

```csharp
public int LeaseIdleTimeoutSeconds = 120;
public int ContentionSliceSeconds = 90;
```

### `Editor/AgentBridgeSettingsStore.cs`

Добавить методы по образцу существующих (`<= 0` возвращает дефолт):

```csharp
public static int GetLeaseIdleTimeoutSeconds();
public static void SetLeaseIdleTimeoutSeconds(int value);
public static int GetContentionSliceSeconds();
public static void SetContentionSliceSeconds(int value);
```

### `Editor/AgentBridgeSetupWindow.cs`

После секции `Scene safety` добавить секцию `Multi-agent`: два `EditorGUILayout.DelayedIntField` — `Lease idle timeout (s)` и `Contention slice (s)`. Изменённое значение сразу сохраняется соответствующим сеттером стора.

### `AgentBridgeUnity/ProjectSettings/AgentBridge.json`

Добавить:

```json
"LeaseIdleTimeoutSeconds": 120,
"ContentionSliceSeconds": 90
```

## Состояние планировщика

### `Editor/BridgePaths.cs`

Добавить свойство:

```csharp
public static string SchedulerStateFile
{
	get { return Path.Combine(WorkingRoot, "scheduler-state.json"); }
}
```

### Новый файл `Editor/SchedulerState.cs`

```csharp
using System;
using System.Collections.Generic;

namespace AgentBridge
{
	[Serializable]
	public class SchedulerState
	{
		public int EditorPid;
		public string HolderAgentSessionId = "";
		public string HolderLastActivityUtc = "";
		public string ContentionStartedUtc = "";
		public bool HolderContextRestored = true;
		public List<SessionContext> Contexts = new List<SessionContext>();
	}
}
```

### Новый файл `Editor/SessionContext.cs`

```csharp
using System;

namespace AgentBridge
{
	[Serializable]
	public class SessionContext
	{
		public string AgentSessionId;
		public SceneSetupState[] Setup;
		public string PrefabStagePath = "";
		public string SavedAtUtc;
	}
}
```

### Новый файл `Editor/SchedulerStateStore.cs`

```csharp
public static class SchedulerStateStore
{
	public static SchedulerState State { get; }
	public static void Save();
}
```

- `State` лениво читает `BridgePaths.SchedulerStateFile` через `JsonUtility.FromJson<SchedulerState>` и кэширует в статике. Отсутствующий или битый файл — новый `SchedulerState`.
- После чтения сверяет `EditorPid` с `Process.GetCurrentProcess().Id`. При несовпадении обнуляет `HolderAgentSessionId`, `HolderLastActivityUtc`, `ContentionStartedUtc`, ставит `HolderContextRestored = true`, записывает текущий pid и сохраняет. `Contexts` при этом сохраняются.
- `Save` пишет атомарно по образцу `PlayModeSceneRecovery.Write` (временный файл, `File.Replace` с fallback на `Copy` + `Delete`).
- Единственный писатель — main thread редактора, блокировок не требуется.

## Планировщик

### Новый файл `Editor/PendingTaskInfo.cs`

```csharp
using System;

namespace AgentBridge
{
	public class PendingTaskInfo
	{
		public string Id;
		public string TaskFilePath;
		public DateTime CreatedUtc;
		public string EffectiveSessionId;
		public string Note;
		public string Kind;
	}
}
```

### Новый файл `Editor/AgentSessionScheduler.cs`

```csharp
public static class AgentSessionScheduler
{
	public static string EffectiveSessionId(string agentSessionId, string taskId);
	public static bool IsAnonymous(string effectiveSessionId);
	public static bool TryPick(List<PendingTaskInfo> pending, DateTime nowUtc, out PendingTaskInfo next, out bool holderChanged, out string previousHolder);
	public static void CommitStart(PendingTaskInfo task, bool holderChanged);
	public static void OnTaskFinished(string effectiveSessionId, DateTime nowUtc);
	public static bool Release(string effectiveSessionId);
	public static void TickIdle(DateTime nowUtc, bool hasPending);
	public static ContentionInfo BuildContention(List<PendingTaskInfo> pending, DateTime nowUtc);
	public static QueuedTaskStatus[] BuildQueue(List<PendingTaskInfo> pending);
	public static void SaveContextFor(string effectiveSessionId, SceneSetupState[] setup, string prefabStagePath, DateTime nowUtc);
	public static SessionContext FindContext(string effectiveSessionId);
}
```

- `EffectiveSessionId`: непустой `agentSessionId` возвращается как есть, пустой — `"anon:" + taskId`.
- `IsAnonymous`: префикс `"anon:"`.
- Группировка: у каждой сессии берётся её самая старая задача по `CreatedUtc`; сессии упорядочиваются по этому времени, задачи внутри сессии — по `CreatedUtc`.

`TryPick`, при пустом `pending` возвращает `false`, иначе:

- Держатель пуст: выбрать глобально старейшую задачу, `holderChanged = true`, `previousHolder = ""`.
- Держатель задан, чужих задач нет: если у держателя есть задачи — выбрать старейшую, `holderChanged = false`; если `ContentionStartedUtc` непуст — очистить и сохранить.
- Держатель задан, чужие задачи есть:
	- `ContentionStartedUtc` пуст — записать `nowUtc`, сохранить.
	- У держателя нет своих задач — ротация немедленно.
	- Слайс истёк (`nowUtc - ContentionStartedUtc >= GetContentionSliceSeconds()`) — ротация.
	- Иначе — выбрать старейшую задачу держателя, `holderChanged = false`.
- Ротация: цель — сессия с глобально старейшей чужой задачей; вернуть её старейшую задачу, `holderChanged = true`, `previousHolder` = текущий держатель. Состояние здесь не мутировать — мутации только в `CommitStart`.

`CommitStart`:

- `HolderLastActivityUtc = nowUtc`.
- При `holderChanged`: `HolderAgentSessionId = task.EffectiveSessionId`, `ContentionStartedUtc = ""`, `HolderContextRestored = FindContext(task.EffectiveSessionId) == null`.
- `Save`.

`OnTaskFinished`:

- `HolderLastActivityUtc = nowUtc`.
- Если сессия анонимна и является держателем — очистить `HolderAgentSessionId`, `ContentionStartedUtc`, `HolderContextRestored = true`.
- `Save`.

`Release`: если `effectiveSessionId` совпадает с держателем — очистить держатель как выше и вернуть `true`, иначе `false`. Сохранение контекста делает вызывающий код до `Release`.

`TickIdle`: при заданном держателе, `hasPending == false` и `nowUtc - HolderLastActivityUtc > GetLeaseIdleTimeoutSeconds()` — очистить держатель (контекст сохраняет вызывающий код), `Save`.

`BuildContention`: чужие задачи — те, чья сессия не равна держателю. `WaitingSessions` — число различных чужих сессий, `OldestWaitSeconds` — от старейшего чужого `CreatedUtc`, `Notes` — различные непустые `Note` чужих задач, не более четырёх, каждая обрезана до 200 символов.

`BuildQueue`: порядок — задачи держателя, затем чужие сессии по старейшей задаче, внутри сессии по `CreatedUtc`; `Position` — сквозная нумерация с единицы.

`SaveContextFor` / `FindContext`: поиск по `AgentSessionId`; при сохранении существующая запись заменяется, `SavedAtUtc = nowUtc`; для анонимных сессий `SaveContextFor` ничего не делает; после вставки список обрезается до восьми записей — удаляются старейшие по `SavedAtUtc`; `Save`.

## Контекст-свитч сцен

### Новый файл `Editor/SceneSetupStateConverter.cs`

Вынести из `PlayModeSceneRecovery` приватные `ToState` и `FromState` в публичный статический класс:

```csharp
public static class SceneSetupStateConverter
{
	public static SceneSetupState[] ToState(SceneSetup[] setup);
	public static SceneSetup[] FromState(SceneSetupState[] setup);
}
```

`PlayModeSceneRecovery` переключить на него, приватные копии удалить.

### Новый файл `Editor/SessionContextSwitcher.cs`

```csharp
public static class SessionContextSwitcher
{
	public static bool TrySaveContext(string effectiveSessionId, out string error);
	public static void RestoreContext(string effectiveSessionId, List<string> logs);
}
```

`TrySaveContext`:

- Анонимная или пустая сессия — вернуть `true` без действий.
- `SceneSafetyGuard.TryPrepareForTask` — при `false` пробросить ошибку и вернуть `false`.
- Запомнить `PrefabStageUtility.GetCurrentPrefabStage()?.assetPath` (пустая строка, если stage не открыт); если stage открыт — `StageUtility.GoToMainStage()`.
- Снять `EditorSceneManager.GetSceneManagerSetup()`, конвертировать через `SceneSetupStateConverter.ToState`, отфильтровать записи с пустым `Path`.
- `AgentSessionScheduler.SaveContextFor(...)`, вернуть `true`.

`RestoreContext`:

- Контекст не найден — выйти без действий.
- Отфильтровать `Setup` до записей, чей файл существует (`File.Exists` от `BridgePaths.ProjectRoot` + путь с разделителями ОС). Если после фильтрации нет ни одной записи с `IsLoaded == true` — добавить в `logs` строку `context restore skipped: saved scenes are missing` и выйти.
- `EditorSceneManager.RestoreSceneManagerSetup(SceneSetupStateConverter.FromState(filtered))`. Исключение — строка `context restore failed: <сообщение>` в `logs`, выйти.
- Если `PrefabStagePath` непуст и asset существует — `PrefabStageUtility.OpenPrefab(PrefabStagePath)`. Исключение или `false` — строка `prefab stage restore failed: <путь>` в `logs`, продолжить.

## Изменения `Editor/TaskCoordinator.cs`

### Сканирование

`TryStartNextTask` заменить на построение списка и выбор планировщиком:

- `BuildPendingList()`: по всем `*.task.json` в inbox применить существующий replay-фильтр (терминальная запись с совпадающим хэшем пропускается). Для каждого оставшегося файла прочитать `TaskRequest`; собрать `PendingTaskInfo` с `CreatedUtc = File.GetCreationTimeUtc`, `EffectiveSessionId = AgentSessionScheduler.EffectiveSessionId(request.AgentSessionId, id)`, `Note`, `Kind`. Нечитаемый или битый `task.json` даёт `PendingTaskInfo` c `EffectiveSessionId = "anon:" + id` и пустыми `Note`/`Kind` — дальше `StartTask` отвергнет его как сегодня.
- `AgentSessionScheduler.TickIdle(DateTime.UtcNow, pending.Count > 0)`; если `TickIdle` снял держатель — до снятия вызвать `SessionContextSwitcher.TrySaveContext` для него; при `false` — `Debug.LogWarning` и снять без контекста.
- Обновить статус очереди (см. ниже).
- `TryPick`; при успехе — `StartTask(next, holderChanged, previousHolder)`.

### Статус очереди

Новый приватный метод `UpdateQueueStatus(List<PendingTaskInfo> pending)`:

- `Current.HolderAgentSessionId = SchedulerStateStore.State.HolderAgentSessionId` (пустая строка — `null`).
- `Current.QueuedTasks = AgentSessionScheduler.BuildQueue(pending)`.
- Сигнатура — конкатенация `Id:Position` всех элементов плюс держатель; хранится в статике; `BridgeStatusWriter.Write()` вызывается только при изменении сигнатуры.

### `StartTask(PendingTaskInfo task, bool holderChanged, string previousHolder)`

Порядок внутри, после существующего чтения запроса, проверки id и записи `queued`-записи журнала (в запись добавить `AgentSessionId = task.EffectiveSessionId`):

- При `holderChanged` и непустом неанонимном `previousHolder`: `SessionContextSwitcher.TrySaveContext(previousHolder, out error)`; при `false` — `FinishTask("runtime_error", null, new List<string> { error }, false)` и выход; планировщик не мутирован, держатель прежний, следующий скан повторит попытку.
- `AgentSessionScheduler.CommitStart(task, holderChanged)`.
- `needsScenes` — `Kind` из набора `csharp`, `ui`, `tests`, `sceneshot`.
- Если `needsScenes` и `SchedulerStateStore.State.HolderContextRestored == false`: выполнить scene-preflight (`SceneSafetyGuard.TryPrepareForTask`, ошибка — `runtime_error` как сейчас), затем `SessionContextSwitcher.RestoreContext(task.EffectiveSessionId, restoreLogs)`, установить `HolderContextRestored = true`, `SchedulerStateStore.Save()`; строки `restoreLogs` передать в `FinishTask` через `extraLogs` завершающего вызова — для этого накопить их в новом приватном поле `_activeRestoreLogs` и дописывать в `FinishTask` к `logs`.
- Иначе, если `Kind` из набора `RequiresScenePreflight` (`csharp`, `ui`, `tests`) — существующий preflight без изменений.
- `RunTask` как сейчас.

### `RunTask`

Добавить ветку:

```csharp
case "release":
	RunReleaseTask(request);
	break;
```

`RunReleaseTask`:

- `effective = AgentSessionScheduler.EffectiveSessionId(request.AgentSessionId, request.Id)`.
- Если `effective` совпадает с держателем: `SessionContextSwitcher.TrySaveContext(effective, out error)`; при `false` добавить `error` в логи, продолжить; `AgentSessionScheduler.Release(effective)`; `FinishTask("success", "released", logs, false)`.
- Иначе `FinishTask("success", "not_holder", null, false)`.

### Завершение задач

- В `FinishTask` перед `TaskJournal.Write`: собрать `BuildPendingList()` без активной задачи и записать `_activeRecord.Contention = AgentSessionScheduler.BuildContention(pending, DateTime.UtcNow)`; после `Write` — `AgentSessionScheduler.OnTaskFinished(_activeRecord.AgentSessionId, DateTime.UtcNow)`; добавить `_activeRestoreLogs` в начало `logs` и обнулить поле в `CleanupActive`.
- В `PollExternallyFinalizedTask` перед `CleanupActive` — `OnTaskFinished(_activeRecord.AgentSessionId, DateTime.UtcNow)`.
- В `OnBeforeAssemblyReload`, в ветке записи `interrupted_by_domain_reload` — после `TaskJournal.Write` вызвать `OnTaskFinished(_activeRecord.AgentSessionId, DateTime.UtcNow)`.
- В `TryFinalizePendingCompileTask` после `TaskJournal.Write` — `OnTaskFinished(record.AgentSessionId, DateTime.UtcNow)`.
- В `AgentTestRunner.FinalizeRecoveredPlayModeRun` после записи терминальной записи журнала — прочитать записанный `TaskRecord` и вызвать `AgentSessionScheduler.OnTaskFinished(record.AgentSessionId, DateTime.UtcNow)`; этот путь финализирует PlayMode-задачу после domain reload в обход `FinishTask`.

## Изменения CLI

### `AgentBridgeCli/CliOptions.cs`

- Свойства `Session` (`string?`) и `Note` (`string?`).
- Разбор `--session <v>` / `--session=<v>`: допустимы 1–64 символа из `[A-Za-z0-9_-]`, иначе `Error = "--session must be 1-64 characters of A-Za-z0-9_-"`.
- Разбор `--note <v>` / `--note=<v>`: непустая строка до 200 символов, иначе `Error = "--note must be 1 to 200 characters"`.

### `AgentBridgeCli/BridgeClient.cs`

- Конструктор принимает `(string projectRoot, string format, string? session, string? note)`; значения сохраняются в поля и подставляются в каждый создаваемый `TaskRequest` (`AgentSessionId = _session ?? ""`, `Note = _note ?? ""`), включая `SubmitPayloadAsync`, `SubmitCompileAsync`, `SubmitTestsAsync`.
- Новый метод `SubmitReleaseAsync(int waitSeconds)`: `TaskRequest` c `Id = TaskIdGenerator.NewId()`, `Kind = "release"`, отправка через `SubmitRequestAsync`.
- Поле `private readonly string _projectRoot;` для health-проверок.

`WaitForTaskAsync` — новая семантика:

- Константа `private const int QueueWaitCapSeconds = 3600;`.
- Переменные: `DateTime? runningSince = null`, `var queuedSince = DateTime.UtcNow`, `var nextProgress = TimeSpan.Zero`, `var nextHealthCheck = TimeSpan.Zero`.
- Цикл с `Task.Delay(250)`:
	- Журнальный файл читается как сейчас; терминальный статус — вывод и `ClassifyResult`.
	- Файл есть, статус не терминальный, `runningSince == null` — зафиксировать `runningSince = DateTime.UtcNow`.
	- `runningSince != null` и `UtcNow - runningSince >= waitSeconds` — текущее поведение таймаута (вывод последнего JSON, код 2). Прогресс-строка в этом режиме: `[agentbridge] <id> running <n>s`.
	- Файла нет (задача в очереди):
		- `UtcNow - queuedSince >= QueueWaitCapSeconds` — вывести `{"Id": <id>, "Status": "queued"}`, код 2.
		- Раз в 5 секунд — `BridgeInspector.Inspect(_projectRoot)`; при `BridgeReady == false` — `WriteError("bridge_unavailable", "Bridge became unavailable while the task was queued: " + health.Code)`, код 3.
		- Прогресс-строка раз в 5 секунд: позиция ищется по `Id` в `health.Bridge.QueuedTasks`; найдена — `[agentbridge] <id> queued <n>s, position <p>/<total>, holder <holder|none>`, не найдена — `[agentbridge] <id> queued <n>s`.

### `AgentBridgeCli/AgentBridgeApplication.cs`

- Создание клиента: `new BridgeClient(projectRoot, options.Format, options.Session, options.Note)`.
- Новая команда `release`: без позиционных аргументов, требует `options.Session`, иначе `WriteError("bad_usage", "usage: agentbridge release --session <id> [--project <path>] [--wait <seconds>]")`; вызывает `client.SubmitReleaseAsync(options.WaitSeconds)`.
- Help-текст: добавить строку команды `release`, а в global options — `--session <id>` (`agent session for fair scheduling`) и `--note <text>` (`intent shown to the session holding the editor`).

### `AgentBridgeCli/TaskResultFormatter.cs`

После `AppendStringArray(..., "Artifacts", ...)`: если в корне есть объект `Contention` с `WaitingSessions > 0` — строка `Contention: <WaitingSessions> waiting, oldest <OldestWaitSeconds>s`, затем каждая строка `Notes` как `- <note>`.

## Обновление скиллов и агентских инструкций

Файлы: `unity-bridge-plugin/skills/unity-bridge/SKILL.md`, `unity-bridge-plugin/skills/unity-ui/SKILL.md`, `Docs/UNITYAGENT-template.md`, `Docs/UNITYAGENT-UI-template.md`, `AgentBridgeUnity/Packages/com.elmortem.agentbridge/UNITYAGENT.md`.

В каждый добавить блок (адаптируя примеры команд под формат файла):

```
## Работа рядом с другими агентами

- Один раз в начале работы сгенерируй id сессии: AB_<дата-время>_<random> и передавай его каждой команде: --session <id>.
- При желании передавай --note "<что делаешь>" — держатель редактора увидит текст в поле Contention своих результатов.
- Поле Contention в результате задачи показывает, сколько чужих сессий ждёт редактор. Закончил серию задач — вызови agentbridge release --session <id>, не жди таймаута.
- Пока чужая сессия держит редактор, команда честно ждёт в очереди и печатает позицию. Это не зависание — не пересоздавай задачу.
```

## Порядок реализации

- Типы протокола: `ContentionInfo`, `QueuedTaskStatus`, поля в `TaskRequest`, `TaskRecord`, `BridgeStatus` обеих сторон.
- Настройки: `AgentBridgeSettings`, стор, окно, `AgentBridge.json`.
- Состояние: `BridgePaths.SchedulerStateFile`, `SchedulerState`, `SessionContext`, `SchedulerStateStore`.
- Планировщик: `PendingTaskInfo`, `AgentSessionScheduler`.
- Контекст: `SceneSetupStateConverter` (с правкой `PlayModeSceneRecovery`), `SessionContextSwitcher`.
- Координатор: сканирование, `StartTask`, `RunReleaseTask`, завершение задач, статус очереди.
- CLI: `CliOptions`, `BridgeClient`, `AgentBridgeApplication`, `TaskResultFormatter`.
- Документация и скиллы.

## После выполнения

- Смени статус в начале этого документа на `Выполнено`.
- Уточни у заказчика, нужно ли обновить документацию проекта под эти изменения.
