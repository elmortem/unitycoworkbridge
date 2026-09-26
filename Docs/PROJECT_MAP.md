# Карта проекта Unity Agent Bridge

Навигационная карта репозитория: что где лежит, за что отвечает и как части связаны между собой.
Адресована агенту и разработчику, правящим сам мост. Правила работы (версии, сборка плагина,
обязательные проверки) — в [`../CLAUDE.md`](../CLAUDE.md), пользовательская документация — в
[`../README.md`](../README.md).

Карту нужно поддерживать в актуальном состоянии: см. раздел
[«Когда обновлять карту»](#когда-обновлять-карту) в конце файла.

---

## 1. Что это за система

Unity Agent Bridge соединяет ИИ-агента с **живым** Unity Editor. Агент не запускает Unity в batch —
он кладёт задачу в очередь уже открытого редактора, пакет внутри редактора выполняет её на главном
потоке и возвращает логи, диагностику и артефакты.

Обмен идёт **через файлы на диске**, а не через сокет: агентский CLI и редактор — два независимых
процесса, каждый из которых может упасть или перезапуститься (domain reload), поэтому весь протокол
устроен как журнал и атомарные записи в `Library/AgentBridge/`.

```
┌──────────────┐   task.json + payload   ┌──────────────────────┐
│  агент / CLI │ ──────────────────────► │ Library/AgentBridge/ │
│ `agentbridge`│ ◄────────────────────── │  Inbox / Journal     │
└──────────────┘   record.json + логи    └──────────────────────┘
                                                    ▲
                                                    │ тик редактора
                                          ┌─────────┴──────────┐
                                          │  Unity Editor      │
                                          │  пакет AgentBridge │
                                          │  Roslyn в памяти   │
                                          └────────────────────┘
```

### Три версионируемых компонента

| Компонент | Каталог | Файл версии | Как доставляется |
|---|---|---|---|
| AgentBridge CLI | `AgentBridgeCli/` | `<Version>` в `AgentBridgeCli.csproj` | GitHub Release, workflow `agentbridge-cli.yml` |
| Unity-пакет | `AgentBridgeUnity/Packages/com.elmortem.agentbridge/` | `version` в `package.json` | UPM по git URL |
| Плагин агента | `unity-bridge-plugin/` | `version` в `.claude-plugin/plugin.json` | ZIP закоммичен в репозиторий |

Каталог `Editor/Coordination/` физически лежит в пакете, но **компилируется и CLI** (через
`<Compile Include>` в csproj). Правка там задевает оба компонента — обе версии поднимаются.

---

## 2. Дерево верхнего уровня

```
AgentBridgeCli/                    .NET 8 консольное приложение `agentbridge` — клиентская половина моста
AgentBridgeCli.Tests/              Тесты CLI (обычная консоль, без xUnit)
AgentBridgeCompile.Tests/          Production-код компиляции/кэша с управляемыми compilation callbacks
AgentBridgeCoordination.Tests/     Тесты coordination-v1 / evidence-v1, включая дочерние процессы
AgentBridgeRecovery.Tests/         Тесты восстановления сцен с управляемыми Unity callbacks
AgentBridgeUnity/                  Unity 2022.3.62f2 — хост-проект пакета и место прогона EditMode/PlayMode тестов
unity-bridge-plugin/               Плагин Claude Code: скиллы unity-bridge и unity-ui
Docs/                              Шаблоны UNITYAGENT*.md, правила, TDD-документы, заметки, эта карта
scripts/                           Сборка плагина, вендоринг Roslyn, установщики, живые приёмочные проверки
.github/workflows/                 agentbridge-cli.yml (релиз CLI), release-contract.yml (версии + ZIP)
.claude/                           Настройки агента в этом репозитории (hooks/bash-guard.sh, settings.local.json)
```

---

## 3. Жизненный цикл задачи

Понимание этой цепочки объясняет, зачем существует большинство файлов пакета.

1. **Постановка.** CLI (`BridgeClient`) проверяет здоровье моста (`BridgeInspector`), генерирует id
   (`TaskIdGenerator`), пишет `Inbox/<id>.task.json` и, если нужно, файл полезной нагрузки
   (`<id>.cs`, `<id>.ui.json`, `<id>.sceneshot.json`).
2. **Пробуждение редактора.** Уснувший редактор не тикает. `WakePolicy` решает, что делать,
   `EditorWaker` шлёт `WM_NULL`, а внутри редактора `EditorTickPump` + `BackgroundTickTimer`
   продолжают качать тик даже без фокуса.
3. **Приём.** `TaskCoordinator` на тике сканирует `Inbox`, читает запросы (`TaskRequestReader`),
   упорядочивает (`TaskQueueOrder`), выбирает следующую с учётом сессии
   (`AgentSessionScheduler`) и координации (`CoordinationGate`).
4. **Преflight.** Проверки перед исполнением: безопасность сцен (`SceneSafetyGuard`,
   `SceneDirtyWatcher`), плеймод (`PlaySessionArbiter`, `UnsanctionedPlayGuard`), валидность
   исходников проекта (`SourceImportVerifier`), запрещённые API в скрипте (`SourceGuardrail`).
5. **Кэш.** Для `compile` и `tests` сначала ищется готовый результат по отпечатку входов
   (`CompileFingerprint`/`CompileCacheStore`, `TestFingerprint`/`TestCacheQuery`), выдаёт его
   `CachedResultServer` без запуска редактора. Дорогая часть — не поиск, а доказательство: пока
   дешёвый отпечаток источников не назвал ни одной записи-кандидата, `CacheLookupWitness` не
   открывается и дайджест входов не считается вовсе. Появился кандидат — свидетели открываются один
   раз за скан, и выдача `tests` требует чистого stat-манифеста вокруг обоих замеров дайджеста, а не
   только их совпадения. Промах запоминает `CacheMissMemo` по отпечатку источников и набору
   кандидатов: пока оба держатся, дайджест пересчитывается не чаще раза в 10 секунд.
6. **Исполнение.** Один из исполнителей: `CSharpTaskExecutor` (Roslyn в памяти),
   `CompileTaskExecutor`, `AgentTestRunner`, `SceneShotTaskExecutor`, `UiTaskRunner`.
7. **Достоверность.** У прогона два свидетеля. Первый — `ValidationInputMonitor`: пока задача идёт,
   он следит, что входы не изменились; сам наблюдатель общий, `InputWatchHub` держит по одному
   `FileSystemWatcher` на корень входов, а монитор лишь открывает над ним окно со своими
   исключениями и своим счётчиком. Его счётчик переносится через domain reload:
   `ValidationEvidence.CarryAcrossReload` на `beforeAssemblyReload` складывает события в
   `SessionState` до того, как наблюдатель умрёт вместе с доменом. Второй свидетель —
   `InputStatManifest`: путь, размер и mtime тех же входов, снятые на рабочем потоке при подготовке,
   сохранённые в `Library/AgentBridge/evidence-manifest-<TaskId>-<попытка>.txt` и сравнённые в
   завершении. Он закрывает разрыв, который наблюдатель проспал: под Unity это опрашивающий
   `System.IO.DefaultWatcher`, и между `InputWatchHub.Shutdown` и новой установкой не наблюдает
   никто. Расхождение манифеста — такое же событие `input_changes` (с полем `Source`), потерянный
   манифест — `unknown`. Результат классифицируется (`EvidenceClassification`) и пишется в
   `EvidenceRecord`.
8. **Завершение.** `TaskJournal` атомарно пишет `Journal/<id>.json`, артефакты уходят в
   `Artifacts/<id>/`, `BridgeStatusWriter` обновляет `status.json`.
9. **Выдача.** CLI дожидается записи журнала и печатает результат (`TaskResultFormatter`) в
   `json` или `human`.

### Коды выхода CLI

| Код | Значение |
|---|---|
| `0` | успех |
| `1` | терминальный отказ задачи (`test_failure`, `stale_input`, `evidence_unavailable`, `no_tests_matched`, `ambiguous_test_filter`) |
| `2` | клиентское ожидание исчерпано (задача может продолжать идти) |
| `3` | проект/мост недоступны или ошибка использования, включая неподтверждённый прогон всех тестов (`all_tests_confirmation_required`) |

---

## 4. Раскладка на диске (протокол)

Владелец путей со стороны редактора — `Editor/BridgePaths.cs`, со стороны CLI — `AgentBridgeCli/BridgePaths.cs`.

```
<project>/Library/AgentBridge/          рабочий корень (переживает всё, кроме чистки Library)
	Inbox/                              <id>.task.json + payload (.cs / .ui.json / .sceneshot.json)
	Journal/                            <id>.json — записи результатов задач
	Artifacts/<id>/                     скриншоты, дампы тестов, прочие файлы задачи
	Coordination/                       состояние coordination-v1 (state.json, marker, транзакции)
	TestCacheV2/                        entry-файлы кэша тестов + атомарный индекс
	cli/                                самообновление CLI из редактора
	status.json                         снимок состояния моста для CLI
	heartbeat                           доказательство, что редактор жив
	project-id                          идентификатор проекта (сверка «тот ли проект»)
	compile-cache.json                  кэш результатов компиляции
	test-cache-<mode>.json              легаси-индекс кэша тестов
	evidence-manifest-<id>-<n>.txt      stat-манифест входов прогона, переживающий domain reload
	pending_<id>.json                   задача, переживающая domain reload
	pending-playmode-scene.json         сцены, которые надо восстановить после плеймода
	play-session.json                   владелец текущего плеймода
	scheduler-state.json                ротация агентских сессий

<project>/Logs/AgentBridge-<writer>-<YYYYMMDD>.jsonl   телеметрия, живёт вне Library
<project>/ProjectSettings/AgentBridge.json            настройки моста (таймауты, политики сцен, KeepCompletedCount)
<project>/Temp/AgentBridge/                           временные файлы задач агента
```

Координация принципиально работает только на обычном локальном пути: UNC, сетевые диски, симлинки и
junction'ы отклоняются (`CoordinationPathPolicy`).

---

## 5. AgentBridgeCli/ — клиентская половина

Namespace `AgentBridge.Cli`, file-scoped. Собирается в исполняемый файл `agentbridge`.

### Вход и диспетчеризация

| Файл | Роль |
|---|---|
| `Program.cs` | top-level entry, целиком делегирует в `AgentBridgeApplication` |
| `AgentBridgeApplication.cs` | диспетчер команд: `csharp`, `ui`, `sceneshot`, `compile`, `tests`, `play`, `stopplay`, `release`, `cancel`, `wait`, а также `status`/`doctor`/`coord` |
| `CliOptions.cs` | разбор аргументов и валидация флагов; неизвестный флаг — ошибка использования, а не позиционный аргумент |
| `BridgeConstants.cs` | версия протокола, допуски свежести heartbeat, id пакета |
| `HostPlatform.cs` | определение ОС клиента и хоста (важно для Linux-песочницы агента) |

### Постановка задач и ожидание

| Файл | Роль |
|---|---|
| `BridgeClient.cs` | запись задачи в `Inbox`, ожидание записи в `Journal`, обработка обрывов |
| `TaskRequest.cs` | DTO запроса задачи (то, что попадает в `<id>.task.json`) |
| `TaskIdGenerator.cs` | генерация стабильных id задач |
| `QueuedTaskStatus.cs` | клиентское представление задачи в очереди |
| `BridgeStatus.cs` | клиентская модель `status.json` |
| `BridgePaths.cs` | клиентская раскладка `Library/AgentBridge/` |
| `ProjectLocator.cs` | поиск корня Unity-проекта от `--project` или текущего каталога |
| `JsonSupport.cs` | общие настройки System.Text.Json для CLI |

### Диагностика и вывод

| Файл | Роль |
|---|---|
| `BridgeInspector.cs` | `status` и `doctor`: собирает `BridgeHealth`, различает фатальные `Problems` и нефатальные `Warnings` |
| `BridgeHealth.cs` | модель отчёта здоровья: свежесть heartbeat, живость процесса редактора, совместимость протокола, совпадение проекта |
| `TaskResultFormatter.cs` | форматирование результата в `json` (стабильный контракт) и `human` |
| `TelemetryLog.cs` | клиентская половина телеметрии в `Logs/AgentBridge-client-*.jsonl`; включённость читается из `status.json`, а не из настроек проекта |

### Пробуждение редактора и плеймод

| Файл | Роль |
|---|---|
| `WakePolicy.cs` | чистое решение «будить или нет и как» — главная точка тестирования пробуждения |
| `WakeAction.cs` | перечисление возможных действий пробуждения |
| `EditorWakeAttempts.cs` | учёт уже сделанных попыток, чтобы не долбить редактор |
| `EditorWaker.cs` | реализация: `WM_NULL`, фокус-тычок как крайняя мера |
| `ManualPlayPolicy.cs` | решение о захвате «ничейного» плеймода: CLI сам гасит его перед задачей агента |
| `AllTestsConfirmation.cs` | гейт полного прогона: `tests` без `--test`/`--category`/`--assembly` отклоняется до обращения к редактору (`all_tests_confirmation_required`), пока не передан `--confirm-all` |

### Координация (клиентская часть)

| Файл | Роль |
|---|---|
| `CoordinationCommands.cs` | группа `coord` (`request`/`submit`, `wait`, `ok`, `end`, …); диспетчеризуется **до** требования живого редактора |
| `CoordinationJsonCodec.cs` | строгий разбор scope/plan через System.Text.Json (реализация `ICoordinationCodec`) |
| `CoordinationResultFormatter.cs` | вывод результатов координации |

---

## 6. Unity-пакет — `AgentBridgeUnity/Packages/com.elmortem.agentbridge/`

Namespace `AgentBridge` (координация — `AgentBridge.Coordination`). Весь код живёт в `Editor/`;
asmdef `AgentBridge` собирается под `includePlatforms: ["Editor"]`.

```
Editor/            вся кодовая база пакета
	Coordination/  общая с CLI папка (без UnityEngine)
	SceneShot/     скриншоты сцены
	Ui/            декларативная вёрстка uGUI
Roslyn~/           вендоренный Roslyn; тильда прячет папку от импорта Unity
UNITYAGENT.md      описание API пакета для агента в чужом проекте
package.json       версия и зависимости пакета
```

### 6.1 Ядро и очередь

| Файл | Роль |
|---|---|
| `AgentBridge.cs` | bootstrap: подписка на тик редактора, поднятие подсистем |
| `TaskCoordinator.cs` | самый крупный файл: очередь, выбор задачи, preflight, исполнение, финализация |
| `TaskCoordinator.Queue.cs` | вторая половина partial-класса: работа с очередью |
| `TaskQueueSnapshot.cs` | **наблюдение** очереди без побочных эффектов (не допускает и не отклоняет работу) |
| `TaskQueueOrder.cs` | ручной порядок очереди, переживающий domain reload через `SessionState` |
| `AgentSessionScheduler.cs` | ротация между агентскими сессиями |
| `SchedulerState.cs` / `SchedulerStateStore.cs` | состояние ротации и его персист |
| `SessionContext.cs` / `SessionContextSwitcher.cs` | сохранение и восстановление контекста сессии (сцены) при переключении; анонимная сессия контекста не имеет |
| `MainThreadDispatcher.cs` | перенос работы на главный поток редактора |
| `CoordinatorTiming.cs` | замер длительности этапов координатора |
| `ContentionInfo.cs` | описание конфликта за занятый ресурс |

### 6.2 Протокол и записи задач

| Файл | Роль |
|---|---|
| `BridgePaths.cs` | единственный владелец раскладки на диске |
| `TaskRequest.cs` / `TaskRequestReader.cs` | DTO запроса и его чтение с диска |
| `TaskRecord.cs` / `TaskRecordOutcome.cs` | запись журнала и её исход |
| `TaskJournal.cs` | атомарная запись/чтение/удаление записей журнала, чистка старых |
| `TaskResultData.cs`, `TaskContext.cs`, `TaskTiming.cs` | сопутствующие DTO задачи |
| `TaskDiagnostic.cs` / `TaskDiagnosticList.cs` | диагностика (ошибки, предупреждения) в результате |
| `TaskLogScope.cs` | перехват логов Unity на время задачи |
| `TaskFileHash.cs` / `CachedHash.cs` | хэш пары «task.json + payload» с кэшем по длине и времени записи |
| `PendingTaskInfo.cs` / `QueuedTaskStatus.cs` | представление задачи в очереди |
| `TaskCancellationPolicy.cs` | политика отмены: чужую задачу можно снять только после 300 с (`long-running-tasks-v1`); человек в редакторе политику обходит |
| `BridgeStatus.cs` / `BridgeStatusWriter.cs` | модель и запись `status.json` и `heartbeat`; запись best-effort: сбой не выходит в жизненный цикл задачи, снимок дописывается на следующем тике |
| `ProjectIdentity.cs` | создание и чтение стабильного `project-id` |
| `HostPlatform.cs` | платформа хоста для `status.json` |

### 6.3 Компиляция (Roslyn)

| Файл | Роль |
|---|---|
| `RoslynResolver.cs` | поиск сборок Roslyn (вендоренных или системных) |
| `RoslynLocation.cs` / `RoslynSourceKind.cs` | где и какого происхождения найденный Roslyn |
| `RoslynProbe.cs` | проверка готовности Roslyn для `doctor` |
| `RoslynCompiler.cs` | компиляция скрипта в память, без файлов в `Assets/` и без domain reload |
| `RoslynReflectionHelper.cs` | доступ к Roslyn через рефлексию |
| `ReferenceCatalog.cs` | набор ссылок на сборки проекта для компиляции |
| `SourceGuardrail.cs` | отклонение блокирующих и модальных API **до** исполнения |
| `GuardrailViolation.cs` | описание нарушения guardrail |
| `CompileResult.cs` | результат компиляции |
| `CSharpTaskExecutor.cs` | исполнение скомпилированной задачи на главном потоке |
| `CSharpTaskOutcome.cs` | исход C#-задачи |
| `TaskMethodResolver.cs` | поиск точки входа в скомпилированном коде |
| `DomainTypeResolver.cs` | поиск типа по имени во всех загруженных сборках |
| `CompileTaskExecutor.cs` | задача `compile`: полная компиляция проекта Unity |
| `SourceImportVerifier.cs` | проверка, что все `.cs` под `Assets/` и `Packages/` реально попали в компиляцию |

### 6.4 Кэш и достоверность (evidence-v1)

| Файл | Роль |
|---|---|
| `CompileFingerprint.cs` | отпечаток входов компиляции |
| `CompileInputContext.cs` | сбор корней входов; Unity API читается только на главном потоке, воркер получает неизменяемый снимок |
| `CompileCacheStore.cs` / `CompileCacheEntry.cs` | хранилище `compile-cache.json` |
| `TestFingerprint.cs` | отпечаток входов тестового прогона |
| `TestCacheQuery.cs` | поиск подходящего кэшированного прогона |
| `TestRunDumpStore.cs` / `TestRunDump.cs` | test-cache-v2: отдельные entry-файлы в `TestCacheV2/` и атомарный индекс |
| `TestCacheIndex.cs` / `TestCacheEntryInfo.cs` | индекс кэша тестов |
| `CachedResultServer.cs` | выдача кэшированных результатов `tests`/`compile` без запуска; общий хэш источников считается один раз за скан, свидетели открываются лениво — только при найденном кандидате и один раз за скан, телеметрия `cache_witness`/`cache_skip` |
| `CacheLookupWitness.cs` | оба свидетеля одной проверки кэша: окно наблюдения и stat-манифест входов, открытые и закрытые вместе, без главного потока |
| `CacheMissMemo.cs` / `CacheMissEntry.cs` | откат промахов: пока отпечаток источников и набор кандидатов те же, дайджест не пересчитывается чаще раза в `RetryMs` (10 с); запись снятой задачи забывается |
| `ValidationInputSnapshot.cs` | SHA-256 содержимого входов |
| `ValidationInputMonitor.cs` | окно наблюдения за изменением входов во время прогона (`stale_input`): свои исключения, свой счётчик, свой вердикт поверх общих наблюдателей |
| `InputWatchHub.cs` | один наблюдатель на корень входов на весь редактор: выдача по ссылкам, доживание 30 с после последнего окна, выметание идлящих |
| `InputWatchRoot.cs` | живой `FileSystemWatcher` над одним корнем и рассылка событий подписанным окнам |
| `InputWatchHubLifetime.cs` | гашение хаба на `beforeAssemblyReload` и `quitting`; вердикты открытых окон при этом сохраняются |
| `ValidationEvidence.cs` | сбор корней и исключений для снимка входов; оба свидетеля прогона — наблюдатель и stat-манифест — и перенос счётчика наблюдателя через domain reload |
| `InputStatManifest.cs` | второй свидетель: снимок «путь, размер, mtime» тех же входов, файл в `Library/AgentBridge/`, сравнение двух снимков в вердикт |
| `InputStatEntry.cs` / `InputStatVerdict.cs` | запись манифеста и вердикт сравнения (число изменений и до 16 названных путей) |
| `InputHashJob.cs` | фоновое хэширование; воркер получает только неизменяемые данные |
| `EvidenceRecord.cs` / `EvidenceClassification.cs` | запись и классификация достоверности результата |
| `ContentHash.cs` | SHA-256 через CNG на Windows (Mono-реализация слишком медленная), портируемая — на остальных |

### 6.5 Тесты

| Файл | Роль |
|---|---|
| `AgentTestRunner.cs` | запуск Test Framework, сбор результатов, артефакты |
| `TestRunFilter.cs` | фильтр прогона (режим, сборки, имена) |
| `TestNameResolver.cs` | разрешение полных/коротких имён по полному каталогу Unity; источник `no_tests_matched` и `ambiguous_test_filter` |
| `TestFilterCoverage.cs` | проверка, покрывает ли кэшированный прогон запрошенные тестовые случаи |
| `TestResultAggregator.cs` | свёртка результатов прогона |
| `TestRunResult.cs`, `TestCaseResult.cs`, `TestFailure.cs` | DTO результатов |
| `TestRunCoalescer.cs` | присоединение новой задачи к уже идущему подходящему прогону вместо повторного запуска; окно наблюдения открывается через `OpenAsync` и делит наблюдатель самого прогона |
| `TestRunAttachments.cs` | раздача результата присоединённым задачам; непокрытые фильтром отцепляются |
| `TestRunLifecycle.cs` | владелец прогона, дедлайн и состояние остановки сквозь domain reload; детектор джоба, закончившегося без `RunFinished` (`runtime_error` через 25 с бездействия) |
| `TestRunnerCancellation.cs` | изоляция различий API отмены между версиями Test Framework |

### 6.6 Сцены и плеймод

| Файл | Роль |
|---|---|
| `SceneSafetyGuard.cs` | решение, безопасно ли выполнять задачу в текущем состоянии сцен |
| `SceneDirtyWatcher.cs` / `SceneDirtyScanner.cs` / `SceneDirtyReport.cs` | обнаружение и описание несохранённых изменений |
| `ScenePolicyMode.cs` | режим политики сцен из настроек |
| `AgentSceneManager.cs` | открытие/закрытие сцен для задачи агента |
| `PlayModeSceneState.cs` / `PlayModeSceneRecovery.cs` | сохранение набора сцен до плеймода и его восстановление после; повторный вход и ожидание cleanup — отдельные регрессии |
| `SceneSetupStateConverter.cs` | конверсия `SceneSetup` в сериализуемую форму |
| `PlaySessionManager.cs` / `PlaySessionState.cs` / `PlaySessionStore.cs` | владение плеймод-сессией и её персист |
| `PlaySessionArbiter.cs` / `StopVerdict.cs` | кто имеет право остановить плеймод: `StopOwn`, `StopUnsanctioned`, `StopPreempt`, `RejectForeign`, `RejectTests` |
| `PlaySessionPhases.cs` | фазы входа и выхода из плеймода |
| `UnsanctionedPlayGuard.cs` | плеймод, запущенный человеком мимо моста |

### 6.7 Тик, пробуждение, фокус

| Файл | Роль |
|---|---|
| `EditorTickPump.cs` | единственный владелец будильника, решение `ShouldSignal` |
| `AgentEditorWakeTimer.cs` | выбор backend пробуждения, `SetTimer` как fallback |
| `BackgroundTickTimer.cs` | независимый короткий вызов потокобезопасного `SignalTick`, останавливается до reload |
| `InteractionModeProbe.cs` | чтение `EditorApplication.interactionMode` через рефлексию (свойство есть не во всех версиях) |
| `FocusGuard.cs` / `FocusGuardNative.cs` | не отбирать фокус у человека |
| `UnfocusedWindowShower.cs` | показ окна без захвата фокуса через `ShowPopupWithMode`; при отсутствии API — предупреждение и обычное окно |

### 6.8 Скриншоты сцены — `Editor/SceneShot/`

| Файл | Роль |
|---|---|
| `SceneShotTaskExecutor.cs` | исполнитель задачи `sceneshot` |
| `SceneShotPayloadParser.cs` | разбор `<id>.sceneshot.json` |
| `SceneShotItem.cs` | описание одного снимка |
| `SceneShotPose.cs` / `SceneShotPoseMode.cs` | положение камеры и способ его задания |
| `SceneShotFramer.cs` | кадрирование по целевым объектам |
| `SceneShotResolution.cs` | разрешение снимка |
| `SceneViewGrabber.cs` | собственно захват SceneView в текстуру |

### 6.9 Декларативный UI — `Editor/Ui/`

| Файл | Роль |
|---|---|
| `UiTaskRunner.cs` | исполнитель задачи `ui` из `*.ui.json` |
| `UiJson.cs` | разбор декларативного документа |
| `UiValue.cs` | приведение значений из JSON к типам Unity |
| `UiNodeApplier.cs` | создание и обновление иерархии `RectTransform` |
| `UiComponentSync.cs` / `UiComponentTypes.cs` | синхронизация компонентов узла с декларацией |
| `UiPath.cs` | адресация узлов путём |
| `UiStage.cs` / `UiPrefabStage.cs` | сцена/префаб как цель применения |
| `UiRefEntry.cs` / `UiRefQueue.cs` / `UiWireEntry.cs` | отложенные ссылки и связывание (`deferred refs`) |
| `UiDumper.cs` | обратный дамп существующей вёрстки в JSON |
| `UiScreenshot.cs` / `UiTaskArtifacts.cs` | скриншот результата и прочие артефакты задачи |

### 6.10 Настройки и окна редактора

| Файл | Роль |
|---|---|
| `AgentBridgeSettings.cs` / `AgentBridgeSettingsStore.cs` | модель и чтение `ProjectSettings/AgentBridge.json` |
| `AgentBridgeSetupWindow.cs` | мастер первичной настройки моста |
| `AgentBridgeSetupBootstrap.cs` | `[InitializeOnLoad]`: показывает мастер один раз, молчит в batch-режиме и после явного отказа |
| `AgentBridgeQueueWindow.cs` | окно очереди задач: наблюдение и ручной порядок |
| `CliUpdater.cs` | пункт меню `Tools/Agent Bridge/Update CLI`, тянет установочный скрипт из репозитория |

### 6.11 Телеметрия

| Файл | Роль |
|---|---|
| `TelemetryLog.cs` | запись JSONL в `Logs/`, ротация по суткам |
| `TelemetryJson.cs` | конверт строки и экранирование |
| `TelemetryField.cs` | типизированное поле события |

---

## 7. Координация — `Editor/Coordination/`

Особая папка: namespace `AgentBridge.Coordination`, **никаких `UnityEngine`/`UnityEditor`**, потому
что эти же исходники компилирует CLI. Протокол `coordination-v1` + `coordination-batch-v1` + `coordination-edit-leases-v1`.

Регистрации декларируют scope и могут пересекаться; очередь выдаёт исключительные edit grants только для пересекающихся записей. Непересекающиеся записи параллельны. Дедлайн автоматически закрывает edit grant, старые orphaned edits также освобождаются; выполняющиеся задачи Unity продолжают удерживать окно до завершения и восстановления. Schema 1 атомарно мигрирует в 2 при мутации, чтобы старые CLI не выдавали конфликтующие права. `LeaseScenarios.cs` проверяет idle, очередь пересечений, срок, миграцию и гонку двух процессов.

### Чистое ядро (общее для пакета и CLI)

| Файл | Роль |
|---|---|
| `CoordinationEngine.cs` | машина состояний протокола: заявки, гранты, шаги, завершение |
| `CoordinationState.cs` | сериализуемое состояние координатора |
| `CoordinationRequest.cs`, `CoordinationReply.cs`, `CoordinationCommand.cs` | заявка, ответ, команда |
| `CoordinationGrant.cs`, `CoordinationParticipant.cs` | выданное право и участник |
| `CoordinationPlan.cs`, `CoordinationStep.cs`, `CoordinationStepUse.cs` | план, его шаги и их потребление |
| `CoordinationPlanRules.cs` | правила валидности плана |
| `CoordinationScope.cs` | разбор и сравнение областей (scope) |
| `CoordinationBatch.cs` | готовые пакеты: проверка готовности и выдача стабильных id задач |
| `CoordinationTombstone.cs` | надгробие завершённой заявки |
| `CoordinationMarker.cs` | пишется один раз рядом с состоянием и не чистится рестартом; отличает потерянный `state.json` от проекта, где координатора никогда не было |
| `CoordinationLimits.cs` | версия схемы и лимиты |
| `CoordinationCodes.cs` | коды ошибок и отказов |
| `CoordinationDigest.cs` | SHA-256 и hex |
| `CoordinationText.cs` | нормализация текста |
| `CoordinationFileStore.cs` | межпроцессная транзакция поверх файлов (`ICoordinationStore`) |
| `SharedFile.cs` | общий протокол файлов моста: атомарная запись с коротким повтором и чтение с `FileShare.ReadWrite \| Delete`. Им пишут `status.json`, `heartbeat`, журнал и кэши, им же читает CLI — иначе `File.Replace` в редакторе падает, пока CLI держит файл |
| `CoordinationStoreException.cs` | ошибки хранилища |
| `CoordinationWaiter.cs` | ожидание изменения состояния |
| `CoordinationPathPolicy.cs` | отказ от UNC, сетевых дисков, симлинков и junction'ов |
| `CoordinationSystemClock.cs` | системная реализация `ICoordinationClock` |
| `ICoordinationStore.cs`, `ICoordinationTransaction.cs`, `ICoordinationCodec.cs`, `ICoordinationClock.cs` | границы, через которые подключаются адаптеры Unity и CLI |

### Адаптеры Unity (вне общей папки, в `Editor/`)

| Файл | Роль |
|---|---|
| `CoordinationUnityCodec.cs` | кодек на `JsonUtility` (в CLI его место занимает `CoordinationJsonCodec`) |
| `CoordinationEditorAdapter.cs` | одна неблокирующая попытка блокировки за тик, подтверждение окна |
| `CoordinationGate.cs` | единственный вход для запуска, кэша и присоединения задач под координацией |
| `CoordinationBatchPump.cs` | публикация шагов сохранённого пакета, сверка журнала после обрывов, автозакрытие окна после восстановления редактора |

---

## 8. AgentBridgeUnity/ — хост-проект Unity

Unity 2022.3.62f2. Нужен, чтобы пакет было где компилировать и где гонять живые проверки.

```
Assets/Scenes/SampleScene.unity              сцена для задач и плеймод-проверок
Assets/Tests/Editor/                         EditMode-тесты (asmdef AgentBridge.ProbeTests)
Assets/Tests/PlayMode/                       PlayMode-тесты (asmdef AgentBridge.PlayModeProbeTests)
Packages/com.elmortem.agentbridge/           ← сам пакет
ProjectSettings/AgentBridge.json             настройки моста
ProjectSettings/CoworkBridge.json            настройки предыдущего поколения моста
*.csproj                                     генерируются Unity, в репозитории не редактируются вручную
```

### EditMode-тесты (`Assets/Tests/Editor/`)

| Файл | Что проверяет |
|---|---|
| `AgentBridgeProbeTests.cs` | базовая работоспособность моста |
| `AgentBridgeCoordinationTests.cs` | координация внутри редактора |
| `AgentBridgeEvidenceTests.cs` | достоверность результатов |
| `AgentBridgeTestCacheTests.cs` | кэш тестовых прогонов |
| `AgentBridgeTestSelectionTests.cs` | разрешение имён и фильтров тестов |
| `AgentBridgeWakeTests.cs` | пробуждение редактора |
| `AgentBridgeTelemetryTests.cs` | телеметрия |
| `AgentBridgeHashPerformanceTests.cs` | производительность хэширования входов |
| `PlayModeGuardrailTests.cs`, `PlaySessionArbiterTests.cs` | защита и арбитраж плеймода |
| `QueueTimeoutReproTests.cs` | воспроизведение таймаутов очереди |
| `TaskQueueSnapshotTests.cs`, `TaskQueueWindowTests.cs` | наблюдение очереди и её окно |
| `TestCancellationPolicyTests.cs` | политика отмены задач |
| `UnfocusedWindowTests.cs` | показ окна без захвата фокуса |

### PlayMode-тесты (`Assets/Tests/PlayMode/`)

`AgentBridgePlayModeProbeTests.cs`, `AgentBridgeCoordinationPlayModeTests.cs`,
`AgentBridgeCancellationPlayModeTests.cs`, `AgentBridgeTestSelectionPlayModeTests.cs`.

---

## 9. Автономные .NET тест-проекты

Все четыре — обычные консольные приложения без xUnit: запускаются через `dotnet run`, печатают
перечень сценариев и завершаются ненулевым кодом при провале. Они гоняют **настоящий** production-код,
подменяя только Unity-колбэки, поэтому не требуют открытого редактора.

| Проект | Что покрывает | Как запустить |
|---|---|---|
| `AgentBridgeCli.Tests/` | разбор флагов, форматирование результата, политика пробуждения, клиентская логика | `dotnet run --project AgentBridgeCli.Tests/AgentBridgeCli.Tests.csproj -c Release` |
| `AgentBridgeCoordination.Tests/` | coordination-v1 и evidence-v1; `Harness.cs` — стенд, `Child.cs`/`BatchHost.cs` — дочерние процессы для гонок и обрывов, `Scenarios.cs`/`BatchScenarios.cs`/`HashingScenarios.cs`/`ObserverHubScenarios.cs`/`StatManifestScenarios.cs`/`CacheLookupScenarios.cs`/`SharedFileScenarios.cs` — сценарии | `dotnet run --project AgentBridgeCoordination.Tests/AgentBridgeCoordination.Tests.csproj -c Release -- --group all` (также `--group state\|store`) |
| `AgentBridgeCompile.Tests/` | executor, отпечаток и кэш компиляции с управляемыми compilation callbacks (`Stubs.cs`) | `dotnet run --project AgentBridgeCompile.Tests -c Release` |
| `AgentBridgeRecovery.Tests/` | восстановление сцен: повторный вход, ожидание cleanup, повторная финализация (`EditorStubs.cs`) | `dotnet run --project AgentBridgeRecovery.Tests -c Release` |

Успешный прогон координации заканчивается `Coordination: PASS` и перечнем покрытых сценариев.

---

## 10. unity-bridge-plugin/ — плагин агента

```
.claude-plugin/plugin.json     имя, версия, автор, описание плагина
skills/unity-bridge/SKILL.md   C#-задачи, compile, tests, sceneshot, play, координация
skills/unity-ui/SKILL.md       декларативная вёрстка uGUI через *.ui.json
.gitattributes                 требование LF для исходников плагина
unity-bridge-plugin.zip        собранный артефакт: закоммичен и проверяется в CI
```

Жёсткие требования к frontmatter скиллов (`description` ≤ 1024 символов, `name` ≤ 64 и равен имени
каталога, только однострочные пары) описаны в [`rules/skill-frontmatter.md`](rules/skill-frontmatter.md)
и проверяются `scripts/build-plugin.ps1`. ZIP собирается **только** этим скриптом: `Compress-Archive`
на Windows пишет обратные слэши в имена записей, и потребитель отклоняет такой архив.

---

## 11. scripts/

### Сборка и установка

| Скрипт | Роль |
|---|---|
| `build-plugin.ps1` | канонический сборщик ZIP плагина: валидация frontmatter, сверка версий компонентов, нормализация CRLF→LF, прямые слэши в именах, сверка хэшей. `-ValidateOnly` ничего не пишет, `-BaseRef` задаёт базу сравнения версий |
| `fetch-roslyn.ps1` | обновление вендоренного Roslyn в `Roslyn~/` |
| `install-agentbridge.ps1` / `install-agentbridge.sh` | установка CLI на Windows и на *nix (включая Linux-песочницу агента) |
| `verify-installer.ps1` | проверка установщика |
| `verify-plugin-line-endings.ps1` | проверка окончаний строк в исходниках плагина; воспроизводит редактор, изменивший часть LF-файла |

### Живые приёмочные проверки (требуют открытого Unity)

| Скрипт | Что доказывает |
|---|---|
| `verify-coordination.ps1` | coordination-v1 через реальный CLI и редактор |
| `verify-coordination-batches.ps1` | три пакета без действий клиентов, frozen payload, fail-fast, цепочка compile/EditMode/PlayMode/compile |
| `verify-test-cancellation.ps1` | живая отмена тестового прогона, восстановление и запуск следующей задачи; сценарий `lost` — джоб без `RunFinished` завершается сам |
| `verify-inert-test-controller.ps1` | оставшийся PlayMode-контроллер и сирота `EditModeRunner` не блокируют свободный Edit Mode. Обязательно **вне** NUnit: сам проверочный прогон делает TestRunner активным |
| `verify-scene-recovery.ps1` | нормальный `RunFinished` → domain reload → cleanup, после чего очередь **продвигается** ещё одной задачей; зелёный NUnit сам по себе доказательством не считается |
| `verify-long-running-tasks.ps1` | защита долгой задачи от чужой отмены и снятие защиты после 300 с |
| `verify-immediate-commands.ps1` | немедленные команды на прогретом кэше |
| `verify-compilation.ps1` | поведение параллельных запросов компиляции |
| `verify-coordinator-performance.ps1` | стоимость тика координатора; `-IncludeBlockingBaseline` добавляет базовую линию |
| `verify-dynamic-tmp.ps1` | задачи, работающие с динамически подключаемыми зависимостями (TMP) |

---

## 12. Docs/

```
PROJECT_MAP.md                 этот файл
compilation.md                 заметка об устройстве компиляции
UNITYAGENT-template.md         шаблон UNITYAGENT.md для чужого проекта
UNITYAGENT-UI-template.md      шаблон UNITYAGENT-UI.md (соглашения вёрстки)
rules/skill-frontmatter.md     обязательные требования к frontmatter скиллов
tdd/                           активные ТДД
tdd/done/                      выполненные ТДД
notes/                         заметки расследований и приложенные к ним артефакты
```

Соглашения именования: ТДД — `Docs/tdd/YYMMDD-HHMM-TDD-<slug>.md`, по-русски, первая строка
`Status: ...`; заметки — `Docs/notes/YYMMDD-HHMM-NOTE-<slug>.md`. Выполненные ТДД переезжают в
`tdd/done/`.

---

## 13. CI и релиз

| Workflow | Что делает |
|---|---|
| `.github/workflows/agentbridge-cli.yml` | сборка и публикация релиза CLI |
| `.github/workflows/release-contract.yml` | fail-closed проверка: изменённый компонент обязан поднять свою версию; сверка закоммиченного ZIP плагина |

Оба слушают `main` и ветку разработки `roslyn-cli`.

Кому какая документация адресована:

| Файл | Читатель |
|---|---|
| `README.md` | человек, ставящий и настраивающий мост |
| `unity-bridge-plugin/skills/*/SKILL.md` | агент, работающий через CLI |
| `Packages/com.elmortem.agentbridge/UNITYAGENT.md` | агент в чужом проекте, где этого репозитория нет |
| `CLAUDE.md` | агент, правящий сам мост |
| `Docs/PROJECT_MAP.md` | тот, кому нужно сориентироваться в коде |

---

## Когда обновлять карту

Карта полезна ровно настолько, насколько она правдива. Обновление карты входит в задачу и не
выносится в отдельный вопрос заказчику — это часть той же правки, а не отдельная работа.

**Обязательно обновить карту**, если правка (в том числе выполнение ТДД) сделала хотя бы одно из:

- добавила, удалила, переименовала или переместила файл с самостоятельной ролью;
- добавила или удалила подсистему, папку, тестовый проект, скрипт в `scripts/`;
- добавила команду CLI, kind задачи, изменила коды выхода или контракт `--format json`;
- изменила раскладку на диске (`Library/AgentBridge/`, `Logs/`, `ProjectSettings/`);
- изменила жизненный цикл задачи или перенесла ответственность между файлами;
- изменила процесс сборки, релиза или состав компонентов.

**Обновлять не нужно** при внутреннем рефакторе без смены ответственности, правке текста
документации, добавлении мелкого хелпера рядом с уже описанным файлом или добавлении тестового
случая в существующий тестовый файл.

Перечень файлов существует ради навигации, а не ради полноты: если новый файл — деталь реализации уже
описанной подсистемы, достаточно убедиться, что описание этой подсистемы всё ещё верно.

Автоматической проверки у карты нет. Единственная защита здесь — суждение того, кто вносит правку.
