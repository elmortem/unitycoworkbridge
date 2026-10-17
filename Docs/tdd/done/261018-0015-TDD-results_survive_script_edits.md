Status: Выполнено

# Результат тестов переживает правки скриптов во время прогона

Если во время прогона тестов соседние агенты меняют только исходники компиляции (`.cs`, `.asmdef`, `.asmref`, `.rsp` и их `.meta`), а до `RunFinished` не прошло ни одной компиляции, мост возвращает результат прогона. Статус ставится по NUnit (`success` или `test_failure`), а Evidence — `valid` с флагом `StartStateOnly`: результат описывает стартовое состояние. Изменения ассетов и компиляция до `RunFinished` по-прежнему дают `stale_input`. Ошибка Test Framework, которая приходит после `RunFinished`, больше не выбрасывает уже полученные результаты.

Поводом стал WaterWalk (Д468). Unity 6000.4, TF 1.6, Enter Play Mode Options без reload. После PlayMode-прогона правки соседей вызывают компиляцию и reload. TF восстанавливает `PlayModeRunTask` с `controller == null`, падает с NRE на `PlayModeRunTask.cs:52` и сообщает `RunFailed`. `AgentTestRunner.TestCallbacks.OnError` вызывает `RequestStop(runtime_error)`, и задача завершается с нулём тестов. Это подтвердили все пять случаев в Editor.log; результаты к тому моменту были уже сохранены (`Saving results to …TestResults.xml`).

## Пакет `AgentBridge` (`AgentBridgeUnity/Packages/com.elmortem.agentbridge/Editor/`)

### `CompileFingerprint.cs`

- Новый публичный метод на существующем массиве `Extensions`:

```csharp
public static bool IsSource(string path)
{
	if (string.IsNullOrEmpty(path))
	{
		return false;
	}
	string name = path.EndsWith(".meta", StringComparison.OrdinalIgnoreCase) ? path.Substring(0, path.Length - 5) : path;
	foreach (string extension in Extensions)
	{
		if (name.EndsWith(extension, StringComparison.OrdinalIgnoreCase))
		{
			return true;
		}
	}
	return false;
}
```

### `ValidationInputMonitor.cs`

- Новое поле `private int _nonSourceEvents;` и свойство `public int NonSourceEventCount { get { lock (_sync) { return _nonSourceEvents; } } }`.
- В обработчике события, сразу после `_events++;`: если `!CompileFingerprint.IsSource(fullPath)`, увеличить `_nonSourceEvents`.

### `InputStatVerdict.cs` и `InputStatManifest.cs`

- В `InputStatVerdict` новое поле `public int NonSourceChanged;`.
- `InputStatManifest.Note`: после `verdict.Changed++` увеличивать `NonSourceChanged`, если `!CompileFingerprint.IsSource(path)`.

### `EvidenceRecord.cs`

- Новое поле `public bool StartStateOnly;`. Значение `true` означает, что входы менялись после старта, но результат относится к стартовому `InputDigest`. CLI передаёт Evidence как есть, а `--format human` уже печатает `Reason`. Правок в CLI нет.

### `ValidationEvidence.cs`

- Новый ключ `public const string NonSourceEventsKey = "AgentBridge_EvidenceNonSourceEvents";`. Он обнуляется, стирается и переносится через reload там же, где `EventsKey`: обнуление на старте наблюдения, `EraseInt` при очистке, в `DisposeMonitor` — `SessionState.SetInt(NonSourceEventsKey, SessionState.GetInt(NonSourceEventsKey, 0) + _monitor.NonSourceEventCount);`.
- Перегрузка `TryComplete`: прежняя сигнатура вызывает новую с `false`. Компиляционный путь `TaskCoordinator` не меняется.

```csharp
public static bool TryComplete(string taskId, bool artifactsPresent, out EvidenceRecord result)
{
	return TryComplete(taskId, artifactsPresent, false, out result);
}

public static bool TryComplete(string taskId, bool artifactsPresent, bool resultsFixedAtStart, out EvidenceRecord result)
```

  Тело остаётся прежним. Последний вызов передаёт флаг в `Complete(taskId, artifactsPresent, resultsFixedAtStart, end, stat)`.
- `Complete`: счётчик `int nonSourceEvents = SessionState.GetInt(NonSourceEventsKey, 0);` и в блоке `_monitor` — `nonSourceEvents += _monitor.NonSourceEventCount;`. Две ветки, которые сейчас ставят `Stale` (по различию дайджестов и по `events > 0`), заменяются одной:

```csharp
bool digestChanged = startDigest != end.Digest;
if (digestChanged || events > 0)
{
	ClassifyChange(record, digestChanged, events, nonSourceEvents, observerOk, stat, resultsFixedAtStart);
	return record;
}
```

- Новый публичный чистый метод, это точка тестирования:

```csharp
public static void ClassifyChange(EvidenceRecord record, bool digestChanged, int events, int nonSourceEvents,
	bool observerOk, InputStatVerdict stat, bool resultsFixedAtStart)
{
	bool sourcesOnly = resultsFixedAtStart
		&& observerOk
		&& stat.Complete
		&& (!digestChanged || stat.Changed > 0)
		&& nonSourceEvents == 0
		&& stat.NonSourceChanged == 0;
	if (sourcesOnly)
	{
		record.Validity = EvidenceRecord.Valid;
		record.StartStateOnly = true;
		record.Reason = "only compiled sources changed after the run started (" + events
			+ " change(s)) and nothing was compiled before its results; the result describes the starting inputs";
		return;
	}
	record.Validity = EvidenceRecord.Stale;
	record.Reason = digestChanged
		? "the inputs changed while the validation ran"
		: "an input was modified and restored while the validation ran";
}
```

`(!digestChanged || stat.Changed > 0)` отсекает случай, когда дайджест разошёлся, а манифест не назвал ни одного пути. Классифицировать такое нечем, поэтому остаётся `stale`.

### `AgentTestRunner.cs`

- Новые ключи:

```csharp
public const string CoordinatorTestCycleKey = "AgentBridge_CoordinatorTestCycle";
private const string CoordinatorResultsTaskKey = "AgentBridge_CoordinatorResultsTask";
private const string CoordinatorResultsFixedKey = "AgentBridge_CoordinatorResultsFixed";
```

- `TryRequestRunForCoordinator`: рядом с остальными `SessionState.SetString(Coordinator…)` добавить `SessionState.SetString(CoordinatorTestCycleKey, CompileTaskExecutor.LastCycleId);`. Там же стереть `CoordinatorResultsTaskKey` и `CoordinatorResultsFixedKey`. `LastCycleId` меняется на каждом `compilationStarted`, в том числе на автоматической компиляции Unity (`UnityCompile_…`), и хранится в `SessionState`.
- `TestCallbacks.RunFinished`: после проверок `IsStopping` и терминального журнала, до `BuildResult`:

```csharp
SessionState.SetString(CoordinatorResultsTaskKey, coordinatorTaskId);
SessionState.SetBool(CoordinatorResultsFixedKey, !EditorApplication.isCompiling
	&& CompileTaskExecutor.LastCycleId == SessionState.GetString(CoordinatorTestCycleKey, ""));
```

- `TestCallbacks.OnError`: если результаты этой задачи уже записаны, остановку не запрашивать:

```csharp
if (SessionState.GetString(CoordinatorResultsTaskKey, "") == taskId)
{
	TaskRecord record;
	if (TaskJournal.TryRead(taskId, out record) && !TaskCoordinator.IsTerminal(record.Status))
	{
		if (record.Logs == null)
		{
			record.Logs = new List<string>();
		}
		record.Logs.Add("Unity Test Framework failed after the results were recorded; the results are kept: " + message);
		TaskJournal.Write(record);
	}
	return;
}
```

  Запись в журнале выживает до финализации: `TryFinalizeCoordinatorRun` читает журнал и дописывает в существующие `Logs`. Обычный путь (`PlayModeSceneRecovery.RecordResult` → восстановление → `FinalizeRecoveredPlayModeRun`, или `FinalizeCoordinatorRun` в EditMode) продолжается без изменений. Отложенная финализация переживает reload через `FinalizationKey`.
- `TryFinalizeCoordinatorRun`: перед `ValidationEvidence.TryComplete` вычислить

```csharp
bool resultsFixedAtStart = SessionState.GetString(CoordinatorResultsTaskKey, "") == taskId
	&& SessionState.GetBool(CoordinatorResultsFixedKey, false);
```

  и передать флаг в новую перегрузку. В блоке `SessionState.EraseString(Coordinator…)` стереть ещё `CoordinatorTestCycleKey`, `CoordinatorResultsTaskKey` и `CoordinatorResultsFixedKey`.
- Кэш и присоединённые задачи. Ветвление после записи журнала:

```csharp
bool promoted = hasDump
	&& ranCleanly
	&& run.total > 0
	&& evidence.Validity == EvidenceRecord.Valid
	&& !evidence.StartStateOnly
	&& !string.IsNullOrEmpty(startSources)
	&& startSources == ValidationEvidence.CompletedSources;
```

  Между `if (promoted)` и веткой `else if (hasDump && ranCleanly)` с `Terminate` добавить новую ветку:

```csharp
else if (hasDump && ranCleanly && evidence.StartStateOnly)
{
	TestRunAttachments.Requeue(taskId);
}
```

  Присоединённая задача могла прийти после правки, поэтому стартовое состояние её правку не покрывает, и она идёт в очередь заново. Статус владельца ставится через существующую ветку `TestResultAggregator.StatusOf(run)`, потому что Validity = `valid`.

## Тесты

- `AgentBridgeEvidenceTests` (EditMode), `ClassifyChange` с `InputStatVerdict { Complete = true }`:
	- `fixedAtStart = true`, `observerOk = true`, `digestChanged = true`, `stat.Changed = 2`, `NonSourceChanged = 0`, `nonSourceEvents = 0` → `valid`, `StartStateOnly = true`;
	- то же, но `stat.NonSourceChanged = 1` → `stale`; то же с `nonSourceEvents = 1` → `stale`; то же с `fixedAtStart = false` → `stale`;
	- `digestChanged = true`, `stat.Changed = 0` → `stale`;
	- `digestChanged = false`, `events = 1`, только исходники → `valid`, `StartStateOnly = true`.
- `AgentBridgeEvidenceTests`, `CompileFingerprint.IsSource`: `A.cs`, `A.cs.meta`, `B.asmdef`, `C.ASMREF`, `csc.rsp` → `true`; `S.unity`, `M.mat`, `D.asset`, пустая строка → `false`.
- `AgentBridgeEvidenceTests.StatManifestSeesAChangeThatWasReverted` должен оставаться зелёным. В нём меняется `.cs`: `Changed` по-прежнему считается, `NonSourceChanged` равно `0`.

## Порядок работ

- `CompileFingerprint.IsSource`, счётчики `NonSourceEventCount` и `NonSourceChanged`.
- `EvidenceRecord.StartStateOnly`, перегрузка `TryComplete`, `ClassifyChange`, ключ `NonSourceEventsKey` в `ValidationEvidence.cs`.
- `AgentTestRunner.cs`: ключи цикла и результатов, `RunFinished`, `OnError`, `TryFinalizeCoordinatorRun`, условие `promoted` и ветка `Requeue`.
- Тесты из раздела «Тесты».
- Поднять минорную версию пакета и плагина от текущих значений в репозитории. CLI не трогается. Пересобрать плагин.
- Документация из раздела «Документация».

## Приёмка

- В WaterWalk запускается `tests --mode PlayMode --test WaterWalk.Tests.PlayMode.SunFlareGroundTests`. Посреди прогона через edit grant меняется и возвращается один `.cs`. Ожидаемый итог — `success` с ненулевыми счётчиками и `Evidence: valid (only compiled sources changed after the run started …)`. Строка `Unity Test Framework failed after the results were recorded` в `Logs` допустима. Подтверждается живым прогоном.
- Тот же прогон, но меняется и возвращается не скрипт (например `.mat`), — итог `stale_input`. Подтверждается живым прогоном.
- Прогон без изменений входов по-прежнему попадает в кэш: повтор отвечает `Cached: true`. Подтверждается живым прогоном.
- Решающее правило покрыто случаями `ClassifyChange` и `IsSource` в `AgentBridgeEvidenceTests`.

## Документация

- `unity-bridge-plugin/skills/unity-bridge/SKILL.md`. Абзац про `ended without RunFinished` / `Unity test run failed:`: результаты, полученные до сбоя фреймворка, теперь сохраняются; перезапуск нужен только при `Tests.total == 0`. Раздел `stale_input`: правки только скриптов после старта без компиляции до результатов не делают прогон протухшим. Такой результат `valid` и описывает стартовое состояние (в `Evidence.Reason` об этом сказано). Если правка агента появилась после старта чужого прогона, мост не отдаст ему этот результат и выполнит его запрос заново.
- `AgentBridgeUnity/Packages/com.elmortem.agentbridge/UNITYAGENT.md`, строки про `runtime_error` и `stale`: то же для агента в чужом проекте.
- `README.md`, описание `stale` в разделе Evidence: исключение для исходников компиляции, `StartStateOnly`, такой результат не попадает в кэш, присоединённые запросы возвращаются в очередь.
- `Docs/PROJECT_MAP.md`: нет.
