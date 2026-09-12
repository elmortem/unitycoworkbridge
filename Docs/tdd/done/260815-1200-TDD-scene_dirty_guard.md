Status: Выполнено

# Unity Agent Bridge — полное подавление модального save-диалога сцены

## Тип изменения

Рефакторинг надёжности существующего scene safety слоя. Протокол задач, набор терминальных статусов и публичный API `AgentSceneManager` не меняются.

## Причина

- `com.unity.test-framework@1.1.33` выполняет `SaveModiedSceneTask` (`UnityEditor.TestRunner/TestRun/Tasks/SaveModiedSceneTask.cs`), который вызывает `EditorSceneManager.SaveCurrentModifiedScenesIfUserWantsTo()`. Задача стоит первой и в EditMode-ветке, и в PlayMode-ветке `TestJobRunner.GetTaskList`, то есть модальный диалог возможен при любом прогоне тестов, а не только PlayMode.
- `TestJobRunner` исполняет список задач асинхронно, подписавшись на `EditorApplication.update` уже после возврата из `TestRunnerApi.Execute`. Между префлайтом в `TaskCoordinator.StartTask` и вызовом `SaveModiedSceneTask` проходят произвольные тики редактора: за это время сцену пачкает работа человека в редакторе, доменный релоад после `compile`-таска, ре-сериализация компонентов, любой editor-callback проекта. Текущий префлайт — разовый снимок в момент старта задачи и это окно не закрывает.
- Префлайт вызывается только для kinds `csharp`, `ui`, `tests`. `compile` (форсирует доменный релоад) и `sceneshot` проходят мимо него.
- Открытый Prefab Stage с несохранёнными правками не проверяется вообще, хотя даёт собственный модальный prompt при смене сцены и при входе в PlayMode.
- Открытая, но выгруженная (`isLoaded == false`) dirty-сцена попадает в общую ветку сохранения; `EditorSceneManager.SaveScene` для неё возвращает `false`, и задача падает с неинформативным текстом.
- Guardrail агентского C# не запрещает `EditorApplication.EnterPlaymode`, присваивание `EditorApplication.isPlaying`, интерактивные save/dialog/panel API и `TestRunnerApi.Execute`, то есть агент может открыть модальное окно в обход всего слоя.

## Контракт поведения

- Ни один путь моста и ни один агентский таск не может довести редактор до модального окна сохранения сцены или префаба.
- Префлайт `SceneSafetyGuard.TryPrepareForTask` выполняется перед всеми kinds: `csharp`, `ui`, `tests`, `compile`, `sceneshot`.
- Состояние «грязно» определяется по всем открытым сценам через `EditorSceneManager.sceneCount` / `EditorSceneManager.GetSceneAt`, включая выгруженные, плюс текущий Prefab Stage.
- Политика dirty-сцены с непустым путём задаётся настройкой `DirtyScenePolicy`:
  - `Save` — значение по умолчанию; сцена тихо сохраняется через `EditorSceneManager.SaveScene`.
  - `Block` — задача завершается как `runtime_error` до исполнения payload, сцена не трогается, диалог не появляется.
- Политика dirty untitled-сцены остаётся в `DirtyUntitledScenePolicy` (`Discard` / `Block`) и работает как сейчас.
- Открытый Prefab Stage с несохранёнными правками подчиняется `DirtyScenePolicy`: `Save` — тихое `PrefabUtility.SaveAsPrefabAsset` по `assetPath` стейджа и снятие dirtiness, `Block` — `runtime_error` без изменения стейджа.
- Dirty-сцена, открытая, но выгруженная, всегда блокирует задачу отдельным сообщением независимо от `DirtyScenePolicy`: сохранить её невозможно, закрывать её мост не имеет права.
- На время активной задачи и на всё окно тестового прогона взводится `SceneDirtyWatcher`. Он подписан на `EditorSceneManager.sceneDirtied` и на собственный `EditorApplication.update`, и приводит редактор в чистое состояние на ближайшем тике после любого загрязнения.
- `SceneDirtyWatcher` взводится до `TestRunnerApi.Execute`, поэтому его подписка на `EditorApplication.update` предшествует подписке `TestJobRunner` и его тик всегда исполняется раньше `SaveModiedSceneTask` в том же кадре.
- Во взведённом окне сцена с путём сохраняется всегда, даже при `DirtyScenePolicy = Block`: прогон уже стартовал и отменить его через API Test Framework 1.1.33 нельзя. Случаи, которые нельзя разрешить без потери данных (untitled при `Block`, выгруженная dirty-сцена, Prefab Stage при `Block`), только протоколируются в запись задачи.
- Каждое срабатывание watcher-а пишет в логи задачи путь сцены, применённое действие и обрезанный стек вызова, по которому видно источник загрязнения.
- Взведённое состояние watcher-а переживает доменный релоад через `SessionState` и снимается только при финализации задачи-владельца.
- Во время PlayMode watcher бездействует: dirtiness плей-мода снимает существующий `PlayModeSceneRecovery`.

## Настройки

### `Editor/AgentBridgeSettings.cs`

Добавить поле:

```csharp
public string DirtyScenePolicy = "Save";
```

### `Editor/AgentBridgeSettingsStore.cs`

Добавить методы:

```csharp
public static bool GetSaveDirtyScenes();
public static void SetSaveDirtyScenes(bool value);
```

`GetSaveDirtyScenes` возвращает `false` только при строке `Block` без учёта регистра; пустое, отсутствующее и неизвестное значение трактуются как `Save`. `SetSaveDirtyScenes(true)` пишет `Save`, `SetSaveDirtyScenes(false)` пишет `Block`.

### `Editor/AgentBridgeSetupWindow.cs`

В секцию `Scene safety` перед существующим toggle добавить toggle `Save dirty scenes before agent tasks`, связанный с `GetSaveDirtyScenes` / `SetSaveDirtyScenes`, и `HelpBox`:

- включено: `Dirty scenes and prefab stages are saved silently before an agent task changes scenes.`
- выключено: `Agent tasks stop without opening a save dialog while a dirty scene or prefab stage is open.`

### `ProjectSettings/AgentBridge.json`

Добавить:

```json
"DirtyScenePolicy": "Save"
```

## Новые типы

### Новый файл `Editor/ScenePolicyMode.cs`

```csharp
namespace AgentBridge
{
	public enum ScenePolicyMode
	{
		Save,
		Block
	}
}
```

### Новый файл `Editor/SceneDirtyReport.cs`

```csharp
using System.Collections.Generic;
using UnityEngine.SceneManagement;

namespace AgentBridge
{
	public class SceneDirtyReport
	{
		public List<Scene> DirtySavedScenes = new List<Scene>();
		public List<Scene> DirtyUntitledScenes = new List<Scene>();
		public List<Scene> TransientScenes = new List<Scene>();
		public List<Scene> DirtyUnloadedScenes = new List<Scene>();
		public List<string> TestScenePaths = new List<string>();
		public int OpenSceneCount;
		public bool PrefabStageDirty;
		public string PrefabStageAssetPath;

		public bool IsClean
		{
			get
			{
				return DirtySavedScenes.Count == 0
					&& DirtyUntitledScenes.Count == 0
					&& TransientScenes.Count == 0
					&& DirtyUnloadedScenes.Count == 0
					&& !PrefabStageDirty;
			}
		}
	}
}
```

`TransientScenes` содержит сцены Unity Test Framework и dirty untitled-сцены, разрешённые к сбросу текущей политикой. `DirtyUntitledScenes` содержит все dirty-сцены без пути, включая попавшие в `TransientScenes`.

### Новый файл `Editor/SceneDirtyScanner.cs`

```csharp
namespace AgentBridge
{
	public static class SceneDirtyScanner
	{
		public static SceneDirtyReport Scan();
	}
}
```

Алгоритм `Scan`:

- `OpenSceneCount = EditorSceneManager.sceneCount`; перебрать `EditorSceneManager.GetSceneAt(i)` и отбросить невалидные.
- Для каждой сцены определить `testScene = SceneSafetyGuard.IsTestScenePath(scene.path)`.
- Тестовую сцену положить в `TransientScenes`, её непустой путь — в `TestScenePaths`, и дальше не классифицировать.
- Сцену с `scene.isDirty == false` пропустить.
- Сцену с `scene.isDirty && !scene.isLoaded` положить в `DirtyUnloadedScenes`.
- Сцену с `scene.isDirty` и пустым путём положить в `DirtyUntitledScenes`, и дополнительно в `TransientScenes`, если `AgentBridgeSettingsStore.GetDiscardDirtyUntitledScenes()` вернул `true`.
- Остальные dirty-сцены положить в `DirtySavedScenes`.
- Через `PrefabStageUtility.GetCurrentPrefabStage()` получить текущий стейдж; при `stage != null && stage.scene.IsValid() && stage.scene.isDirty` выставить `PrefabStageDirty = true` и `PrefabStageAssetPath = stage.assetPath`.

Скан не мутирует ничего.

### Новый файл `Editor/SceneDirtyWatcher.cs`

```csharp
namespace AgentBridge
{
	[InitializeOnLoad]
	public static class SceneDirtyWatcher
	{
		public const string OwnerTaskKey = "AgentBridge_SceneDirtyWatcher";

		public static bool IsArmed { get; }
		public static void Arm(string ownerTaskId);
		public static void Disarm(string ownerTaskId);
		public static List<string> DrainLogs();
	}
}
```

Поведение:

- Статический конструктор читает `SessionState.GetString(OwnerTaskKey, "")` и при непустом значении восстанавливает подписки; это возвращает watcher после доменного релоада внутри PlayMode-прогона.
- `Arm` пишет `ownerTaskId` в `SessionState`, снимает и заново вешает подписки `EditorSceneManager.sceneDirtied += OnSceneDirtied` и `EditorApplication.update += OnUpdate`. Повторный `Arm` с тем же id идемпотентен.
- `Disarm` снимает подписки и стирает ключ, только если переданный `ownerTaskId` совпадает с сохранённым или если сохранённый пуст.
- `OnSceneDirtied(Scene scene)` не выполняет нормализацию немедленно: выставляет внутренний флаг `_pending`, запоминает `scene.path` (для сцены без пути — `scene.name + " (untitled)"`) и однострочный стек вызова, полученный из `System.Environment.StackTrace`: пропустить первые три кадра, взять следующие восемь, склеить через ` <- `, схлопнуть переводы строк.
- `OnUpdate` выходит немедленно при `!_pending`, при `EditorApplication.isPlayingOrWillChangePlaymode`, при `EditorApplication.isCompiling` и при повторном входе (флаг `_running`).
- `OnUpdate` сбрасывает `_pending`, вызывает `SceneSafetyGuard.NormalizeArmed(out List<string> actions, out List<string> blocked)` и складывает в буфер логов по строке на каждое действие и каждую блокировку, добавляя к первой строке кадра сохранённый стек: `scene dirtied during task <id>: <target>; <action>; source: <stack>`.
- Буфер логов хранится в `SessionState` под ключом `OwnerTaskKey + "_Logs"` строками через `\n`, чтобы пережить доменный релоад; `DrainLogs` читает его, стирает ключ и возвращает список.
- Размер буфера ограничен 50 строками; при переполнении новые строки отбрасываются, а последней строкой буфера становится `scene dirty watcher log truncated`.

## Изменения `Editor/SceneSafetyGuard.cs`

Публичный API становится таким:

```csharp
public static bool TryPrepareForTask(out string error);
public static bool TryVerifyClean(out string error);
public static void NormalizeArmed(out List<string> actions, out List<string> blocked);
public static void EnsureSafeForSceneChange();
public static bool IsTestScenePath(string path);
public static void ClearOpenSceneDirtiness();
public static void DeleteTestSceneAsset(string path);
public static void DeleteAllTestSceneAssets();
```

Перечисление сцен во всех методах класса перевести с `SceneManager.sceneCount` / `SceneManager.GetSceneAt` на `EditorSceneManager.sceneCount` / `EditorSceneManager.GetSceneAt`.

`TryPrepareForTask` (мутирующий префлайт) выполняет по порядку:

- `SceneDirtyReport report = SceneDirtyScanner.Scan()`.
- Если `report.DirtyUnloadedScenes.Count > 0` — вернуть `false` с сообщением `An open scene is unloaded and has unsaved changes: <path>. Load and save it, or close it.` Первый путь из списка.
- Если `report.DirtyUntitledScenes.Count > 0` и `GetDiscardDirtyUntitledScenes() == false` — вернуть `false` с текущим сообщением `A dirty untitled scene is open. Save or close it, or enable Discard dirty untitled scenes in Agent Bridge Setup.`
- Если `report.PrefabStageDirty` и `GetSaveDirtyScenes() == false` — вернуть `false` с сообщением `A prefab stage has unsaved changes: <assetPath>. Save or close it, or enable Save dirty scenes in Agent Bridge Setup.`
- Если `report.DirtySavedScenes.Count > 0` и `GetSaveDirtyScenes() == false` — вернуть `false` с сообщением `A dirty scene is open: <path>. Save it, or enable Save dirty scenes in Agent Bridge Setup.` Первый путь из списка.
- Сохранить каждую сцену из `report.DirtySavedScenes` через `EditorSceneManager.SaveScene(scene)`; при `false` вернуть ошибку `Failed to save dirty scene before an agent task: <path>` и залогировать успешные сохранения строкой `[AgentBridge] Saved dirty scene before task: <path>`.
- Если `report.PrefabStageDirty` — сохранить стейдж методом `SavePrefabStage(out string error)`; при неудаче вернуть `false` с этой ошибкой.
- Дальше — существующая обработка `report.TransientScenes`: снять dirtiness, при `report.TransientScenes.Count == report.OpenSceneCount` создать чистую сцену `DefaultGameObjects` / `Single`, иначе закрыть каждую transient-сцену через `CloseScene(scene, true)`.
- Удалить test scene assets из `report.TestScenePaths` и вызвать `DeleteAllTestSceneAssets`.

`TryVerifyClean` (немутирующая проверка) вызывает `SceneDirtyScanner.Scan` и возвращает `true` при `report.IsClean`. Иначе возвращает `false` и сообщение `Scene state became dirty before the operation started: <target>`, где `<target>` — первый непустой из: путь выгруженной dirty-сцены, путь dirty-сцены, `<untitled>`, `assetPath` стейджа.

`NormalizeArmed` не возвращает ошибок и не бросает исключений; каждое действие — в `actions`, каждая неразрешимая ситуация — в `blocked`:

- Сохранить каждую сцену из `DirtySavedScenes` через `SaveScene` независимо от `DirtyScenePolicy`; успех — `saved <path>`, отказ — `failed to save <path>` в `blocked`.
- Для `DirtyUntitledScenes`: если сцена попала в `TransientScenes` — обработать как transient (снять dirtiness, закрыть или заменить чистой сценой) и записать `discarded untitled scene <name>`; иначе записать в `blocked` строку `untitled scene <name> left dirty by policy Block`.
- Для `DirtyUnloadedScenes` записать в `blocked` строку `unloaded scene <path> left dirty`.
- Для `PrefabStageDirty`: при `GetSaveDirtyScenes() == true` вызвать `SavePrefabStage` и записать `saved prefab stage <assetPath>` или содержимое ошибки в `blocked`; при `false` записать в `blocked` строку `prefab stage <assetPath> left dirty by policy Block`.
- Тестовые сцены обрабатываются как сейчас: снятие dirtiness, закрытие, удаление ассетов.
- Любое исключение внутри `NormalizeArmed` перехватывается и добавляется в `blocked` строкой `normalize failed: <message>`.

Приватный метод сохранения стейджа:

```csharp
private static bool SavePrefabStage(out string error)
```

- Получить стейдж через `PrefabStageUtility.GetCurrentPrefabStage()`; при `null` вернуть `true`.
- При пустом `stage.assetPath` вернуть `false` с сообщением `Prefab stage has no asset path and cannot be saved silently.`
- Вызвать `PrefabUtility.SaveAsPrefabAsset(stage.prefabContentsRoot, stage.assetPath, out bool saved)`; при `saved == false` вернуть `false` с сообщением `Failed to save prefab stage: <assetPath>`.
- Снять dirtiness стейджа существующим приватным `ClearSceneDirtiness(stage.scene)` и вернуть `true`.

## Изменения `Editor/TaskCoordinator.cs`

- `RequiresScenePreflight` удалить; префлайт выполняется для любой задачи после создания активной записи и до `RunTask`.
- Сразу после успешного префлайта вызвать `SceneDirtyWatcher.Arm(_activeRecord.Id)`.
- В `FinishTask` перед записью журнала добавить `logs.AddRange(SceneDirtyWatcher.DrainLogs())`.
- В `CleanupActive` вызвать `SceneDirtyWatcher.Disarm(_activeTaskId)` для всех kinds, кроме `tests`: для `tests` watcher снимает `AgentTestRunner` после финализации прогона.
- В `OnBeforeAssemblyReload` для kind `tests` watcher не снимать.

## Изменения `Editor/AgentTestRunner.cs`

- В `TryRequestRunForCoordinator` для обоих режимов, после существующей проверки на PlayMode и до `SessionState.SetString`, вызвать `SceneSafetyGuard.TryPrepareForTask`; при ошибке вернуть `abortedResult` с этим сообщением и `aborted = true`.
- Для `TestMode.PlayMode` оставить вызов `PlayModeSceneRecovery.Begin` после префлайта.
- Непосредственно перед `api.Execute` вызвать `SceneSafetyGuard.TryVerifyClean`; при `false` стереть ключи `SessionState`, при PlayMode вызвать `PlayModeSceneRecovery.Cancel`, вернуть `abortedResult` с сообщением проверки и не запускать прогон.
- Перед `api.Execute` вызвать `SceneDirtyWatcher.Arm(taskId)`; в блоке `catch` вызвать `SceneDirtyWatcher.Disarm(taskId)`.
- В `FinalizeCoordinatorRun` перед записью журнала добавить строки `SceneDirtyWatcher.DrainLogs()` в `record.Logs` и вызвать `SceneDirtyWatcher.Disarm(taskId)`.

## Изменения `Editor/PlayModeSceneRecovery.cs`

- В `Begin` заменить прямой вызов `SceneSafetyGuard.TryPrepareForTask` на него же с сохранением текущей семантики; дополнительных изменений в сигнатуре нет.
- В `CompleteRecovery` после успешного восстановления setup и до финализации вызвать `SceneSafetyGuard.TryPrepareForTask(out string tailError)`; непустую ошибку присоединить к `recoveryError` через существующий `AppendError`.
- В `Cancel` вызвать `SceneDirtyWatcher.Disarm(state != null ? state.TaskId : "")` до удаления файла состояния.

## Guardrail агентских исходников

### `Editor/SourceGuardrail.cs`

Расширить `CheckSceneTransitionCall` до `CheckForbiddenCall` с сохранением текущих правил и сообщений и добавить запрещённые вызовы:

- `EditorSceneManager.SaveCurrentModifiedScenesIfUserWantsTo`
- `EditorSceneManager.SaveModifiedScenesIfUserWantsTo`
- `EditorApplication.EnterPlaymode`
- `EditorApplication.ExitPlaymode`
- `EditorApplication.Exit`
- `EditorUtility.DisplayDialog`
- `EditorUtility.DisplayDialogComplex`
- `EditorUtility.OpenFilePanel`
- `EditorUtility.OpenFolderPanel`
- `EditorUtility.SaveFilePanel`
- `EditorUtility.SaveFilePanelInProject`
- `PrefabStageUtility.OpenPrefab`
- `AssetDatabase.OpenAsset`
- `TestRunnerApi.Execute`

Сообщение violation: `modal or interactive editor API is not allowed in agent tasks`.

Добавить проверку присваивания: в `TryValidate` обрабатывать узлы вида `SimpleAssignmentExpression`, и если левая часть — `SimpleMemberAccessExpression` с типом `EditorApplication` и именем `isPlaying` или `isPaused`, писать violation с тем же сообщением.

## Документация

Обновить:

- `Packages/com.elmortem.agentbridge/UNITYAGENT.md` — раздел политики сцен: описать `DirtyScenePolicy`, поведение с Prefab Stage, блокировку на выгруженной dirty-сцене и то, что префлайт работает для всех kinds.
- `unity-bridge-plugin/skills/unity-bridge/SKILL.md` — тот же раздел плюс расширенный список запрещённых guardrail-вызовов.
- `README.md` — раздел `Scene Safety` (обе политики таблицей, выгруженная dirty-сцена, watcher, список запрещённых API) и уточнение в разделе `Scene Screenshots`, что префлайт работает и для `sceneshot`.
- `unity-bridge-plugin/skills/unity-ui/SKILL.md` — раздел про открытый Prefab Stage и dirty-сцены перед `ui`-задачей.
- Шаблоны `Docs/UNITYAGENT-template.md` и `Docs/UNITYAGENT-UI-template.md` не трогаем: они описывают кастомные API проекта и соглашения вёрстки, scene safety в них не фигурирует.

## Проверки

### EditMode regression tests в `Assets/Tests/Editor/AgentBridgeProbeTests.cs`

- `DirtyScenePolicy = Save`: dirty-сцена с путём сохраняется, `TryPrepareForTask` возвращает `true`.
- `DirtyScenePolicy = Block`: dirty-сцена с путём остаётся dirty, `TryPrepareForTask` возвращает `false`, сообщение содержит путь сцены.
- `TryVerifyClean` возвращает `false` для только что помеченной dirty-сцены и `true` после `TryPrepareForTask`.
- `SceneDirtyScanner.Scan` относит dirty-сцену с путём в `DirtySavedScenes`, untitled — в `DirtyUntitledScenes`, `Assets/InitTestScene*.unity` — в `TransientScenes` и `TestScenePaths`.
- `SceneDirtyWatcher.Arm` + `EditorSceneManager.MarkSceneDirty` + один прогон `EditorApplication.update` приводят сцену к чистому состоянию, `DrainLogs` возвращает непустой список, повторный `DrainLogs` — пустой.
- `SceneDirtyWatcher.Disarm` с чужим `ownerTaskId` не снимает подписку.
- Guardrail отклоняет `EditorSceneManager.SaveCurrentModifiedScenesIfUserWantsTo()`, `EditorApplication.EnterPlaymode()`, `EditorApplication.isPlaying = true` и `EditorUtility.DisplayDialog(...)`, и принимает `AgentSceneManager.OpenScene(...)`.
- Отсутствующее значение `DirtyScenePolicy` трактуется как `Save`.

Каждый scene-тест сохраняет исходный `SceneManagerSetup`, восстанавливает его в `TearDown` без интерактивного API и возвращает обе настройки политики в значения по умолчанию.

### End-to-end Unity gates

- `dotnet build AgentBridgeUnity/AgentBridge.csproj --no-restore` проходит без ошибок.
- Bridge compile task завершается `success`.
- EditMode test task для `AgentBridge.ProbeTests` проходит полностью.
- PlayMode test task для `AgentBridge.PlayModeProbeTests` проходит полностью.
- Сценарий воспроизведения: C#-таском открыть сохранённую сцену, изменить её без сохранения, затем запустить tests task — прогон стартует без модального окна, сцена сохранена, в логах задачи есть строка о сохранении.
- Тот же сценарий при `DirtyScenePolicy = Block` даёт `runtime_error` до старта прогона, сцена остаётся dirty, модального окна нет.
- Сценарий с открытым Prefab Stage и несохранёнными правками: при `Save` стейдж сохраняется тихо, при `Block` задача отклоняется.
- После PlayMode-прогона `PlayModeSceneRecovery.IsPending == false`, `SessionState` ключи watcher-а стёрты, `Assets/InitTestScene*.unity` отсутствуют.

---

## Правки после реализации

### `NormalizeArmed` не разрушает состояние стартовавшего прогона

Предписанное поведение «тестовые сцены обрабатываются как сейчас: снятие dirtiness, закрытие, удаление ассетов» и «untitled transient — закрыть или заменить чистой сценой» ломает PlayMode-прогон. Воспроизведено на первом же прогоне: `UnityEditor.TestTools.TestRunner.RuntimeTestLauncherBase.CreateBootstrapScene` создаёт bootstrap-сцену и вызывает `MarkSceneDirty`; watcher срабатывал на ближайшем тике, закрывал сцену и удалял её ассет, после чего прогон завершался `runtime_error` с `PlayMode test run ended before a result was recorded` (запись `Task_20260815_133506_429_5f45de1f`).

Во взведённом окне цель — только не-dirty редактор, а не чистый набор сцен: закрывать сцены и удалять ассеты в этот момент нельзя, они принадлежат исполняющемуся прогону. Поэтому `NormalizeArmed`:

- dirty-сцену с путём сохраняет (как в спецификации);
- dirty untitled-сцену при политике `Discard` только снимает с dirtiness (`cleared untitled scene <name>`), при `Block` — пишет в `blocked`;
- dirty тестовую сцену только снимает с dirtiness (`cleared test scene <path>`), не закрывает и не удаляет ассет;
- выгруженную dirty-сцену и Prefab Stage обрабатывает как в спецификации.

Закрытие transient-сцен и удаление тестовых ассетов остаётся в префлайте `TryPrepareForTask` и в `PlayModeSceneRecovery`, то есть вне окна прогона.

### Мелочи

- `TaskCoordinator.TryFinalizePendingCompileTask` снимает watcher и сливает его логи в запись: compile-таск финализируется после доменного релоада, мимо `CleanupActive`.
- `TaskCoordinator.StartTestsTask` снимает watcher, если `TryRequestRunForCoordinator` вернул `false`: прогон не стартовал, снимать watcher некому.
- Guardrail ловит `TestRunnerApi.Execute` синтаксически: по тексту получателя и по локальным переменным, в объявлении которых упомянут `TestRunnerApi` (семантической модели у guardrail нет).
- Сценарий Prefab Stage покрыт EditMode-тестами (`SceneSafetyGuard_SavesDirtyPrefabStage`, `SceneSafetyGuard_BlocksDirtyPrefabStageWhenConfigured`), а не ручным прогоном: `PrefabStageUtility.OpenPrefab` теперь запрещён guardrail и недоступен из агентского таска.

## Версии

- `Packages/com.elmortem.agentbridge/package.json`: `0.11.0` → `0.12.0`.
- `unity-bridge-plugin/.claude-plugin/plugin.json`: `1.9.2` → `1.10.0`.
- `AgentBridgeCli` не менялся, версия CLI осталась `1.8.0`; `BridgeConstants.ProtocolVersion` не менялся.

## Результаты проверок

- `dotnet build AgentBridgeUnity/AgentBridge.csproj --no-restore` — без ошибок и предупреждений.
- Bridge compile task — `success`.
- EditMode `AgentBridge.ProbeTests` — 17/17 passed.
- PlayMode `AgentBridge.PlayModeProbeTests` — 2/2 passed, модального окна нет, после прогона `Assets/InitTestScene*.unity` отсутствуют и файл состояния recovery удалён.
- Сценарий `Save`: csharp-таск оставил `Assets/AgentBridgeDirtyProbe.unity` dirty (маркера в файле нет) → tests task стартовал без диалога, сцена сохранена префлайтом (маркер в файле есть), статус `success`.
- Сценарий `Block`: тот же таск → tests task завершился `runtime_error` с `A dirty scene is open: Assets/AgentBridgeDirtyProbe.unity...`, сцена осталась dirty, диалога нет.

## После выполнения

- Статус вверху документа: `Выполнено`.
- Открытый вопрос заказчику: нужно ли обновлять проектную документацию (README, шаблоны `Docs/UNITYAGENT-template.md`) под новое поведение scene safety — `Packages/.../UNITYAGENT.md` и скилл `unity-bridge` уже обновлены.
