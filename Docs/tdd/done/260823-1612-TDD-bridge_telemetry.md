Status: Выполнено

Отклонения от плана, сделанные при реализации (оба — чтобы события отвечали на вопросы из «Задачи»):

- `bridge_start` пишется не из `BridgeStatusWriter.WriteOnLoad()`, а из первого тика
  `EditorTickPump.OnUpdate` (через `BridgeStatusWriter.WriteStartTelemetry()`). Будильник ставится
  на первом апдейте, то есть строго после всех статических конструкторов, поэтому в `WriteOnLoad`
  поле `Wake` по построению всегда было бы `none`. Проверено: теперь пишется `thread`.
- `task_finish` вынесен в `TelemetryLog.TaskFinished(TaskRecord)` и вызывается на всех путях, где
  запись получает терминальный статус, а не только в `TaskCoordinator.FinishTask`. Задачи `compile`,
  `tests`, `play`/`stopplay` и ответы из кэша финализируются мимо `FinishTask`, поэтому по плану
  срез «кто и на сколько занимает очередь» из Части 5 не считал бы как раз самые долгие задачи, а
  поле `Cached` не было бы `true` никогда. `TotalMs` для задачи, финализированной за доменным
  релоадом, считается из `StartedAtUtc`/`FinishedAtUtc` записи.

# ТДД: телеметрия моста

Тип: новая подсистема внутри существующего кода пакета и CLI.

## Задача

Собирать факты, по которым потом можно ответить: почему задача ждала так долго, кто и на сколько занимает
очередь, срабатывают ли таймауты и какие, засыпал ли редактор после починки будильника, сколько времени
съедают play-сессии и чем агенты вообще заняты.

## Принятые решения

- Писателей два, файла два, соединяются по идентификатору задачи: редактор пишет то, что видит только он
  (очередь, аренда, таймауты, сон), CLI пишет то, что видит только он (реальное ожидание агента и код выхода).
  Оба процесса живут на одной машине и на одних системных часах, расхождения времени нет.
- Формат — JSONL: одна строка равна одному событию. Время — unix ms UTC.
- Ротация — файл на сутки, хранение `TelemetryKeepDays` суток.
- Читается сырой JSONL; отдельной команды-агрегатора нет — фиксированный агрегатор не покажет как раз
  неочевидное, ради чего всё и затевается.
- `AgentSessionScheduler` остаётся чистым и не пишет ничего: события аренды порождает `TaskCoordinator`,
  у которого есть все нужные данные в точках вызова.

---

## Часть 1. Формат

Файлы:

- `Logs/AgentBridge-editor-YYYYMMDD.jsonl`
- `Logs/AgentBridge-client-YYYYMMDD.jsonl`

`AgentBridgeUnity/Logs/` уже в `.gitignore` репозитория, менять его не нужно.

Общий конверт каждой строки, поля всегда в этом порядке и всегда присутствуют:

```json
{"T":1756000000000,"W":"editor","E":"task_start","S":"AB_20260823_1500_a1f","Id":"t_0a1b",...}
```

- `T` — unix ms UTC.
- `W` — писатель: `editor` или `client`.
- `E` — имя события.
- `S` — agent session id, пустая строка если её нет.
- `Id` — id задачи, пустая строка если событие не про задачу.

Дальше идут поля конкретного события.

### События редактора

| `E` | Поля |
|---|---|
| `bridge_start` | `Package`, `Unity`, `Wake`, `Interaction`, `Pid` |
| `tick_gap` | `GapMs`, `HasWork`, `Focused` |
| `task_start` | `Kind`, `WaitedMs`, `QueueDepth`, `Rotated`, `Note` |
| `task_finish` | `Kind`, `Status`, `TotalMs`, `Cached`, `Waiting`, `OldestWaitS` |
| `lease_grant` | `Reason`, `Prev` |
| `lease_release` | `Reason`, `HeldMs` |
| `play_open` | `RequestedS` |
| `play_close` | `Reason`, `ActualMs` |
| `watchdog` | `What`, `Kind`, `LimitS` |

- `WaitedMs` — от создания файла задачи до старта.
- `QueueDepth` — размер очереди в момент выбора, включая стартующую задачу.
- `Rotated` — смена держателя аренды на этой задаче.
- `Waiting` и `OldestWaitS` — из `ContentionInfo` на момент завершения.
- `lease_grant.Reason` — `first` или `rotation`; `Prev` — предыдущий держатель или пустая строка.
- `lease_release.Reason` — `idle_timeout` или `release_cmd`.
- `watchdog.What` — `task_timeout`, `compile_no_reload` или `play_enter`.

### События клиента

| `E` | Поля |
|---|---|
| `cli_submit` | `Cmd`, `Note` |
| `cli_wake` | `Action`, `AgeMs` |
| `cli_exit` | `Cmd`, `Code`, `Status`, `QueuedMs`, `RunningMs`, `Posts`, `Focuses` |

- `Code` — код выхода процесса CLI.
- `Status` — терминальный статус задачи или `queued`, если клиент ушёл по исчерпанию ожидания.
- `QueuedMs` и `RunningMs` — сколько клиент ждал появления журнальной записи и сколько ждал её терминального
  статуса.
- `Posts` и `Focuses` — сколько раз пришлось будить редактор за это ожидание.

---

## Часть 2. Unity-пакет

### `Editor/BridgePaths.cs` — путь к логам

```csharp
public static string LogsRoot
{
	get { return EnsureDirectory(Path.Combine(ProjectRoot, "Logs")); }
}

public static string TelemetryFile(string writer, DateTime utc)
{
	return Path.Combine(LogsRoot, "AgentBridge-" + writer + "-" + utc.ToString("yyyyMMdd") + ".jsonl");
}
```

### `Editor/AgentBridgeSettings.cs` и `Editor/AgentBridgeSettingsStore.cs`

В настройки добавить поля:

```csharp
public bool TelemetryEnabled = true;
public int TelemetryKeepDays = 14;
```

В стор — геттеры в стиле соседних:

```csharp
public static bool GetTelemetryEnabled()
{
	AgentBridgeSettings settings = Load();
	return settings.TelemetryEnabled;
}

public static int GetTelemetryKeepDays()
{
	AgentBridgeSettings settings = Load();
	if (settings.TelemetryKeepDays <= 0)
	{
		return 14;
	}

	return settings.TelemetryKeepDays;
}
```

`ProjectSettings/AgentBridge.json` в репозитории дополнить теми же двумя ключами со значениями по умолчанию.

### `Editor/TelemetryField.cs` — новый файл

```csharp
namespace AgentBridge
{
	public struct TelemetryField
	{
		public string Name;
		public string RawValue;

		public static TelemetryField Text(string name, string value)
		{
			return new TelemetryField
			{
				Name = name,
				RawValue = "\"" + TelemetryJson.Escape(value) + "\""
			};
		}

		public static TelemetryField Number(string name, long value)
		{
			return new TelemetryField
			{
				Name = name,
				RawValue = value.ToString(System.Globalization.CultureInfo.InvariantCulture)
			};
		}

		public static TelemetryField Flag(string name, bool value)
		{
			return new TelemetryField
			{
				Name = name,
				RawValue = value ? "true" : "false"
			};
		}
	}
}
```

### `Editor/TelemetryJson.cs` — новый файл

```csharp
using System;
using System.Globalization;
using System.Text;

namespace AgentBridge
{
	public static class TelemetryJson
	{
		private const int MaxTextLength = 200;

		public static string Escape(string value)
		{
			if (string.IsNullOrEmpty(value))
			{
				return "";
			}

			string trimmed = value.Length > MaxTextLength ? value.Substring(0, MaxTextLength) : value;
			var builder = new StringBuilder(trimmed.Length + 8);

			foreach (char symbol in trimmed)
			{
				switch (symbol)
				{
					case '"':
						builder.Append("\\\"");
						break;
					case '\\':
						builder.Append("\\\\");
						break;
					case '\n':
						builder.Append("\\n");
						break;
					case '\r':
						builder.Append("\\r");
						break;
					case '\t':
						builder.Append("\\t");
						break;
					default:
						if (symbol < ' ')
						{
							builder.Append("\\u").Append(((int)symbol).ToString("x4", CultureInfo.InvariantCulture));
						}
						else
						{
							builder.Append(symbol);
						}

						break;
				}
			}

			return builder.ToString();
		}

		public static string BuildLine(
			DateTime utc,
			string writer,
			string eventName,
			string agentSessionId,
			string taskId,
			TelemetryField[] fields)
		{
			var builder = new StringBuilder(160);
			builder.Append("{\"T\":").Append(new DateTimeOffset(utc, TimeSpan.Zero).ToUnixTimeMilliseconds());
			builder.Append(",\"W\":\"").Append(writer).Append('"');
			builder.Append(",\"E\":\"").Append(eventName).Append('"');
			builder.Append(",\"S\":\"").Append(Escape(agentSessionId)).Append('"');
			builder.Append(",\"Id\":\"").Append(Escape(taskId)).Append('"');

			if (fields != null)
			{
				foreach (TelemetryField field in fields)
				{
					builder.Append(",\"").Append(field.Name).Append("\":").Append(field.RawValue);
				}
			}

			builder.Append('}');
			return builder.ToString();
		}
	}
}
```

### `Editor/TelemetryLog.cs` — новый файл

```csharp
using System;
using System.IO;
using System.Text;
using System.Threading;

namespace AgentBridge
{
	public static class TelemetryLog
	{
		private const int WriteAttempts = 3;
		private const int RetryDelayMs = 20;

		private static readonly UTF8Encoding Utf8 = new UTF8Encoding(false);
		private static string _prunedDay = "";

		public static void Write(string eventName, string agentSessionId, string taskId, TelemetryField[] fields)
		{
			if (!AgentBridgeSettingsStore.GetTelemetryEnabled())
			{
				return;
			}

			try
			{
				DateTime nowUtc = DateTime.UtcNow;
				Prune(nowUtc);
				string line = TelemetryJson.BuildLine(nowUtc, "editor", eventName, agentSessionId ?? "", taskId ?? "", fields);
				Append(BridgePaths.TelemetryFile("editor", nowUtc), line);
			}
			catch
			{
			}
		}

		private static void Append(string path, string line)
		{
			byte[] bytes = Utf8.GetBytes(line + "\n");

			for (int attempt = 0; attempt < WriteAttempts; attempt++)
			{
				try
				{
					using (var stream = new FileStream(path, FileMode.Append, FileAccess.Write, FileShare.ReadWrite))
					{
						stream.Write(bytes, 0, bytes.Length);
					}

					return;
				}
				catch (IOException)
				{
					Thread.Sleep(RetryDelayMs);
				}
			}
		}

		private static void Prune(DateTime nowUtc)
		{
			string day = nowUtc.ToString("yyyyMMdd");
			if (_prunedDay == day)
			{
				return;
			}

			_prunedDay = day;
			DateTime threshold = nowUtc.Date.AddDays(-AgentBridgeSettingsStore.GetTelemetryKeepDays());

			foreach (string file in Directory.GetFiles(BridgePaths.LogsRoot, "AgentBridge-*.jsonl"))
			{
				try
				{
					if (File.GetLastWriteTimeUtc(file) < threshold)
					{
						File.Delete(file);
					}
				}
				catch
				{
				}
			}
		}
	}
}
```

### `Editor/SchedulerState.cs` — время выдачи аренды

Добавить поле:

```csharp
public string HolderSinceUtc = "";
```

В `AgentSessionScheduler.CommitStart`, внутри блока `if (holderChanged)`:

```csharp
state.HolderSinceUtc = DateTime.UtcNow.ToString("o");
```

В `AgentSessionScheduler.ClearHolder` дописать `state.HolderSinceUtc = "";`.

Добавить публичный помощник в `AgentSessionScheduler` — он читает состояние, но ничего не пишет:

```csharp
public static long HeldMs(DateTime nowUtc)
{
	DateTime since;
	if (!TryParseUtc(SchedulerStateStore.State.HolderSinceUtc, out since))
	{
		return 0;
	}

	return (long)(nowUtc - since).TotalMilliseconds;
}
```

### `Editor/BridgeStatus.cs` и `Editor/BridgeStatusWriter.cs`

В статус добавить `public bool TelemetryEnabled;`, заполнять в `WriteOnLoad()`:

```csharp
Current.TelemetryEnabled = AgentBridgeSettingsStore.GetTelemetryEnabled();
```

Там же, последней строкой `WriteOnLoad()` после `Write()`:

```csharp
TelemetryLog.Write("bridge_start", "", "", new[]
{
	TelemetryField.Text("Package", Current.PackageVersion),
	TelemetryField.Text("Unity", Current.UnityVersion),
	TelemetryField.Text("Wake", Current.WakeTimerKind ?? "none"),
	TelemetryField.Text("Interaction", Current.InteractionMode ?? "unknown"),
	TelemetryField.Number("Pid", Current.EditorPid)
});
```

### `Editor/EditorTickPump.cs` — детектор сна

Это главный сигнал: он показывает, остаётся ли редактор засыпающим после починки будильника.

Добавить поля и константу:

```csharp
private const double GapThresholdMs = 2000d;
private static double _lastUpdateTime;
```

В начале `OnUpdate`, сразу после вычисления `now`:

```csharp
if (_lastUpdateTime > 0d)
{
	double gapMs = (now - _lastUpdateTime) * 1000d;
	if (gapMs >= GapThresholdMs)
	{
		TelemetryLog.Write("tick_gap", "", "", new[]
		{
			TelemetryField.Number("GapMs", (long)gapMs),
			TelemetryField.Flag("HasWork", HasWork),
			TelemetryField.Flag("Focused", UnityEditorInternal.InternalEditorUtility.isApplicationActive)
		});
	}
}

_lastUpdateTime = now;
```

### `Editor/TaskCoordinator.cs` — задачи и аренда

`StartTask` получает дополнительный параметр `int queueDepth`; вызовы в `TryStartNextTask`,
`TryStartPlaySessionTask` и `TryStartStopplayTask` передают `pending.Count`.

В `StartTask`, сразу после `AgentSessionScheduler.CommitStart(task, holderChanged);`:

```csharp
if (holderChanged)
{
	TelemetryLog.Write("lease_grant", task.EffectiveSessionId, id, new[]
	{
		TelemetryField.Text("Reason", string.IsNullOrEmpty(previousHolder) ? "first" : "rotation"),
		TelemetryField.Text("Prev", previousHolder)
	});
}

TelemetryLog.Write("task_start", task.EffectiveSessionId, id, new[]
{
	TelemetryField.Text("Kind", request.Kind),
	TelemetryField.Number("WaitedMs", (long)(DateTime.UtcNow - task.CreatedUtc).TotalMilliseconds),
	TelemetryField.Number("QueueDepth", queueDepth),
	TelemetryField.Flag("Rotated", holderChanged),
	TelemetryField.Text("Note", request.Note ?? "")
});
```

В `FinishTask`, сразу после `TaskJournal.Write(_activeRecord);`:

```csharp
TelemetryLog.Write("task_finish", _activeRecord.AgentSessionId, _activeRecord.Id, new[]
{
	TelemetryField.Text("Kind", _activeRecord.Kind),
	TelemetryField.Text("Status", status),
	TelemetryField.Number("TotalMs", _activeRecord.Timing.TotalMs),
	TelemetryField.Flag("Cached", _activeRecord.Cached),
	TelemetryField.Number("Waiting", _activeRecord.Contention.WaitingSessions),
	TelemetryField.Number("OldestWaitS", _activeRecord.Contention.OldestWaitSeconds)
});
```

В `TryStartNextTask`, внутри блока, где обнаружена потеря аренды по простою (там, где сейчас вызывается
`SessionContextSwitcher.TrySaveContext(idleHolder, ...)`), перед этим вызовом:

```csharp
TelemetryLog.Write("lease_release", idleHolder, "", new[]
{
	TelemetryField.Text("Reason", "idle_timeout"),
	TelemetryField.Number("HeldMs", AgentSessionScheduler.HeldMs(nowUtc))
});
```

Порядок важен: `HeldMs` должен читаться до того, как состояние держателя будет перезаписано; на этой ветке
`ClearHolder` уже отработал, поэтому вызов `HeldMs` перенести выше — вычислить `long heldMs` до
`AgentSessionScheduler.TickIdle(...)` и использовать сохранённое значение.

В `RunReleaseTask`, перед `AgentSessionScheduler.Release(effective);`:

```csharp
TelemetryLog.Write("lease_release", effective, request.Id, new[]
{
	TelemetryField.Text("Reason", "release_cmd"),
	TelemetryField.Number("HeldMs", AgentSessionScheduler.HeldMs(DateTime.UtcNow))
});
```

В `CheckTimeout`, перед вызовом `FinishTask("timeout", ...)`:

```csharp
TelemetryLog.Write("watchdog", _activeRecord.AgentSessionId, _activeRecord.Id, new[]
{
	TelemetryField.Text("What", "task_timeout"),
	TelemetryField.Text("Kind", _activeRecord.Kind),
	TelemetryField.Number("LimitS", timeoutSeconds)
});
```

В `PollCompileTask`, сразу после `if (!CompileTaskExecutor.IsTimedOut()) { return; }`:

```csharp
TelemetryLog.Write("watchdog", _activeRecord.AgentSessionId, _activeRecord.Id, new[]
{
	TelemetryField.Text("What", "compile_no_reload"),
	TelemetryField.Text("Kind", "compile"),
	TelemetryField.Number("LimitS", 20)
});
```

### `Editor/PlaySessionManager.cs` — play-сессии

В `BeginPlay`, сразу после `PlaySessionStore.Write(state);`:

```csharp
TelemetryLog.Write("play_open", state.OwnerAgentSessionId, request.Id, new[]
{
	TelemetryField.Number("RequestedS", seconds)
});
```

В `ReconcileEntering`, в ветке таймаута входа, перед `FinalizeRecord(state.TaskId, "runtime_error", ...)`:

```csharp
TelemetryLog.Write("watchdog", state.OwnerAgentSessionId, state.TaskId, new[]
{
	TelemetryField.Text("What", "play_enter"),
	TelemetryField.Text("Kind", "play"),
	TelemetryField.Number("LimitS", (long)EnterTimeoutSeconds)
});
```

В `ReconcileActive` и `ReconcileExiting`, непосредственно перед каждым `PlaySessionStore.Delete();`:

```csharp
TelemetryLog.Write("play_close", state.OwnerAgentSessionId, state.TaskId, new[]
{
	TelemetryField.Text("Reason", string.IsNullOrEmpty(state.StopReason) ? "external" : state.StopReason),
	TelemetryField.Number("ActualMs", ElapsedMs(state))
});
```

Приватный помощник в том же классе:

```csharp
private static long ElapsedMs(PlaySessionState state)
{
	DateTime startedAtUtc;
	if (!TryParseUtc(state.StartedAtUtc, out startedAtUtc))
	{
		return 0;
	}

	return (long)(DateTime.UtcNow - startedAtUtc).TotalMilliseconds;
}
```

---

## Часть 3. CLI

### `TelemetryLog.cs` — новый файл

Клиент не читает `ProjectSettings/AgentBridge.json`: включённость телеметрии он берёт из статуса, который уже
прочитан инспектором. Если статус недоступен — не пишет ничего.

```csharp
using System.Text;
using System.Text.Json;

namespace AgentBridge.Cli;

internal sealed class TelemetryLog
{
	private const int WriteAttempts = 3;
	private const int RetryDelayMs = 20;
	private const int MaxTextLength = 200;

	private readonly string _logsRoot;
	private readonly bool _enabled;

	public TelemetryLog(string projectRoot, bool enabled)
	{
		_logsRoot = Path.Combine(projectRoot, "Logs");
		_enabled = enabled;
	}

	public void Write(string eventName, string? session, string? taskId, Dictionary<string, object?> fields)
	{
		if (!_enabled)
		{
			return;
		}

		try
		{
			var nowUtc = DateTime.UtcNow;
			var payload = new Dictionary<string, object?>
			{
				["T"] = new DateTimeOffset(nowUtc, TimeSpan.Zero).ToUnixTimeMilliseconds(),
				["W"] = "client",
				["E"] = eventName,
				["S"] = Trim(session),
				["Id"] = Trim(taskId)
			};

			foreach (var pair in fields)
			{
				payload[pair.Key] = pair.Value is string text ? Trim(text) : pair.Value;
			}

			Directory.CreateDirectory(_logsRoot);
			var path = Path.Combine(_logsRoot, "AgentBridge-client-" + nowUtc.ToString("yyyyMMdd") + ".jsonl");
			Append(path, JsonSerializer.Serialize(payload, JsonSupport.Task));
		}
		catch
		{
		}
	}

	private static string Trim(string? value)
	{
		if (string.IsNullOrEmpty(value))
		{
			return "";
		}

		return value.Length > MaxTextLength ? value[..MaxTextLength] : value;
	}

	private static void Append(string path, string line)
	{
		var bytes = new UTF8Encoding(false).GetBytes(line + "\n");

		for (var attempt = 0; attempt < WriteAttempts; attempt++)
		{
			try
			{
				using var stream = new FileStream(path, FileMode.Append, FileAccess.Write, FileShare.ReadWrite);
				stream.Write(bytes, 0, bytes.Length);
				return;
			}
			catch (IOException)
			{
				Thread.Sleep(RetryDelayMs);
			}
		}
	}
}
```

### `BridgeStatus.cs` — зеркало поля

```csharp
public bool TelemetryEnabled { get; set; }
```

### `BridgeClient.cs` — точки съёма

Конструктор получает готовый журнал: сигнатура становится

```csharp
public BridgeClient(string projectRoot, string format, string? session, string? note, TelemetryLog telemetry)
```

поле `_telemetry` сохраняется. В `AgentBridgeApplication.RunAsync` клиент создаётся так:

```csharp
var telemetry = new TelemetryLog(projectRoot, health.Bridge?.TelemetryEnabled ?? false);
var client = new BridgeClient(projectRoot, options.Format, options.Session, options.Note, telemetry);
```

`SubmitPayloadAsync` и `SubmitRequestAsync` — сразу после записи файла задачи в инбокс:

```csharp
_telemetry.Write("cli_submit", _session, request.Id, new Dictionary<string, object?>
{
	["Cmd"] = request.Kind,
	["Note"] = _note ?? ""
});
```

`WaitForTaskAsync` — учёт и итог. В начале метода:

```csharp
var waitStartedUtc = DateTime.UtcNow;
DateTime? runningStartedUtc = null;
```

`runningStartedUtc ??= now;` ставится там же, где сейчас `runningSince ??= now;`.

В блоке пробуждения из предыдущего ТДД, после инкремента счётчика попытки:

```csharp
_telemetry.Write("cli_wake", _session, taskId, new Dictionary<string, object?>
{
	["Action"] = action == WakeAction.Post ? "post" : "focus",
	["AgeMs"] = pulse.HeartbeatAgeMs ?? 0
});
```

Каждый выход из `WaitForTaskAsync` проходит через общий локальный метод, который и пишет итог:

```csharp
int Complete(int code, string status)
{
	var finishedUtc = DateTime.UtcNow;
	var runningMs = runningStartedUtc == null
		? 0
		: (long)(finishedUtc - runningStartedUtc.Value).TotalMilliseconds;
	var queuedMs = (long)((runningStartedUtc ?? finishedUtc) - waitStartedUtc).TotalMilliseconds;

	_telemetry.Write("cli_exit", _session, taskId, new Dictionary<string, object?>
	{
		["Cmd"] = kind,
		["Code"] = code,
		["Status"] = status,
		["QueuedMs"] = queuedMs,
		["RunningMs"] = runningMs,
		["Posts"] = attempts.PostAttempts,
		["Focuses"] = attempts.FocusAttempts
	});

	return code;
}
```

`kind` — новый строковый параметр `WaitForTaskAsync(string taskId, int waitSeconds, string kind)`; все вызывающие
методы передают свой `request.Kind`, а `AgentBridgeApplication` для команды `wait` передаёт `"wait"`.

Заменить существующие выходы:

- терминальный статус — `WriteResult(json); return Complete(ClassifyResult(json), StatusOf(json));`
- исчерпание клиентского бюджета — `WriteResult(json); return Complete(2, "running");`
- исчерпание очередного бюджета — `WriteResult(...); return Complete(2, "queued");`
- `task_not_found`, `bridge_unavailable`, `bridge_asleep`, `invalid_task_id` — `return Complete(3, "<code>");`

`StatusOf(json)` — приватный статический метод, читающий поле `Status` через `JsonDocument`, с `""` при разборе
с ошибкой.

---

## Часть 4. Тесты

### `AgentBridgeCli.Tests/Program.cs`

Добавить вызов `RunTelemetryTests(root);` и метод:

```csharp
static void RunTelemetryTests(string temporaryRoot)
{
	var project = Path.Combine(temporaryRoot, "TelemetryProject");
	CreateProject(project);

	var disabled = new TelemetryLog(project, false);
	disabled.Write("cli_submit", "s1", "t1", new Dictionary<string, object?> { ["Cmd"] = "csharp" });
	Expect(!Directory.Exists(Path.Combine(project, "Logs")), "disabled telemetry must not create the folder");

	var enabled = new TelemetryLog(project, true);
	enabled.Write("cli_submit", "s1", "t1", new Dictionary<string, object?> { ["Cmd"] = "csharp", ["Note"] = "тест \"кавычки\"" });
	enabled.Write("cli_exit", "s1", "t1", new Dictionary<string, object?> { ["Code"] = 0 });

	var file = Directory.GetFiles(Path.Combine(project, "Logs"), "AgentBridge-client-*.jsonl").Single();
	var lines = File.ReadAllLines(file);
	Expect(lines.Length == 2, "each event must be one line");

	using var document = JsonDocument.Parse(lines[0]);
	Expect(document.RootElement.GetProperty("E").GetString() == "cli_submit", "event name must round-trip");
	Expect(document.RootElement.GetProperty("W").GetString() == "client", "writer must be marked");
	Expect(document.RootElement.GetProperty("Note").GetString()!.Contains('"'), "quotes must survive escaping");
}
```

### `AgentBridgeUnity/Assets/Tests/Editor/AgentBridgeTelemetryTests.cs` — новый файл

```csharp
using NUnit.Framework;
using AgentBridge;

public class AgentBridgeTelemetryTests
{
	[Test]
	public void EscapesControlCharactersAndQuotes()
	{
		Assert.AreEqual("a\\\"b", TelemetryJson.Escape("a\"b"));
		Assert.AreEqual("a\\nb", TelemetryJson.Escape("a\nb"));
		Assert.AreEqual("a\\\\b", TelemetryJson.Escape("a\\b"));
	}

	[Test]
	public void BuildsEnvelopeInFixedOrder()
	{
		var line = TelemetryJson.BuildLine(
			new System.DateTime(2026, 8, 23, 12, 0, 0, System.DateTimeKind.Utc),
			"editor",
			"task_start",
			"s1",
			"t1",
			new[] { TelemetryField.Number("QueueDepth", 3), TelemetryField.Flag("Rotated", true) });

		Assert.AreEqual(
			"{\"T\":1787832000000,\"W\":\"editor\",\"E\":\"task_start\",\"S\":\"s1\",\"Id\":\"t1\",\"QueueDepth\":3,\"Rotated\":true}",
			line);
	}

	[Test]
	public void TruncatesLongText()
	{
		Assert.AreEqual(200, TelemetryJson.Escape(new string('x', 500)).Length);
	}
}
```

Ожидаемое значение `T` в тесте пересчитать при написании: это unix ms для указанной даты.

---

## Часть 5. Как читать

Строки соединяются по `Id`. Готовые срезы для разбора:

```bash
cd AgentBridgeUnity/Logs

# кто и сколько держал редактор
cat AgentBridge-editor-*.jsonl | python3 -c "
import sys, json, collections
held = collections.Counter()
for line in sys.stdin:
    e = json.loads(line)
    if e['E'] == 'task_finish':
        held[e['S']] += e['TotalMs']
for session, ms in held.most_common():
    print(f'{ms/1000:8.1f}s  {session}')
"

# самые долгие ожидания в очереди
cat AgentBridge-editor-*.jsonl | grep '"task_start"' | python3 -c "
import sys, json
rows = [json.loads(l) for l in sys.stdin]
for e in sorted(rows, key=lambda r: -r['WaitedMs'])[:20]:
    print(e['WaitedMs'], e['Kind'], e['S'], e['Note'])
"

# засыпал ли редактор и когда
grep '"tick_gap"' AgentBridge-editor-*.jsonl

# все сработавшие таймауты
grep '"watchdog"' AgentBridge-editor-*.jsonl

# чем ожидание закончилось для агента
grep '"cli_exit"' AgentBridge-client-*.jsonl
```

---

## Часть 6. Версии и сборка

- `AgentBridgeUnity/Packages/com.elmortem.agentbridge/package.json`: `0.19.0` → `0.20.0`.
- `AgentBridgeCli/AgentBridgeCli.csproj`: `1.13.0` → `1.14.0`.
- `unity-bridge-plugin/.claude-plugin/plugin.json`: `1.17.0` → `1.17.1`; в `skills/unity-bridge/SKILL.md` одной
  строкой указать, что мост ведёт телеметрию в `Logs/AgentBridge-*.jsonl` и её можно читать напрямую при разборе
  зависаний.
- Пересобрать плагин: `powershell -NoProfile -ExecutionPolicy Bypass -File scripts/build-plugin.ps1`.
- Прогнать тесты CLI:
  `dotnet build AgentBridgeCli/AgentBridgeCli.csproj -c Release` и
  `dotnet run --project AgentBridgeCli.Tests/AgentBridgeCli.Tests.csproj -c Release`.

## Проверка результата

- Выполнить подряд `agentbridge compile --session TEST_A --note "проверка телеметрии"` и
  `agentbridge release --session TEST_A`. В `Logs/AgentBridge-editor-<дата>.jsonl` появляются `task_start`,
  `task_finish`, `lease_grant`, `lease_release`; в `Logs/AgentBridge-client-<дата>.jsonl` — `cli_submit` и
  `cli_exit` с теми же `Id`.
- Запустить две команды из разных сессий одновременно: у второй `task_start.WaitedMs` заметно больше нуля,
  `QueueDepth` равен двум, а у первой `task_finish.Waiting` равен единице.
- Выставить `TelemetryEnabled: false` в `ProjectSettings/AgentBridge.json`, перезагрузить домен и убедиться,
  что новые строки не появляются ни у редактора, ни у клиента.
- Каждая строка обоих файлов разбирается `json.loads` без ошибок.

---

После выполнения:

- Смени статус в начале документа на `Выполнено`.
- Уточни у заказчика, нужно ли обновлять документацию проекта под внесённые изменения.
