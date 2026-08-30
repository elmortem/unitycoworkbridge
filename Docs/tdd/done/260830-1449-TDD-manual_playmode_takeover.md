Status: Выполнено

# Захват ничейного плеймода агентом

## Суть

Плеймод, запущенный пользователем вручную, не принадлежит никакой агентской сессии: файла play-сессии нет, координатор берёт из очереди только `stopplay`, остальные задачи висят в inbox. CLI при этом печатает только `queued Ns, position X/Y` и по исчерпании бюджета выходит с кодом 2 — агент трактует это как «мост не отвечает».

Новое поведение: агент главнее ничейного плеймода. Когда задача агента не может стартовать из-за ручного плеймода, CLI сам подкладывает `stopplay`, дожидается его завершения и продолжает ждать исходную задачу. Плеймод, которым владеет другая агентская сессия, не трогается — для него действуют прежние правила `PlaySessionArbiter` (`RejectForeign`, преемптинг по дедлайну и простою).

Задеты все три компонента: пакет, CLI, плагин.

## Пакет: свежесть статуса

### `Editor/BridgeStatusWriter.cs`

- В `WriteOnLoad` заполнять play-поля из стора вместо `null`, чтобы сразу после domain reload агентского входа в плеймод статус не выглядел как ручной плеймод:

```csharp
	PlaySessionState playSession = PlaySessionStore.Read();
	Current.IsPlaying = EditorApplication.isPlayingOrWillChangePlaymode;
	Current.PlaySessionAgentId = playSession != null && !string.IsNullOrEmpty(playSession.OwnerAgentSessionId)
		? playSession.OwnerAgentSessionId
		: null;
	Current.PlaySessionDeadlineUtc = playSession != null ? playSession.DeadlineUtc : null;
```

- Ручной вход и выход из плеймода при отключённом domain reload (Enter Play Mode Options) сейчас вообще не обновляют `status.json`. В `OnUpdate` добавить синхронизацию флага:

```csharp
	private static void OnUpdate()
	{
		Beat();
		SyncPlayingFlag();
	}

	private static void SyncPlayingFlag()
	{
		bool playing = EditorApplication.isPlayingOrWillChangePlaymode;
		if (Current.IsPlaying == playing)
		{
			return;
		}

		Current.IsPlaying = playing;
		Write();
	}
```

Запись происходит только при смене флага, транзитные записи `PlaySessionManager.WriteStatus` она не конфликтует — значения совпадут и повторной записи не будет.

## CLI: обнаружение и захват

### Новый файл `AgentBridgeCli/ManualPlayPolicy.cs`

Чистое решение «гасить ли ничейный плеймод сейчас», по образцу `WakePolicy` — точка тестирования без файловой системы:

```csharp
namespace AgentBridge.Cli;

internal static class ManualPlayPolicy
{
	public const int MaxStops = 3;

	public static bool IsManualPlaying(BridgeStatus? bridge)
	{
		return bridge != null && bridge.IsPlaying && string.IsNullOrEmpty(bridge.PlaySessionAgentId);
	}

	public static bool ShouldStop(BridgeHealth? health, string kind, int stopsSoFar)
	{
		if (stopsSoFar >= MaxStops)
		{
			return false;
		}

		if (kind == "stopplay")
		{
			return false;
		}

		if (health == null || !health.BridgeReady)
		{
			return false;
		}

		return IsManualPlaying(health.Bridge);
	}
}
```

- `MaxStops` ограничивает число захватов на одно ожидание: если пользователь трижды подряд заново запускает плеймод, CLI перестаёт бороться и ждёт как раньше, с подсказкой в очередном сообщении очереди.

### `AgentBridgeCli/BridgeInspector.cs`

- В `Inspect`, в блоке `if (health.Bridge != null)`, рядом с остальными `Warnings`:

```csharp
	if (ManualPlayPolicy.IsManualPlaying(health.Bridge))
	{
		health.Warnings.Add("editor_playing_manual");
	}
```

- `BridgeReady` и `Problems` не меняются: мост жив, `stopplay` проходит.

### `AgentBridgeCli/BridgeClient.cs`

- В `WaitForTaskAsync` завести локальный счётчик `var manualStops = 0;` рядом с `attempts`.
- В ветке очереди (после проверки существования task-файла, перед проверкой `QueueWaitCapSeconds`) добавить захват:

```csharp
	if (ManualPlayPolicy.ShouldStop(lastHealth, kind, manualStops))
	{
		manualStops++;
		Console.Error.WriteLine("[agentbridge] " + taskId
			+ " editor is in play mode without an agent session; stopping it (stopplay #" + manualStops + ")");
		await StopManualPlayAsync(taskId);
		lastHealth = null;
		nextHealthPoll = DateTime.MinValue;
		continue;
	}
```

- Сброс `lastHealth` и `nextHealthPoll` заставляет следующий виток перечитать здоровье до нового решения — повторный захват возможен только после свежего статуса, снова показавшего ручной плеймод.
- Новый приватный метод — подложить `stopplay` и молча дождаться его терминального статуса, ничего не выводя в stdout исходной задачи:

```csharp
	private async Task StopManualPlayAsync(string forTaskId)
	{
		var stopId = TaskIdGenerator.NewId();
		var request = new TaskRequest
		{
			Id = stopId,
			Kind = "stopplay",
			AgentSessionId = _session ?? "",
			Note = "auto-stop manual play for " + forTaskId
		};
		Directory.CreateDirectory(_paths.Inbox);
		Directory.CreateDirectory(_paths.Journal);
		await File.WriteAllBytesAsync(
			Path.Combine(_paths.Inbox, stopId + ".task.json"),
			JsonSerializer.SerializeToUtf8Bytes(request, JsonSupport.Task));

		var journalFile = Path.Combine(_paths.Journal, stopId + ".json");
		var deadline = DateTime.UtcNow.AddSeconds(StopManualPlaySeconds);
		var status = "timeout";
		while (DateTime.UtcNow < deadline)
		{
			if (TryReadFile(journalFile, out var json) && TryGetTerminalStatus(json, out var terminal))
			{
				status = terminal;
				break;
			}

			await Task.Delay(250);
		}

		if (status != "success")
		{
			Console.Error.WriteLine("[agentbridge] auto stopplay " + stopId + " ended as " + status
				+ "; the task stays queued");
		}

		_telemetry.Write("cli_autostop", _session, stopId, new Dictionary<string, object?>
		{
			["For"] = forTaskId,
			["Status"] = status
		});
	}
```

- Константа рядом с `QueueWaitCapSeconds`: `private const int StopManualPlaySeconds = 120;` — выход из плеймода тянет domain reload, на тяжёлом проекте это долго.
- Сигнатуру `TryGetTerminalStatus` при необходимости привести к `out string status` (сейчас вызывается с `out _`), чтобы вернуть терминальный статус наружу.
- В `DescribeQueuePosition` добавить причину в хвост обеих веток возврата:

```csharp
	var suffix = ManualPlayPolicy.IsManualPlaying(health.Bridge)
		? ", editor playing (manual), run 'agentbridge stopplay' to take over"
		: "";
```

- В выходе по `QueueWaitCapSeconds` добавить причину в JSON, когда последний известный статус показывал ручной плеймод:

```csharp
	var payload = new Dictionary<string, object?>
	{
		["Id"] = taskId,
		["Status"] = "queued"
	};
	if (ManualPlayPolicy.IsManualPlaying(lastHealth?.Bridge))
	{
		payload["Reason"] = "editor_playing_manual";
	}
```

### `AgentBridgeCli/AgentBridgeApplication.cs`

- В `WriteHealth` пометить ручной плеймод в human-выводе:

```csharp
	var playing = health.Bridge.IsPlaying ? "yes" : "no";
	if (!string.IsNullOrEmpty(health.Bridge.PlaySessionAgentId))
	{
		playing += " (session " + health.Bridge.PlaySessionAgentId
			+ ", until " + (health.Bridge.PlaySessionDeadlineUtc ?? "unknown") + ")";
	}
	else if (health.Bridge.IsPlaying)
	{
		playing += " (manual)";
	}
```

## Тесты CLI

### `AgentBridgeCli.Tests/Program.cs`

- Добавить вызов `RunManualPlayPolicyTests();` в последовательность в начале файла и функцию:

```csharp
static void RunManualPlayPolicyTests()
{
	static BridgeHealth Health(bool ready, bool playing, string? owner)
	{
		return new BridgeHealth
		{
			BridgeReady = ready,
			Bridge = new BridgeStatus { IsPlaying = playing, PlaySessionAgentId = owner }
		};
	}

	Expect(ManualPlayPolicy.ShouldStop(Health(true, true, null), "csharp", 0), "manual play must be stopped for a queued task");
	Expect(!ManualPlayPolicy.ShouldStop(Health(true, true, "agent-a"), "csharp", 0), "an owned play session must not be touched");
	Expect(!ManualPlayPolicy.ShouldStop(Health(true, true, null), "stopplay", 0), "stopplay must not stop play for itself");
	Expect(!ManualPlayPolicy.ShouldStop(Health(true, true, null), "csharp", ManualPlayPolicy.MaxStops), "exhausted attempts must fall back to waiting");
	Expect(!ManualPlayPolicy.ShouldStop(Health(false, true, null), "csharp", 0), "an unready bridge must not receive stopplay");
	Expect(!ManualPlayPolicy.ShouldStop(null, "csharp", 0), "missing health must not trigger a stop");
	Expect(!ManualPlayPolicy.ShouldStop(Health(true, false, null), "csharp", 0), "an idle editor must not be stopped");
}
```

- В `RunHealthTests` добавить случай: статус-файл с `IsPlaying: true` и пустым `PlaySessionAgentId` при живом heartbeat — `health.Warnings` содержит `editor_playing_manual`, `health.BridgeReady` остаётся `true`.
- Если `BridgeHealth`/`BridgeStatus` недоступны тестовому проекту с нужным уровнем видимости — проверить наличие `InternalsVisibleTo` в `AgentBridgeCli.csproj` (остальные internal-типы тесты уже используют, ничего менять не должно понадобиться).

## Документация

- `README.md`: в разделе про плеймод — абзац о захвате: ничейный (ручной) плеймод CLI останавливает автоматически перед запуском задач агента, до трёх попыток на одно ожидание; агентские play-сессии защищены как раньше. В перечень диагностики добавить warning `editor_playing_manual` и поле `Reason: editor_playing_manual` в результате `Status: queued`.
- `unity-bridge-plugin/skills/unity-bridge/SKILL.md`: в разделе про play/status — что ручной плеймод пользователя агенту не помеха: CLI сам гасит его и выполняет задачу; специальных действий не нужно; `doctor` показывает `editor_playing_manual`, строка очереди — `editor playing (manual)`. Следить за лимитом frontmatter по `Docs/rules/skill-frontmatter.md` (описание скилла не меняется — правится тело).
- `AgentBridgeUnity/Packages/com.elmortem.agentbridge/UNITYAGENT.md`: одно предложение в разделе про плеймод — ничейный плеймод останавливается мостом автоматически при поступлении агентских задач через CLI.
- `CLAUDE.md`, «Карта проекта», список CLI: после строки про `WakePolicy.cs` добавить `ManualPlayPolicy.cs` — решение о захвате ничейного плеймода.

## Версии и сборка

- `AgentBridgeCli/AgentBridgeCli.csproj`: `<Version>` `1.14.0` → `1.15.0`.
- `AgentBridgeUnity/Packages/com.elmortem.agentbridge/package.json`: `version` `0.21.0` → `0.22.0`.
- `unity-bridge-plugin/.claude-plugin/plugin.json`: `version` `1.18.0` → `1.19.0`.
- Пересобрать плагин: `powershell -NoProfile -ExecutionPolicy Bypass -File scripts/build-plugin.ps1` — до `invalid_entries=0` и `zip_validation=PASS`.
- Прогнать тесты CLI:

```bash
dotnet build AgentBridgeCli/AgentBridgeCli.csproj -c Release
dotnet run --project AgentBridgeCli.Tests/AgentBridgeCli.Tests.csproj -c Release
```

## Ручная проверка

- Запустить плеймод в редакторе руками, выполнить `agentbridge csharp <task> --wait 120` — в stderr появляется строка про `stopplay #1`, плеймод гаснет, задача выполняется, код выхода 0.
- Во время агентской play-сессии (`agentbridge play --session a`) выполнить `agentbridge csharp --session b` — захвата нет, задача ждёт по прежним правилам.
- `agentbridge status --format human` при ручном плеймоде — `Playing: yes (manual)`; `agentbridge doctor` — warning `editor_playing_manual`.
- С отключённым domain reload в Enter Play Mode Options войти в плеймод руками — `agentbridge status` показывает `IsPlaying: true` без перезапуска редактора.

## После выполнения

- Смени статус в начале этого документа на `Выполнено`.
- Уточни у заказчика, нужно ли дополнительно обновить документацию проекта под эти изменения.
