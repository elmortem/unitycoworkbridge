Status: Выполнено

# Unity Agent Bridge — Этап 1: движок исполнения на Roslyn — Agent Execution Spec

## References (not inlined)

- Конвенции кода: глобальный CLAUDE.md пользователя (табы, один тип на файл, без комментариев в коде, сериализуемые поля public с большой буквы, без MonoBehaviour где можно обойтись, без GetComponent в рантайме).
- Предварительный дизайн-разбор: `Docs/notes/260723-1100-NOTE-http_roslyn_execution.md`. Этот ТДД реализует его первую половину и отменяет часть решений заметки (см. «Отличия от заметки»).
- Образец окна установки: `D:\Hobby\Repositories\unitypcg\ProjectPCG\Assets\Plugins\PCG4U\Setup\` — `PcgSetupBootstrap`, `PcgSetupWindow`, `PcgPackageInstaller`, `PcgExtrasWindow`.
- Skills: `unity-bridge`, `unity-ui` — обновляются в этом же прогоне.
- Hooks: нет.

## Что делает Этап 1

Мост перестаёт исполнять задачи через Unity Script Compilation Pipeline. C# компилируется Roslyn в память и запускается в main thread без domain reload и без единого файла в `Assets`. Транспорт на этом этапе остаётся файловым, но переезжает в `Library` и получает конверт запроса/ответа, который Этап 2 переложит в HTTP без изменения контракта.

Заодно продукт переименовывается из Cowork Bridge в Agent Bridge, а пакет переезжает внутрь тестового Unity-проекта.

Вне Этапа 1: HTTP-сервер, descriptor, bearer token, идемпотентность по сети, watchdog `editor_unresponsive`, поддержка Unity 6 в гейтах.

## Отличия от заметки (заметка устарела в этих местах)

- Нет `protocolVersion` в запросе и ответе.
- Нет `timeoutSeconds` в запросе — таймаут живёт в настройках моста.
- Нет маркера `.done`: запись результата атомарна (temp + `File.Replace`), поэтому частично записанный файл прочитать нельзя.
- Интервал тиков не 16 мс: на простое 500 мс, во время активной задачи 33 мс.
- `com.unity.pipeline` не является зависимостью и не читается.
- Roslyn имеет три источника с выбором через окно установки, а не один зафиксированный набор DLL в пакете.
- `compile` и `tests` — самостоятельные типы задач, а не побочный эффект и не двухфазное ожидание.

## Предусловия (проверить первым делом, при невыполнении — остановиться и сообщить)

- Unity Editor запущен с проектом `AgentBridgeUnity`. Проверка: файл `AgentBridgeUnity/Library/EditorInstance.json` существует и содержит `process_id`.
- Версия редактора 2022.3.62f2 (`AgentBridgeUnity/ProjectSettings/ProjectVersion.txt`).
- Старый мост включён и отвечает. Проверка выполняется гейтом Unit 1.

Если Editor не запущен — остановиться и написать: «Открой AgentBridgeUnity в Unity и включи мост через Tools → Cowork Bridge → Start». Не пытаться запускать Unity самостоятельно.

## Foundations (shared, used across units)

### Пути

- Корень репозитория: `D:\Hobby\Repositories\unitycoworkbridge` (далее — корень; все команды запускаются из него).
- Unity-проект: `AgentBridgeUnity/`.
- Пакет после переезда: `AgentBridgeUnity/Packages/com.elmortem.agentbridge/`.
- Рабочий каталог моста: `AgentBridgeUnity/Library/AgentBridge/`.
  - `Inbox/` — входящие задачи
  - `Journal/` — записи задач (единственный источник состояния и результата)
  - `Artifacts/<TaskId>/` — артефакты задачи (дампы, скриншоты)
  - `Roslyn/` — DLL Roslyn при источнике NuGet или Local
  - `status.json` — состояние домена
  - `heartbeat` — признак живого редактора
  - `bridge.sh`, `bridge.ps1` — клиент
- Настройки: `AgentBridgeUnity/ProjectSettings/AgentBridge.json`.
- Легаси-inbox (живёт до Unit 20): `AgentBridgeUnity/Assets/Editor/AgentBridge/`.

### Ренейм-карта (применяется механически, Unit 2)

- `namespace CoworkBridge` → `namespace AgentBridge`; `CoworkBridge.Ui` → `AgentBridge.Ui`
- `CoworkBridge.cs` / `class CoworkBridge` → `AgentBridge.cs` / `class AgentBridge`
- `CoworkBridgeSettings` → `AgentBridgeSettings`; `CoworkBridgeSettingsStore` → `AgentBridgeSettingsStore`
- `CoworkEditorWakeTimer` → `AgentEditorWakeTimer`; `CoworkTestRunner` → `AgentTestRunner`
- `CoworkBridge.asmdef` → `AgentBridge.asmdef`, поле `name` → `AgentBridge`
- `com.elmortem.coworkbridge` → `com.elmortem.agentbridge`; `displayName` → `Unity Agent Bridge`
- `ProjectSettings/CoworkBridge.json` → `ProjectSettings/AgentBridge.json`
- `Assets/Editor/CoworkBridge/` → `Assets/Editor/AgentBridge/`
- меню `Tools/Cowork Bridge/...` → `Tools/Agent Bridge/...`
- префикс логов `[CoworkBridge]` → `[AgentBridge]`
- ключи `SessionState`: `CoworkBridge_*` → `AgentBridge_*`
- `UNITYCOWORK.md` → `UNITYAGENT.md`; `UNITYCOWORK-UI.md` → `UNITYAGENT-UI.md`; шаблоны в `Docs/` аналогично
- Не трогать: `Docs/tdd/done/**` (архив), `.git/**`

### Типы данных (один тип — один файл, namespace `AgentBridge`)

```csharp
[Serializable]
public class TaskRequest
{
	public string Id;
	public string Kind;
	public string PayloadFile;
	public string TestMode;
	public string[] AssemblyNames;
	public string[] TestNames;
	public string[] CategoryNames;
}
```

`Kind` принимает ровно одно из: `csharp`, `ui`, `compile`, `tests`.
`PayloadFile` — имя файла рядом с `<TaskId>.task.json` в `Inbox`: `<TaskId>.cs` для `csharp`, `<TaskId>.ui.json` для `ui`, пустая строка для `compile` и `tests`.

```csharp
[Serializable]
public class TaskRecord
{
	public string Id;
	public string Kind;
	public string Status;
	public string Hash;
	public string ReturnValue;
	public List<string> Logs = new List<string>();
	public List<TaskDiagnostic> Diagnostics = new List<TaskDiagnostic>();
	public bool ForeignErrors;
	public List<string> Artifacts = new List<string>();
	public TestRunResult Tests;
	public TaskTiming Timing = new TaskTiming();
	public string SessionId;
	public string StartedAtUtc;
	public string FinishedAtUtc;
}

[Serializable]
public class TaskDiagnostic
{
	public string Code;
	public string Severity;
	public string Message;
	public string File;
	public int Line;
	public int Column;
}

[Serializable]
public class TaskTiming
{
	public int QueuedMs;
	public int CompileMs;
	public int ExecuteMs;
	public int TotalMs;
}

[Serializable]
public class BridgeStatus
{
	public string SessionId;
	public string AssemblyBuildTimeUtc;
	public bool Enabled;
	public string RoslynSource;
	public bool RoslynReady;
	public bool SignalTickAvailable;
	public int LoadedTaskAssemblies;
	public int ExecutedTasks;
	public string ActiveTaskId;
}
```

`TestRunResult`, `TestFailure` уже существуют и переиспользуются как есть.

### Статусы задачи

Промежуточные: `queued`, `compiling`, `running`.
Терминальные: `success`, `compiler_error`, `runtime_error`, `timeout`, `canceled`, `interrupted_by_domain_reload`, `rejected`.

Запись в `Journal/<TaskId>.json` создаётся сразу при приёме задачи со статусом `queued` и переписывается при каждой смене статуса. Любая запись — во временный файл `<TaskId>.json.tmp` с последующим `File.Replace`/`File.Move`, чтобы клиент никогда не читал полуфайл.

### Идемпотентность

При приёме задачи мост считает SHA-256 от содержимого `<TaskId>.task.json` плюс содержимого `PayloadFile` (если он есть) и кладёт в `TaskRecord.Hash`. Если запись с таким `Id` уже есть:

- совпал хеш и статус терминальный → ничего не выполнять, оставить запись как есть;
- совпал хеш и статус промежуточный → ничего не делать, задача уже в работе;
- хеш не совпал → записать статус `rejected` с текстом `id_conflict` в `Logs` и не выполнять.

### Настройки (`AgentBridgeSettings`, файл `ProjectSettings/AgentBridge.json`)

```csharp
public bool Enabled;
public int KeepCompletedCount = 10;
public int TaskTimeoutSeconds = 300;
public int IdleTickIntervalMs = 500;
public int ActiveTickIntervalMs = 33;
public string RoslynSource = "Auto";
public string RoslynLocalPath = "";
public bool EmitPdb = true;
public int ClientWaitSeconds = 110;
```

`RoslynSource` принимает: `Auto`, `UnityBuiltin`, `Project`, `NuGet`, `Local`.

### Контракт таск-кода (`kind = csharp`)

Поддерживаются ровно две сигнатуры, обе `public static` в `public static class` с именем, равным `<TaskId>`:

```csharp
public static Task<string> Run()
public static Task<string> Run(CancellationToken cancellationToken)
```

Если есть обе — вызывается вторая. Другие сигнатуры → `rejected`.

### Клиент

`bash AgentBridgeUnity/Library/AgentBridge/bridge.sh <команда> [аргументы]`

- `csharp <path-to-cs>` — копирует исходник в `Inbox`, создаёт задачу, ждёт результат
- `ui <path-to-ui-json>` — то же для UI-задачи
- `compile` — задача компиляции проекта
- `tests [--mode EditMode|PlayMode] [--assembly A] [--test T] [--category C]` — прогон тестов
- `wait <TaskId>` — до-дождаться уже созданной задачи
- `status` — прочитать `status.json` и `heartbeat`

Общий флаг `--wait <секунды>`, по умолчанию `ClientWaitSeconds` из настроек, иначе 110.

Поведение: `TaskId` генерируется клиентом как `Task_YYYYMMDD_HHMMSS`; корень проекта клиент вычисляет от собственного расположения (`<script>/../..`); прогресс печатается в stderr раз в 5 секунд; в stdout попадает ровно один JSON — содержимое `Journal/<TaskId>.json`.

Коды выхода: `0` — статус `success`; `1` — любой другой терминальный статус; `2` — предел ожидания исчерпан, задача ещё идёт (в stdout `{"Id":"...","Status":"running"}`); `3` — мост недоступен или ошибка использования.

Мост считается недоступным, если файла `heartbeat` нет или его содержимое старше 15 секунд.

## Invariants (must hold throughout)

- Изменяются только файлы внутри корня репозитория. Ничего за его пределами.
- `Docs/tdd/done/**` не изменяется.
- Публичный формат UI-задач не меняется: `prefab`, `actions`, `apply`/`delete`/`dump`/`shot` и семантика узлов остаются как в текущем `unity-ui` SKILL.md.
- Зависимости `package.json` не растут: только `com.unity.test-framework`.
- Ни один файл не создаётся и не изменяется под `AgentBridgeUnity/Assets/` при выполнении задачи типа `csharp` — кроме того, что делает сам код задачи.
- В коде нет комментариев, отступы — табы, каждый тип лежит в своём файле.
- После каждого юнита старый путь исполнения продолжает работать вплоть до Unit 20.

## Execution Plan

Юниты выполняются строго по порядку.

### Unit 1 — Переезд пакета в Unity-проект

- Goal: пакет лежит внутри `AgentBridgeUnity`, Unity его видит, старый мост отвечает на задачу.
- Touch: переместить `CoworkBridge/` → `AgentBridgeUnity/Packages/com.elmortem.coworkbridge/` командой `git mv`; создать `.gitignore` записи; создать `AgentBridgeUnity/ProjectSettings/CoworkBridge.json` с `{"Enabled":true,"KeepCompletedCount":10,"AsyncTimeoutSeconds":300}`.
- How: в корневой `.gitignore` добавить строки `AgentBridgeUnity/Library/`, `AgentBridgeUnity/Temp/`, `AgentBridgeUnity/Logs/`, `AgentBridgeUnity/obj/`, `AgentBridgeUnity/UserSettings/`, `AgentBridgeUnity/*.csproj`, `AgentBridgeUnity/*.sln`. Затем дождаться, пока Unity подхватит embedded-пакет: проверять появление `AgentBridgeUnity/Assets/Editor/CoworkBridge/wait-for-result.sh` (мост создаёт его при загрузке домена) в цикле до 120 секунд.
- Gate: файл `AgentBridgeUnity/Assets/Editor/CoworkBridge/wait-for-result.sh` существует; затем создать `AgentBridgeUnity/Assets/Editor/CoworkBridge/Task_probe.cs` с классом `Task_probe`, возвращающим `"probe ok"`, и выполнить `bash AgentBridgeUnity/Assets/Editor/CoworkBridge/wait-for-result.sh Task_probe 300` — в выводе `"status": "success"` и `"return_value": "probe ok"`.
- On failure: если за 120 секунд файл не появился или задача не прошла — остановиться и сообщить: «Открой AgentBridgeUnity в Unity, дай ему импортировать пакет и включи Tools → Cowork Bridge → Start». Не пытаться запускать Unity, не менять код моста.

### Unit 2 — Тотальный ренейм Cowork → Agent

- Goal: во всём репозитории, кроме архива и `.git`, нет строки `cowork` в любом регистре; мост под новыми именами отвечает на задачу.
- Touch: все файлы пакета, `README.md`, `Docs/UNITYCOWORK-template.md`, `Docs/UNITYCOWORK-UI-template.md`, `unity-bridge-plugin/.claude-plugin/plugin.json`, оба `unity-bridge-plugin/skills/*/SKILL.md`.
- How: применить ренейм-карту из Foundations. Порядок обязателен, иначе теряется актуатор:
  - переименовать каталог пакета `git mv AgentBridgeUnity/Packages/com.elmortem.coworkbridge AgentBridgeUnity/Packages/com.elmortem.agentbridge`;
  - переименовать файлы и правкой заменить содержимое по карте;
  - заранее создать `AgentBridgeUnity/ProjectSettings/AgentBridge.json` с `Enabled: true` и заранее создать каталог `AgentBridgeUnity/Assets/Editor/AgentBridge/`;
  - в `README.md` заменить git-URL установки на `https://github.com/elmortem/unitycoworkbridge.git?path=AgentBridgeUnity/Packages/com.elmortem.agentbridge` и все упоминания путей;
  - пересобрать `unity-bridge-plugin/unity-bridge-plugin.zip` из содержимого `unity-bridge-plugin/` (zip корнем должен быть `.claude-plugin/` и `skills/`, как в текущем архиве);
  - поднять `version` в `package.json` до `0.6.0`, в `plugin.json` до `1.3.0`;
  - выполнить старой задачей (через легаси-путь, он ещё жив под старым именем каталога `Assets/Editor/CoworkBridge`) вызов `AssetDatabase.Refresh()`, чтобы Unity перекомпилировала пакет.
- Gate: `grep -rn -i "cowork" --exclude-dir=.git --exclude-dir=done --exclude-dir=Library --exclude-dir=Temp --exclude-dir=obj . | grep -v "unitycoworkbridge.git"` не возвращает строк; затем задача-проба в новом каталоге: `bash AgentBridgeUnity/Library/../Assets/Editor/AgentBridge/wait-for-result.sh Task_rename 300` возвращает `"status": "success"`.
- On failure: если после ренейма мост перестал отвечать — вернуть `Enabled: true` в `ProjectSettings/AgentBridge.json`, проверить, что имя каталога пакета и `name` в `package.json` совпадают, повторить не более 3 раз, затем остановиться и сообщить, что нужен ручной перезапуск Unity.

### Unit 3 — Разрешения для автономного прогона

- Goal: агент может запускать клиент моста без запроса прав.
- Touch: `.claude/settings.local.json`.
- How: в `permissions.allow` удалить `"Bash(bash Assets/Editor/CoworkBridge/wait-for-result.sh:*)"`, `"Bash(ls Assets/Editor/CoworkBridge/*.cs)"`, `"Bash(ls Assets/Editor/CoworkBridge/wait-for-result.sh)"`; добавить `"Bash(bash AgentBridgeUnity/Assets/Editor/AgentBridge/wait-for-result.sh:*)"`, `"Bash(bash AgentBridgeUnity/Library/AgentBridge/bridge.sh:*)"`, `"Bash(git mv:*)"`.
- Gate: `grep -c "AgentBridge" .claude/settings.local.json` возвращает не меньше 2.
- On failure: одна попытка, затем остановиться и сообщить.

### Unit 4 — Настройки, статус и heartbeat

- Goal: мост пишет состояние домена в файл, по которому агент видит, что его правка доехала.
- Touch: `AgentBridgeSettings.cs` (новые поля из Foundations), `AgentBridgeSettingsStore.cs` (геттеры новых полей с дефолтами), новые `BridgeStatus.cs`, `BridgePaths.cs`, `BridgeStatusWriter.cs`.
- How: `BridgePaths` — статический класс с путями из Foundations, все производятся от `Path.GetDirectoryName(Application.dataPath)`, каталоги создаются при первом обращении. `BridgeStatusWriter.WriteOnLoad()` вызывается из `[InitializeOnLoad]` и пишет `status.json` атомарно: `SessionId` — новый GUID на домен, `AssemblyBuildTimeUtc` — `File.GetLastWriteTimeUtc(typeof(BridgeStatusWriter).Assembly.Location)` в формате `o`. `BridgeStatusWriter.Beat()` пишет в файл `heartbeat` текущее время Unix в миллисекундах, но не чаще раза в 2 секунды; вызывается из `EditorApplication.update`.
- Gate: задача через легаси-путь читает `Library/AgentBridge/status.json` и возвращает его содержимое — в выводе непустой `SessionId` и `AssemblyBuildTimeUtc`; повторное чтение файла `heartbeat` через 5 секунд даёт большее число.
- On failure: ≤3 попытки, затем остановиться и сообщить.

### Unit 5 — EditorTickPump

- Goal: редактор без фокуса продолжает крутить `EditorApplication.update` с заданным интервалом, а не замирает.
- Touch: новый `EditorTickPump.cs`; `AgentEditorWakeTimer.cs` остаётся как fallback.
- How: один раз найти `EditorApplication.SignalTick` через reflection (`typeof(EditorApplication).GetMethod("SignalTick", BindingFlags.NonPublic | BindingFlags.Static)`), создать делегат `Action` через `Delegate.CreateDelegate` и звать его из `EditorApplication.update` не чаще, чем раз в текущий интервал. Интервал: `ActiveTickIntervalMs`, если есть активная задача, иначе `IdleTickIntervalMs`. Ставить `Application.runInBackground = true` при старте. Если метод не найден — `SignalTickAvailable = false` в статусе, один `Debug.LogWarning`, и вместо него `AgentEditorWakeTimer.Start()`. Отписка в `beforeAssemblyReload` и `EditorApplication.quitting`.
- Gate: задача через легаси-путь возвращает `SignalTickAvailable` из статуса; затем, не переключая фокус на Unity, прочитать `heartbeat` дважды с паузой 10 секунд — второе значение больше первого минимум на 8000.
- On failure: если `SignalTick` не найден, это не провал — зафиксировать `false` и идти дальше. Если heartbeat не растёт вообще — ≤3 попытки, затем остановиться.

### Unit 6 — MainThreadDispatcher

- Goal: код, поставленный в очередь из любого потока, исполняется в main thread редактора.
- Touch: новый `MainThreadDispatcher.cs`.
- How: `ConcurrentQueue<Action>` плюс `Enqueue(Action)` и `Enqueue<T>(Func<T>)`, возвращающий `Task<T>` через `TaskCompletionSource<T>`. Обработка из `EditorApplication.update`, не более 16 элементов за тик. Исключение внутри элемента переносится в `TaskCompletionSource`, а не гасит очередь. Очередь очищается в `beforeAssemblyReload` с переводом незавершённых `TaskCompletionSource` в `SetCanceled`.
- Gate: задача через легаси-путь: из `Task.Run` вызвать `MainThreadDispatcher.Enqueue(() => UnityEditor.EditorApplication.timeSinceStartup)`, дождаться, вернуть строку — статус `success`, исключений в логах нет.
- On failure: ≤3 попытки, затем остановиться.

### Unit 7 — Резолвер Roslyn и проба источников

- Goal: мост умеет находить сборки Roslyn из трёх источников и честно сообщает, какие из них рабочие.
- Touch: новые `RoslynSourceKind.cs`, `RoslynLocation.cs`, `RoslynResolver.cs`, `RoslynProbe.cs`.
- How: `RoslynSourceKind` — enum `Auto`, `UnityBuiltin`, `Project`, `NuGet`, `Local`. Источники:
  - `UnityBuiltin` — искать `Microsoft.CodeAnalysis.CSharp.dll` в `EditorApplication.applicationContentsPath` рекурсивно, глубина не более 4;
  - `Project` — искать `Microsoft.CodeAnalysis.CSharp` среди уже загруженных сборок `AppDomain.CurrentDomain.GetAssemblies()`, затем рекурсивно в `Assets/`;
  - `NuGet`/`Local` — каталог `Library/AgentBridge/Roslyn/` либо `RoslynLocalPath`.
  Проверка источника — не наличие файла, а фактическая загрузка: `Assembly.LoadFrom` найденной DLL и успешное получение типа `Microsoft.CodeAnalysis.CSharp.CSharpSyntaxTree`. Неудача любого рода ловится и превращается в текст причины. `RoslynResolver` ставит обработчик `AppDomain.CurrentDomain.AssemblyResolve`, который отдаёт DLL из выбранного каталога только для имён, ещё не загруженных в домен. `Auto` пробует порядок `Project`, `Local`, `NuGet`, `UnityBuiltin` и берёт первый рабочий.
- Gate: задача через легаси-путь зовёт `RoslynProbe.Run()` и возвращает строку вида `UnityBuiltin=<ok|причина>; Project=...; NuGet=...; Local=...` — строка присутствует в `return_value`, и хотя бы один источник `ok`.
- On failure: если ни один источник не рабочий — не изобретать обходных путей, перейти к Unit 8, поставить в отчёте пометку и продолжить: Unit 8 даёт способ доустановить Roslyn. Если и после Unit 8 ни один источник не рабочий — остановиться и сообщить.

### Unit 8 — Окно установки

- Goal: пользователь видит список источников Roslyn с реальным статусом, ставит нужный кнопкой и может вернуться и поменять выбор.
- Touch: новые `AgentBridgeSetupBootstrap.cs`, `AgentBridgeSetupWindow.cs`, `RoslynInstaller.cs`.
- How: повторить структуру PCG-сетапа. `[InitializeOnLoad]` bootstrap через `EditorApplication.delayCall`: если `Application.isBatchMode` — выход; если резолвер даёт рабочий источник — выход; если в `SessionState` стоит ключ `AgentBridge_SetupDismissed` — выход; иначе открыть окно. Окно фиксированного размера 460×320, заголовок `Unity Agent Bridge Setup`, открывается также из меню `Tools/Agent Bridge/Setup...`. По строке на источник: название, короткое описание, справа либо `Ready`, либо `Not found`, либо причина несовместимости, и кнопка `Use` для рабочих; для `NuGet` — кнопка `Download`. Кнопки заблокированы, пока `RoslynInstaller.IsBusy`. Выбор пишется в `RoslynSource` настроек. `RoslynInstaller` качает через `UnityWebRequest` пакеты с `https://api.nuget.org/v3-flatcontainer/<id>/<version>/<id>.<version>.nupkg` и распаковывает `lib/netstandard2.0/*.dll` через `System.IO.Compression.ZipFile` в `Library/AgentBridge/Roslyn/`. Набор и версии фиксированы: `microsoft.codeanalysis.common 4.9.2`, `microsoft.codeanalysis.csharp 4.9.2`, `system.collections.immutable 8.0.0`, `system.reflection.metadata 8.0.0`, `system.runtime.compilerservices.unsafe 6.0.0`, `system.memory 4.5.5`, `system.buffers 4.5.1`, `system.numerics.vectors 4.5.0`, `system.threading.tasks.extensions 4.5.4`, `system.text.encoding.codepages 8.0.0`. `IsBusy` и событие `Completed` — как у `PcgPackageInstaller`.
- Gate: задача через легаси-путь открывает окно (`AgentBridgeSetupWindow.Open()`) и закрывает его, возвращает `"setup window ok"` — статус `success`, в логах нет исключений; затем повторная проба из Unit 7 показывает хотя бы один `ok`.
- On failure: ≤3 попытки. Если скачивание с NuGet не проходит — не искать зеркал и не менять версии, оставить источник со статусом причины и идти дальше, если рабочим оказался другой источник.

### Unit 9 — ReferenceCatalog

- Goal: компилятор получает ссылки на все сборки домена без пересборки списка на каждую задачу.
- Touch: новый `ReferenceCatalog.cs`.
- How: снапшот строится при первом обращении: все сборки `AppDomain.CurrentDomain.GetAssemblies()`, у которых `IsDynamic == false`, непустой `Location` и файл существует. Исключить сборки, имя которых начинается с `AgentTask_`. `MetadataReference` кэшируются в `Dictionary<string, MetadataReference>` по нормализованному пути; запись считается валидной, пока совпадают размер файла и `LastWriteTimeUtc`. Снапшот сбрасывается в `CompilationPipeline.compilationFinished` и в `afterAssemblyReload`. Публичный API: `IReadOnlyList<MetadataReference> GetReferences()`, `void Invalidate()`, `int Count`.
- Gate: задача через легаси-путь возвращает `ReferenceCatalog.Count` дважды подряд — оба значения больше 50 и равны между собой.
- On failure: ≤3 попытки, затем остановиться.

### Unit 10 — SourceGuardrail

- Goal: код, который заблокирует главный поток, отклоняется до исполнения.
- Touch: новый `SourceGuardrail.cs`, новый `GuardrailViolation.cs`.
- How: работает по `SyntaxTree`, полученному от Roslyn. Отклоняются:
  - вызов метода `Wait` на любом выражении;
  - `GetAwaiter()` с последующим `GetResult()`;
  - обращение к члену `Result` на результате вызова метода или на идентификаторе, имя которого заканчивается на `Task` или `task`;
  - `Thread.Sleep` в любом виде;
  - `while (true)` и `for (;;)`, в теле которых нет ни одного `await`.
  На каждое нарушение — `GuardrailViolation` с текстом причины, строкой и колонкой. Метод `bool TryValidate(SyntaxTree tree, out List<GuardrailViolation> violations)`.
- Gate: после Unit 14 повторно проверяется реальной задачей. Здесь гейт статический: `grep -n "Thread.Sleep\|GetAwaiter\|while (true)" AgentBridgeUnity/Packages/com.elmortem.agentbridge/Editor/SourceGuardrail.cs` возвращает не меньше 3 строк.
- On failure: ≤3 попытки, затем остановиться.

### Unit 11 — RoslynCompiler

- Goal: исходник задачи компилируется в память с корректной диагностикой и номерами строк.
- Touch: новые `RoslynCompiler.cs`, `CompileResult.cs`.
- How: `CompileResult` несёт `Assembly Assembly`, `List<TaskDiagnostic> Diagnostics`, `bool Success`. Парсинг: `CSharpSyntaxTree.ParseText(source, path: <полный путь к сохранённому .cs>, cancellationToken: token)` с `LanguageVersion.Latest`. После парсинга — `SourceGuardrail.TryValidate`; при нарушениях вернуть `Success = false` и специальный признак, который координатор превратит в `rejected`. Компиляция: `CSharpCompilation.Create("AgentTask_" + taskId + "_" + counter, new[]{tree}, ReferenceCatalog.GetReferences(), new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary, optimizationLevel: OptimizationLevel.Debug))`. Emit в `MemoryStream`; при `EmitPdb` — второй `MemoryStream` под PDB и `Assembly.Load(peBytes, pdbBytes)`, иначе `Assembly.Load(peBytes)`. Диагностики уровня `Error` и `Warning` переносятся в `TaskDiagnostic` с `Code`, `Severity`, `Message` и позицией из `Location.GetLineSpan()` (строки и колонки — с единицы, не с нуля). Весь метод выполняется не в main thread и принимает `CancellationToken`.
- Gate: задача через легаси-путь компилирует строку `public static class T { public static int X() { return y; } }` и возвращает первую диагностику — в `return_value` есть `CS0103` и `line` равен 1; вторая компиляция валидного класса возвращает `"compiled ok"`.
- On failure: ≤3 попытки, затем остановиться и сообщить, какой источник Roslyn использовался.

### Unit 12 — Журнал

- Goal: состояние и результат каждой задачи лежат в одном атомарно записываемом файле, старые записи подрезаются.
- Touch: новые `TaskRecord.cs`, `TaskDiagnostic.cs`, `TaskTiming.cs`, `TaskJournal.cs`.
- How: `TaskJournal.Write(TaskRecord)` пишет `Journal/<Id>.json.tmp` и переносит поверх целевого файла. `TaskJournal.TryRead(string id, out TaskRecord)`. `TaskJournal.Trim(int keep)` оставляет `keep` самых свежих по `FinishedAtUtc` (записи без него считаются самыми свежими), удаляет остальные вместе с `Inbox/<Id>.*` и `Artifacts/<Id>/`. `Trim` вызывается на простое, не чаще раза в 30 секунд.
- Gate: задача через легаси-путь пишет 12 записей и зовёт `Trim(10)`; затем `ls AgentBridgeUnity/Library/AgentBridge/Journal | wc -l` возвращает `10`.
- On failure: ≤3 попытки, затем остановиться.

### Unit 13 — TaskCoordinator и inbox

- Goal: задачи из `Library/AgentBridge/Inbox` подхватываются, выполняются строго по одной и получают терминальный статус.
- Touch: новые `TaskCoordinator.cs`, `TaskRequest.cs`, `TaskContext.cs`, `TaskLogScope.cs`; правка `AgentBridge.cs` — добавить вызов запуска координатора, ничего не удаляя.
- How: сканирование `Inbox` из `EditorApplication.update` не чаще раза в 250 мс. Берётся самый старый `*.task.json`, для которого в журнале нет терминальной записи. Пока `EditorApplication.isCompiling` — не стартовать. Пока есть активная задача — не стартовать вторую. Последовательность статусов: `queued` → (`compiling` только для `csharp`) → `running` → терминальный. Идемпотентность — по правилам из Foundations. `TaskContext` несёт `Id`, `Kind`, `CancellationToken`, путь к каталогу артефактов и метод `AddArtifact(string relativePath)`. `TaskLogScope` подписывается на `Application.logMessageReceivedThreaded`, копит строки в потокобезопасный буфер и отдаёт их в `TaskRecord.Logs` при завершении; одновременно активен ровно один scope. Таймаут: при превышении `TaskTimeoutSeconds` записать `timeout`, отменить токен и на следующем тике вызвать `EditorUtility.RequestScriptReload()`. При `beforeAssemblyReload` все незавершённые записи переводятся в `interrupted_by_domain_reload` и повторно не запускаются.
- Gate: положить вручную `Inbox/Task_c1.task.json` с `Kind: "compile"` и через 30 секунд `cat AgentBridgeUnity/Library/AgentBridge/Journal/Task_c1.json` показывает терминальный статус (на этом шаге допустим `rejected` с текстом «unknown kind» — сам `compile` появится в Unit 16).
- On failure: ≤3 попытки, затем остановиться.

### Unit 14 — kind = csharp

- Goal: C#-задача компилируется Roslyn, стартует в main thread, переживает `await` и отдаёт результат без domain reload и без файлов в `Assets`.
- Touch: новые `CSharpTaskExecutor.cs`, `TaskMethodResolver.cs`; правка `TaskCoordinator.cs` — ветка `csharp`.
- How: исходник читается из `Inbox/<Id>.cs`. Компиляция — в worker thread через `Task.Run`. Полученная сборка ищется по имени типа, равному `<TaskId>`, только в этой сборке — обхода всех сборок домена больше нет. Разрешение метода: сначала `Run(CancellationToken)`, затем `Run()`; тип возврата обязан быть `Task<string>`, иначе `rejected`. Вызов метода — через `MainThreadDispatcher`. Ожидание завершения `Task<string>` — из `EditorApplication.update`, как в текущем `AsyncTaskWatcher`, но состояние живёт в координаторе. Счётчики загруженных task-сборок и суммарного размера PE пишутся в `status.json`.
- Gate: три подряд команды, каждая возвращает ожидаемое:
  - задача с `await Task.Delay(1500)` и возвратом строки → `"Status": "success"` и правильный `ReturnValue`;
  - задача с `Thread.Sleep(100)` → `"Status": "rejected"` и упоминание guardrail в `Logs`;
  - `git status --porcelain AgentBridgeUnity/Assets` после всех трёх — пустой вывод.
  Задачи на этом шаге кладутся руками: файл `Inbox/<Id>.cs` плюс `Inbox/<Id>.task.json`, результат читается из `Journal/<Id>.json`.
- On failure: ≤3 попытки на каждую из трёх проверок, затем остановиться и сообщить, какая именно провалилась.

### Unit 15 — kind = ui

- Goal: существующие UI-задачи работают через новый координатор, артефакты лежат в `Library`.
- Touch: `Ui/UiTaskRunner.cs` — сменить сигнатуру на `TaskResultData Execute(string payloadPath, TaskContext context)` и убрать вызов записи результата; `Ui/UiTaskArtifacts.cs` — корень артефактов берётся из `TaskContext`; правка `TaskCoordinator.cs` — ветка `ui`.
- How: логика применения узлов, `dump` и `shot` не меняется ни на строку — меняются только вход (путь к payload вместо `taskId` + `coworkPath`) и выход (возврат данных вместо записи файла). Каждый созданный файл регистрируется через `context.AddArtifact`.
- Gate: положить UI-задачу, создающую префаб `Assets/Prefabs/AgentBridgeProbe.prefab` с одним дочерним узлом и делающую `shot`; `Journal/<Id>.json` имеет `"Status": "success"`, а `Artifacts` содержит путь к PNG, который существует на диске и весит больше 1000 байт.
- On failure: ≤3 попытки. Не переписывать `UiNodeApplier`, `UiDumper`, `UiScreenshot` — если падает внутри них, значит сломан вход; чинить вход.

### Unit 16 — kind = compile

- Goal: агент может заставить Unity скомпилировать проект и получить список ошибок, переживая вызванный этим domain reload.
- Touch: новый `CompileTaskExecutor.cs`; правка `TaskCoordinator.cs`.
- How: записать в `SessionState` ключ `AgentBridge_CompileTask` с `Id`, перевести запись в `running`, вызвать `AssetDatabase.Refresh()` и `CompilationPipeline.RequestScriptCompilation()`. Подписки `CompilationPipeline.assemblyCompilationFinished` копят ошибки в статическое поле и дублируют их в файл `Library/AgentBridge/pending_<Id>.json`, потому что домен будет выгружен. После `afterAssemblyReload` координатор видит ключ в `SessionState`, читает файл ошибок, удаляет его и пишет терминальный статус: `success` при пустом списке, иначе `compiler_error`. `ForeignErrors` считается как раньше — истина, если хоть одна ошибка не относится к файлам задачи. Если компиляция не потребовалась и reload не произошёл, завершить задачу по таймеру 20 секунд статусом `success`.
- Gate: две проверки:
  - создать `AgentBridgeUnity/Assets/Editor/Broken.cs` с заведомой ошибкой, выполнить задачу `compile` — `"Status": "compiler_error"`, `Diagnostics` содержит `CS`-код, `ForeignErrors` равно `true`; удалить файл;
  - выполнить задачу `compile` на чистом проекте — `"Status": "success"`.
- On failure: ≤3 попытки. Обязательно удалить `Broken.cs` даже при провале.

### Unit 17 — kind = tests

- Goal: прогон тестов — одна задача с одним результатом, без второго файла и второго ожидания.
- Touch: `AgentTestRunner.cs` — вернуть результат в координатор вместо записи `testresult_*`; правка `TaskCoordinator.cs`.
- How: `Kind = "tests"` читает `TestMode`, `AssemblyNames`, `TestNames`, `CategoryNames` из `TaskRequest`. Логика фильтров и `SaveOpenScenes` перед PlayMode сохраняется. Идентификатор задачи кладётся в `SessionState`, callback моста остаётся персистентным, поэтому прогон переживает входы и выходы из Play Mode. По завершении `TestRunResult` кладётся в `TaskRecord.Tests`, статус — `success`, если прогон состоялся (включая красные тесты), и `runtime_error`, если прогон не стартовал; поле `Tests.aborted` при этом истинно, а причина лежит в `Tests.message`.
- Gate: создать `AgentBridgeUnity/Assets/Tests/Editor/AgentBridgeProbeTests.cs` с asmdef и двумя тестами — один проходит, один падает; выполнить задачу `tests` с `TestMode: "EditMode"` и `AssemblyNames: ["AgentBridge.ProbeTests"]` — `"Status": "success"`, `Tests.passed` равно 1, `Tests.failed` равно 1.
- On failure: ≤3 попытки. Тестовые файлы оставить в проекте — они нужны финальному гейту.

### Unit 18 — Клиент

- Goal: одна команда создаёт задачу, ждёт её и печатает результат; работает в Windows, macOS и Linux, без Python и Node.
- Touch: новые `bridge.sh` и `bridge.ps1` в корне пакета, новый `ClientInstaller.cs` (по образцу нынешнего `WaitScriptInstaller`), `.gitattributes` — правило `*.sh text eol=lf`.
- How: оба скрипта реализуют одинаковый интерфейс из Foundations и не имеют внешних зависимостей: только встроенные средства оболочки. Корень проекта вычисляется от расположения скрипта. Генерация `TaskId` — из локального времени. Ожидание — цикл со сном 1 секунда, чтением `Journal/<TaskId>.json` и проверкой поля `"Status"` на принадлежность терминальному множеству. Проверка живости — файл `heartbeat`. Прогресс печатается в stderr раз в 5 секунд строкой `[bridge] <TaskId> <kind> <секунды>s`. `ClientInstaller` копирует оба файла из пакета в `Library/AgentBridge/` при загрузке домена, если их там нет или они отличаются по размеру.
- Gate: `bash AgentBridgeUnity/Library/AgentBridge/bridge.sh status` печатает JSON с `SessionId`; затем C#-задача целиком через клиент: записать файл во временный путь и выполнить `bash AgentBridgeUnity/Library/AgentBridge/bridge.sh csharp <path>` — код выхода 0 и `"Status": "success"` в stdout.
- On failure: ≤3 попытки на `bridge.sh`. `bridge.ps1` проверить нельзя из bash — считать его готовым, если он реализует те же команды и присутствует в `Library`; проверка PowerShell-версии остаётся человеку.

### Unit 19 — Скиллы и документация

- Goal: агент, читающий скилл, работает по новому протоколу и ничего не знает о файлах в `Assets`.
- Touch: `unity-bridge-plugin/skills/unity-bridge/SKILL.md`, `unity-bridge-plugin/skills/unity-ui/SKILL.md`, `AgentBridgeUnity/Packages/com.elmortem.agentbridge/UNITYAGENT.md`, `Docs/UNITYAGENT-template.md`, `Docs/UNITYAGENT-UI-template.md`, `README.md`, `unity-bridge-plugin/unity-bridge-plugin.zip`.
- How: в `unity-bridge` SKILL.md заменить весь протокол выполнения на: сгенерировать имя задачи, написать `.cs` во временный файл, выполнить `bash <project>/Library/AgentBridge/bridge.sh csharp <файл>`, прочитать JSON из stdout. Добавить разделы про `compile` и `tests` как отдельные команды. Убрать всё про `wait-for-result.sh`, `clean.command`, `result_*.json`, `.done`, `foreign_errors` как файловый признак (остаётся поле `ForeignErrors` в ответе), про починку битого `.cs` ради разблокировки компиляции редактора — задачи больше не участвуют в компиляции проекта, поэтому битый исходник ничего не блокирует, и правило «перепиши в no-op» удаляется. Указать правило разрешений `Bash(bash Library/AgentBridge/bridge.sh:*)`. В обоих скиллах поиск файлов конвенций — по обоим именам: сначала `UNITYAGENT.md`/`UNITYAGENT-UI.md`, затем `UNITYCOWORK.md`/`UNITYCOWORK-UI.md`. `UNITYAGENT.md` пакета переписать с шаблона C#-задачи, зовущей `RequestRun`, на описание команды `bridge.sh tests`. README привести в соответствие: новое имя, новый путь пакета, новый протокол, раздел про `wait-for-result.sh` убрать. Пересобрать zip плагина.
- Gate: `grep -rn "wait-for-result\|result_<TaskName>\|clean.command\|Assets/Editor/CoworkBridge\|Assets/Editor/AgentBridge" unity-bridge-plugin README.md` не возвращает строк; `unzip -l unity-bridge-plugin/unity-bridge-plugin.zip` показывает оба SKILL.md.
- On failure: ≤3 попытки, затем остановиться.

### Unit 20 — Удаление старого пути

- Goal: в пакете не осталось файлового watcher'а по `Assets`, компиляции задач силами Unity и shell-скрипта ожидания результата.
- Touch: удалить `WaitScriptInstaller.cs`, `wait-for-result.sh`, `TaskCleaner.cs`, `ResultWriter.cs`, `AsyncTaskWatcher.cs`, `TaskData.cs`, `TaskResult.cs`, `CompilerError.cs`, `CompilerErrorList.cs` и все их `.meta`; из `AgentBridge.cs` удалить сканирование `Assets/Editor/AgentBridge`, обработку `PendingTaskKey`, подписки на `CompilationPipeline` под старую схему, пункты меню `Run Task...`, `Clean Completed`, `Clean All` и обработку `clean.command`. Оставить: `Start`, `Stop`, `Cancel Running Task`, `Setup...`. Удалить каталог `AgentBridgeUnity/Assets/Editor/AgentBridge/`.
- How: `TaskRunner.cs` заменяется на `TaskMethodResolver.cs` из Unit 14, если ещё не заменён. `AgentEditorWakeTimer.cs` остаётся — он fallback для `EditorTickPump`. После удаления выполнить `compile` через клиент и убедиться, что проект собирается.
- Gate: четыре проверки подряд:
  - `ls AgentBridgeUnity/Packages/com.elmortem.agentbridge/wait-for-result.sh` возвращает ошибку «нет такого файла»;
  - `bash AgentBridgeUnity/Library/AgentBridge/bridge.sh compile` → `"Status": "success"`;
  - `bash AgentBridgeUnity/Library/AgentBridge/bridge.sh csharp <файл с await и Debug.Log>` → `"Status": "success"`, логи содержат строку из `Debug.Log`;
  - `bash AgentBridgeUnity/Library/AgentBridge/bridge.sh tests --mode EditMode --assembly AgentBridge.ProbeTests` → `"Status": "success"`, `Tests.total` равно 2;
  - `git status --porcelain AgentBridgeUnity/Assets` показывает только тестовые файлы из Unit 17 и ничего от моста.
- On failure: ≤3 попытки. Если после удаления перестал работать любой из типов задач — восстановить удалённый файл, из-за которого сломалось, и остановиться с описанием, а не выдумывать замену.

## Done (/goal condition)

Все двадцать юнитов выполнены, и в транскрипте есть вывод четырёх финальных команд, запущенных из корня репозитория:

- `bash AgentBridgeUnity/Library/AgentBridge/bridge.sh status` печатает JSON с непустым `SessionId` и `RoslynReady` равным `true`;
- `bash AgentBridgeUnity/Library/AgentBridge/bridge.sh compile` печатает JSON со `"Status": "success"`;
- `bash AgentBridgeUnity/Library/AgentBridge/bridge.sh csharp <временный файл с await Task.Delay и Debug.Log>` печатает JSON со `"Status": "success"`, правильным `ReturnValue` и строкой из `Debug.Log` в `Logs`;
- `bash AgentBridgeUnity/Library/AgentBridge/bridge.sh tests --mode EditMode --assembly AgentBridge.ProbeTests` печатает JSON со `"Status": "success"` и `Tests.total` равным 2.

И выполнены три проверки-ограничения:

- `grep -rn -i "cowork" --exclude-dir=.git --exclude-dir=done --exclude-dir=Library --exclude-dir=Temp --exclude-dir=obj . | grep -v "unitycoworkbridge.git"` не возвращает строк;
- `git status --porcelain AgentBridgeUnity/Assets` показывает только файлы тестовой сборки `AgentBridge.ProbeTests`;
- `ls AgentBridgeUnity/Packages/com.elmortem.agentbridge/wait-for-result.sh` возвращает ошибку отсутствия файла.

Ограничения, которые должны держаться всё время: изменения только внутри корня репозитория; `Docs/tdd/done/**` не тронут; зависимости `package.json` не выросли.

Остановиться после 150 ходов в любом случае.

## End-of-run report (the agent does this when the goal is met or it stops)

- Поставить `Status` в шапке этого файла в `Выполнено`.
- Сообщить: какие юниты закрыты; какие гейты потребовали повторов и почему; какой источник Roslyn оказался рабочим и что показала проба остальных; на чём остановился, если остановился.
- Отдельно сообщить, если `bridge.ps1` не проверялся — его проверка остаётся за человеком.
- Пометка, но не действие: уточни у заказчика, нужно ли обновлять проектную документацию под эти изменения.
