Status: В работе

# Потерянный конец тестового прогона и ложный cancel_blocked

Если Unity-джоб тестов завершается без `RunFinished`, задача `tests` зависает навсегда, а после отмены держит очередь в `cancel_blocked`. Так бывает при ошибке внутри Test Framework (`RunFailed` → `IErrorCallbacks.OnError`) и при внутреннем `StopRun`. Результат: такая задача сама завершается как `runtime_error` не позже чем через 25 секунд после конца джоба, отмена финализируется, очередь продвигается. Инцидент WaterWalk 2026-09-24 (`Task_20260924_191012_962_d707abe5`, Unity 6000.4.0f1, Test Framework 1.6.0, пакет 0.34.0): в 19:47:18 `CancelTestRun` вернул `false` (джоба уже нет), а `RunningReason()` бесконечно возвращал `Unity EditMode runner object remains`. `EditModeRunner` уничтожается только в `RunFinishedInvocationEvent.Execute` (`testJobData.editModeRunner.Dispose()`); на путях ошибки и отмены ScriptableObject остаётся и переживает domain reload.

## Пакет `com.elmortem.agentbridge`

### `Editor/TestRunnerCancellation.cs`

- В `RunningReason()` удалить строку `if (HasRunner("UnityEditor.TestTools.TestRunner.EditModeRunner")) return "Unity EditMode runner object remains";`. Реальную активность закрывают проверки выше: `IsTestRunActive`/`IsRunActive` и сохранённые `TestJobDataHolder.TestRuns[].isRunning`, включая resume после reload.
- `HasRunner` остаётся: его использует проверка `PlaymodeTestsController`.
- `Request(string jobId)` не меняется.

### `Editor/TestRunLifecycle.cs`

- В `State` добавить поле `public long InactiveSince;`.
- Добавить константу и две чистые функции:

```csharp
public const long LostRunGraceMs = 25000;

public static long TrackInactivity(bool idle, long inactiveSince, long now)
{
	if (!idle)
	{
		return 0;
	}
	if (inactiveSince == 0)
	{
		return now;
	}
	return inactiveSince;
}

public static bool IsLost(long inactiveSince, long now)
{
	return inactiveSince != 0 && now - inactiveSince >= LostRunGraceMs;
}
```

- В `Tick()`, ветка `if (!string.IsNullOrEmpty(state.Outcome))`: отмену у Unity запрашивать только для `canceled`. У `runtime_error` джоб доигрывает свои cleanup-задачи режима Error, и `CancelTestRun` их прервёт:

```csharp
string diagnostic = state.Outcome == "canceled" ? TestRunnerCancellation.Request(state.JobId) : "";
```

  Остальная ветка без изменений: диагностика после 30 с, ожидание `IsRunning()`, восстановление сцен, `FinalizeCancellation(state.Id, state.Outcome, state.Reason)`.
- В `Tick()` перед последней строкой (`if (terminal && ...) SessionState.EraseString(Key);`) добавить детектор потерянного конца:

```csharp
if (!terminal && hasRecord && state.Submitted)
{
	bool idle = !EditorApplication.isPlayingOrWillChangePlaymode
		&& !EditorApplication.isCompiling
		&& !PlayModeSceneRecovery.IsPending
		&& !AgentTestRunner.HasPendingFinalization(state.Id)
		&& !TestRunnerCancellation.IsRunning();
	long inactiveSince = TrackInactivity(idle, state.InactiveSince, now);
	if (inactiveSince != state.InactiveSince)
	{
		state.InactiveSince = inactiveSince;
		Save(state);
	}
	if (IsLost(inactiveSince, now))
	{
		RequestStop(state.Id, "runtime_error", "Unity test job ended without RunFinished; no results for " + (LostRunGraceMs / 1000) + " s");
		return;
	}
}
```

  Детектор стоит после ветки с `Outcome` и потому срабатывает только для прогона без исхода. Следующий `Tick` финализирует задачу через существующую ветку остановки.

### `Editor/AgentTestRunner.cs`

- Добавить публичный метод:

```csharp
public static bool HasPendingFinalization(string taskId)
{
	return _finalization != null && _finalization.TaskId == taskId;
}
```

- `TestCallbacks` реализует `ICallbacks, IErrorCallbacks` (интерфейс есть в Test Framework 1.1.33 и 1.6.0, namespace `UnityEditor.TestTools.TestRunner.Api` уже подключён):

```csharp
public void OnError(string message)
{
	string taskId = SessionState.GetString(CoordinatorTestTaskKey, "");
	if (string.IsNullOrEmpty(taskId))
	{
		return;
	}
	TestRunLifecycle.RequestStop(taskId, "runtime_error", "Unity test run failed: " + message);
}
```

  `RequestStop` игнорирует повторный исход и чужой id, журнал переводит в `canceling`. После ошибки `RunFinishedInvocationEvent` не выполняется (`RunOnError = DoNotRunOnError`), поэтому гонки с `RunFinished` нет.

## Тесты

- `TestCancellationPolicyTests` (EditMode), новые случаи:
  - `TrackInactivity(false, x, now) == 0` для `x = 0` и `x = 123`;
  - `TrackInactivity(true, 0, 1000) == 1000`; `TrackInactivity(true, 500, 1000) == 500`;
  - `IsLost(0, long.MaxValue) == false`; `IsLost(1000, 1000 + LostRunGraceMs - 1) == false`; `IsLost(1000, 1000 + LostRunGraceMs) == true`.
- `QueueTimeoutReproTests` (EditMode), новый `[UnityTest]` с `[Explicit("Run with scripts/verify-test-cancellation.ps1; stops the Unity job without RunFinished")]`:

```csharp
public IEnumerator FrameworkStopsWithoutRunFinished()
{
	Mark("lost begin");
	yield return null;
	var runners = EditorApplication.update.GetInvocationList()
		.Where(d => d.Target != null && d.Target.GetType().FullName == "UnityEditor.TestTools.TestRunner.TestRun.TestJobRunner")
		.Select(d => d.Target)
		.Distinct()
		.ToList();
	Assert.AreEqual(1, runners.Count, "Expected exactly one subscribed TestJobRunner");
	runners[0].GetType().GetMethod("StopRun", BindingFlags.Instance | BindingFlags.NonPublic).Invoke(runners[0], null);
	double deadline = EditorApplication.timeSinceStartup + 120;
	while (EditorApplication.timeSinceStartup < deadline)
	{
		yield return null;
	}
}
```

  В файл добавить `using System.Linq;` и `using System.Reflection;`. Приватный `StopRun()` без аргументов есть у `TestJobRunner` в 1.1.33 и 1.6.0: он снимает джоб с регистрации и отписывает update без `RunFinished`, оставляя `EditModeRunner`. Корутина теста после этого не продвигается.
- `scripts/verify-test-cancellation.ps1`: добавить в `$cases` сценарий `@('lost', 'EditMode', 'QueueTimeoutReproTests.FrameworkStopsWithoutRunFinished', 'runtime_error')`. Для `lost` не вызывать `cancel`; маркер ждать с `--wait 60`. Дополнительно проверить, что `Logs` финальной записи содержит `ended without RunFinished`.
- `scripts/verify-inert-test-controller.ps1`: в пробу добавить сироту `EditModeRunner`. Тип `UnityEditor.TestTools.TestRunner.EditModeRunner` через reflection, `ScriptableObject.CreateInstance(type)` с `hideFlags = HideFlags.HideAndDontSave`. Проверить, что `Resources.FindObjectsOfTypeAll(type).Length > 0` и `RunningReason()` пуст, в `finally` вызвать `DestroyImmediate`. Строка результата: `PASS: inert controllers and orphan EditModeRunner do not block Edit Mode`.
- Существующие проверки, которые затрагивает изменение: `TestCancellationPolicyTests.ActiveFrameworkRunStillBlocksInEditMode`, все сценарии `verify-test-cancellation.ps1`, `verify-scene-recovery.ps1`, `dotnet run --project AgentBridgeRecovery.Tests -c Release`.

## Порядок работ

- `TestRunnerCancellation.cs`: убрать проверку объекта `EditModeRunner`.
- `AgentTestRunner.cs`: `HasPendingFinalization`, `IErrorCallbacks.OnError`.
- `TestRunLifecycle.cs`: `InactiveSince`, `LostRunGraceMs`, `TrackInactivity`, `IsLost`, запрос отмены только для `canceled`, детектор в `Tick`.
- Тесты: новые случаи в `TestCancellationPolicyTests`, `QueueTimeoutReproTests.FrameworkStopsWithoutRunFinished`.
- Скрипты: сценарий `lost` в `verify-test-cancellation.ps1`, сирота `EditModeRunner` в `verify-inert-test-controller.ps1`.
- Документация, версия пакета 0.34.1, версия плагина 1.30.1, пересборка плагина через `scripts/build-plugin.ps1`.

## Приёмка

- Сирота `EditModeRunner` при свободном Edit Mode не делает `RunningReason()` непустым — `verify-inert-test-controller.ps1` PASS.
- Джоб, остановленный без `RunFinished`, завершает задачу `runtime_error` с `ended without RunFinished` в логах. Следующая задача очереди выполняется — сценарий `lost` в `verify-test-cancellation.ps1`.
- Отмена живых прогонов не сломана: сценарии `plain`, `recovery`, `play`, `orphan`, отмена ожидающей и активной задачи — `canceled`/`success`, как раньше.
- Реальный прогон по-прежнему держит редактор: `ActiveFrameworkRunStillBlocksInEditMode` проходит; длинный `ResponsiveTestOutlivesTimeout` (35 с, дольше порога 25 с) не финализируется детектором.
- Решения детектора по времени покрыты новыми случаями `TestCancellationPolicyTests`.
- В WaterWalk после обновления пакета `Task_20260924_191012_962_d707abe5` финализируется как `canceled`, `QueueBlockReason` пуст, очередь продвигается без рестарта редактора.

## Документация

- `README.md`, абзац про отмену (строка со словами `cancel_blocked:<TaskId>`): прогон, чей Unity-джоб закончился без результатов (ошибка Test Framework), завершается `runtime_error` не позже чем через 25 секунд. Сирота `EditModeRunner` очередь не держит.
- `AgentBridgeUnity/Packages/com.elmortem.agentbridge/UNITYAGENT.md`, раздел отмены и раздел `runtime_error`: тот же факт для агента в чужом проекте.
- `unity-bridge-plugin/skills/unity-bridge/SKILL.md`, пункт про `long-running-tasks-v1` и раздел `Status == "runtime_error"`: `runtime_error` с `ended without RunFinished` или `Unity test run failed:` означает сбой Test Framework, а не тестов; перезапусти прогон.
- `Docs/notes/YYMMDD-HHMM-NOTE-lost-test-run-end.md`: разбор инцидента WaterWalk из вступления. Разрывы тиков до 240 с связаны с долгими синхронными тестами (100 из 103 разрывов >30 с за 20–24.09 пришлись на задачи `tests`, одинаково в фокусе и без), а не с блокировкой экрана.
- `Docs/PROJECT_MAP.md` не меняется: новых файлов и ответственностей нет.
