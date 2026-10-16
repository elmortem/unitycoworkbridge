# CLAUDE.md

Инструкции для агента, работающего в этом репозитории. Пользовательская документация — в `README.md`.

## Что это

Unity Agent Bridge — мост между ИИ-агентом и Unity Editor с независимой очередью. Агент пишет
C#-скрипт или декларативную задачу, CLI через скрытый внешний queue-server сохраняет её даже при
закрытой или зависшей Unity; пакет внутри доступного редактора компилирует скрипт в
памяти через Roslyn (без domain reload, без файлов в `Assets/`) и выполняет на главном потоке,
возвращая логи и результат в stdout.

Три независимо версионируемых компонента:

| Компонент | Где | Версия | Публикация |
|---|---|---|---|
| AgentBridge CLI | `AgentBridgeCli/` | `<Version>` в `AgentBridgeCli/AgentBridgeCli.csproj` | GitHub Release, workflow `agentbridge-cli.yml` |
| Unity-пакет | `AgentBridgeUnity/Packages/com.elmortem.agentbridge/` | `version` в его `package.json` | UPM по git URL, публикация не нужна |
| Плагин агента | `unity-bridge-plugin/` | `version` в `.claude-plugin/plugin.json` | ZIP закоммичен в репозиторий |

## Карта проекта

Полная карта — **[`Docs/PROJECT_MAP.md`](Docs/PROJECT_MAP.md)**: жизненный цикл задачи, раскладка на
диске, все подсистемы и файлы с их ролями, тестовые проекты и скрипты. Начинай с неё, когда нужно
понять, где что лежит. Ниже — короткая выжимка для быстрой ориентации.

```
AgentBridgeCli/                     .NET 8 CLI `agentbridge` — клиент моста
AgentBridgeCli.Tests/               Тесты CLI: обычная консоль (Program.cs), без xUnit
AgentBridgeRecovery.Tests/          Настоящий код восстановления сцен с управляемыми Unity callbacks;
                                    регрессии повторного входа, ожидания cleanup и повторной финализации
AgentBridgeCompile.Tests/           Production executor/fingerprint/cache с управляемыми compilation callbacks;
                                    dotnet run --project AgentBridgeCompile.Tests -c Release
AgentBridgeCoordination.Tests/      Тесты coordination-v1/evidence-v1: консоль, `--group state|store|all`,
                                    дочерние процессы для гонок и обрывов
AgentBridgeUnity/                   Unity 2022.3.62f2 — хост-проект пакета
  Assets/Tests/Editor/              EditMode-тесты моста (probe, guardrail, arbiter)
  Assets/Tests/PlayMode/            PlayMode-тесты
  Packages/com.elmortem.agentbridge/  ← сам пакет, основная кодовая база
  ProjectSettings/AgentBridge.json  Настройки моста (таймауты, политики сцен, KeepCompletedCount)
unity-bridge-plugin/                Плагин Claude Code: скиллы unity-bridge и unity-ui
Docs/                               PROJECT_MAP.md (полная карта), шаблоны UNITYAGENT*.md, правила,
                                    заметки, TDD-документы
scripts/                            build-plugin.ps1, fetch-roslyn.ps1, install-agentbridge.ps1/.sh,
                                    verify-coordination.ps1 (живая приёмка coordination-v1 через CLI)
.github/workflows/                  agentbridge-cli.yml (релиз CLI), release-contract.yml (версии + ZIP)
```

### CLI (`AgentBridgeCli/`)

Команды: `csharp`, `ui`, `sceneshot`, `compile`, `tests`, `play`, `stopplay`, `release`, `wait`,
`status`, `doctor`, `coord`, `queue`, `cancel <TaskId>`. Общие флаги: `--project`, `--wait`, `--format json|human`, `--session`,
`--note`, `--coord-window`, `--coord-step`. Неизвестный флаг — ошибка использования, а не позиционный
аргумент. Коды выхода: `0` успех операции/исполнения или принятие с `--wait 0`, `1` отказ
(включая `test_failure`, `stale_input`, `evidence_unavailable`), `2` бюджет клиента исчерпан,
`3` проект/диспетчер недоступен, файловая ошибка или ошибка использования. Принятая задача при
выходе 2 сохраняет Id; ответ содержит `Accepted: true`, команду продолжения и `EditorState`.
Недоступность редактора не запрещает постановку и операции `queue`/`coord`.

- `AgentBridgeApplication.cs` — диспетчер команд
- `CliOptions.cs` — разбор аргументов и валидация флагов
- `BridgeClient.cs` — постановка через диспетчер и ожидание результата с отдельным EditorState
- `QueueClient.cs`, `QueueServer.cs` — скрытый автозапуск отдельного CLI-процесса на проект и файловые сообщения; сервер единолично меняет очередь и координацию
- `QueueRuntime.cs` — снимок исполняемых файлов CLI по хешу содержимого в системном временном каталоге `AgentBridgeQueueRuntime`, чтобы долгоживущий сервер не блокировал обновление/пересборку CLI на Windows. Новый клиент обновляет `launch.json`; при смене runtime сервер уступает владение между транзакциями.
- `DispatcherHostPolicy.cs` — чистые решения о владельце диспетчера: при живом редакторе с `dispatcher-host-v1` диспетчер держит редактор на своей машине, клиент/сервер на другой ОС его не запускает и уступает
- `QueueTaskStore.cs`, `QueueTask.cs` — устойчивые запросы/payload, claims, намерения отмены, порядок и результаты в `Library/AgentBridge/Queue/`
- `QueueDiscussionStore.cs` — переговоры о прогонах дольше 300 с, 15 с на ответ, бронь и уведомления; `QueueDiscussionTests.cs` проверяет гонки, повторную подачу и возврат из ожидания. `queue reserve/request-stop/respond/inbox`, `--expected-seconds`, `--reservation` и курсоры обсуждений обслуживает внешний диспетчер. Отмена по возрасту без переговоров запрещена.
- `QueueCommands.cs` — `queue list`, `status <id>`, `cancel <id>`, `move <id> [before-id]`, `shutdown`; работают без Unity
- `BridgeInspector.cs` — `status` / `doctor`, фатальные `Problems` и нефатальные `Warnings`
- `TaskResultFormatter.cs` — вывод `json` (стабильный контракт) и `human`
- `ProjectLocator.cs`, `BridgePaths.cs` — поиск проекта и раскладка `Library/AgentBridge/`
- Пробуждение уснувшего редактора: `WakePolicy.cs` (чистое решение, точка тестирования),
  `WakeAction.cs`, `EditorWakeAttempts.cs`, `EditorWaker.cs` (`WM_NULL`, фокус-тычок как крайняя мера)
- `ManualPlayPolicy.cs` — решение о захвате ничейного плеймода: CLI сам гасит его перед задачей агента
- `TelemetryLog.cs` — клиентская половина телеметрии в `Logs/AgentBridge-client-*.jsonl`;
  включённость берётся из `status.json`, а не из настроек проекта
- Координация: `CoordinationCommands.cs` (группа `coord`, мутации через диспетчер без зависимости
  от status/heartbeat редактора), `CoordinationJsonCodec.cs` (System.Text.Json, строгий разбор scope/plan),
  `CoordinationResultFormatter.cs`
- Готовые пакеты: `coord submit` (синоним `coord request`) сохраняет PayloadFile и сразу регистрирует все TaskIds, включая будущие шаги. `CoordinationBatch.cs` проверяет готовность и выдаёт стабильные id; `CoordinationBatchPump.cs` публикует шаги, сверяет журнал после обрывов и закрывает окно после восстановления. При закрытии пакета неисполненные шаги получают терминальные результаты. `scripts/verify-coordination-batches.ps1` проверяет frozen payload, fail-fast и compile/EditMode/PlayMode/compile через реальную Unity.
- Совместимость: обновляй CLI и пакет вместе. `independent-queue-v1` требуется исполнителю для запуска принятых задач; старый пакет оставляет их в очереди. Приём и координационные мутации не блокируются отсутствующим capability в status.json.
- Известная ошибка Unity compiler включает `InputRepairPending`: input edits проходят перед заблокированными validation-пакетами; `edit-end` или новый успешный цикл снимает блокировку. Та же живая проверка воспроизводит double→float и проверяет исправление перед двумя уже ожидающими соседними прогонами без их отмены.

### Пакет (`AgentBridgeUnity/Packages/com.elmortem.agentbridge/Editor/`)

- Ядро: `AgentBridge.cs` (bootstrap), `TaskCoordinator.cs` (выбор и жизненный цикл исполнения Unity —
  самый крупный файл), `AgentSessionScheduler.cs` (ротация между агентскими сессиями),
  `QueueEditorAdapter.cs` (неблокирующие запросы диспетчеру), `Queueing/` (общие с CLI транспорт и DTO)
- Исполнители: `CSharpTaskExecutor.cs`, `CompileTaskExecutor.cs`, `AgentTestRunner.cs`,
  `SceneShot/SceneShotTaskExecutor.cs`, `Ui/UiTaskRunner.cs`
- Отмена тестов: `TestRunLifecycle.cs` хранит владельца, дедлайн и состояние остановки сквозь domain reload; `TestRunnerCancellation.cs` изолирует совместимость с API разных версий Test Framework. `scripts/verify-test-cancellation.ps1` проверяет живую отмену, восстановление и запуск следующей задачи.
  `scripts/verify-inert-test-controller.ps1` проверяет вне NUnit, что оставшийся PlayMode-контроллер не блокирует свободный Edit Mode. Внутри NUnit такую проверку делать нельзя: сам проверочный прогон делает TestRunner активным.
  После правок восстановления запускай `dotnet run --project AgentBridgeRecovery.Tests -c Release` (входит в Release Contract) и `scripts/verify-scene-recovery.ps1` в свободном Unity-хосте. Живая проверка требует продвижения очереди после двух успешных PlayMode-прогонов, а не только зелёного результата NUnit.
- Компиляция: `RoslynResolver.cs`, `RoslynCompiler.cs`, `ReferenceCatalog.cs`,
  `SourceGuardrail.cs` (отклонение блокирующих и модальных API до исполнения)
- Координация: общая папка `Coordination/` (namespace `AgentBridge.Coordination`, без UnityEngine —
  эти же исходники компилирует CLI): `CoordinationEngine.cs` (чистая машина состояний),
  `CoordinationFileStore.cs` (межпроцессная транзакция), `CoordinationWaiter.cs`,
  `CoordinationPathPolicy.cs`, `CoordinationScope.cs`, `CoordinationPlanRules.cs`, DTO по файлам,
  `SharedFile.cs` (запись/чтение файлов моста между процессами — любой новый читатель файлов
  редактора в CLI обязан идти через него, иначе ломает `File.Replace` на Windows).
  Адаптеры вне общей папки: `CoordinationUnityCodec.cs` (JsonUtility), `CoordinationEditorAdapter.cs`
  (чтение снимка и запросы внешнему диспетчеру, подтверждение окна), `CoordinationGate.cs`
  (подтверждение принятого hash перед запуском, кэшем и присоединением). Unity не держит блокировку
  диспетчера/координации. Отмена до claim терминальна; после claim — только намерение до остановки
  исполнителя и восстановления сцен. Защита чужой выполняющейся задачи в первые 300 секунд сохраняется.
- Достоверность: `EvidenceRecord.cs`, `ValidationInputSnapshot.cs` (SHA-256 содержимого входов),
  `ValidationInputMonitor.cs` (окно над общими наблюдателями; `OpenAsync` для ежесекундных путей),
  `InputWatchHub.cs` + `InputWatchRoot.cs` + `InputWatchHubLifetime.cs` (один `FileSystemWatcher` на
  корень входов на весь редактор), `ValidationEvidence.cs`, `EvidenceClassification.cs`,
  `InputStatManifest.cs` + `InputStatEntry.cs` + `InputStatVerdict.cs` (второй свидетель:
  путь/размер/mtime входов в файле, переживающем domain reload)
- Кэш: `CompileFingerprint.cs`, `CompileCacheStore.cs`, `TestFingerprint.cs`, `TestCacheQuery.cs`,
  `TestRunDumpStore.cs` (test-cache-v2: отдельные entry-файлы и атомарный индекс),
  `TestCacheIndex.cs`, `TestCacheEntryInfo.cs`
- Сцены и плей мод: `SceneSafetyGuard.cs`, `SceneDirtyWatcher.cs`, `AgentSceneManager.cs`,
  `PlaySessionManager.cs`, `PlayModeSceneRecovery.cs`, `UnsanctionedPlayGuard.cs`, `FocusGuard.cs`
- Тик и пробуждение: `EditorTickPump.cs` (единственный владелец будильника, `ShouldSignal`),
  `AgentEditorWakeTimer.cs` (выбор backend, `SetTimer` как fallback), `BackgroundTickTimer.cs`
  (независимый короткий вызов потокобезопасного `SignalTick`, остановка до reload), `InteractionModeProbe.cs`
- Протокол: `BridgePaths.cs`, `BridgeStatusWriter.cs`, `TaskJournal.cs`, `TaskRecord.cs`
- Выбор тестов: `TestNameResolver.cs` разрешает полные/короткие имена по полному каталогу Unity; `TestFilterCoverage.cs` проверяет покрытие кэша по тестовым случаям. `no_tests_matched` и `ambiguous_test_filter` — терминальные ошибки тестов, CLI возвращает `1`.
- Телеметрия: `TelemetryLog.cs` (запись JSONL в `Logs/`, ротация по суткам), `TelemetryJson.cs`
  (конверт строки и экранирование), `TelemetryField.cs`
- `Roslyn~/` — вендоренный Roslyn (тильда прячет папку от импорта Unity), обновляется
  `scripts/fetch-roslyn.ps1`; лицензии в `Roslyn~/THIRD-PARTY-NOTICES.md`
- `UNITYAGENT.md` — описание API пакета для агента в чужом проекте

### Плагин (`unity-bridge-plugin/`)

```
.claude-plugin/plugin.json          имя, версия, автор, описание
skills/unity-bridge/SKILL.md        C#-таски, compile, tests, sceneshot, play
skills/unity-ui/SKILL.md            декларативная вёрстка uGUI через *.ui.json
unity-bridge-plugin.zip             собранный артефакт, закоммичен и проверяется в CI
```

## Обязательные правила

### 1. Frontmatter скиллов: `description` ≤ 1024 символов

Полная инструкция и порядок проверки: **[`Docs/rules/skill-frontmatter.md`](Docs/rules/skill-frontmatter.md)**.

Коротко: `description` ≤ 1024 символов, `name` ≤ 64 символа и равен имени каталога скилла, только
однострочные пары `ключ: значение` — никаких блочных скаляров. Иначе агент-хост откажется грузить
скилл с ошибкой `field 'description' in SKILL.md must be at most 1024 characters`, хотя сам ZIP
будет валидным.

Правил недостаточно, поэтому проверка встроена в `scripts/build-plugin.ps1` и валит сборку
(`frontmatter_validation`). **Никогда не обходи её через правку лимитов в скрипте** — режь описание.

### 2. После изменений — поднять версию и собрать плагин

Каждый изменённый компонент обязан увеличить **свою** версию. Это fail-closed: и
`scripts/build-plugin.ps1`, и GitHub Action **Release Contract** отклоняют изменение пакета, плагина
или CLI без большей версии.

Порядок для любой правки:

1. Определи, какие из трёх компонентов задеты (`AgentBridgeCli/*`, `AgentBridgeCli.Tests/*` → CLI;
   `AgentBridgeUnity/Packages/com.elmortem.agentbridge/*` → пакет;
   `unity-bridge-plugin/.claude-plugin/*` или `unity-bridge-plugin/skills/*` → плагин).
   Особый случай: `AgentBridgeUnity/Packages/com.elmortem.agentbridge/Editor/Coordination/*` и `Editor/Queueing/*` живут в
   пакете, но компилируется и в CLI через `<Compile Include>` — правка там задевает **оба**
   компонента, обе версии поднимаются.
2. Подними версию каждого задетого компонента в его файле версии (таблица выше). Багфикс — patch,
   новое поведение — minor.
3. Пересобери плагин — **всегда**, даже если менялся только Unity-пакет или CLI: ZIP закоммичен, и
   сборка заодно прогоняет проверки версий и frontmatter.

   ```powershell
   powershell -NoProfile -ExecutionPolicy Bypass -File scripts/build-plugin.ps1
   ```

   Успешный прогон заканчивается `invalid_entries=0` и `zip_validation=PASS`.
   `-ValidateOnly` проверяет, ничего не перезаписывая; `-BaseRef <ref>` задаёт базу сравнения версий
   (по умолчанию `HEAD`, то есть рабочее дерево против последнего коммита).

4. Прогони тесты CLI, если трогал CLI:

   ```bash
   dotnet build AgentBridgeCli/AgentBridgeCli.csproj -c Release
   dotnet run --project AgentBridgeCli.Tests/AgentBridgeCli.Tests.csproj -c Release
   ```

   При правке Windows-установщика после сборки CLI запускай
   `powershell -NoProfile -ExecutionPolicy Bypass -File scripts/verify-installer.ps1`.
   Он проверяет обе сборки, откат и замену exe при работающем клиенте; входит в Windows CI.
   Запуск установщика из редактора проверяет EditMode-класс `CliUpdaterTests` через мост.

5. Прогони тесты координации, если трогал `Editor/Coordination/`, evidence или кэш тестов.
   Папка `Coordination/` компилируется и Unity, и CLI — правка в ней задевает оба компонента:

   ```bash
   dotnet run --project AgentBridgeCoordination.Tests/AgentBridgeCoordination.Tests.csproj -c Release -- --group all
   ```

   Успешный прогон заканчивается `Coordination: PASS` и перечнем покрытых сценариев.

**Никогда не собирай ZIP через `Compress-Archive`.** На Windows он пишет обратные слэши в имена
записей, и потребитель падает с `Zip file contains path with invalid characters`. Канонический
скрипт пишет прямые слэши, отклоняет `\`, абсолютные пути, `..`, дубликаты и запрещённые в Windows
символы, затем сверяет хэш каждого файла в архиве с исходником.

Перед упаковкой скрипт приводит CRLF к LF в UTF-8 Markdown/JSON исходниках плагина: Git уже требует LF, но редактор может снова записать CRLF. `-ValidateOnly` ничего не меняет и отклоняет такие исходники. После исправлений упаковки проверяй архив на свежем Git checkout: локальная проверка сама по себе не доказывает совпадение с байтами в Git.

### 3. Документация и карта проекта — решай сам по масштабу правки

Обновление документации входит в задачу и не выносится в отдельный вопрос заказчику. Реши сам, что
задето, и обнови это в том же изменении. Это не проверяется автоматикой — единственная защита здесь
твоё суждение.

Что смотреть по масштабу:

| Масштаб правки | Что обновить |
|---|---|
| Внутренний рефактор без смены поведения | ничего; только код и, если нужно, комментарии |
| Новое или изменённое поведение, флаг, поле статуса, код ошибки, предупреждение | `README.md`; при изменении контракта задач и диагностики — `unity-bridge-plugin/skills/*/SKILL.md` и `AgentBridgeUnity/Packages/com.elmortem.agentbridge/UNITYAGENT.md` |
| Новая команда CLI, новый kind задачи, смена кодов выхода | всё перечисленное выше плюс `Docs/PROJECT_MAP.md` и раздел «Карта проекта» в этом файле |
| Новый файл с самостоятельной ролью, переезд или удаление файла | `Docs/PROJECT_MAP.md`; выжимка в этом файле — только если задета папка верхнего уровня. Перечень существует ради навигации, а не полноты — мелкий хелпер рядом с уже упомянутым файлом в него не добавляется |
| Изменение процесса сборки, релиза или обязательных правил | этот файл и раздел `## Releasing` в `README.md` |

Кому что адресовано: `README.md` — человеку, ставящему и настраивающему мост; `SKILL.md` — агенту,
работающему через CLI; `UNITYAGENT.md` пакета — агенту в чужом проекте, где нет этого репозитория;
`CLAUDE.md` — агенту, правящему сам мост. Один и тот же факт формулируется под своего читателя, а не
копируется дословно.

Правка `unity-bridge-plugin/skills/*` или файлов пакета — это правка компонента: версия поднимается
и плагин пересобирается по правилу 2, включая случай, когда изменилась только документация.

### 4. После ТДД и любой значимой правки — обновить `Docs/PROJECT_MAP.md`

Полная карта проекта — [`Docs/PROJECT_MAP.md`](Docs/PROJECT_MAP.md). Она читается первой, когда надо
сориентироваться в коде, поэтому устаревшая карта вреднее отсутствующей. Закрывая ТДД или внося
любую другую правку, реши сам, задета ли карта, и обнови её **в том же изменении** — это часть
задачи, а не отдельный вопрос заказчику.

Обновить карту обязательно, если правка:

- добавила, удалила, переименовала или переместила файл с самостоятельной ролью;
- добавила или удалила подсистему, папку, тестовый проект, скрипт в `scripts/`;
- добавила команду CLI, kind задачи, изменила коды выхода или контракт `--format json`;
- изменила раскладку на диске (`Library/AgentBridge/`, `Logs/`, `ProjectSettings/`);
- изменила жизненный цикл задачи или перенесла ответственность между файлами;
- изменила процесс сборки, релиза или состав компонентов.

Обновлять не нужно при внутреннем рефакторе без смены ответственности, правке текста документации,
добавлении мелкого хелпера рядом с уже описанным файлом или добавлении тестового случая в
существующий тестовый файл.

Карта — не компонент: её правка сама по себе не поднимает версии и не требует пересборки плагина.
Автоматической проверки у карты нет, единственная защита здесь — твоё суждение.

## Соглашения

- Отступы — табы, и в C#, и в PowerShell, и в JSON пакета. `.editorconfig` в репозитории нет.
- Namespace CLI — `AgentBridge.Cli`, file-scoped. Код пакета — namespace `AgentBridge`.
- Пакетный код живёт только в `Editor/`: asmdef `AgentBridge` собирается под `includePlatforms: ["Editor"]`.
- Документы проектирования — `Docs/tdd/YYMMDD-HHMM-TDD-<slug>.md`, по-русски, с первой строкой
  `Status: ...`. Выполненные переезжают в `Docs/tdd/done/`. Заметки — `Docs/notes/YYMMDD-HHMM-NOTE-<slug>.md`.
- Сообщения коммитов — как в истории: `Version <plugin> Unity Agent Version <package> AgentBridge CLI Version <cli>`
  плюс строка с сутью изменения. В сообщение попадают только те версии, которые реально поднимались.
- Ветка разработки — `roslyn-cli`; оба workflow слушают `main` и `roslyn-cli`.
- Артефакты Unity (`Library/`, `Temp/`, `Logs/`, `obj/`) не трогать и не коммитить.
