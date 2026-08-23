Status: Выполнено

# ТДД: лимиты play-сессии и вытеснение

Тип: новое поведение в существующем механизме play-сессий.

## Задача

Play-сессия — единственное состояние моста, которое чужой агент не может снять никаким способом: чужой
`stopplay` отклоняется безусловно, чужие задачи молча лежат в инбоксе, позиции в очереди не обновляются, а
владелец может занять редактор на 600 секунд. Агенты при этом злоупотребляют play вместо написания
PlayMode-тестов.

Меняется три вещи:

- дефолтная длительность play-сессии сокращается до 15 секунд;
- чужой `stopplay` вытесняет сессию, когда дедлайн истёк или владелец простаивает дольше грейса;
- во время play очередь в `status.json` продолжает обновляться, а отказы чужим командам содержат дедлайн.

Попутно чинится замеченный по телеметрии дефект: отклонённая задача не запоминает хэш в журнальной записи,
из-за чего протухший файл задачи из инбокса заново отклоняется после каждого доменного релоада.

## Принятые решения

- Никаких квот и кулдаунов на play: злоупотребление считается по телеметрии (`play_open`/`play_close` уже
  пишутся), ограничения — только по данным.
- Простой владельца измеряется от последней активности его задач внутри сессии, а не от начала сессии.
- Вытеснение работает и когда файл состояния сессии есть, а редактор при этом завис в play: решение принимает
  чистый арбитр по данным состояния, а не по фазе редактора.
- `PlaySessionMaxSeconds` (600) не меняется: явный `--seconds` — осознанный запрос, защита от него — вытеснение
  по простою, а не срезание потолка.

---

## Часть 1. Настройки

### `Editor/AgentBridgeSettings.cs`

- `PlaySessionDefaultSeconds = 30` → `PlaySessionDefaultSeconds = 15`.
- Новое поле после `AgentPlayGraceSeconds`:

```csharp
public int PlayOwnerIdleSeconds = 10;
```

### `Editor/AgentBridgeSettingsStore.cs`

- В `GetPlaySessionDefaultSeconds()` запасное значение `120` → `15`.
- Новая пара в стиле соседних:

```csharp
public static int GetPlayOwnerIdleSeconds()
{
	AgentBridgeSettings settings = Load();
	if (settings.PlayOwnerIdleSeconds <= 0)
	{
		return 10;
	}

	return settings.PlayOwnerIdleSeconds;
}

public static void SetPlayOwnerIdleSeconds(int value)
{
	AgentBridgeSettings settings = Load();
	settings.PlayOwnerIdleSeconds = value;
	Save(settings);
}
```

### `AgentBridgeUnity/ProjectSettings/AgentBridge.json`

`"PlaySessionDefaultSeconds": 120` → `15`; добавить `"PlayOwnerIdleSeconds": 10`.

---

## Часть 2. Состояние сессии и активность владельца

### `Editor/PlaySessionState.cs`

Добавить поле после `DeadlineUtc`:

```csharp
public string OwnerLastActivityUtc;
```

### `Editor/PlaySessionManager.cs`

В `BeginPlay`, при создании `state`, инициализировать:

```csharp
OwnerLastActivityUtc = nowUtc.ToString("o"),
```

Добавить публичный метод — его вызывает координатор при каждом старте задачи владельца внутри сессии:

```csharp
public static void TouchOwnerActivity()
{
	PlaySessionState state = PlaySessionStore.Read();
	if (state == null || state.Phase != PlaySessionPhases.Active)
	{
		return;
	}

	state.OwnerLastActivityUtc = DateTime.UtcNow.ToString("o");
	PlaySessionStore.Write(state);
}
```

В `BeginStop` ветка `state == null` дополняется инициализацией нового поля пустой строкой — сериализатор не
должен писать `null`:

```csharp
OwnerLastActivityUtc = ""
```

---

## Часть 3. Арбитр

### `Editor/StopVerdict.cs`

Добавить значение:

```csharp
StopPreempt,
```

### `Editor/PlaySessionArbiter.cs`

Сигнатура расширяется временем и грейсом; вся логика вытеснения живёт здесь и нигде больше.

```csharp
using System;

namespace AgentBridge
{
	public static class PlaySessionArbiter
	{
		public static StopVerdict Judge(
			PlaySessionState state,
			string callerEffectiveSessionId,
			bool isPlaying,
			bool testsPending,
			DateTime nowUtc,
			int ownerIdleSeconds)
		{
			if (testsPending)
			{
				return StopVerdict.RejectTests;
			}

			if (state == null)
			{
				return isPlaying ? StopVerdict.StopUnsanctioned : StopVerdict.NotPlaying;
			}

			if (string.Equals(state.OwnerAgentSessionId ?? "", callerEffectiveSessionId ?? "", StringComparison.Ordinal))
			{
				return StopVerdict.StopOwn;
			}

			if (CanPreempt(state, nowUtc, ownerIdleSeconds))
			{
				return StopVerdict.StopPreempt;
			}

			return StopVerdict.RejectForeign;
		}

		public static bool CanPreempt(PlaySessionState state, DateTime nowUtc, int ownerIdleSeconds)
		{
			DateTime deadlineUtc;
			if (TryParseUtc(state.DeadlineUtc, out deadlineUtc) && nowUtc >= deadlineUtc)
			{
				return true;
			}

			DateTime lastActivityUtc;
			if (!TryParseUtc(state.OwnerLastActivityUtc, out lastActivityUtc))
			{
				// A session written by an older package version has no activity field; the
				// deadline alone decides for it.
				return false;
			}

			return (nowUtc - lastActivityUtc).TotalSeconds >= ownerIdleSeconds;
		}

		private static bool TryParseUtc(string value, out DateTime result)
		{
			return DateTime.TryParse(
				value,
				System.Globalization.CultureInfo.InvariantCulture,
				System.Globalization.DateTimeStyles.RoundtripKind,
				out result);
		}
	}
}
```

Дедлайн в норме гасит сессию сам через `ReconcileActive`; ветка `nowUtc >= deadlineUtc` в `CanPreempt` — путь
восстановления для редактора, который завис в play и сам себя не гасит.

---

## Часть 4. Координатор

### `Editor/TaskCoordinator.cs` — `TryStartPlaySessionTask`

Метод переписывается в части обработки чужих задач. Текущее тело до цикла не меняется; цикл становится таким:

```csharp
DateTime nowUtc = DateTime.UtcNow;
int ownerIdleSeconds = AgentBridgeSettingsStore.GetPlayOwnerIdleSeconds();

foreach (PendingTaskInfo task in pending)
{
	bool isOwner = string.Equals(task.EffectiveSessionId, owner, StringComparison.Ordinal);

	if (!isOwner)
	{
		if (task.Kind == "stopplay")
		{
			if (PlaySessionArbiter.CanPreempt(state, nowUtc, ownerIdleSeconds))
			{
				if (next == null || task.CreatedUtc < next.CreatedUtc)
				{
					next = task;
				}
			}
			else
			{
				RejectTaskFile(task.TaskFilePath, task.Id, "stopplay",
					"play_session_held_by:" + owner + ";deadline:" + (state.DeadlineUtc ?? ""));
			}
		}
		else if (task.Kind == "release")
		{
			_rejectedTaskHashes[task.TaskFilePath] = TaskFileHash.HashOf(task.TaskFilePath, PayloadPathOf(task.TaskFilePath));
			WriteTerminal(task.Id, "release", "success", "not_holder",
				TaskFileHash.HashOf(task.TaskFilePath, PayloadPathOf(task.TaskFilePath)));
		}

		continue;
	}

	if (task.Kind != "csharp" && task.Kind != "sceneshot" && task.Kind != "stopplay")
	{
		RejectTaskFile(task.TaskFilePath, task.Id, task.Kind, "kind not allowed during play session");
		continue;
	}

	if (next == null || task.CreatedUtc < next.CreatedUtc)
	{
		next = task;
	}
}
```

После цикла, перед существующим прогревом аренды, добавить обновление очереди — во время play она сейчас не
обновляется вообще, и ждущие клиенты видят протухшие позиции:

```csharp
UpdateQueueStatus(pending);
```

Перед `StartTask(next, false, "");`, только когда `next` принадлежит владельцу:

```csharp
if (string.Equals(next.EffectiveSessionId, owner, StringComparison.Ordinal))
{
	PlaySessionManager.TouchOwnerActivity();
}
```

### `Editor/TaskCoordinator.cs` — `RunStopplayTask`

Вызов арбитра получает новые аргументы:

```csharp
StopVerdict verdict = PlaySessionArbiter.Judge(
	state, effective, EditorApplication.isPlaying, PlayModeSceneRecovery.IsPending,
	DateTime.UtcNow, AgentBridgeSettingsStore.GetPlayOwnerIdleSeconds());
```

В `switch` ветка `RejectForeign` дополняется дедлайном:

```csharp
case StopVerdict.RejectForeign:
	FinishTask("rejected", null,
		new List<string> { "play_session_held_by:" + (state.OwnerAgentSessionId ?? "")
			+ ";deadline:" + (state.DeadlineUtc ?? "") }, false);
	return;
```

После `switch` вызов `BeginStop` учитывает вытеснение:

```csharp
UnsanctionedPlayGuard.ClearMark();
string stopReason = verdict == StopVerdict.StopOwn
	? "stopplay"
	: verdict == StopVerdict.StopPreempt ? "preempted" : "manual";
PlaySessionManager.BeginStop(request.Id, stopReason);
```

Телеметрия ничего дополнительно не требует: `play_close` запишет `Reason: "preempted"` штатно.

### `Editor/TaskCoordinator.cs` — хэш в терминальных записях

Дефект: `WriteTerminal` не заполняет `Hash`, поэтому после доменного релоада (когда очищается словарь
`_rejectedTaskHashes`) файл отклонённой задачи в `BuildPendingList` не совпадает по хэшу с терминальной записью
и заново попадает в очередь, где отклоняется снова — и так после каждого релоада.

Сигнатура меняется на:

```csharp
private static void WriteTerminal(string id, string kind, string status, string logLine, string hash)
```

В теле добавляется `Hash = hash` в инициализатор записи. Все существующие вызовы обновляются:

- в `RejectTaskFile`: хэш уже вычислен строкой выше — сохранить его в локальную переменную и передать;
- в `StartTask` (ветка `id_conflict`): передать `hash`;
- в `TryStartPlaySessionTask` (ветка `release`): вычислить один раз в локальную переменную, передать её же и в
  `_rejectedTaskHashes` (в коде выше уже так написано).

---

## Часть 5. Тесты

### `AgentBridgeUnity/Assets/Tests/Editor/PlaySessionArbiterTests.cs`

Существующие вызовы `Judge` дополняются двумя аргументами: `new DateTime(2026, 1, 1, 0, 0, 30, DateTimeKind.Utc)`
и `10` — это момент внутри дедлайна при свежей активности, все старые вердикты сохраняются. В
`SessionOwnedBy` добавить `OwnerLastActivityUtc = "2026-01-01T00:00:25.0000000Z"`.

Новые тесты:

```csharp
[Test]
public void Judge_PreemptsForeignAfterDeadline()
{
	StopVerdict verdict = PlaySessionArbiter.Judge(
		SessionOwnedBy("agent-a"), "agent-b", true, false,
		new System.DateTime(2026, 1, 1, 0, 2, 1, System.DateTimeKind.Utc), 10);
	Assert.AreEqual(StopVerdict.StopPreempt, verdict);
}

[Test]
public void Judge_PreemptsForeignWhenOwnerIsIdle()
{
	StopVerdict verdict = PlaySessionArbiter.Judge(
		SessionOwnedBy("agent-a"), "agent-b", true, false,
		new System.DateTime(2026, 1, 1, 0, 0, 36, System.DateTimeKind.Utc), 10);
	Assert.AreEqual(StopVerdict.StopPreempt, verdict);
}

[Test]
public void Judge_RejectsForeignWhileOwnerIsActive()
{
	StopVerdict verdict = PlaySessionArbiter.Judge(
		SessionOwnedBy("agent-a"), "agent-b", true, false,
		new System.DateTime(2026, 1, 1, 0, 0, 30, System.DateTimeKind.Utc), 10);
	Assert.AreEqual(StopVerdict.RejectForeign, verdict);
}

[Test]
public void Judge_OwnerStopsOwnSessionRegardlessOfActivity()
{
	StopVerdict verdict = PlaySessionArbiter.Judge(
		SessionOwnedBy("agent-a"), "agent-a", true, false,
		new System.DateTime(2026, 1, 1, 0, 0, 30, System.DateTimeKind.Utc), 10);
	Assert.AreEqual(StopVerdict.StopOwn, verdict);
}

[Test]
public void CanPreempt_LegacyStateWithoutActivityFallsBackToDeadline()
{
	PlaySessionState state = SessionOwnedBy("agent-a");
	state.OwnerLastActivityUtc = "";
	Assert.IsFalse(PlaySessionArbiter.CanPreempt(
		state, new System.DateTime(2026, 1, 1, 0, 1, 0, System.DateTimeKind.Utc), 10));
	Assert.IsTrue(PlaySessionArbiter.CanPreempt(
		state, new System.DateTime(2026, 1, 1, 0, 2, 0, System.DateTimeKind.Utc), 10));
}
```

---

## Часть 6. Документация

### `unity-bridge-plugin/skills/unity-bridge/SKILL.md`, раздел play/stopplay

- Дефолт сессии теперь 15 секунд; `--seconds` по-прежнему поднимает до максимума.
- Чужую активную сессию остановить нельзя, **пока владелец работает**: отказ выглядит как
  `play_session_held_by:<id>;deadline:<UTC>`. Если дедлайн истёк или владелец не подавал задач около 10 секунд,
  чужой `stopplay` вытесняет сессию; в логах владельца закрытие видно как `stopped:preempted`.
- Владельцу: сессию держит активной только подача задач (`csharp`, `sceneshot`); пауза длиннее ~10 секунд
  отдаёт play первому чужому `stopplay`. Закончил — сними сессию сам, не жди вытеснения.
- play — инструмент визуальной проверки; поведение проверяется PlayMode-тестами, а не серией play-сессий.

### `AgentBridgeUnity/Packages/com.elmortem.agentbridge/UNITYAGENT.md` и `README.md`

Те же три факта словами для своего читателя: новый дефолт, правило вытеснения, настройка
`PlayOwnerIdleSeconds`. В `README.md` — в таблицу настроек.

---

## Часть 7. Версии и сборка

- `AgentBridgeUnity/Packages/com.elmortem.agentbridge/package.json`: `0.20.0` → `0.21.0`.
- `unity-bridge-plugin/.claude-plugin/plugin.json`: `1.17.1` → `1.18.0`.
- CLI не меняется — версия остаётся.
- Пересобрать плагин: `powershell -NoProfile -ExecutionPolicy Bypass -File scripts/build-plugin.ps1`;
  прогон обязан закончиться `invalid_entries=0` и `zip_validation=PASS`.

## Проверка результата

- `agentbridge play --note "проверка" --session OWNER` без `--seconds`: в результате `playing_until` через
  15 секунд от старта.
- Во время активной сессии `OWNER` (сразу после открытия) выполнить `agentbridge stopplay --session OTHER` —
  `rejected` с `play_session_held_by:OWNER;deadline:<UTC>`.
- Открыть сессию на 60 секунд, ничего не подавать 10+ секунд, выполнить `agentbridge stopplay --session OTHER` —
  `success`, `stopped:preempted`; в телеметрии `play_close` с `Reason: "preempted"`.
- Открыть сессию и в цикле подавать `sceneshot` от владельца, параллельно чужой `stopplay` — отказ, пока цикл
  идёт, и вытеснение после его остановки.
- Во время play `agentbridge status --format json` показывает непустой `QueuedTasks` для ждущей чужой задачи.
- Протухший `*.task.json` в инбоксе с терминальной записью в журнале не порождает новых `rejected`-записей
  после доменного релоада.

---

После выполнения:

- Смени статус в начале документа на `Выполнено`.
- Уточни у заказчика, нужно ли обновлять документацию проекта под внесённые изменения.
