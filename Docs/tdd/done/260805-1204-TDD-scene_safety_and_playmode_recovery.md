Status: Выполнено

# Unity Agent Bridge — безопасная смена сцен и восстановление после PlayMode

## Тип изменения

Рефакторинг надёжности существующего Unity Editor bridge с сохранением текущего JSON-протокола задач и терминальных статусов.

## Контракт поведения

- Ни один автоматический путь Bridge не вызывает `SaveCurrentModifiedScenesIfUserWantsTo`, `SaveModifiedScenesIfUserWantsTo` или другой API, способный открыть модальный диалог сохранения сцены.
- Перед запуском `csharp`, `ui` и `tests` Bridge нормализует все открытые сцены.
- Dirty-сцена с непустым путём, не принадлежащая Unity Test Framework, сохраняется через `EditorSceneManager.SaveScene`. Ошибка сохранения завершает задачу как `runtime_error` до выполнения payload.
- Dirty-сцена без пути обрабатывается настройкой `DirtyUntitledScenePolicy`:
  - `Discard` — значение по умолчанию; сцена закрывается без сохранения, а если она единственная, заменяется чистой сценой `DefaultGameObjects`.
  - `Block` — задача завершается как `runtime_error`, сцена остаётся открытой, модальный диалог не появляется.
- Сцена Unity Test Framework определяется точным шаблоном `Assets/InitTestScene*.unity` или путём bootstrap-сцены, записанным активной PlayMode-транзакцией. Она всегда закрывается без сохранения, а её asset удаляется.
- Новая задача не стартует, пока `EditorApplication.isPlayingOrWillChangePlaymode == true` или существует незавершённая PlayMode scene-recovery транзакция.
- PlayMode test task становится терминальным только после возврата в Edit Mode, восстановления исходного scene setup и удаления временных test scenes.
- `compile: success` допустим только после синхронного Unity import и проверки, что каждый проектный `.cs` имеет `.meta`, AssetDatabase GUID, назначенную сборку и присутствует в source inventory этой сборки.
- Успешный `compile` является только структурным gate. Поведенческим proof считается релевантный успешный EditMode/PlayMode test task.

## Проверка импорта исходников

### Новый файл `Editor/SourceImportVerifier.cs`

После `AssetDatabase.Refresh(ForceSynchronousImport)` проверить все импортируемые `.cs` под `Assets` и `Packages`, исключая служебные package-каталоги с суффиксом `~`:

- отсутствует `.meta` — `ABIMPORT001`;
- `AssetDatabase.AssetPathToGUID` пуст — `ABIMPORT002`;
- `CompilationPipeline.GetAssemblyNameFromScriptPath` пуст — `ABIMPORT003`;
- путь отсутствует среди `CompilationPipeline.GetAssemblies().sourceFiles` — `ABIMPORT004`.

`CompileTaskExecutor.ConsumePending` добавляет эти диагностики к compiler diagnostics и возвращает `compiler_error`, если список не пуст.

## Настройка

### `Editor/AgentBridgeSettings.cs`

Добавить сериализуемое поле:

```csharp
public string DirtyUntitledScenePolicy = "Discard";
```

### `Editor/AgentBridgeSettingsStore.cs`

Добавить методы:

```csharp
public static bool GetDiscardDirtyUntitledScenes();
public static void SetDiscardDirtyUntitledScenes(bool value);
```

Пустое, неизвестное и отсутствующее значение трактуется как `Discard`. `false` записывает строку `Block`, `true` записывает `Discard`.

### `Editor/AgentBridgeSetupWindow.cs`

Добавить секцию `Scene safety` и toggle `Discard dirty untitled scenes`. Изменение toggle сразу сохраняется через `AgentBridgeSettingsStore`.

### `ProjectSettings/AgentBridge.json`

Добавить:

```json
"DirtyUntitledScenePolicy": "Discard"
```

## Scene safety API

### Новый файл `Editor/SceneSafetyGuard.cs`

Добавить публичный статический класс:

```csharp
public static class SceneSafetyGuard
{
	public static bool TryPrepareForTask(out string error);
	public static void EnsureSafeForSceneChange();
	public static bool IsTestScenePath(string path);
	public static void ClearOpenSceneDirtiness();
}
```

`TryPrepareForTask` работает на Editor main thread:

- Получает snapshot всех открытых сцен до мутаций.
- Разделяет сцены на обычные, dirty untitled и test scenes.
- Сохраняет каждую dirty обычную сцену отдельно и проверяет результат `SaveScene`.
- При политике `Block` возвращает `false` до закрытия любой dirty untitled-сцены.
- Перед закрытием transient-сцен вызывает `EditorSceneManager.ClearSceneDirtiness`.
- Если после удаления transient-сцен остаётся хотя бы одна обычная сцена, закрывает transient-сцены через `CloseScene(scene, true)`.
- Если transient-сцены составляют весь setup, создаёт одну чистую сцену через `NewSceneSetup.DefaultGameObjects` и `NewSceneMode.Single`.
- Удаляет существующие test scene assets через `AssetDatabase.DeleteAsset`.
- Удаление test scene идемпотентно: после физического удаления stale GUID/path в `AssetDatabase` не превращает повторную recovery-попытку в ошибку.
- Не вызывает интерактивные save API.

`EnsureSafeForSceneChange` вызывает `TryPrepareForTask` и бросает `InvalidOperationException` с полученной ошибкой при `false`.

`ClearOpenSceneDirtiness` очищает dirtiness всех валидных открытых сцен и используется только внутри активной PlayMode recovery-транзакции. В Unity 2022.3 `EditorSceneManager.ClearSceneDirtiness(Scene)` является internal API, поэтому класс один раз получает точный метод через reflection (`Public | NonPublic | Static`, параметр `Scene`) и fail-closed бросает `MissingMethodException`, если поддерживаемая версия Unity не предоставляет его.

### Новый файл `Editor/AgentSceneManager.cs`

Добавить публичный статический API для C#-тасков:

```csharp
public static class AgentSceneManager
{
	public static Scene OpenScene(string scenePath, OpenSceneMode mode = OpenSceneMode.Single);
	public static Scene NewScene(NewSceneSetup setup = NewSceneSetup.DefaultGameObjects, NewSceneMode mode = NewSceneMode.Single);
	public static bool CloseScene(Scene scene, bool removeScene = true);
	public static void RestoreSceneManagerSetup(SceneSetup[] setup);
	public static void LoadScene(string sceneName, LoadSceneMode mode = LoadSceneMode.Single);
	public static AsyncOperation LoadSceneAsync(string sceneName, LoadSceneMode mode = LoadSceneMode.Single);
	public static AsyncOperation UnloadSceneAsync(string sceneName);
}
```

Каждый метод сначала вызывает `SceneSafetyGuard.EnsureSafeForSceneChange`, затем ровно один соответствующий метод `EditorSceneManager`.

## Guardrail агентских исходников

### `Editor/SourceGuardrail.cs`

Для каждого `InvocationExpression` добавить проверку прямых scene-transition вызовов.

Запрещённые вызовы:

- `EditorSceneManager.OpenScene`
- `EditorSceneManager.NewScene`
- `EditorSceneManager.CloseScene`
- `EditorSceneManager.RestoreSceneManagerSetup`
- `SceneManager.LoadScene`
- `SceneManager.LoadSceneAsync`
- `SceneManager.UnloadSceneAsync`

В violation записывать: `direct scene transition is not allowed; use AgentBridge.AgentSceneManager`.

Вызовы `AgentSceneManager` разрешены.

## PlayMode recovery state

### `Editor/BridgePaths.cs`

Добавить `PlayModeSceneStateFile`, возвращающий `Library/AgentBridge/pending-playmode-scene.json`.

### Новый файл `Editor/PlayModeSceneState.cs`

Добавить сериализуемые типы:

```csharp
public class PlayModeSceneState
{
	public string TaskId;
	public SceneSetupState[] OriginalSetup;
	public string BootstrapScenePath;
	public TestRunResult Result;
	public bool HasResult;
}

public class SceneSetupState
{
	public string Path;
	public bool IsLoaded;
	public bool IsActive;
	public bool IsSubScene;
}
```

### Новый файл `Editor/PlayModeSceneRecovery.cs`

Добавить статический lifecycle-компонент:

```csharp
public static class PlayModeSceneRecovery
{
	public static bool IsPending { get; }
	public static void Start();
	public static bool Begin(string taskId, out string error);
	public static void CaptureBootstrapScene();
	public static void RecordResult(TestRunResult result);
}
```

`Begin` сохраняет текущий `EditorSceneManager.GetSceneManagerSetup()` в `PlayModeSceneStateFile` после успешного scene preflight.

`Start` идемпотентно подписывается на `EditorApplication.playModeStateChanged`. При наличии pending-файла в Edit Mode планирует recovery через `EditorApplication.delayCall`.

Lifecycle:

- `ExitingPlayMode` — вызвать `SceneSafetyGuard.ClearOpenSceneDirtiness`.
- `RunFinished` — очистить dirtiness до того, как Test Framework запросит выход: на Unity-версиях, показывающих save prompt до события `ExitingPlayMode`, это последняя гарантированная безмодальная точка.
- `EnteredEditMode` — оставить `IsPending == true` и запланировать recovery через `delayCall`, чтобы Unity Test Framework завершил собственный callback первым.
- `Tick` — при записанном результате гарантированно запросить выход из PlayMode, а после завершения перехода запланировать recovery; это закрывает пропущенный или слишком ранний `EnteredEditMode` callback.
- Собственная `EditorApplication.update`-подписка recovery завершает транзакцию после устойчивого EditMode независимо от сохранности состояния `TaskCoordinator` и порядка `delayCall` Test Framework.
- Recovery — повторно очистить dirtiness, восстановить сохранённый `SceneSetup[]`, удалить bootstrap asset и остальные `Assets/InitTestScene*.unity`, удалить pending-файл, передать сохранённый результат в `AgentTestRunner.FinalizeRecoveredPlayModeRun`.
- При невозможности восстановить setup открыть чистую сцену `DefaultGameObjects`, удалить test assets и завершить test task как `runtime_error` с `aborted = true`.
- Pending-файл записывать через временный файл и атомарную замену, чтобы domain reload не оставлял частичный JSON.

## Test runner и очередь

### `Editor/AgentTestRunner.cs`

- Удалить `EditorSceneManager.SaveOpenScenes()` из `BuildFilter`.
- Отклонять старт теста при `EditorApplication.isPlayingOrWillChangePlaymode`.
- Перед PlayMode `Execute` вызвать `PlayModeSceneRecovery.Begin`.
- В `RunStarted` вызвать `CaptureBootstrapScene`.
- В `RunFinished` для PlayMode записать результат через `RecordResult`; не делать journal terminal до recovery.
- Для EditMode сохранить существующую немедленную финализацию.
- Добавить `FinalizeRecoveredPlayModeRun(string taskId, TestRunResult run, string recoveryError)`; `aborted == true` или непустой `recoveryError` дают `runtime_error`.

### `Editor/TaskCoordinator.cs`

- В idle-ветке не запускать очередь при `EditorApplication.isPlayingOrWillChangePlaymode` или `PlayModeSceneRecovery.IsPending`.
- После создания активной записи, но до `RunTask`, для `csharp`, `ui` и `tests` вызвать `SceneSafetyGuard.TryPrepareForTask`.
- При ошибке завершить запись как `runtime_error` с точным сообщением guard.
- Действующий JSON-конверт и список терминальных статусов не менять.

## Документация агента

Обновить:

- `Packages/com.elmortem.agentbridge/UNITYAGENT.md`
- `unity-bridge-plugin/skills/unity-bridge/SKILL.md`

Документация требует использовать `AgentBridge.AgentSceneManager` для смены Editor-сцен и описывает настройку `DirtyUntitledScenePolicy`.

## Проверки

### EditMode regression tests

Добавить Editor test assembly reference на `AgentBridge` и тесты:

- Dirty сохранённая scene сохраняется без интерактивного API.
- Dirty untitled scene при `Discard` заменяется чистой сценой.
- Dirty untitled scene при `Block` остаётся открытой, guard возвращает ошибку.
- `Assets/InitTestScene*.unity` закрывается без сохранения и удаляется.
- Source guardrail отклоняет прямой `EditorSceneManager.OpenScene` и принимает `AgentSceneManager.OpenScene`.
- Отсутствующая настройка трактуется как `Discard`.

Каждый scene test сохраняет исходный `SceneManagerSetup` и восстанавливает его в `TearDown` без модального API.

### End-to-end Unity gates

- `dotnet build AgentBridgeUnity/AgentBridge.csproj --no-restore` проходит без ошибок.
- Bridge compile task завершается `success` и не содержит `ABIMPORT001`–`ABIMPORT004`; это подтверждает импорт и assembly membership, но не заменяет тесты.
- EditMode test task для `AgentBridge.ProbeTests` проходит полностью.
- PlayMode test task для `AgentBridge.PlayModeProbeTests` проходит полностью.
- После PlayMode отсутствуют `Assets/InitTestScene*.unity`, `PlayModeSceneRecovery.IsPending == false`, исходная сцена восстановлена.
- Следующая C#-задача открывает сохранённую сцену через `AgentSceneManager.OpenScene` и завершается без пользовательского ввода.

---

После выполнения:

- Поменяй статус вверху документа на `Выполнено`.
- Уточни у заказчика, нужно ли обновить проектную документацию, чтобы отразить новое поведение scene safety.
