Status: Выполнено

# Диспетчер на машине редактора

Сейчас любой клиент, не видя heartbeat диспетчера 5 с, записывает свой исполняемый файл в `Queue/launch.json` и запускает свой сервер; работающий сервер, увидев чужой runtime, уступает. Клиент из Linux-песочницы так перехватывает очередь у Windows-сервера: в `OceanariumUnity` за 15.10 Windows-сервер уступил 58 раз. Сервер в песочнице работает через смонтированную папку медленно, его heartbeat отстаёт, Windows-сторона перехватывает обратно, и каждая смена — секунды без ответов.

Результат: у диспетчера один владелец. Пока редактор жив и его пакет объявляет `dispatcher-host-v1`, диспетчер работает на машине редактора: редактор сам держит его живым, чужой (foreign) клиент не запускает свой сервер и не пишет `launch.json`, сервер на машине редактора не уступает runtime с другой ОС и не завершается по простою при живом редакторе. Чужой сервер, увидев живой редактор, передаёт владение и завершается. При закрытом редакторе или старом пакете сервер, как прежде, запускает первый клиент.

## Общий транспорт `Editor/Queueing/`

### `QueueFiles.cs`

- Рядом с `Capability`: `public const string HostCapability = "dispatcher-host-v1";`.

## CLI `AgentBridgeCli/`

### `DispatcherHostPolicy.cs` — новый файл

Чистые решения по образцу `WakePolicy.cs`. `ForeignHost` относителен ОС текущего процесса, поэтому функции общие для клиента и сервера.

```csharp
namespace AgentBridge.Cli;

internal static class DispatcherHostPolicy
{
	public const long IdleExitMs = 120000;
	public const int InspectIntervalMs = 1000;

	public static bool EditorAlive(BridgeHealth health) =>
		health.Bridge != null && health.ProjectMatches && health.EditorProcessAlive != false
		&& health.HeartbeatAgeMs != null && health.HeartbeatAgeMs <= health.HeartbeatToleranceMs;

	public static bool HostKeepsDispatcher(BridgeHealth health) =>
		health.Bridge?.Capabilities.Contains(QueueFiles.HostCapability) == true;

	// True for a process on another OS than the editor: it must neither start nor keep a server.
	public static bool HostOwnsDispatcher(BridgeHealth health) =>
		health.ForeignHost && EditorAlive(health) && HostKeepsDispatcher(health);

	// For a non-current launch: a host server yields only to a launcher it could run itself.
	public static bool ShouldYield(bool artifactExists, bool foreign) => artifactExists || foreign;

	public static bool MayIdleExit(long idleMs, BridgeHealth? health) =>
		idleMs > IdleExitMs && (health == null || health.ForeignHost || !EditorAlive(health));
}
```

### `QueueRuntime.cs`

- Вынести из `IsCurrent` выбор файла, опознающего runtime: `internal static string Artifact(QueueLaunch launch)` — `launch.Arguments[0]`, если это `.dll`, иначе `launch.Executable`. `IsCurrent` использует его.

### `BridgeHealth.cs`, `BridgeInspector.cs`, `AgentBridgeApplication.cs`

- `BridgeHealth`: `public string DispatcherOwner { get; set; } = "";`.
- `Inspect`, перед `health.Code`: `health.DispatcherOwner = DispatcherHostPolicy.HostOwnsDispatcher(health) ? "editor_host" : "any_client";`.
- `WriteHealth`, ветка `human`, внутри `if (health.ForeignHost)` после строки `Host:`: `"Dispatcher: " + (health.DispatcherOwner == "editor_host" ? "editor host keeps it running; this client never starts one" : "started by any client")`.

### `QueueClient.cs`

- `PrepareLaunch`: условие записи `launch.json` заменить на `if (!BridgeInspector.Inspect(_project).ForeignHost)`.
- `CallAsync`: `BridgeHealth? health = null; bool deferred = false;` до цикла; ветка запуска:

```csharp
if (deadline.ElapsedMilliseconds >= nextStart && !QueueFiles.Alive(_project))
{
	health = BridgeInspector.Inspect(_project);
	if (DispatcherHostPolicy.HostOwnsDispatcher(health)) deferred = true;
	else { Start(); starts++; }
	nextStart = deadline.ElapsedMilliseconds + 3000;
}
```

- После цикла: `bool alive = QueueFiles.Alive(_project); health ??= BridgeInspector.Inspect(_project); string reason = alive ? "server_silent" : deferred && starts == 0 ? "host_dispatcher_missing" : "launch_failed";`. В `queue_unacknowledged` добавить `["Reason"] = reason`, `["Foreign"] = health.ForeignHost`. `Message` ответа `dispatcher_unavailable` по причине: `host_dispatcher_missing` — `"The editor host (" + health.HostOs + ") owns the dispatcher but did not start it within N s. Check agentbridge status; the host needs the native CLI (Tools → Agent Bridge → Update CLI)."`; `server_silent` — `"The queue server is alive but did not acknowledge request <Id> within N s."`; `launch_failed` — прежний текст. Хвост про `Task` и durable request сохраняется.

### `QueueServer.cs`

- `120000` в `Loop` → `DispatcherHostPolicy.IdleExitMs`. До `while`: `BridgeHealth? health = null; long lastInspect = 0;`. Первый блок тела цикла, до чтения `launch.json`:

```csharp
if (health == null || now - lastInspect >= DispatcherHostPolicy.InspectIntervalMs)
{
	health = BridgeInspector.Inspect(_project);
	lastInspect = now;
	if (DispatcherHostPolicy.HostOwnsDispatcher(health)) { Log("queue_handover", "", "", health.HostOs); break; }
}
```

- Блок `launch.json`: при `!QueueRuntime.IsCurrent(launch)` уступать только если `DispatcherHostPolicy.ShouldYield(File.Exists(QueueRuntime.Artifact(launch)), health.ForeignHost)`, иначе `Log("queue_launch_ignored", "", "", launch.Executable)`; `lastLaunchJson = launchJson` в обоих случаях.
- Выход по простою: `if (DispatcherHostPolicy.MayIdleExit(now - _lastUse, health)) break;`.

## Пакет `Editor/`

### `BridgeStatusWriter.cs`

- В `BuildCapabilities` добавить `Queueing.QueueFiles.HostCapability` в начальный список.

### `QueueEditorAdapter.cs`

- Поля: `private static DateTime _lastAliveCheck; private static bool _launchWarned;`.
- `Tick()`: первой строкой `EnsureServer();`, дальше прежняя проверка `_introduced` и hello.
- `EnsureServer()`: в начале `DateTime now = DateTime.UtcNow; if (now - _lastAliveCheck < TimeSpan.FromSeconds(1)) return; _lastAliveCheck = now;`. Остальное прежнее; в ветке `if (!File.Exists(native))` перед `return` — `WarnLaunchFailed(native)`.
- `WarnLaunchFailed(string path)`: один раз за домен (`_launchWarned`): `Debug.LogWarning("[AgentBridge] Queue dispatcher cannot start: no CLI at " + path + ". Install it via Tools → Agent Bridge → Update CLI.")` и `TelemetryLog.Write("dispatcher_launch_failed", "", "", new[] { TelemetryField.Text("Path", path) })`.

## Тесты

`AgentBridgeCli.Tests/DispatcherHostTests.cs` — новый файл, `internal static class DispatcherHostTests`, `RunAsync(string root)`; вызов в `Program.cs` после `QueueReplyLifetimeTests.RunAsync(root)`. Фикстура проекта как `CreateProject` в `QueueReplyLifetimeTests`; `WriteStatus(project, hostOs, string[] capabilities, long heartbeatAgeMs, int editorPid)` пишет `project-id`, `status.json` с тем же `ProjectId`, `ProjectPath = project`, `ProtocolVersion = 1`, `Enabled`, `RoslynReady`, `Capabilities`, и `heartbeat` нужного возраста. `Foreign` — ОС, отличная от `HostPlatform.Current`; `Full` — `{ QueueFiles.Capability, QueueFiles.HostCapability }`; `Legacy` — только `QueueFiles.Capability`. Телеметрия читается из `Logs/AgentBridge-client-*.jsonl` через `JsonDocument`.

- `policy_owner`: `(Foreign, Full, 0, 0x7FFFFFFF)` → `HostOwnsDispatcher(Inspect)` true, `DispatcherOwner == "editor_host"`; `Legacy` → false; `Full` с возрастом 90 000 → false; `(Current, Full, 0, Environment.ProcessId)` → false, `"any_client"`.
- `policy_yield`: `ShouldYield(false, false)` false; `(true, false)` и `(false, true)` true.
- `policy_idle`: `MayIdleExit(130000, Inspect)` при `(Current, Full, 0, Environment.ProcessId)` false; при возрасте 90 000 true; `MayIdleExit(100000, …)` false; `(130000, null)` true.
- `foreign_client_defers`: `(Foreign, Full, 0, 0x7FFFFFFF)`; `new QueueClient(project) { AcknowledgeTimeoutMs = 1500 }.CallAsync(list)` → `dispatcher_unavailable`, `Message` содержит `owns the dispatcher`; в `Queue/` нет `launch.json` и `heartbeat`; `queue_unacknowledged` с `Reason == "host_dispatcher_missing"` и `Foreign == true`.
- `foreign_client_launches_without_capability`: `(Foreign, Legacy, 0, 0x7FFFFFFF)`; `list` → `Ok`; `Queue/launch.json` есть; `shutdown`, `Until(!Alive)`.
- `foreign_server_hands_over`: `(Current, Full, 0, Environment.ProcessId)`; `list` → `Ok`; затем `(Foreign, Full, 0, 0x7FFFFFFF)`; `Until(!Alive)`; дождаться выхода процесса из `server.json`, как в `QueueRuntimeTests`; `queue_handover` в телеметрии.
- `host_server_ignores_foreign_launch`: `(Current, Full, 0, Environment.ProcessId)`; одним экземпляром клиента `list` → `Ok`, запомнить `Pid` из `server.json`; записать `launch.json` с `Executable = Path.Combine(project, "missing", "agentbridge")` и аргументами `queue-server --project <project>`; через 1 500 мс `list` тем же клиентом → `Ok`, `Pid` прежний; `queue_launch_ignored` в телеметрии; `shutdown`.
- Существующие тесты, которые затрагивает изменение: `IndependentQueueTests`, `QueueRuntimeTests`, `RunQueueBudgetTests`, `RunStaleSubmissionTests`, `RunForeignHostTests`.

## Порядок работ

- `QueueFiles.HostCapability`; `QueueRuntime.Artifact`; `DispatcherHostPolicy.cs`.
- `BridgeHealth.DispatcherOwner`, присвоение в `Inspect`, строка `Dispatcher:` в `WriteHealth`.
- `QueueClient.cs`: `launch.json` только от родного клиента, отложенный запуск, `Reason` и сообщения.
- `QueueServer.cs`: ежесекундный `Inspect`, передача владения, правило уступки, простой при живом редакторе.
- Пакет: capability, `EnsureServer` из `Tick`, предупреждение о недоступном CLI.
- `DispatcherHostTests.cs` и вызов из `Program.cs`.
- Версии: CLI `1.25.0` → `1.26.0`, пакет `0.36.1` → `0.37.0`, плагин `1.33.0` → `1.33.1`; пересборка плагина.
- Документация и живая проверка в хост-проекте `AgentBridgeUnity`.

## Приёмка

- Чужой клиент при живом редакторе не запускает сервер и объясняет отказ: `foreign_client_defers`.
- Старый пакет без capability — прежнее поведение: `foreign_client_launches_without_capability`.
- Чужой сервер уходит, когда редактор жив: `foreign_server_hands_over`.
- Сервер на машине редактора не уступает launcher с другой ОС: `host_server_ignores_foreign_launch`; `QueueRuntimeTests` проходит.
- Редактор держит диспетчер живым: в хост-проекте с открытым редактором после `agentbridge queue shutdown` файл `Queue/heartbeat` снова свежий не позже чем через 5 с без вызовов CLI; за 5 минут без задач диспетчер не завершается; `status.json` содержит `dispatcher-host-v1`; `status --format json` содержит `DispatcherOwner: any_client`.
- `AgentBridgeCli.Tests: PASS`; `build-plugin.ps1` — `invalid_entries=0`, `zip_validation=PASS`; пакет компилируется в хост-проекте, EditMode-тесты зелёные.

## Документация

- `README.md`: в «Agents whose shell runs in a Linux sandbox» — абзац о владельце (при живом редакторе с `dispatcher-host-v1` диспетчер работает на машине редактора, редактор держит его сам, клиент из песочницы ждёт и свой не запускает; при закрытом редакторе сервер запускает первый клиент) и строка `Dispatcher:` в `status`. В «Independent queue» — сервер не завершается по простою при живом редакторе и не уступает launcher, который не может запустить. Телеметрия: `queue_unacknowledged` + `Reason`, `Foreign`; новые `client` `queue_handover` (`Result` — ОС редактора), `queue_launch_ignored` (`Result` — исполняемый файл); `editor` `dispatcher_launch_failed` (`Path`).
- `unity-bridge-plugin/skills/unity-bridge/SKILL.md`, код выхода `3`: для `dispatcher_unavailable` читать `Message`; если в нём `owns the dispatcher`, диспетчер обязан запустить редактор на своей машине — проверить `agentbridge status`, не повторять в цикле, при повторении попросить пользователя выполнить Update CLI на машине редактора.
- `UNITYAGENT.md` пакета, «Независимая очередь»: при живом редакторе диспетчер работает на его машине, агент из песочницы свой не запускает.
- `Docs/PROJECT_MAP.md`: строка `DispatcherHostPolicy.cs` в таблице CLI; в строках `QueueClient.cs`, `QueueServer.cs`, `QueueEditorAdapter.cs` — отложенный запуск, передача владения, поддержание диспетчера редактором.
