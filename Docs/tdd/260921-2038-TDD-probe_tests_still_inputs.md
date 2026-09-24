Status: Готов к реализации

# Тесты моста не двигают входы проекта

Вид: новая функция в существующем коде. Набор `AgentBridge.ProbeTests` всегда завершается `stale_input`, потому что сам пишет во входы. Проверено живым прогоном в хост-проекте (`Task_20260921_203254_144_469633c3`): наблюдатель насчитал 10 событий по `Assets/AgentBridgeSceneSafetyTest.unity` и пробным ассетам в корне `Assets`, манифест — одно по `ProjectSettings/AgentBridge.json`. Один тест, который ассеты не трогает (`Task_20260921_203535_928_e9e37145`, `SourceGuardrail_RejectsModalAndInteractiveEditorApi`), тоже `stale`: `SetUp` пишет `AgentBridge.json` через `SetDiscardDirtyUntitledScenes`/`SetSaveDirtyScenes`, а `Save` пишет файл всегда, даже при неизменных значениях. После изменения набор ведёт настройки в подменённом файле под `Temp`, пробные ассеты кладёт в объявленную проектом папку-фикстуру, которая исключена из входов только пока перед прогоном была пуста, и `AgentBridge.ProbeTests` получает `Evidence: valid`. `ignore` не расширяется.

Заодно закрывается вторая проверка: `compile` с реальным импортом (`Task_20260921_203135_579_040a9032`, новый `.cs` под `Assets`, один domain reload по `bridge_start`) дал `Evidence: valid`, digest 447 файлов. `README.md:437` и `SKILL.md:376` утверждают обратное и правятся в документации.

## Настройки в `Packages/com.elmortem.agentbridge/Editor`

### `AgentBridgeSettings.cs`

- Поле `public string[] FixtureRoots = new string[0];` — пути от корня проекта с прямыми слэшами, например `Assets/AgentBridgeProbeFixture`.

### `AgentBridgeSettingsStore.cs`

- Поле `public static string PathOverride = "";`. `GetSettingsPath()` возвращает `PathOverride`, если он непустой, иначе прежний путь.
- `Save`: перед `File.WriteAllText` прочитать существующий файл; если он есть и его содержимое равно `json` (`string.Equals`, `Ordinal`), не писать. `_cached = null` в обоих случаях.
- `public static string[] GetFixtureRoots()` → `Load().FixtureRoots ?? new string[0]`.

### `ValidationEvidence.cs`

- `CollectExcludedRoots`: цикл по `CoordinationGate.DeclaredFixtureRoots()` вынести в приватный `AddFixtureRoots(List<string> excluded, string projectRoot, string[] fixtures)` и вызвать дважды: для плана и для `AgentBridgeSettingsStore.GetFixtureRoots()`. Правило прежнее: корень с файлами — не фикстура.

### `ValidationInputSnapshot.cs`

- `IsExcluded`: до проверки префикса — если `normalized` без учёта регистра равен `Normalize(root).TrimEnd('/') + ".meta"`, вернуть `true`. Мета-файл папки-фикстуры принадлежит ей: Unity создаёт и удаляет его вместе с папкой.

## Хост-проект `AgentBridgeUnity`

### `ProjectSettings/AgentBridge.json`

- Добавить `"FixtureRoots": ["Assets/AgentBridgeProbeFixture"]`.

### `Assets/Tests/Editor/AgentBridgeProbeTests.cs`

- Константы: `FixtureRoot = "Assets/AgentBridgeProbeFixture"`, `SavedScenePath = FixtureRoot + "/AgentBridgeSceneSafetyTest.unity"`, `MissingMetaSourcePath = FixtureRoot + "/AgentBridgeMissingMetaProbe.cs"`, `PrefabProbePath = FixtureRoot + "/AgentBridgePrefabStageProbe.prefab"`. `TestScenePath` остаётся в корне `Assets`: `SceneSafetyGuard.IsTestScenePath` принимает `InitTestScene*.unity` только там, и это уже объявленный скретч моста.
- Поле `private string _originalSettingsOverride;`. В `SetUp` до первого `AgentBridgeSettingsStore.Set*`: запомнить `PathOverride`; скопировать `ProjectSettings/AgentBridge.json` в `Path.Combine(BridgePaths.ProjectRoot, "Temp", "AgentBridge", "probe-settings.json")` (создать папку); `PathOverride = <этот путь>`. Затем `AssetDatabase.CreateFolder("Assets", "AgentBridgeProbeFixture")`, если папки нет.
- `TearDown`: после существующих удалений — `AssetDatabase.DeleteAsset(FixtureRoot)`; в конце `PathOverride = _originalSettingsOverride`. Папка перед следующим прогоном не существует, поэтому исключается.
- `SettingsStore_TreatsMissingDirtyScenePolicyAsSave`: `settingsPath` брать из подменённого файла (`Temp/AgentBridge/probe-settings.json`), остальное без изменений.
- Пробный источник без `.meta` лежит в фикстуре, `SourceImportVerifier.ValidateRoot` обходит весь `Assets` и находит его там же.

### `Assets/Tests/Editor/AgentBridgeWakeTests.cs`

- Тот же приём с `PathOverride` и копией настроек в `SetUp`/`TearDown` (или в начале и `finally` теста, если `SetUp` нет).

## Тесты

- `AgentBridgeCoordination.Tests/Scenarios.cs`, `C12_InputDigest`: добавить случай — папка-фикстура объявлена, её `<root>.meta` рядом с ней не входит в digest, а файл `<root>Other.meta` входит.
- `AgentBridgeEvidenceTests` (EditMode): новый `FixtureRootFromSettingsIsExcludedOnlyWhileEmpty` — `PathOverride` на временный файл с `FixtureRoots`, `CollectExcludedRoots` содержит абсолютный путь папки, пока её нет или она пуста, и не содержит, когда в ней лежит файл. Новый `SaveSkipsIdenticalContent` — два `SetSaveDirtyScenes(true)` подряд, mtime файла не меняется.
- Затрагиваемые существующие: `AgentBridgeProbeTests` целиком, `AgentBridgeWakeTests`, `C12`, `C13`.

## Порядок работ

- `AgentBridgeSettings.FixtureRoots`, `PathOverride`, `Save` без лишней записи, `GetFixtureRoots`; `AgentBridgeEvidenceTests` на них.
- `IsExcluded` с мета-файлом корня, случай в `C12`; `CollectExcludedRoots` с фикстурами из настроек.
- `AgentBridge.json` хост-проекта, перенос пробных путей и подмена настроек в `AgentBridgeProbeTests`, `AgentBridgeWakeTests`.
- Живая приёмка: `tests --test AgentBridgeProbeTests`, затем `--test SourceGuardrail_RejectsModalAndInteractiveEditorApi`, затем полный EditMode-набор хост-проекта; телеметрия `input_changes` по этим задачам — в заметку.
- Версия пакета `0.34.0` → `0.35.0`, плагин по правилам `CLAUDE.md`, документация.

## Приёмка

- `tests --test AgentBridgeProbeTests` в хост-проекте: все зелёные, `Evidence: valid`, событий `input_changes` по задаче нет.
- Полный EditMode-набор хост-проекта: `Evidence: valid`; повтор — `Cached: true`.
- После прогона папки `Assets/AgentBridgeProbeFixture` и её `.meta` нет, `ProjectSettings/AgentBridge.json` не изменён (`git status` чистый по этому файлу).
- Файл, оставленный в папке-фикстуре вручную, возвращает её во входы: следующий прогон `ProbeTests` даёт `stale_input` с этой папкой в `input_changes`.
- `Coordination: PASS` со всеми группами.

## Документация

- `README.md`: описание входов (`:421`) — фикстурные корни объявляются и в `ProjectSettings/AgentBridge.json`, и в плане, оба только пока пусты; `:437` — убрать утверждение про `unknown` у compile с импортом, заменить на факт: снимок берётся после импорта, результат `valid`.
- `unity-bridge-plugin/skills/unity-bridge/SKILL.md:376` — тот же абзац про compile убрать; в разделе `tests` добавить, что тесты, которым нужны ассеты, кладут их в объявленную папку-фикстуру.
- `Docs/PROJECT_MAP.md`: строки `AgentBridgeSettingsStore.cs`, `ValidationEvidence.cs`, `ValidationInputSnapshot.cs`; описание `ProjectSettings/AgentBridge.json` в выжимке `CLAUDE.md`.
- `Docs/notes/260921-2038-NOTE-probe-tests-inputs.md` — новая: три живых задачи из этого ТДД с путями из `input_changes`, результат compile с импортом, результат приёмки.
