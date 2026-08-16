Status: Выполнено

# Play Mode Control — Agent Execution Spec

Механизм управления плей модом через Agent Bridge: санкционированная play-сессия с владельцем (agent session), команда `stopplay`, авто-выход из несанкционированного агентского плей мода, Game View скриншоты в плее, ужесточение guardrail.

Проблема, которую решаем: агенты обходными путями (ExecuteMenuItem, рефлексия) запускают плей мод из csharp-тасков и виснут навсегда — `TaskCoordinator.OnUpdate` при `isPlayingOrWillChangePlaymode` не берёт новые таски, а канала «выключить плей мод» не существует.

## References (not inlined)

- Стиль кода: как в существующих файлах `AgentBridgeUnity/Packages/com.elmortem.agentbridge/Editor/` — табы, явные типы, каждый тип в отдельном файле, сериализуемые поля public с большой буквы, без условий в одну строку.
- Прогон компиляции и тестов Unity — через CLI `agentbridge` (skill unity-bridge): `agentbridge compile`, `agentbridge tests --mode EditMode`.
- Сборка CLI: `dotnet build AgentBridgeCli -c Release`.
- Ключевые существующие механизмы, на которые опираемся (читать перед правкой): `TaskCoordinator.cs` (очередь, active slot, PollExternallyFinalizedTask, FinalizeOrphanRecords, OnBeforeAssemblyReload), `PlayModeSceneRecovery.cs` (паттерн state-файла и atomic write), `AgentSessionScheduler.cs` (lease владельца), `SceneSafetyGuard.cs`, `SourceGuardrail.cs`, `SceneShotTaskExecutor.cs` (state machine через Tick), CLI: `AgentBridgeApplication.cs`, `BridgeClient.cs`, `CliOptions.cs`.

## Foundations (shared, used across units)

Новые типы, каждый в своём файле в `AgentBridgeUnity/Packages/com.elmortem.agentbridge/Editor/`:

```csharp
[Serializable]
public class PlaySessionState
{
	public string TaskId;               // id play-таска, открывшего сессию; "" для stopplay без сессии
	public string OwnerAgentSessionId;  // effective session id владельца
	public string Phase;                // значения из PlaySessionPhases
	public string StartedAtUtc;         // ISO "o"
	public string DeadlineUtc;          // ISO "o"
	public string PendingStopTaskId;    // id stopplay-таска, ожидающего финализации; "" если нет
	public string StopReason;           // "stopplay" | "deadline" | "external" | ""
}

public static class PlaySessionPhases
{
	public const string Entering = "entering";
	public const string Active = "active";
	public const string Exiting = "exiting";
}

public static class PlaySessionStore
{
	public static PlaySessionState Read();      // null если файла нет или JSON битый
	public static void Write(PlaySessionState state); // atomic: .new + File.Replace, как в PlayModeSceneRecovery.Write
	public static void Delete();
	public static bool Exists { get; }
}

public enum StopVerdict
{
	NotPlaying,        // плей мода нет — no-op success
	StopOwn,           // своя сессия — гасим
	StopUnsanctioned,  // плей мод без сессии (застрявший/человеческий) — гасим
	RejectForeign,     // чужая play-сессия — reject
	RejectTests        // идёт tests-плеймод (PlayModeSceneRecovery.IsPending) — reject
}

public static class PlaySessionArbiter
{
	// Чистая функция, без обращения к EditorApplication — всё передаётся параметрами.
	public static StopVerdict Judge(PlaySessionState state, string callerEffectiveSessionId, bool isPlaying, bool testsPending);
}
```

Правила `Judge` (единственный источник истины о том, кто кого может гасить):

- `testsPending` → `RejectTests`.
- `state == null && !isPlaying` → `NotPlaying`.
- `state == null && isPlaying` → `StopUnsanctioned` (любая сессия, включая анонимную — пользовательское решение: stopplay гасит любой плей мод, кроме чужой активной сессии).
- `state != null && state.OwnerAgentSessionId == callerEffectiveSessionId` → `StopOwn`.
- `state != null && state.OwnerAgentSessionId != callerEffectiveSessionId` → `RejectForeign`.

Новое в `BridgePaths.cs`:

```csharp
public static string PlaySessionFile
{
	get { return Path.Combine(WorkingRoot, "play-session.json"); }
}
```

Ключи `SessionState` (константы в `UnsanctionedPlayGuard`):

- `AgentBridge.UnsanctionedPlayTaskId` — маркер «плей мод запущен агентом», значение: id таска-виновника или "unknown".
- `AgentBridge.LastTaskFinishUtc`, `AgentBridge.LastTaskFinishId` — момент и id последнего завершённого таска.

## Invariants (must hold throughout)

- Семантика tests-плеймода не меняется: `PlayModeSceneRecovery`, `pending-playmode-scene.json`, `AgentTestRunner` — поведение идентично текущему; новые механизмы при `PlayModeSceneRecovery.IsPending` бездействуют.
- Поведение существующих kind'ов (csharp/ui/compile/tests/sceneshot/release) без play-сессии и вне плей мода — идентично текущему.
- `BridgeConstants.ProtocolVersion` (CLI) и `ProtocolVersion` в статусе не меняются: все изменения формата аддитивны.
- Правки только внутри: `AgentBridgeUnity/Packages/com.elmortem.agentbridge/**`, `AgentBridgeCli/**` (кроме bin/obj), `AgentBridgeUnity/Assets/Tests/**`, `README.md`, `unity-bridge-plugin/skills/unity-bridge/SKILL.md`, этот файл (Status).
- Человеческий плей мод (без маркера агента и без play-сессии) авто-выходом НЕ гасится никогда.
- Табы, каждый тип в отдельном файле, сериализуемые поля public PascalCase.

## Execution Plan

Юниты идут в указанном порядке.

### Unit 1 — Настройки

- Goal: три новых числа доступны через `AgentBridgeSettingsStore` и видны в окне настроек.
- Touch: `Editor/AgentBridgeSettings.cs`, `Editor/AgentBridgeSettingsStore.cs`, `Editor/AgentBridgeSetupWindow.cs`.
- How: в `AgentBridgeSettings` добавить поля `public int PlaySessionDefaultSeconds = 120;`, `public int PlaySessionMaxSeconds = 600;`, `public int AgentPlayGraceSeconds = 5;`. В `AgentBridgeSettingsStore` добавить геттеры `GetPlaySessionDefaultSeconds()`, `GetPlaySessionMaxSeconds()`, `GetAgentPlayGraceSeconds()` — по образцу `GetTaskTimeoutSeconds()`. В `AgentBridgeSetupWindow` добавить редактирование этих полей рядом с существующими числовыми (скопировать паттерн существующей числовой строки).
- Gate: `agentbridge compile --format json` → Status "success".
- On failure: ≤2 попытки исправить компиляцию, затем остановиться и доложить.

### Unit 2 — Протокол: TaskRequest и BridgeStatus (обе стороны)

- Goal: запрос несёт `PlaySeconds`, статус несёт состояние плей мода; редакторная и CLI-копии типов совпадают по полям.
- Touch: `Editor/TaskRequest.cs`, `Editor/BridgeStatus.cs`, `AgentBridgeCli/TaskRequest.cs`, `AgentBridgeCli/BridgeStatus.cs`.
- How: в оба `TaskRequest` добавить `public int PlaySeconds;`. В оба `BridgeStatus` добавить `public bool IsPlaying;`, `public string PlaySessionAgentId;`, `public string PlaySessionDeadlineUtc;`. Поля аддитивные, версию протокола не трогать.
- Gate: `agentbridge compile` → success; `dotnet build AgentBridgeCli -c Release` → exit 0.
- On failure: ≤2 попытки, затем остановиться и доложить.

### Unit 3 — PlaySessionState/Store/Arbiter + тесты

- Goal: типы из Foundations существуют, стор атомарен, вердикты арбитра покрыты EditMode-тестами.
- Touch: новые файлы `Editor/PlaySessionState.cs`, `Editor/PlaySessionPhases.cs`, `Editor/PlaySessionStore.cs`, `Editor/PlaySessionArbiter.cs`, `Editor/StopVerdict.cs`; `Editor/BridgePaths.cs` (свойство `PlaySessionFile`); новый файл `Assets/Tests/Editor/PlaySessionArbiterTests.cs`.
- How: реализовать ровно по Foundations. `Write` — атомарно по образцу `PlayModeSceneRecovery.Write` (файл `.new`, `File.Replace`, fallback copy+delete). Тесты арбитра: по одному тесту на каждый из пяти вердиктов, все входы — параметры, без EditorApplication.
- Gate: `agentbridge tests --mode EditMode --test AgentBridge.Tests.PlaySessionArbiterTests` (подобрать точный фильтр под фактический namespace тестов, образец — существующий `AgentBridgeProbeTests.cs`) → success, failed 0.
- On failure: ≤3 попытки на падающий тест, затем остановиться и доложить. Не менять вердикты ради зелёного теста — правила зафиксированы в Foundations.

### Unit 4 — PlaySessionManager (state machine)

- Goal: единый идемпотентный `Reconcile()`, который по персистентному состоянию и флагам редактора двигает сессию по фазам и финализирует журнальные записи; переживает domain reload.
- Touch: новый файл `Editor/PlaySessionManager.cs`.
- How: статический класс с методами `Reconcile()`, `BeginPlay(TaskRequest request, TaskRecord record, out string error)`, `BeginStop(string taskId, string reason)`, `IsSessionActive { get; }` (store != null && Phase == Active). Вся логика — только через `PlaySessionStore`, `EditorApplication.isPlaying`/`isPlayingOrWillChangePlaymode`, `TaskJournal`, `DateTime.UtcNow`. Поведение `Reconcile()` по фазам:
  - store == null → ничего.
  - `Entering`:
    - `EditorApplication.isPlaying` → Phase = Active, `PlaySessionStore.Write`; финализировать журнальную запись `TaskId`: если запись существует и не терминальна — Status "success", ReturnValue `"playing_until:" + DeadlineUtc`, FinishedAtUtc = now, `TaskJournal.Write`, `AgentSessionScheduler.OnTaskFinished(OwnerAgentSessionId, now)`; обновить статус-файл (см. ниже).
    - не playing и не `isPlayingOrWillChangePlaymode` и `now - StartedAtUtc > 15s` → вход в плей провалился (например, ошибки компиляции): финализировать запись `TaskId` как "runtime_error" с логом "failed to enter play mode", `PlaySessionStore.Delete()`, статус.
  - `Active`:
    - `!EditorApplication.isPlaying && !isPlayingOrWillChangePlaymode` → плей мод завершён извне (человек нажал Stop): если `PendingStopTaskId` пуст — залогировать `Debug.LogWarning("[AgentBridge] play session ended externally")`; финализировать `PendingStopTaskId` (если есть) как success "stopped:external"; `Delete()`; статус.
    - `now >= DeadlineUtc` → Phase = Exiting, StopReason = "deadline", Write; `SceneSafetyGuard.ClearOpenSceneDirtiness()` в try, `EditorApplication.ExitPlaymode()`.
  - `Exiting`:
    - `EditorApplication.isPlaying` → повторно `ExitPlaymode()` не чаще раза в секунду (хранить last-request в статике; статика может обнулиться после reload — это ок, повторный вызов безвреден).
    - `!isPlayingOrWillChangePlaymode` → выход завершён: `SceneSafetyGuard.TryPrepareForTask(out tail)` в try (нормализация хвоста, ошибку в лог записи); финализировать `PendingStopTaskId` (если есть) как success, ReturnValue `"stopped:" + StopReason`; `Delete()`; статус; `Debug.Log("[AgentBridge] play session stopped: " + StopReason)`.
  - Финализация журнальной записи — всегда идемпотентно: `TaskJournal.TryRead` → если уже терминальна, пропустить.
  - Обновление статус-файла (одна приватная функция): `BridgeStatusWriter.Current.IsPlaying = EditorApplication.isPlayingOrWillChangePlaymode`, `PlaySessionAgentId` = Owner или null, `PlaySessionDeadlineUtc` = Deadline или null, `BridgeStatusWriter.Write()`.
  - `BeginPlay`: валидации по порядку, при нарушении вернуть false и error: пустой `request.AgentSessionId` → "play requires --session"; `PlayModeSceneRecovery.IsPending` → "tests are running"; `PlaySessionStore.Exists` → "play session already active"; `EditorApplication.isPlayingOrWillChangePlaymode` → "editor is already playing". Затем: seconds = `request.PlaySeconds`; если <= 0 → `GetPlaySessionDefaultSeconds()`; clamp сверху `GetPlaySessionMaxSeconds()`. Write state (Phase = Entering, TaskId = request.Id, Owner = effective session id, StartedAtUtc = now, DeadlineUtc = now + seconds, PendingStopTaskId = "", StopReason = ""), статус, `EditorApplication.EnterPlaymode()`.
  - `BeginStop(taskId, reason)`: если store == null — создать state (TaskId = "", Owner = "", Phase = Exiting, StartedAtUtc = now, DeadlineUtc = now); иначе store.Phase = Exiting; в обоих случаях PendingStopTaskId = taskId, StopReason = reason, Write; `ClearOpenSceneDirtiness()` в try; `ExitPlaymode()` только если `isPlaying`; если плей мода уже нет — `Reconcile()` немедленно финализирует.
- Gate: `agentbridge compile` → success.
- On failure: ≤3 попытки, затем остановиться и доложить. Не изобретать дополнительные фазы и файлы.

### Unit 5 — UnsanctionedPlayGuard (авто-выход)

- Goal: агентский плей мод без сессии гасится автоматически; человеческий — не трогается.
- Touch: новый файл `Editor/UnsanctionedPlayGuard.cs`; `Editor/TaskCoordinator.cs` (две точки подписки и две записи SessionState).
- How:
  - `UnsanctionedPlayGuard`: `Start()`/`Stop()` (подписка на `EditorApplication.playModeStateChanged`), `Tick()`, `RecordTaskFinish(string taskId)`, `ClearMark()`.
  - `RecordTaskFinish`: `SessionState.SetString(LastTaskFinishUtc, now "o")`, `SetString(LastTaskFinishId, taskId)`. Вызывать из `TaskCoordinator.FinishTask` сразу после `TaskJournal.Write(_activeRecord)` с `_activeRecord.Id`.
  - Обработчик `PlayModeStateChange.EnteringPlayMode`: если `PlayModeSceneRecovery.IsPending` или `PlaySessionStore.Exists` → return (санкционировано). Иначе agentCaused = `TaskCoordinator.HasActiveTask` ИЛИ (now − LastTaskFinishUtc) ≤ `GetAgentPlayGraceSeconds()`. Если agentCaused → `SessionState.SetString(UnsanctionedPlayTaskId, activeTaskId ?? LastTaskFinishId ?? "unknown")`. Для чтения активного id добавить в `TaskCoordinator` свойство `public static string ActiveTaskId { get { return _activeTaskId; } }`.
  - `Tick()`: маркер пуст → return. Маркер есть и `EditorApplication.isPlaying` и `!PlaySessionStore.Exists` → `ClearOpenSceneDirtiness()` в try, `ExitPlaymode()`, один раз `Debug.LogWarning("[AgentBridge] unsanctioned play mode entered by agent task <id>; exiting automatically")` (одноразовость — через статический флаг). Маркер есть и `!isPlayingOrWillChangePlaymode` → дописать в журнальную запись виновника (если `TaskJournal.TryRead(id)` успешен) строку лога "this task entered play mode; the bridge exited it automatically" и `TaskJournal.Write` (в try/catch, ошибки молча глотать), `ClearMark()`.
  - Подписать `UnsanctionedPlayGuard.Start()`/`Stop()` из `TaskCoordinator.Start()`/`Stop()` рядом с `PlayModeSceneRecovery`.
- Gate: `agentbridge compile` → success.
- On failure: ≤3 попытки, затем остановиться и доложить.

### Unit 6 — Интеграция в TaskCoordinator: kind "play", "stopplay", пампы

- Goal: play/stopplay проходят полный жизненный цикл через координатор; во время play-сессии владелец гоняет csharp/sceneshot; чужой stopplay отбивается; застрявший плей мод лечится stopplay от любого.
- Touch: `Editor/TaskCoordinator.cs`.
- How:
  - `OnUpdate`, в самое начало после `PlayModeSceneRecovery.Tick()`: добавить `UnsanctionedPlayGuard.Tick(); PlaySessionManager.Reconcile();`.
  - `RunTask`: два новых case. `"play"` → `if (!PlaySessionManager.BeginPlay(request, _activeRecord, out error)) { FinishTask("rejected", null, new List<string> { error }, false); return; }` — после успешного BeginPlay запись остаётся running, слот финализируется как у tests: полагаться на `PollExternallyFinalizedTask` (см. ниже). `"stopplay"` → вычислить `StopVerdict` через `PlaySessionArbiter.Judge(PlaySessionStore.Read(), effectiveSessionId, EditorApplication.isPlaying, PlayModeSceneRecovery.IsPending)`; по вердикту: `NotPlaying` → `FinishTask("success", "not_playing", null, false)`; `RejectTests` → `FinishTask("rejected", null, new List<string> { "tests are running" }, false)`; `RejectForeign` → `FinishTask("rejected", null, new List<string> { "play_session_held_by:" + owner }, false)`; `StopOwn`/`StopUnsanctioned` → `UnsanctionedPlayGuard.ClearMark(); PlaySessionManager.BeginStop(request.Id, verdict == StopVerdict.StopOwn ? "stopplay" : "manual");` запись остаётся running до финализации менеджером.
  - Поллинг: в `OnUpdate` ветка активного таска — для kind "play" и "stopplay" использовать существующий `PollExternallyFinalizedTask()` (расширить условие `else if (_activeRecord != null && (_activeRecord.Kind == "tests" || _activeRecord.Kind == "play" || _activeRecord.Kind == "stopplay"))`). ВАЖНО: `PollExternallyFinalizedTask` для tests вызывает `OnTaskFinished` сам — для play/stopplay `OnTaskFinished` уже вызывает менеджер при финализации записи; чтобы не дёргать его дважды, вынести вызов `OnTaskFinished` из `PollExternallyFinalizedTask` под условие `Kind == "tests"`.
  - `CheckTimeout`: для kind "play"/"stopplay" таймаут таска не применять (у сессии свой дедлайн): ранний return при этих kind.
  - `OnBeforeAssemblyReload`: kinds "play" и "stopplay" обрабатывать как "tests" — `CleanupActive()` без записи терминального статуса (их финализирует `Reconcile` после перезагрузки).
  - `FinalizeOrphanRecords`: к исключениям (compileTaskId, testTaskId) добавить id из `PlaySessionStore.Read()`: `state.TaskId` и `state.PendingStopTaskId`.
  - `StartTask`: пропуск преflight — условие `request.Kind != "release"` заменить на `request.Kind != "release" && request.Kind != "stopplay" && !PlaySessionManager.IsSessionActive` (в плей моде `SceneSafetyGuard.TryPrepareForTask` не нужен и вреден). Блок `NeedsScenes(...) && !HolderContextRestored` дополнительно обусловить `&& !PlaySessionManager.IsSessionActive`. В `NeedsScenes` добавить case "play" → true (play-сессия работает на сценах владельца).
  - Гейт сканирования в `OnUpdate` — заменить текущий ранний return `if (EditorApplication.isPlayingOrWillChangePlaymode || PlayModeSceneRecovery.IsPending) return;` на:

```csharp
if (PlayModeSceneRecovery.IsPending)
{
	return;
}

if (EditorApplication.isPlayingOrWillChangePlaymode)
{
	if (PlaySessionManager.IsSessionActive)
	{
		TryStartPlaySessionTask();
	}
	else
	{
		TryStartStopplayTask();
	}

	return;
}
```

  - `TryStartPlaySessionTask()`: `BuildPendingList(null)`; owner = `PlaySessionStore.Read().OwnerAgentSessionId`. Пройти pending: таски владельца с kind "csharp"/"sceneshot"/"stopplay" — кандидаты, взять старейший по CreatedUtc и запустить через `StartTask(next, false, "")` (holder не менять); таски владельца с другими kind → `RejectTaskFile(file, id, kind, "kind not allowed during play session")`; чужие stopplay → `RejectTaskFile(file, id, "stopplay", "play_session_held_by:" + owner)`; чужой "release" → оставить существующей логике нельзя (обычный пик выключен) — обработать здесь же: записать терминальную запись "success"/"not_holder" как в `RunReleaseTask` для не-владельца (использовать `WriteTerminal(id, "release", "success", "not_holder")` и пометить файл в `_rejectedTaskHashes`); остальные чужие таски → оставить в очереди молча. После запуска/отказов обновить `SchedulerStateStore.State.HolderLastActivityUtc = DateTime.UtcNow.ToString("o"); SchedulerStateStore.Save();` чтобы lease владельца не истекал во время сессии.
  - `TryStartStopplayTask()`: `BuildPendingList(null)`; из pending взять старейший с kind "stopplay" (любой сессии) и запустить через `StartTask(next, false, "")`. Больше ничего не запускать.
  - `CancelActive` (кнопка в окне): если активный kind "play"/"stopplay" — дополнительно вызвать `PlaySessionManager.BeginStop("", "manual")` перед `FinishTask("canceled", ...)`, чтобы редактор не остался играть.
- Gate: `agentbridge compile` → success; `agentbridge tests --mode EditMode` → success, failed 0 (существующие тесты не сломаны).
- On failure: ≤3 попытки, затем остановиться и доложить. Не переписывать планировщик — только описанные точки.

### Unit 7 — sceneshot view:"game"

- Goal: элемент sceneshot-пейлоада с `"view": "game"` в плей моде сохраняет PNG реального Game View (включая overlay-UI); вне плей мода — runtime_error с понятным сообщением.
- Touch: `Editor/SceneShot/SceneShotItem.cs`, `Editor/SceneShot/SceneShotPayloadParser.cs`, `Editor/SceneShot/SceneShotTaskExecutor.cs`; тест `Assets/Tests/Editor/SceneShotPayloadParserTests.cs` (создать или дополнить существующий парсер-тест, если он есть — сначала поискать).
- How:
  - `SceneShotItem`: поле `public string View;` ("scene" по умолчанию, "game").
  - Парсер: принять ключ `view`, валидные значения "scene"/"game", иное → ошибка парсинга с текстом допустимых значений; по умолчанию "scene".
  - Executor, `TickPrepare`, если `item.View == "game"`: если `!EditorApplication.isPlaying` → лог "game view shot requires play mode (use agentbridge play)", `_status = "runtime_error"`, `_completed = true`, return. Иначе: убедиться, что Game View существует — `Type gameViewType = Type.GetType("UnityEditor.GameView,UnityEditor");` `EditorWindow.GetWindow(gameViewType, false, null, true)` в try (ошибку — в лог, продолжать); вычислить целевой путь `Path.Combine(_context.ArtifactsDirectory, BuildFileName(item.Name))` (директорию создать), вызвать `ScreenCapture.CaptureScreenshot(fullPath, 1)`; перейти в новое состояние ожидания файла: poll до 5.0 секунд (константа `GameShotTimeoutSeconds`) — файл существует и размер > 0 и не изменился между двумя тиками → готово: `_context.AddArtifact(fullPath)`, summary "gameshot <name> -> <path>", `_index++`; по таймауту → лог "game view capture timed out (is a Game View rendering?)", `_status = "runtime_error"`, `_completed = true`. Ширина/высота элемента для game-шота игнорируются — одна строка в лог. Окно SceneView для game-шотов не создавать.
  - Состояние ожидания встроить в существующую машину: новое булево `_awaitingGameFile` + поля пути/дедлайна, ветка в `Tick()` перед `TickPrepare()`.
- Gate: `agentbridge compile` → success; парсер-тест на `view` проходит: `agentbridge tests --mode EditMode` → success.
- On failure: ≤3 попытки, затем остановиться и доложить.

### Unit 8 — Guardrail hardening + тесты

- Goal: дешёвые обходы плей мода режутся на компиляции агентского csharp.
- Touch: `Editor/SourceGuardrail.cs`; новый файл `Assets/Tests/Editor/PlayModeGuardrailTests.cs`.
- How:
  - В `IsModalCall` для `EditorApplication` добавить `ExecuteMenuItem` (любой вызов — причина существующая `ModalApiReason` не подходит; добавить отдельную причину). Точнее: в `CheckForbiddenCall` после существующих проверок — если `typeName == "EditorApplication" && methodName == "ExecuteMenuItem"` → `AddViolation(..., "ExecuteMenuItem is not allowed in agent tasks")`.
  - Новая проверка строковых литералов: в основном цикле `TryValidate` добавить ветку `IsKind(node, "StringLiteralExpression")`; получить текст через `(string)node.Token.ValueText`; если значение ∈ {"EnterPlaymode", "ExitPlaymode", "EnterPlayMode", "ExitPlayMode", "isPlaying", "Edit/Play"} → `AddViolation(..., "play mode control is not allowed in agent tasks; use agentbridge play/stopplay")`.
  - Тесты (по образцу существующих guardrail-тестов в `AgentBridgeProbeTests.cs` — сначала прочитать, как они получают syntax tree): violation для `EditorApplication.ExecuteMenuItem("Edit/Play")`; violation для `typeof(EditorApplication).GetMethod("EnterPlaymode")`; violation для литерала "isPlaying"; НЕТ violation для чтения свойства `bool p = EditorApplication.isPlaying;`; НЕТ violation для обычной строки "hello".
- Gate: `agentbridge tests --mode EditMode` → success, failed 0.
- On failure: ≤3 попытки на тест, затем остановиться и доложить. Списки литералов и методов не расширять сверх указанного.

### Unit 9 — CLI: play / stopplay

- Goal: `agentbridge play [--seconds N] --session <id>` и `agentbridge stopplay [--session <id>]` работают, help и status их отражают.
- Touch: `AgentBridgeCli/CliOptions.cs`, `AgentBridgeCli/AgentBridgeApplication.cs`, `AgentBridgeCli/BridgeClient.cs`.
- How:
  - `CliOptions`: распарсить `--seconds <int>` (по образцу `--wait`); значение ≤ 0 → ошибка bad_usage.
  - `AgentBridgeApplication`: case "play" — требует `options.Session != null`, usage-строка `"usage: agentbridge play [--seconds N] --session <id> [--project <path>] [--wait <seconds>]"`; вызывает новый `client.SubmitPlayAsync(options.Seconds, options.WaitSeconds)`. Case "stopplay" — session опционален; `client.SubmitStopplayAsync(options.WaitSeconds)`. Обе строки добавить в help-текст после `release`.
  - `BridgeClient`: `SubmitPlayAsync(int seconds, int waitSeconds)` — `TaskRequest { Id = NewId(), Kind = "play", PlaySeconds = seconds, AgentSessionId = _session ?? "", Note = _note ?? "" }` через `SubmitRequestAsync`; `SubmitStopplayAsync` аналогично с Kind = "stopplay".
  - `WriteHealth` (human): после "Active task" добавить строку `"Playing: " + (yes/no)`, а при активной сессии — `" (session <PlaySessionAgentId>, until <PlaySessionDeadlineUtc>)"`.
- Gate: `dotnet build AgentBridgeCli -c Release` → exit 0; `dotnet AgentBridgeCli/bin/Release/net8.0/agentbridge.dll help` (точное имя dll посмотреть в csproj/bin) — вывод содержит "play" и "stopplay".
- On failure: ≤2 попытки, затем остановиться и доложить.

### Unit 10 — Документация

- Goal: агенты узнают о новом механизме из своих контрактов.
- Touch: `AgentBridgeUnity/Packages/com.elmortem.agentbridge/UNITYAGENT.md`, `unity-bridge-plugin/skills/unity-bridge/SKILL.md`, `README.md`.
- How: в каждый файл добавить раздел о плей моде (стилистически как соседние разделы): правило «никогда не входить в плей мод из csharp-таска — guardrail это режет; используй `agentbridge play`»; синтаксис `play [--seconds N] --session <id>` и `stopplay [--session <id>]`; что во время своей play-сессии разрешены только csharp и sceneshot (включая `"view": "game"`), остальное — reject "play session active"; что чужую play-сессию остановить нельзя (`play_session_held_by:<id>`); что застрявший плей мод любой агент может погасить `stopplay`; что сессия сама завершается по дедлайну; поля статуса `IsPlaying`/`PlaySessionAgentId`/`PlaySessionDeadlineUtc`. В README — краткое упоминание и отсылка к SKILL.md.
- Gate: `grep -l "stopplay"` находит все три файла; в SKILL.md есть строка с `view.*game` (регулярка `view.*game`).
- On failure: ≤2 попытки, затем остановиться и доложить.

### Unit 11 — Живой E2E в редакторе

- Goal: полный цикл подтверждён на живом редакторе.
- Touch: ничего не менять; только временные файлы тасков в `<project>/Temp/AgentBridge/`.
- How: использовать свежесобранный CLI (`dotnet AgentBridgeCli/bin/Release/net8.0/<dll>` или обычный `agentbridge`, если `agentbridge doctor` показывает новую версию CLI). Последовательность (каждый шаг — команда и проверка вывода):
  - `agentbridge compile` → success (редактор подхватил новый код пакета).
  - `agentbridge play --seconds 90 --session e2e-a` → Status "success", ReturnValue начинается с "playing_until:".
  - `agentbridge status` → `IsPlaying: true`, `PlaySessionAgentId: "e2e-a"`.
  - csharp-таск от e2e-a c телом `return UnityEditor.EditorApplication.isPlaying.ToString();` → ReturnValue "True".
  - sceneshot-таск от e2e-a с одним элементом `{"name": "game", "view": "game"}` → success, артефакт-PNG существует и размер > 10 KB.
  - `agentbridge stopplay --session e2e-b` → Status "rejected", лог содержит "play_session_held_by:e2e-a".
  - `agentbridge stopplay --session e2e-a` → Status "success", ReturnValue "stopped:stopplay".
  - `agentbridge status` → `IsPlaying: false`; `agentbridge release --session e2e-a` → success.
- Gate: все восемь проверок из How видны в транскрипте с фактическим выводом команд.
- On failure: любой шаг — ≤2 повтора; если редактор остался в плей моде — один `stopplay --session e2e-a`, затем остановиться и доложить с транскриптом упавшего шага. Не чинить редактор обходными скриптами.

## Done (/goal condition)

Выполнено, когда в транскрипте видно все четыре пункта:

- `agentbridge compile --format json` завершился со Status "success" и `dotnet build AgentBridgeCli -c Release` завершился с кодом 0.
- `agentbridge tests --mode EditMode` завершился со Status "success" и failed 0, включая новые PlaySessionArbiterTests и PlayModeGuardrailTests.
- E2E-последовательность Unit 11 пройдена: play → status IsPlaying:true → csharp "True" → gameshot-PNG → чужой stopplay rejected → свой stopplay success → status IsPlaying:false.
- `grep stopplay` находит UNITYAGENT.md, SKILL.md и README.md.

Ограничения: семантика tests-плеймода и существующих kind'ов вне плея не изменена; ProtocolVersion не изменён; правки только в разрешённых путях из Invariants. Остановиться после 50 ходов, даже если условие не достигнуто, и доложить.

## End-of-run report (the agent does this when the goal is met or it stops)

- Поменять Status в начале этого файла на `Выполнено`.
- Доложить: какие юниты закрыты, какие гейты потребовали повторов, на чём остановился и почему.
- Flag — do NOT act: уточни у заказчика, нужно ли обновлять проектную документацию под эти изменения.
