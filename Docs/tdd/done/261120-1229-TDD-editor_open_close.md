Status: Выполнено

# Открытие и закрытие Unity агентом

Агент получает две команды: `agentbridge editor open` запускает редактор нужной версии на проекте и сразу возвращает PID, `agentbridge editor close --reason <текст> [--force]` закрывает его штатно или убивает процесс. Цикла ожидания готовности нет: после запуска агент пользуется обычными `status` и `wait`, а очередь хранит задачи, пока редактора нет. Результат: агент сам проходит круг «закрыть → открыть → выполнить задачу» на свободном редакторе, включая принудительное закрытие. Команды работают только на машине редактора.

## Пакет `com.elmortem.agentbridge`

### `Editor/BridgeStatus.cs`, `Editor/BridgeStatusWriter.cs`

- `BridgeStatus`: поле `public string EditorPath;`.
- `WriteOnLoad()`: `Current.EditorPath = EditorApplication.applicationPath;` рядом с `UnityVersion`. Файл `status.json` переживает закрытие редактора, поэтому путь доступен CLI и после выхода.

## CLI `AgentBridgeCli`

### `BridgeStatus.cs`, `CliOptions.cs`

- `BridgeStatus`: `public string? EditorPath { get; set; }`.
- `CliOptions`: `public bool Force` (флаг `--force`, как `--fresh`) и `public string? EditorPath` (`--editor-path` через `TryTakeText`).

### `EditorLocator.cs` — новый файл

```csharp
// Где лежит исполняемый файл Unity для проекта. Чистые функции: окружение и чтение файлов
// передаются снаружи, чтобы порядок кандидатов проверялся без установленной Unity.
internal static class EditorLocator
{
	// Значение строки "m_EditorVersion: X" из ProjectSettings/ProjectVersion.txt; "" при отсутствии.
	public static string ProjectVersion(string projectRoot);

	// Путь к бинарнику: "<X>.app" → "<X>.app/Contents/MacOS/Unity", иначе без изменений.
	public static string Executable(string path);

	// Кандидаты по порядку, без проверки существования и без повторов:
	// 1. explicitPath (--editor-path);
	// 2. statusEditorPath, если statusVersion == version;
	// 3. корень из secondaryInstallPath.json (JSON-строка), если файл читается и строка не пуста;
	// 4. стандартный корень Hub.
	// Для 3 и 4 путь внутри корня: windows "<root>\<version>\Editor\Unity.exe",
	// macos "<root>/<version>/Unity.app", linux "<root>/<version>/Editor/Unity".
	// Каждый кандидат проходит Executable.
	public static List<string> Candidates(string version, string? explicitPath, string? statusEditorPath,
		string? statusVersion, string os, Func<string, string?> environment, Func<string, string?> readText);

	// Первый существующий файл из Candidates с настоящими Environment и SharedFile.TryReadAllText; иначе null.
	public static string? Resolve(string version, string? explicitPath, BridgeStatus? status);
}
```

Корни по `os` (`HostPlatform.Windows` / `MacOs` / `Linux`):

| ОС | `secondaryInstallPath.json` | Стандартный корень Hub |
|---|---|---|
| windows | `%APPDATA%\UnityHub\` | `%ProgramFiles%\Unity\Hub\Editor` |
| macos | `$HOME/Library/Application Support/UnityHub/` | `/Applications/Unity/Hub/Editor` |
| linux | `$HOME/.config/UnityHub/` | `$HOME/Unity/Hub/Editor` |

### `EditorCommands.cs` — новый файл

```csharp
internal static class EditorCommands
{
	internal static int CloseWaitMs = 30000;
	internal static int KillWaitMs = 10000;
	private const int PollMs = 250;
	public const int ReasonLimit = 200;
	// Тесты подменяют запуск; возвращает PID.
	internal static Func<ProcessStartInfo, int> Launch = info => { using var process = Process.Start(info)!; return process.Id; };

	public static async Task<int> RunAsync(string projectRoot, BridgePaths paths, BridgeHealth health,
		string[] arguments, CliOptions options, TelemetryLog telemetry);
}
```

Общее для обеих команд:

- Разбор: `editor open` либо `editor close` с непустым `--reason` не длиннее `ReasonLimit`; иначе `bad_usage` через `AgentBridgeApplication.WriteError`.
- `health.ForeignHost` → `editor_control_foreign_host`.
- `running` = `health.Bridge != null && health.ProjectMatches && health.EditorProcessAlive == true`.
- Вывод и коды выхода — по образцу `DialogCommands`: объект исхода, `JsonSupport.Write` с полями `Ok`, `Code`, `Pid`, `EditorPath`, `UnityVersion`, `Detail`, `BlockedBy`; в `human` — строка `editor: <Code> …` и строки `Blocked by dialog: …`.

`open`:

- `running` → `already_running`, `Pid` из статуса.
- `Temp/UnityLockfile` существует и не открывается с `FileAccess.ReadWrite, FileShare.None` (`IOException`) → `project_locked`: проект держит процесс Unity без живого моста.
- `version = EditorLocator.ProjectVersion(projectRoot)`, `path = EditorLocator.Resolve(version, options.EditorPath, health.Bridge)`; `null` → `editor_not_found`, в `Detail` — версия и список кандидатов.
- Запуск по образцу `QueueClient.Start`, но с видимым окном: `new ProcessStartInfo(path) { UseShellExecute = OperatingSystem.IsWindows(), RedirectStandardInput = !OperatingSystem.IsWindows(), RedirectStandardOutput = !OperatingSystem.IsWindows(), RedirectStandardError = !OperatingSystem.IsWindows(), WorkingDirectory = projectRoot }`, `ArgumentList`: `-projectPath`, `projectRoot`. Исход `launched` с `Pid`.
- Телеметрия `cli_editor_open`: `Code`, `Pid`, `Path`.

`close`:

- Не `running` → `not_running`.
- Без `--force`, по порядку: непустой `health.Bridge.ActiveTaskId` → `task_running`, `Detail` — TaskId и `HolderAgentSessionId`; `health.Bridge.IsPlaying` → `editor_playing`, `Detail` — `PlaySessionAgentId` или `manual`; непустой `health.BlockedBy` → `blocked_by_dialog` (код выхода 1), окна в `BlockedBy`.
- `process = Process.GetProcessById(pid)`; имя процесса не начинается с `Unity` (без учёта регистра) → `pid_mismatch`.
- Без `--force`: `process.CloseMainWindow()` вернул `false` → `close_refused`. Иначе каждые `PollMs` до `CloseWaitMs`: `process.HasExited` → `closed`; `BridgeInspector.Inspect(projectRoot).BlockedBy` не пуст → `close_blocked_by_dialog` с этими окнами (Unity спросила про несохранённое); время вышло → `close_pending`.
- С `--force`: `process.Kill()`, `WaitForExit(KillWaitMs)` → `killed`, иначе `kill_failed`.
- Телеметрия `cli_editor_close`: `Code`, `Pid`, `Force`, `Reason`.

| Код | Выход |
|---|---|
| `launched`, `already_running`, `closed`, `killed`, `not_running` | 0 |
| `project_locked`, `editor_not_found`, `task_running`, `editor_playing`, `blocked_by_dialog`, `pid_mismatch`, `close_refused`, `kill_failed` | 1 |
| `close_blocked_by_dialog`, `close_pending` | 2 |
| `bad_usage`, `editor_control_foreign_host` | 3 |

### `AgentBridgeApplication.cs`

- Рядом с веткой `dialog`: `if (command == "editor") return await EditorCommands.RunAsync(projectRoot, paths, health, commandArguments, options, telemetry);`.
- `WriteHelp`: `editor open [--editor-path PATH]` и `editor close --reason TEXT [--force]`.

## Тесты

- `EditorCommandTests` (`AgentBridgeCli.Tests`, вызов из `Program.cs` после `DialogAnswerTests.RunAsync`):
	- `ProjectVersion` читает версию из файла с двумя строками (`m_EditorVersion`, `m_EditorVersionWithRevision`).
	- `Candidates`: порядок четырёх источников на windows; путь из статуса пропускается при другой версии; `.app` из статуса и корня macos превращается в `…/Contents/MacOS/Unity`; нечитаемый `secondaryInstallPath.json` не ломает список.
	- `open` при живом PID в статусе (PID теста, как в `EditorDialogTests`) → `already_running`/0, `Launch` не вызван.
	- `open` без статуса с `--editor-path` на существующий файл → `launched`/0, в `Launch` пришли этот файл и аргументы `-projectPath`, корень проекта.
	- `open` при удерживаемом `Temp/UnityLockfile` → `project_locked`/1; без кандидатов → `editor_not_found`/1.
	- `close` при мёртвом PID → `not_running`/0; при `ActiveTaskId` в статусе → `task_running`/1; при `IsPlaying` → `editor_playing`/1; при чистом статусе с PID теста → `pid_mismatch`/1 (тестовый процесс не Unity, поэтому ни закрытие, ни убийство не исполняются); `close` без `--reason` → `bad_usage`/3.
- Существующие тесты, которые затрагивает изменение: нет.

## Порядок работ

- Снять факт на свободном Windows-хосте в PowerShell: запомнить `(Get-Process -Id <EditorPid>).Path`, вызвать `.CloseMainWindow()`, дождаться выхода процесса за 60 с; вернуть редактор `Start-Process <Path> -ArgumentList '-projectPath', '<проект>'` и дождаться `status` `ready`. Процесс не вышел — остановиться: штатное закрытие сообщением окну не работает.
- Пакет: `BridgeStatus.cs`, `BridgeStatusWriter.cs`.
- CLI: `BridgeStatus.cs`, `CliOptions.cs`, `EditorLocator.cs` и его случаи в `EditorCommandTests`.
- CLI: `EditorCommands.cs`, `AgentBridgeApplication.cs`, командные случаи `EditorCommandTests`.
- `scripts/verify-editor-control.ps1` — новый, для свободного Windows-хоста с чистыми сценами. Шаги: `status` `ready` и непустой `bridge.editorPath`; `editor close --reason verification` → `closed`/0, процесса нет; повтор → `not_running`/0; `compile --wait 0` принят при закрытом редакторе; `editor open` → `launched`/0; опрос `status` до `ready` (до 300 с); `editor open` → `already_running`/0; `wait <id>` принятой задачи → `success`; `editor close --force --reason verification` → `killed`/0; `editor open`, `ready`, `compile` → `success`.
- Документация по разделу ниже; версии CLI, пакета и плагина и пересборка плагина — по правилу 2 `CLAUDE.md`.

## Приёмка

- Штатное закрытие: `closed`/0 и процесса редактора нет — `verify-editor-control.ps1`.
- Запуск: `launched`/0, `status` доходит до `ready`, повторный `open` даёт `already_running` — тот же скрипт.
- Задача, принятая при закрытом редакторе, завершается `success` после открытия — тот же скрипт.
- Принудительное закрытие: `killed`/0, после `open` проект компилируется — тот же скрипт.
- Порядок поиска исполняемого файла и коды выхода 0/1/3 — `EditorCommandTests`.

## Документация

- `unity-bridge-plugin/skills/unity-bridge/SKILL.md`: в разделе «Код выхода `3`» убрать фразу «Не запускай Unity самостоятельно.»; в «Команды CLI» добавить раздел «`editor open` и `editor close`»:
	- Открывать и закрывать редактор можно самому; решай сам, нужен ли человек. Мимо CLI (`Start-Process`, `taskkill`) Unity не запускай и не убивай.
	- `agentbridge editor open` — когда задаче нужен редактор, а `status` показывает `editor_process_not_running` или `status_missing`. Ответ приходит сразу, готовности команда не ждёт: подавай задачи как обычно, очередь их примет, `wait` дождётся. Если через 5 минут `status` не `ready` и не `blocked_by_dialog`, редактор стоит на стартовом окне (Safe Mode, обновление проекта), которого мост не видит, — зови человека. `editor_not_found` → повтори с `--editor-path <путь к Unity>`, если знаешь путь; `project_locked` → проект открыт в Unity без моста, зови человека.
	- `agentbridge editor close --reason "<зачем>"` — штатное закрытие, как крестиком окна. `task_running` → дождись или отмени свою задачу, чужую согласуй через `queue request-stop`. `editor_playing` → сначала `stopplay`; ручной плеймод человека не трогай без причины. `blocked_by_dialog` → сначала ответь на окно. Код 2 `close_blocked_by_dialog` → Unity спросила про несохранённое: реши по правилам модальных окон, ответь `dialog answer` и повтори `editor close`.
	- `--force` убивает процесс: несохранённое пропадает, выполнявшаяся задача завершится `interrupted_by_domain_reload` и повторена не будет. Только когда редактор не отвечает (процесс жив, `heartbeat_stale` держится, `cancel_blocked`, окно без кнопок) и штатное закрытие не помогло.
	- Перезапуск — `editor close`, затем `editor open`. Задачи в очереди сохраняются. Команды работают только на машине редактора.
- `UNITYAGENT.md` пакета: те же правила одним абзацем.
- `README.md`: раздел про `editor open` / `editor close` с таблицей кодов, поле `EditorPath` в `status.json`, события `cli_editor_open` и `cli_editor_close` в таблице телеметрии, строка в «Limitations» о стартовых окнах.
- `Docs/PROJECT_MAP.md`: команда `editor`, `EditorCommands.cs` и `EditorLocator.cs` в разделе 5; `verify-editor-control.ps1` в разделе 11.
- `CLAUDE.md`: `editor` в списке команд CLI, строка про два новых файла.
