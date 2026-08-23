Status: Выполнено

# ТДД: пробуждение главного цикла редактора и наблюдаемость сна моста

Тип: рефакторинг существующего механизма пробуждения плюс новая диагностика.

## Проблема

Всё в мосте едет на `EditorApplication.update`: скан очереди в `TaskCoordinator.OnUpdate`, `PollCompileTask`,
`CheckTimeout`, продолжения `MainThreadDispatcher` и heartbeat. Unity перестаёт вызывать `update`, когда окно
редактора не в фокусе, и троттлит его согласно Preferences → General → Interaction Mode. Нет тиков — очередь
стоит, компиляция не стартует, таймауты не срабатывают, heartbeat протухает, агенты получают `heartbeat_stale`.

Текущие два слоя пробуждения дырявые:

- `EditorTickPump` дёргает `EditorApplication.SignalTick` изнутри самого `EditorApplication.update` и при этом
  выходит без вызова, если с прошлого раза прошло меньше `IdleTickIntervalMs`. Между вызовами мост зависит от
  собственного фонового тика Unity, то есть ровно от того, что троттлится.
- `AgentEditorWakeTimer` (WinAPI `SetTimer`) — единственный внешний источник пробуждения — берёт hwnd из
  `Process.GetCurrentProcess().MainWindowHandle`, который на `[InitializeOnLoad]` может быть нулевым. При неудаче
  `Start()` молча выходит, повторов нет, и в статусе это никак не отражается.

## Состав изменений

- Unity-пакет: будильник переводится на потоковый таймер без hwnd, ставится и переставляется из тика,
  `SignalTick` перестаёт троттлиться при наличии работы, в статус добавляются поля наблюдаемости.
- CLI: heartbeat проверяется не только пока задача в очереди, но и пока она выполняется; при протухшем heartbeat
  клиент будит редактор сообщением, а фокус-тычок остаётся крайней мерой; `doctor` печатает предупреждения.

---

## Часть 1. Unity-пакет

### `Editor/AgentEditorWakeTimer.cs` — переписать целиком

Таймер ставится на очередь сообщений потока (`hWnd = NULL`), поэтому дескриптор окна больше не нужен и не может
не разрешиться. `WM_TIMER` от такого таймера попадает в очередь главного потока и будит цикл сообщений Unity.

```csharp
using System;
using System.Runtime.InteropServices;
using UnityEngine;

namespace AgentBridge
{
	public static class AgentEditorWakeTimer
	{
		private const int MinimumIntervalMs = 15;
		private const double RetryIntervalSeconds = 1d;

		private static UIntPtr _timerId;
		private static int _intervalMs;
		private static double _nextAttemptTime;

		public static bool Installed { get; private set; }

		public static string Kind { get; private set; }

		[DllImport("user32.dll", SetLastError = true)]
		private static extern UIntPtr SetTimer(IntPtr hWnd, UIntPtr nIDEvent, uint uElapse, IntPtr lpTimerFunc);

		[DllImport("user32.dll", SetLastError = true)]
		private static extern bool KillTimer(IntPtr hWnd, UIntPtr uIDEvent);

		public static void Ensure(int intervalMs, double nowSeconds)
		{
			if (Application.platform != RuntimePlatform.WindowsEditor)
			{
				Kind = "unsupported";
				return;
			}

			int clamped = intervalMs < MinimumIntervalMs ? MinimumIntervalMs : intervalMs;
			if (Installed && _intervalMs == clamped)
			{
				return;
			}

			if (!Installed && nowSeconds < _nextAttemptTime)
			{
				return;
			}

			Stop();
			_nextAttemptTime = nowSeconds + RetryIntervalSeconds;

			UIntPtr id = SetTimer(IntPtr.Zero, UIntPtr.Zero, (uint)clamped, IntPtr.Zero);
			if (id == UIntPtr.Zero)
			{
				Kind = "none";
				return;
			}

			_timerId = id;
			_intervalMs = clamped;
			Installed = true;
			Kind = "thread";
		}

		public static void Stop()
		{
			if (!Installed)
			{
				return;
			}

			KillTimer(IntPtr.Zero, _timerId);
			_timerId = UIntPtr.Zero;
			_intervalMs = 0;
			Installed = false;
			Kind = "none";
		}
	}
}
```

Инициализировать `Kind` значением `"none"` в объявлении свойства нельзя (авто-свойство), поэтому присвоить его в
`Stop()` и в ветках `Ensure`; стартовое значение `null` до первого вызова допустимо, в статус оно пишется через
`?? "none"`.

### `Editor/EditorTickPump.cs` — переписать логику подписки и тика

Изменения:

- Подписка на `EditorApplication.update` выполняется **всегда**, а не только когда найден `SignalTick`.
  Отсутствие `SignalTick` больше не отключает пампу: она продолжает содержать будильник.
- Вызов `AgentEditorWakeTimer.Start()` из ветки отсутствия `SignalTick` убрать: таймер теперь ведёт `OnUpdate`.
- Добавить публичное поле `HasPendingWork` и свойство `HasWork`.
- Добавить чистый статический предикат `ShouldSignal` — он же точка тестирования.
- Публиковать состояние будильника в статус при изменении.

```csharp
public static bool HasActiveTask;
public static bool HasPendingWork;

public static bool HasWork
{
	get { return HasActiveTask || HasPendingWork; }
}

public static bool ShouldSignal(double nowSeconds, double lastSignalSeconds, bool hasWork, int intervalMs)
{
	if (hasWork)
	{
		return true;
	}

	return (nowSeconds - lastSignalSeconds) * 1000d >= intervalMs;
}

private static void OnUpdate()
{
	double now = EditorApplication.timeSinceStartup;
	int intervalMs = HasWork
		? AgentBridgeSettingsStore.GetActiveTickIntervalMs()
		: AgentBridgeSettingsStore.GetIdleTickIntervalMs();

	AgentEditorWakeTimer.Ensure(intervalMs, now);
	PublishWakeState();

	if (!ShouldSignal(now, _lastTickTime, HasWork, intervalMs))
	{
		return;
	}

	_lastTickTime = now;

	if (_signalTick == null)
	{
		return;
	}

	_signalTick();
}

private static void PublishWakeState()
{
	string kind = AgentEditorWakeTimer.Kind ?? "none";
	if (BridgeStatusWriter.Current.WakeTimerInstalled == AgentEditorWakeTimer.Installed
		&& BridgeStatusWriter.Current.WakeTimerKind == kind)
	{
		return;
	}

	BridgeStatusWriter.Current.WakeTimerInstalled = AgentEditorWakeTimer.Installed;
	BridgeStatusWriter.Current.WakeTimerKind = kind;
	BridgeStatusWriter.Write();
}
```

`Unsubscribe` дополнительно вызывает `AgentEditorWakeTimer.Stop()`.

### `Editor/TaskCoordinator.cs` — поднимать флаг работы

В `BuildPendingList`, непосредственно перед `return pending;` в конце метода:

```csharp
EditorTickPump.HasPendingWork = pending.Count > 0;
```

Ранний `return pending;` в начале метода (когда каталога `Inbox` нет) дополнить той же строкой.

### `Editor/InteractionModeProbe.cs` — новый файл

```csharp
using System;
using System.Reflection;
using UnityEditor;

namespace AgentBridge
{
	public static class InteractionModeProbe
	{
		public static string Read()
		{
			PropertyInfo property = typeof(EditorApplication).GetProperty(
				"interactionMode",
				BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic);

			if (property != null)
			{
				try
				{
					object value = property.GetValue(null);
					if (value != null)
					{
						return value.ToString();
					}
				}
				catch
				{
				}
			}

			return "unknown";
		}

		public static bool IsThrottled(string mode)
		{
			return string.Equals(mode, "Default", StringComparison.OrdinalIgnoreCase)
				|| string.Equals(mode, "MonitorRefreshRate", StringComparison.OrdinalIgnoreCase)
				|| string.Equals(mode, "Custom", StringComparison.OrdinalIgnoreCase);
		}
	}
}
```

Любое значение, кроме перечисленных трёх, считается нетроттлящим и предупреждения не даёт. `"unknown"` —
штатный результат на версиях Unity без этого свойства, он ничего не ломает.

### `Editor/BridgeStatus.cs` — новые поля

Добавить после `SignalTickAvailable`:

```csharp
public bool WakeTimerInstalled;
public string WakeTimerKind;
public string InteractionMode;
```

### `Editor/BridgeStatusWriter.cs` — заполнение при загрузке

В `WriteOnLoad()` перед вызовом `Write()`:

```csharp
Current.WakeTimerInstalled = AgentEditorWakeTimer.Installed;
Current.WakeTimerKind = AgentEditorWakeTimer.Kind ?? "none";
Current.InteractionMode = InteractionModeProbe.Read();
```

### `Editor/AgentBridge.cs` — снять владение таймером

В `Initialize()` убрать `AgentEditorWakeTimer.Start()`; подписки `beforeAssemblyReload` и `quitting` на
`AgentEditorWakeTimer.Stop` оставить без изменений. В `Stop()` вызов `AgentEditorWakeTimer.Stop()` оставить.

---

## Часть 2. CLI

### `BridgeStatus.cs` — зеркало новых полей

```csharp
public bool WakeTimerInstalled { get; set; }
public string? WakeTimerKind { get; set; }
public string? InteractionMode { get; set; }
```

### `BridgeHealth.cs` — предупреждения

```csharp
public List<string> Warnings { get; set; } = new();
```

### `BridgeInspector.cs` — заполнение предупреждений

Внутри блока `if (health.Bridge != null)`, после проверки `RoslynReady`:

```csharp
if (!health.Bridge.SignalTickAvailable)
{
	health.Warnings.Add("signal_tick_missing");
}

if (!health.Bridge.WakeTimerInstalled
	&& string.Equals(health.HostOs, HostPlatform.Windows, StringComparison.OrdinalIgnoreCase))
{
	health.Warnings.Add("wake_timer_missing");
}

if (IsThrottledInteractionMode(health.Bridge.InteractionMode))
{
	health.Warnings.Add("interaction_throttled");
}
```

Приватный помощник в том же классе:

```csharp
private static bool IsThrottledInteractionMode(string? mode)
{
	if (string.IsNullOrWhiteSpace(mode))
	{
		return false;
	}

	var normalized = mode.Trim();
	return string.Equals(normalized, "Default", StringComparison.OrdinalIgnoreCase)
		|| string.Equals(normalized, "MonitorRefreshRate", StringComparison.OrdinalIgnoreCase)
		|| string.Equals(normalized, "Custom", StringComparison.OrdinalIgnoreCase);
}
```

`Warnings` не участвуют в `BridgeReady`, `Ok`, `Code` и не меняют коды выхода.

### `WakeAction.cs` — новый файл

```csharp
namespace AgentBridge.Cli;

internal enum WakeAction
{
	None,
	Post,
	Focus
}
```

### `WakePolicy.cs` — новый файл

```csharp
namespace AgentBridge.Cli;

internal static class WakePolicy
{
	public const long StaleThresholdMs = 5000;
	public const int MaxPostAttempts = 5;
	public const int MaxFocusAttempts = 1;
	public const double AttemptIntervalSeconds = 3d;

	public static WakeAction Decide(
		long? heartbeatAgeMs,
		bool editorIsForeground,
		int postAttempts,
		int focusAttempts,
		double secondsSinceLastAttempt)
	{
		if (heartbeatAgeMs == null)
		{
			return WakeAction.None;
		}

		if (heartbeatAgeMs < StaleThresholdMs)
		{
			return WakeAction.None;
		}

		if (secondsSinceLastAttempt < AttemptIntervalSeconds)
		{
			return WakeAction.None;
		}

		if (editorIsForeground)
		{
			return WakeAction.None;
		}

		if (postAttempts < MaxPostAttempts)
		{
			return WakeAction.Post;
		}

		if (focusAttempts < MaxFocusAttempts)
		{
			return WakeAction.Focus;
		}

		return WakeAction.None;
	}
}
```

Фокус-тычок не выполняется, пока не исчерпаны попытки разбудить редактор сообщением, и никогда не выполняется,
если окно редактора уже на переднем плане.

### `EditorWakeAttempts.cs` — новый файл

```csharp
namespace AgentBridge.Cli;

internal sealed class EditorWakeAttempts
{
	public int PostAttempts { get; set; }
	public int FocusAttempts { get; set; }
	public DateTime LastAttemptUtc { get; set; } = DateTime.MinValue;

	public bool Exhausted
	{
		get { return PostAttempts >= WakePolicy.MaxPostAttempts && FocusAttempts >= WakePolicy.MaxFocusAttempts; }
	}
}
```

### `EditorWaker.cs` — новый файл

Основное средство — `PostMessage(hwnd, WM_NULL)`: сообщение попадает в очередь окна редактора и будит цикл, не
трогая фокус. Фокус-тычок с возвратом прежнего окна — крайняя мера.

```csharp
using System.Diagnostics;
using System.Runtime.InteropServices;

namespace AgentBridge.Cli;

internal static class EditorWaker
{
	private const uint WmNull = 0x0000;
	private const int FocusHoldMs = 250;

	[DllImport("user32.dll", SetLastError = true)]
	private static extern bool PostMessage(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);

	[DllImport("user32.dll")]
	private static extern IntPtr GetForegroundWindow();

	[DllImport("user32.dll")]
	private static extern bool SetForegroundWindow(IntPtr hWnd);

	[DllImport("user32.dll", SetLastError = true)]
	private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint processId);

	public static bool IsEditorForeground(int editorPid)
	{
		if (!OperatingSystem.IsWindows())
		{
			return false;
		}

		var foreground = GetForegroundWindow();
		if (foreground == IntPtr.Zero)
		{
			return false;
		}

		GetWindowThreadProcessId(foreground, out var processId);
		return processId == (uint)editorPid;
	}

	public static bool TryPost(int editorPid)
	{
		if (!OperatingSystem.IsWindows())
		{
			return false;
		}

		var window = ResolveMainWindow(editorPid);
		if (window == IntPtr.Zero)
		{
			return false;
		}

		return PostMessage(window, WmNull, IntPtr.Zero, IntPtr.Zero);
	}

	public static bool TryFocus(int editorPid)
	{
		if (!OperatingSystem.IsWindows())
		{
			return false;
		}

		var window = ResolveMainWindow(editorPid);
		if (window == IntPtr.Zero)
		{
			return false;
		}

		var previous = GetForegroundWindow();
		SetForegroundWindow(window);
		Thread.Sleep(FocusHoldMs);

		if (previous != IntPtr.Zero && previous != window)
		{
			SetForegroundWindow(previous);
		}

		return true;
	}

	private static IntPtr ResolveMainWindow(int editorPid)
	{
		try
		{
			using var process = Process.GetProcessById(editorPid);
			return process.MainWindowHandle;
		}
		catch
		{
			return IntPtr.Zero;
		}
	}
}
```

### `BridgeClient.cs` — heartbeat и пробуждение в обеих фазах ожидания

Проверка здоровья сейчас живёт только в ветке «записи ещё нет». Её нужно вынести перед разветвлением, чтобы она
работала и пока задача выполняется.

В начале `WaitForTaskAsync` добавить:

```csharp
var attempts = new EditorWakeAttempts();
var nextHealthPoll = DateTime.MinValue;
```

Внутри `while (true)`, сразу после `var now = DateTime.UtcNow;` и **до** ветки `if (hasRecord)`:

```csharp
if (now >= nextHealthPoll)
{
	nextHealthPoll = now.AddSeconds(3);
	var pulse = BridgeInspector.Inspect(_projectRoot);
	var asleep = pulse.Problems.Contains("heartbeat_stale");

	if (!pulse.BridgeReady && !asleep)
	{
		return WriteError("bridge_unavailable", "Bridge became unavailable while the task was waiting: " + pulse.Code);
	}

	if (asleep)
	{
		var pid = pulse.Bridge?.EditorPid ?? 0;
		var action = WakePolicy.Decide(
			pulse.HeartbeatAgeMs,
			EditorWaker.IsEditorForeground(pid),
			attempts.PostAttempts,
			attempts.FocusAttempts,
			(now - attempts.LastAttemptUtc).TotalSeconds);

		if (action == WakeAction.Post)
		{
			attempts.PostAttempts++;
			attempts.LastAttemptUtc = now;
			EditorWaker.TryPost(pid);
			Console.Error.WriteLine("[agentbridge] editor asleep for "
				+ (pulse.HeartbeatAgeMs ?? 0) / 1000 + "s, waking (post #" + attempts.PostAttempts + ")");
		}
		else if (action == WakeAction.Focus)
		{
			attempts.FocusAttempts++;
			attempts.LastAttemptUtc = now;
			EditorWaker.TryFocus(pid);
			Console.Error.WriteLine("[agentbridge] editor still asleep, focus poke");
		}
		else if (attempts.Exhausted)
		{
			return WriteError(
				"bridge_asleep",
				"The Unity editor stopped ticking and did not wake up. Focus the editor window, "
				+ "and set Preferences > General > Interaction Mode to No Throttling.");
		}
	}
	else
	{
		attempts.PostAttempts = 0;
		attempts.FocusAttempts = 0;
	}
}
```

В существующей ветке `if (hasRecord)` блок `nextHealthCheck` больше не нужен: удалить объявление
`nextHealthCheck`, вычисление `queued >= nextHealthCheck` и вложенную проверку `BridgeInspector.Inspect`, оставив
только печать позиции в очереди по расписанию `nextQueueReport` (переименованное поле того же назначения,
интервал 5 секунд) и `DescribeQueuePosition` без изменений. Значение `health` для `DescribeQueuePosition` брать
из последнего `pulse`, сохранённого в локальную переменную `lastHealth`.

Успешное пробуждение обнуляет счётчики, поэтому одна долгая задача с редкими провалами в сон не исчерпывает
лимит попыток.

### `AgentBridgeApplication.cs` — вывод предупреждений

В `WriteDoctor`, в человеческом формате, после цикла по `health.Problems`:

```csharp
foreach (var warning in health.Warnings)
{
	Console.Out.WriteLine("! " + warning);
}
```

В `WriteHealth`, в человеческом формате, внутри блока `if (health.Bridge != null)` после строки `Roslyn:`:

```csharp
Console.Out.WriteLine("Wake timer: "
	+ (health.Bridge.WakeTimerInstalled ? (health.Bridge.WakeTimerKind ?? "installed") : "missing"));
Console.Out.WriteLine("Interaction mode: " + (health.Bridge.InteractionMode ?? "unknown"));
```

JSON-формат меняется автоматически: `Warnings` и новые поля попадают в сериализацию `BridgeHealth` и
`BridgeStatus`.

---

## Часть 3. Тесты

### `AgentBridgeCli.Tests/Program.cs`

Добавить вызов `RunWakePolicyTests();` в список после `RunContentionFormattingTests();` и метод:

```csharp
static void RunWakePolicyTests()
{
	Expect(WakePolicy.Decide(null, false, 0, 0, 100d) == WakeAction.None, "unknown heartbeat age must not poke");
	Expect(WakePolicy.Decide(1000, false, 0, 0, 100d) == WakeAction.None, "fresh heartbeat must not poke");
	Expect(WakePolicy.Decide(9000, true, 0, 0, 100d) == WakeAction.None, "focused editor must not be poked");
	Expect(WakePolicy.Decide(9000, false, 0, 0, 1d) == WakeAction.None, "attempts must respect the interval");
	Expect(WakePolicy.Decide(9000, false, 0, 0, 100d) == WakeAction.Post, "stale heartbeat must post first");
	Expect(
		WakePolicy.Decide(9000, false, WakePolicy.MaxPostAttempts, 0, 100d) == WakeAction.Focus,
		"focus poke only after posts are exhausted");
	Expect(
		WakePolicy.Decide(9000, false, WakePolicy.MaxPostAttempts, WakePolicy.MaxFocusAttempts, 100d) == WakeAction.None,
		"exhausted attempts must stop poking");
}
```

`WakePolicy`, `WakeAction` и `EditorWakeAttempts` объявлены `internal`, тестовый проект уже видит внутренние типы
CLI — дополнительной настройки не требуется.

### `AgentBridgeUnity/Assets/Tests/Editor/AgentBridgeWakeTests.cs` — новый файл

```csharp
using NUnit.Framework;
using AgentBridge;

public class AgentBridgeWakeTests
{
	[Test]
	public void SignalsEveryTickWhileWorkIsPending()
	{
		Assert.IsTrue(EditorTickPump.ShouldSignal(10d, 9.999d, true, 500));
	}

	[Test]
	public void ThrottlesWhenIdle()
	{
		Assert.IsFalse(EditorTickPump.ShouldSignal(10d, 9.9d, false, 500));
		Assert.IsTrue(EditorTickPump.ShouldSignal(10d, 9.4d, false, 500));
	}

	[Test]
	public void UnknownInteractionModeIsNotThrottled()
	{
		Assert.IsFalse(InteractionModeProbe.IsThrottled("unknown"));
		Assert.IsFalse(InteractionModeProbe.IsThrottled("NoThrottling"));
		Assert.IsTrue(InteractionModeProbe.IsThrottled("MonitorRefreshRate"));
	}
}
```

---

## Часть 4. Документация плагина

В `unity-bridge-plugin/skills/unity-bridge/SKILL.md`, в раздел о диагностике, добавить:

- Код ошибки `bridge_asleep` означает, что главный цикл редактора уснул и не проснулся после попыток
  разбудить его. Задача при этом остаётся в очереди и выполнится, когда редактор оживёт; повторять команду не
  нужно, достаточно дождаться и запросить результат по тому же id через `agentbridge wait <id>`.
- Если `agentbridge doctor` печатает `! interaction_throttled`, человеку стоит выставить
  Preferences → General → Interaction Mode = No Throttling: без этого редактор в фоне засыпает штатно.

---

## Часть 5. Версии и сборка

- `AgentBridgeUnity/Packages/com.elmortem.agentbridge/package.json`: `0.18.0` → `0.19.0`.
- `AgentBridgeCli/AgentBridgeCli.csproj`: `<Version>1.12.0</Version>` → `1.13.0`.
- `unity-bridge-plugin/.claude-plugin/plugin.json`: `1.16.2` → `1.17.0`.
- Пересобрать плагин: `powershell -NoProfile -ExecutionPolicy Bypass -File scripts/build-plugin.ps1`;
  прогон обязан закончиться `invalid_entries=0` и `zip_validation=PASS`.
- Прогнать тесты CLI:
  `dotnet build AgentBridgeCli/AgentBridgeCli.csproj -c Release` и
  `dotnet run --project AgentBridgeCli.Tests/AgentBridgeCli.Tests.csproj -c Release`.

## Проверка результата

- Свернуть окно Unity, из терминала выполнить `agentbridge compile --wait 60`. Задача должна выполниться без
  прикосновения к редактору, в stderr не должно появиться ни одной строки про сон.
- `agentbridge status --format human` при свёрнутом редакторе печатает `Wake timer: thread`.
- `agentbridge doctor --format json` содержит массив `Health.Warnings`; при троттлящем Interaction Mode в нём
  присутствует `interaction_throttled`, а коды выхода команды не меняются.

---

После выполнения:

- Смени статус в начале документа на `Выполнено`.
- Уточни у заказчика, нужно ли обновлять документацию проекта под внесённые изменения.
