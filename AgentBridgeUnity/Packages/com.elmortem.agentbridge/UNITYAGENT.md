# Запуск тестов Unity через Bridge

Как прогнать EditMode (юнит) и PlayMode тесты прямо из Bridge и получить результат (passed/failed + детали падений) одной командой клиента — без отдельного C#-таска и без второго ожидания.

Прогон дорог (для PlayMode — вход в Play Mode). Запускай тесты как финальную проверку завершённого этапа работы, а не после каждой правки.

`agentbridge compile` — только структурный gate. Перед `success` мост синхронно импортирует проектные исходники и проверяет для каждого `.cs` наличие `.meta`, AssetDatabase GUID, назначенную Unity сборку и присутствие в source inventory этой сборки. Ошибки импорта возвращаются кодами `ABIMPORT001`–`ABIMPORT004`. Даже успешный `compile` не является поведенческим доказательством: завершённая работа требует релевантного прогона `agentbridge tests`.

## Домен

- запусти тесты, прогони тесты, проверь тесты
- запусти юнит-тесты / EditMode тесты
- запусти PlayMode тесты / игровые тесты
- проверь, что тесты проходят, не сломал ли я тесты
- прогони тесты сборки X, тесты класса Y, тест с категорией Z
- после правок кода — убедись, что всё зелёное

## Как это работает

`tests` — самостоятельный тип задачи (`Kind: "tests"`), а не побочный эффект C#-таска. Мост сам владеет персистентным колбэком (переживает domain reload при входе/выходе из Play Mode) и по завершении прогона пишет результат прямо в запись задачи (`Journal/<TaskId>.json`, поле `Tests`) — второго файла и второго ожидания не требуется.

## Команда

```bash
agentbridge tests [--mode EditMode|PlayMode] [--assembly A] [--test T] [--category C] [--wait <секунды>]
```

Каждый из `--assembly`/`--test`/`--category` можно повторять несколько раз — фильтры комбинируются. Без `--mode` — `EditMode`. Команда сама создаёт задачу, ждёт её и печатает результирующий JSON (`TaskRecord`) в stdout.

```bash
agentbridge tests --mode EditMode --assembly MyGame.Tests --wait 600
```

Для PlayMode-прогонов, которые могут идти долго, увеличивай `--wait`.

## Чтение результата

- Код выхода `0` и `Status: "success"` → прогон завершён, все выполненные тесты зелёные.
- Код выхода `1` и `Status: "test_failure"` → есть упавшие или inconclusive тесты; покажи `Tests.failures` пользователю.
- `Status: "runtime_error"` и `Tests.aborted == true` → прогон не стартовал; `Tests.message` обычно говорит «выйди из Play Mode и перезапусти».
- Код выхода `2` → таймаут ожидания клиента, прогон ещё идёт (PlayMode может быть долгим) — дождись через `agentbridge wait <TaskId> --wait <секунды>`.

## PlayMode

PlayMode-прогон надёжен при любой настройке Enter Play Mode (Reload Domain включён или выключен) — колбэк и scene-recovery state моста персистентные. Перед прогоном мост сохраняет dirty-сцены с путём, отбрасывает временные `Assets/InitTestScene*.unity` и dirty untitled-сцены, затем запоминает исходный scene setup. После выхода из Play Mode мост отбрасывает тестовые изменения, восстанавливает setup и только после этого завершает задачу. Интерактивные save-диалоги в этом пути не используются. Если на момент запроса редактор уже входит в Play Mode или находится в нём, прогон не стартует — `Status` будет `runtime_error`, `Tests.aborted: true`.

## Безопасная смена сцен из C#-задач

Не вызывай `EditorSceneManager.OpenScene`, `NewScene`, `CloseScene`, `RestoreSceneManagerSetup` и runtime `SceneManager.LoadScene*` напрямую: guardrail отклонит задачу. Используй `AgentBridge.AgentSceneManager` с теми же основными операциями. Перед переходом он сохраняет dirty-сцены с путём, удаляет тестовые сцены и применяет политику dirty untitled-сцен без модального окна.

Guardrail также отклоняет модальные и интерактивные Editor API: `EditorSceneManager.SaveCurrentModifiedScenesIfUserWantsTo` / `SaveModifiedScenesIfUserWantsTo`, `EditorApplication.EnterPlaymode` / `ExitPlaymode` / `Exit` / `ExecuteMenuItem`, присваивание `EditorApplication.isPlaying` и `isPaused`, `EditorUtility.DisplayDialog` / `DisplayDialogComplex` / `OpenFilePanel` / `OpenFolderPanel` / `SaveFilePanel` / `SaveFilePanelInProject`, `PrefabStageUtility.OpenPrefab`, `AssetDatabase.OpenAsset`, `TestRunnerApi.Execute`.

## Плей мод

Никогда не входи в плей мод из `csharp`-таска — ни напрямую, ни через `ExecuteMenuItem`, ни рефлексией: guardrail режет и вызовы, и строковые литералы `"EnterPlaymode"`, `"ExitPlaymode"`, `"isPlaying"`, `"Edit/Play"`. Читать `EditorApplication.isPlaying` можно.

Законный канал — отдельные команды CLI:

```bash
agentbridge play [--seconds N] --note <intent> --session <id>
agentbridge stopplay [--session <id>]
```

- `play` требует `--session` (сессия становится владельцем плей мода) и `--note` — короткое намерение сессии; без любого из них команда отклоняется. `ReturnValue` — `playing_until:<UTC>`. Без `--seconds` берётся `PlaySessionDefaultSeconds` (30), сверху обрезается `PlaySessionMaxSeconds` (600); обе настройки — в `ProjectSettings/AgentBridge.json` и в **Tools → Agent Bridge → Setup...**.
- Во время своей play-сессии выполняются только `csharp` и `sceneshot` (включая `"view": "game"` — снимок настоящего Game View с overlay-UI). Остальные kind'ы отклоняются с `kind not allowed during play session`.
- Чужую play-сессию остановить нельзя: `stopplay` вернёт `rejected` с `play_session_held_by:<id>`.
- Плей мод без сессии (застрявший таск, ручной запуск) гасит `stopplay` от любого агента. Вне плей мода `stopplay` — no-op со `success` и `ReturnValue: "not_playing"`.
- Сессия завершается сама по дедлайну. Если человек нажал Stop, сессия закрывается сама с `stopped:external`.
- Пока идут тесты, `play`/`stopplay` отклоняются с `tests are running` — семантика PlayMode-прогонов не меняется.
- Агентский плей мод в обход guardrail мост гасит автоматически и дописывает в журнальную запись виновника строку `this task entered play mode; the bridge exited it automatically`. Плей мод, запущенный человеком, мост не трогает.
- Вход в плей мод не отбирает фокус у пользователя: и `play`, и `tests --mode PlayMode` гасят автофокусировку Game View, а сессия идёт с `Application.runInBackground` — игра тикает, даже когда Unity в фоне. Если редактор всё-таки вылез вперёд, мост возвращает фокус прежнему окну (максимум дважды за вход, чтобы не спорить с человеком, который сам кликнул в Unity).

Состояние видно в `agentbridge status`: `IsPlaying`, `PlaySessionAgentId`, `PlaySessionDeadlineUtc`.

## Политика сцен

Префлайт scene safety выполняется перед задачей любого типа: `csharp`, `ui`, `tests`, `compile`, `sceneshot`. Он смотрит на все открытые сцены (включая выгруженные) и на открытый Prefab Stage.

- `DirtyScenePolicy` в `ProjectSettings/AgentBridge.json`: `Save` (по умолчанию) — dirty-сцена с путём и dirty Prefab Stage тихо сохраняются перед задачей; `Block` — задача завершается как `runtime_error` до исполнения payload, сцена и стейдж не трогаются, диалог не появляется.
- `DirtyUntitledScenePolicy`: `Discard` (по умолчанию) закрывает dirty untitled-сцену без сохранения; `Block` оставляет её открытой и завершает задачу как `runtime_error`.
- Открытая, но выгруженная dirty-сцена всегда блокирует задачу: сохранить её нельзя, а закрывать её мост не имеет права. Загрузи и сохрани её сам либо закрой.
- На время задачи и на всё окно тестового прогона взведён watcher: если сцену пачкает что-то уже после префлайта, он приводит редактор в чистое состояние на ближайшем тике и пишет в логи задачи путь сцены, применённое действие и источник загрязнения. Внутри уже стартовавшего прогона сцена с путём сохраняется даже при `DirtyScenePolicy = Block` — отменить прогон в этот момент нельзя.

Обе настройки доступны в **Tools → Agent Bridge → Setup...**.

## Скриншоты сцены

Чтобы посмотреть на открытую сцену, не пиши C#-таск со `SceneView` и захватом пикселей — есть отдельный тип задачи: `agentbridge sceneshot <файл>.sceneshot.json`. Декларативный JSON перечисляет ракурсы (`frame` — автокадрирование объекта, `pose` — явная поза камеры), мост снимает каждый в PNG и возвращает пути в `Artifacts`. Снимок делается перерисовкой вью в текстуру, поэтому не зависит от того, виден ли редактор на экране. Служебное окно показывается без активации, так что снимок не выдёргивает Unity поверх того, в чём работает человек; для `"view": "game"` уже открытый Game View берётся как есть и не фокусируется. Формат и ограничения — в скилле `unity-bridge`.

Снимается текущая открытая сцена; сам таск её не меняет, но перед ним отрабатывает общий префлайт сцен — при `DirtyScenePolicy = Save` несохранённая сцена будет тихо сохранена, при `Block` таск завершится `runtime_error`. Нужна другая сцена — сначала открой её C#-таском через `AgentBridge.AgentSceneManager`.

## Примечания

- Один прогон — одна команда `tests`. Не запускай вторую, пока первая не завершилась (мост выполняет задачи строго по одной).
- Для запуска тестов нужен пакет **Unity Test Framework** (`com.unity.test-framework`) — он есть в проекте по умолчанию.
- Записи задач чистит мост автоматически (авто-трим последних N успешных по `KeepCompletedCount` в `ProjectSettings/AgentBridge.json`). Отдельно ничего чистить не нужно.

## Работа рядом с другими агентами

В одном редакторе может работать несколько агентов сразу. Мост выполняет задачи строго по одной и делит редактор между сессиями честно — но только если агент называет свою сессию.

- Один раз в начале работы сгенерируй id сессии: `AB_<дата-время>_<random>` — и передавай его каждой команде: `--session <id>`.
- По желанию добавляй `--note "<что делаешь>"` — держатель редактора увидит текст в поле `Contention` своих результатов.
- Поле `Contention` в результате задачи показывает, сколько чужих сессий ждёт редактор и как давно. Закончил серию задач — вызови `agentbridge release --session <id>`, не жди таймаута.
- Пока чужая сессия держит редактор, команда честно ждёт в очереди и печатает в stderr `queued <n>s, position <p>/<total>, holder <id>`. Это не зависание — не отменяй команду и не пересоздавай задачу.
- Сцены и открытый Prefab Stage запоминаются на сессию: когда редактор возвращается к твоей сессии, мост восстанавливает то, в чём ты работал.
