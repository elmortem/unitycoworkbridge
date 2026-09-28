Status: Выполнено

# Unity Agent Bridge — безопасный доступ к Prefab Stage

## Причина

- ТДД `260815-1200-TDD-scene_dirty_guard` внёс `PrefabStageUtility.OpenPrefab` в список модальных API guardrail: уход с несохранённого стейджа открывает save-диалог Unity. Безопасной обёртки, как `AgentSceneManager` для сцен, не появилось, и режим префаба стал недоступен агенту целиком.
- Проекты держат редакторские механики внутри префабов и снимают кадры приёмки оттуда. Агенты не могли ни открыть стейдж, ни снять его: `SceneShotFramer` искал цель только в `SceneManager.GetSceneAt`, а сцена стейджа туда не входит.

## Контракт

- `AgentSceneManager.OpenPrefab(path)` → `PrefabStage`: проверяет `.prefab`-путь и существование ассета (`ArgumentException`), для уже открытого префаба возвращает текущий стейдж без переключения, иначе `SceneSafetyGuard.EnsureSafeForSceneChange()` и `PrefabStageUtility.OpenPrefab`.
- `AgentSceneManager.ClosePrefab()` → `bool`: без стейджа `false`; иначе префлайт и `StageUtility.GoToMainStage()`. `GetOpenPrefab()` — текущий стейдж.
- Guardrail: `PrefabStageUtility.OpenPrefab`, `StageUtility.GoToMainStage`, `StageUtility.GoToStage` отклоняются с подсказкой `use AgentBridge.AgentSceneManager.OpenPrefab / ClosePrefab`; из списка модальных API `OpenPrefab` убран.
- `sceneshot`: необязательное поле верхнего уровня `prefab`. Стейдж открывается отдельным тиком до первого кадра; после последнего кадра, ошибки или отмены исполнитель возвращает прежний стейдж (основная сцена или ранее открытый префаб). Таймаут и отмена в обход тика — через `SceneShotTaskExecutor.Abandon`, вызываемый из `TaskCoordinator.CleanupActive`; во время `beforeAssemblyReload` стейдж не переключается. Ошибка возврата пишется в `Logs` и не меняет статус снимков. `prefab` вместе с `"view": "game"` отклоняется парсером.
- `SceneShotFramer`: при открытом стейдже ищет цель только в нём, корень префаба первым (UI-префаб лежит под сгенерированным environment canvas). Путь с `/` сначала пробуется от корня префаба (`Board/Cell`), затем как полный путь; в стейдже корень назван по имени файла префаба, а не по имени, с которым его сохраняли. Объект без `Renderer` кадрируется по углам `RectTransform`.

- Вне основного стейджа Scene View рисует над камерой breadcrumb-заголовок. `SceneViewGrabber.StageHeaderPoints` читает его высоту (`SceneView.m_StageHandling.breadcrumbHeight`, рефлексия). Окно снимка выше на эту высоту, `CropTop` срезает полосу, и PNG остаётся ровно запрошенного размера. Если внутреннего API нет, высота 0 и заголовок остаётся в кадре.

## Проверки

- EditMode `AgentBridgeProbeTests`: `SourceGuardrail_RejectsDirectPrefabStageChange`, `SourceGuardrail_AllowsAgentSceneManagerPrefab`, `AgentSceneManager_OpensAndClosesPrefabStage`, `AgentSceneManager_SavesDirtyStageBeforeSwitchingPrefab`, `AgentSceneManager_ClosePrefabThrowsUnderBlockPolicy`, `AgentSceneManager_OpenPrefabRejectsMissingAsset`, `SceneShotPayloadParser_ReadsPrefab`, `SceneShotFramer_ResolvesTargetInsideOpenPrefabStage`.
- Живая проверка (Unity 2022.3.62f2, CLI 1.23.1): `sceneshot` с `prefab` из основной сцены — PNG с содержимым префаба без заголовка, после задачи основной стейдж; ошибка кадра тоже закрывает стейдж; из открытого другого префаба — `reopened prefab stage <другой>`; `csharp` `OpenPrefab` → `sceneshot` без `prefab` → `ClosePrefab`; прямой `PrefabStageUtility.OpenPrefab` — `rejected` с подсказкой; снимок основной сцены без изменений.
- Прогоны probe-тестов дают `stale_input`, потому что тесты создают ассеты в `Assets/`. Это старое поведение, его разбирает `260921-2038-TDD-probe_tests_still_inputs`.

## Версии

- Пакет `0.34.3` → `0.35.0`, плагин `1.31.0` → `1.32.0`.
