Status: Выполнено — U1–U7 реализованы, V1–V6 пройдены на живом редакторе (см. «Отчёт исполнения» в конце файла)

# Координация директоров и достоверная приёмка — исполняемое ТДД

## Контекст исполнения

- SpecId: `bridge_coordination_v1`.
- Целевой репозиторий: `D:/Hobby/Repositories/unitycoworkbridge`.
- Тестовый Unity-проект: `D:/Hobby/Repositories/unitycoworkbridge/AgentBridgeUnity`.
- Исходник пайплайна: `D:/Hobby/Repositories/unity-pipeline/plugins/unity-pipeline`.
- Исполнитель: навык `director` этого пайплайна; операции Editor — через установленный `unity-bridge`.
- Прочитать целевые `CLAUDE.md`, `README.md`, `Docs/rules/skill-frontmatter.md` и `scripts/build-plugin.ps1` перед реализацией.
- Нормативный контракт: [coordination-v1](../../plugins/unity-pipeline/references/coordination-v1.md). Он входит в поставку Unity Pipeline. Если ТДД передают отдельно, передать вместе этот файл; реализация без прочитанного контракта не начинается. Конкретные названия команд, состояния, бюджеты и правила несовместимости берутся из него.
- Политика: [проверки](../../plugins/unity-pipeline/references/verification.md), [исполнение](../../plugins/unity-pipeline/references/execution-policy.md).
- Это задание на последующую реализацию. Подготовка данного ТДД не меняет Bridge, его установленный пакет или WaterWalk.
- Предел одного автономного запуска: 120 минут работы; сохранить незавершённое состояние по истечении, не ставить `Выполнено`. Не более двух исправлений одной собственной причины провала. Возобновление продолжает прежние ids и результаты.

## Проблема и результат

Несколько агентов меняют один Unity-проект и выполняют проверки в общем Editor. Существующая сессионная очередь сериализует команды, но не файловые записи. Чужая запись C# может оборвать PlayMode. Кэш хранит только последний прогон режима. `AgentTestRunner` может вернуть success, но не продвинуть кэш при изменившихся исходниках; исполнитель не получает явного запрета принять этот результат.

Нужно обеспечить согласованные права на файлы и короткие окна Editor, независимую от Editor-тика регистрацию/ожидание, защиту от повторной доставки, достоверную маркировку доказательств и несколько кэшированных наборов. Claude и Codex используют одну CLI-поверхность. Сообщения хостов ускоряют реакцию, но не являются источником прав.

Проверяемый пример: A пишет в подсистему X, B в Y. A завершает edit grant и просит проверки. B заканчивает свой текущий пакет, закрывает grant. Только после этого Editor выдаёт A окно. B может читать/планировать, но новый edit grant ожидает. A выполняет конечный план, возвращает Editor; B продолжает без чужого вмешательства в файлы. Пропавший B с активным grant требует восстановления, а не молчаливого отъёма.

## Что уже есть

Проверено по исходникам 2026-09-12; перед изменением сверить актуальность:

| Файл в unitycoworkbridge | Точка интеграции |
|---|---|
| `AgentBridgeCli/AgentBridgeApplication.cs` | Parse/dispatch, health-проверка сейчас предшествует всем рабочим командам. `coord` должен быть доступен до требования живого Editor. |
| `AgentBridgeCli/CliOptions.cs`, `BridgeClient.cs`, `BridgePaths.cs` | Общие флаги, подача/ожидание запросов, пути. Существующий wait не отменяет запрос при exit 2. |
| `AgentBridgeCli/TaskResultFormatter.cs` | Краткий human-вывод; новая validity должна быть видна без разбора JSON агентом. |
| `AgentBridgeUnity/Packages/com.elmortem.agentbridge/Editor/TaskCoordinator.cs` | Выбор, запуск, завершение, cache serving и восстановление после reload. |
| `Editor/AgentSessionScheduler.cs`, `SchedulerStateStore.cs` | Существующая аренда и сцены сессии; не использовать этот single-Editor store как межпроцессный coordination store. |
| `Editor/TaskRequest.cs`, `TaskRecord.cs`, `BridgeStatusWriter.cs` | Поля протокола и capabilities. |
| `Editor/AgentTestRunner.cs`, `TestRunAttachments.cs` | Фиксация результата, продвижение dump, присоединённые запросы. |
| `Editor/TestRunDumpStore.cs`, `TestCacheQuery.cs`, `TestFilterCoverage.cs` | Сейчас один `test-cache-<mode>.json`; расширить поиск без ослабления покрытия. |
| `Editor/CompileFingerprint.cs`, `TestFingerprint.cs` | Текущий source fingerprint хэширует пути/размеры/mtime ограниченных расширений; он не является полным digest входов приёмки. |
| `AgentBridgeCli.Tests/Program.cs` | Существующая консольная регрессия без xUnit; сохранить её запуск. |

Все `Editor/...` в таблице находятся внутри пакета `AgentBridgeUnity/Packages/com.elmortem.agentbridge/`.

## Инварианты

- Изменять только целевой репозиторий Bridge. WaterWalk и установленный пользовательский Bridge не являются тестовой площадкой; не менять их настройки/файлы и не отнимать редактор.
- Сохранить чужую незакоммиченную работу через исходный снимок/дельту; не требовать чистого Git, stash, reset или commit. По пересечению чужого файла договориться с владельцем.
- Без активных coordination-регистраций legacy-команды и коды выхода сохраняются. ProtocolVersion не повышать ради аддитивных полей; совместимость новых возможностей определяется capabilities.
- Любой проект с активным grant имеет не более одного окна; окно и файловый edit grant не сосуществуют. Scope одного участника не пересекается с чужим живым scope.
- Scope не позволяет писать без edit grant. Новый неизвестный писатель ОС не считается управляемым участником; нарушение снимает пригодность доказательства.
- Не вводить daemon, сетевой сервис, MCP, зависимости от конкретного агента, глобальные machine locks или глобальные пользовательские настройки.
- Не отключать AssetDatabase refresh/reload на неопределённый срок и не блокировать Unity main thread ожиданием файлового lock. Не считать истечение deadline отменой исполняющейся задачи.
- Нельзя принимать evidence при неизвестных/менявшихся входах, нулевом покрытии, утерянных обязательных артефактах, aborted/inconclusive или отсутствующем человеческом решении.
- Пакетный код остаётся Editor-only, совместимым с Unity 2022.3 и 6.4; CLI остаётся net8.0.

## Foundations

### Общий код без Unity

Новые типы положить в `AgentBridgeUnity/Packages/com.elmortem.agentbridge/Editor/Coordination/`, namespace `AgentBridge.Coordination`. Один публичный тип на файл. CLI подключает эти исходники через `<Compile Include="../AgentBridgeUnity/Packages/com.elmortem.agentbridge/Editor/Coordination/**/*.cs" LinkBase="Coordination" />`; никаких копий реализации. Код этой папки не ссылается на UnityEngine/UnityEditor и не использует API новее доступных в Unity 2022.3 .NET Standard 2.1. Платформенные/JSON операции вводятся через интерфейсы.

Сериализуемые DTO — `[Serializable]`, public fields, массивы/List вместо словарей, даты — UTC Unix ms, ids/диджесты — строки. Unity использует JsonUtility в отдельном адаптере вне общей папки; CLI — System.Text.Json с IncludeFields. Сериализация сохраняет одни и те же данные, сравнение контрактов тестируется в обоих адаптерах.

Основные файлы и сигнатуры:

```csharp
public interface ICoordinationClock { long UtcNowMs { get; } }
public interface ICoordinationCodec
{
    string Serialize(CoordinationState state);
    CoordinationState Deserialize(string json);
}
public interface ICoordinationTransaction : IDisposable
{
    CoordinationState State { get; }
    void Commit();
}
public interface ICoordinationStore
{
    CoordinationState Read();
    bool TryBegin(out ICoordinationTransaction transaction);
}
public sealed class CoordinationEngine
{
    public CoordinationReply Apply(CoordinationState state, CoordinationCommand command, long nowMs);
    public CoordinationReply Inspect(CoordinationState state, string session, string requestId, long nowMs);
}
```

Engine — чистое преобразование состояния, без чтения файлов/Unity и ожидания. DTO `CoordinationState`, `CoordinationCommand`, `CoordinationReply`, `CoordinationParticipant`, `CoordinationRequest`, `CoordinationGrant`, `CoordinationPlan`, `CoordinationStep` — по отдельным файлам. Точный внешний JSON и флаги определены нормативным контрактом. Внутреннее состояние включает SchemaVersion=1, ProjectId, Epoch, Revision, NextTicket, Participants, Requests, Grants и tombstones завершённых token/request для идемпотентности.

Участник хранит Session, SpecId, Owner, RepoRoot, Paths, ParticipantGeneration и lifecycle. Запрос хранит id, ticket, owner, kind, payload digest, plan, state, timestamps и terminal reason. Grant хранит owner, request, token, epoch, participant generation, kind, deadline, active task ids и использованные StepIds. Произвольное изменение полей файла агентом не является поддержанным API. Abandon меняет generation только целевой сессии; чужие активные писатели не должны терять учёт права или считаться завершёнными из-за восстановления соседа.

### Межпроцессное хранилище

`CoordinationFileStore.cs` использует отдельный постоянный `transaction.lock` в CoordinationRoot, открытый с FileShare.None на короткую транзакцию read-validate-apply-write. Lock-файл никогда не удаляется для «восстановления». Сериализованный `state.json` записывается во временный файл уникального имени в том же каталоге, Flush с записью на диск, затем атомарная замена/переименование. Нельзя fallback-копированием перезаписывать опубликованный JSON частями. После сбоя сохраняется последний целый снимок; непонятный/повреждённый снимок — `coordination_corrupt`, не автоматический пустой state.

Editor делает одну неблокирующую TryBegin за тик; если lock занят, возвращается в update. CLI использует ограниченный retry с общим пределом 5 секунд; после него `coordination_busy` (exit 3), существующий UUID позволяет безопасно повторить. Не выполнять хэширование больших деревьев, ожидание редактора и отправку сообщений под lock. Перед commit сверять captured revision для решений, подготовленных вне транзакции.

Тест конкурентного открытия несколькими процессами является обязательным условием объявления capability на поддержанной ОС. v1 предназначен для локальной файловой системы. Сетевые/неоднозначные пути отклоняются, а не объявляются защищёнными.

`CoordinationWaiter.cs` ждёт изменения Revision, используя FileSystemWatcher как подсказку и перечитывая snapshot после события/при контрольном интервале 1 секунда. Watcher устанавливается до повторного чтения, чтобы не потерять событие между read и subscribe. Один ответ на meaningful change/timeout; служебные тики/повтор status не увеличивают Revision. CancellationToken прекращает клиентское ожидание, но не изменяет состояние запроса.

### Новые поля существующего протокола

- CLI TaskRequest и пакетный `TaskRequest`: `CoordinationWindowToken`, `CoordinationStepId`.
- `TaskRecord`: `Evidence` типа `EvidenceRecord` с public fields `Validity`, `InputDigest`, `EndInputDigest`, `WindowId`, `Reason`, `ArtifactsPresent`.
- `BridgeStatus`: отдельно аддитивные capabilities `coordination-v1`, `evidence-v1`, `test-cache-v2`; краткое coordination-состояние без чужих tokens.
- Новый terminal status `stale_input` классифицируется exit 1. `unknown` evidence также не является новой успешной приёмкой: терминальный `evidence_unavailable` exit 1 для задач validation-окна. Legacy JSON без Evidence продолжает читаться.

## План реализации

Критерии U проверяются чтением diff/структуры. Написать весь функционал и тестовый код U1–U7 до запуска V. Ранний запуск тестов не требуется. Если отдельная Editor-операция не может быть выполнена без компиляции нового пакета, допустима одна такая компиляция с указанной причиной.

### U1 — Чистая машина состояний и права

- Цель: переходы нормативного протокола детерминированы, права никогда не пересекаются.
- Touch: новая общая папка Coordination; csproj CLI и новый `AgentBridgeCoordination.Tests/AgentBridgeCoordination.Tests.csproj`.
- How: реализовать команды, all-or-nothing scope reservation, FIFO, grants, pause, orphaned, draining, idempotency и проверку epoch. Границы каталогов сравнивать по сегментам: `Foo/` не включает `Foobar/`. Scope expansion при конфликте не частично меняет регистрацию. Начало window с активным edit grant своего владельца — отказ `edit_active`, чтобы избежать самоблокировки. Новые registry-записи во время окна только idle. Terminal tombstones сохранять до leave сессии; после leave старые операции возвращают closed/stale-token, никогда не создают заново прежнее право.
- Реализовать default/edit/window бюджеты и отказ renew после pause_requested. Deadline active task переводит окно в draining; окно освобождается только после terminal active task. Истёкший writer остаётся orphaned. `abandon` в протоколе доступен CLI, но требует явного решения пользователя по навыку; не является автоматическим retry.
- Gate реализации: все операции представлены в engine, у каждой ветки отказа есть стабильный Code и отсутствие частичной записи; no Unity dependencies.
- Отложенные проверки: V1, V2.
- Сбой: не вводить fallback «разрешить, если давно молчит». Сохранить диагностику и восстановить конкретный переход, максимум два исправления причины.

### U2 — Атомарное хранилище и независимое ожидание

- Цель: две CLI и Editor видят одно состояние, обрыв записи не выдаёт второе право.
- Touch: `CoordinationFileStore.cs`, `CoordinationWaiter.cs`, `CoordinationPathPolicy.cs` и интерфейсы общей папки; JSON-адаптеры CLI/пакета вне неё.
- How: реализовать транзакции и waiter по Foundations. Проверить canonical ProjectId и физический путь до открытия store, отказаться от сетевого/aliased/reparse scope. Создание epoch только для действительно отсутствующего состояния; пропавший state при существующем coordination marker требует явного восстановления. Marker хранит ProjectId/Epoch, не очищается при перезапуске Editor. Read может видеть прежнюю целую revision, но выдача grant требует свежей транзакции.
- Crash между созданием temp и replace оставляет старое состояние. Остаточные temp очищать только свои по явно проверенному coordination root; не удалять request/journal другого механизма. Повреждение состояния не подменять пустым.
- Gate реализации: все писатели используют один store, ни один не пишет state напрямую; main-thread код не ждёт lock.
- Отложенные проверки: V1, V2, V3.
- Сбой: блокировка/повреждение даёт диагностический отказ, не reset. Не требовать ручной фокусировки Editor для работы coord-команд.

### U3 — CLI и capability negotiation

- Цель: весь coordination-контракт доступен из любого агента без зависимости от занятого Editor.
- Touch: `AgentBridgeCli/CoordinationCommands.cs`, `CoordinationJsonCodec.cs`, `CoordinationResultFormatter.cs`; существующие `AgentBridgeApplication.cs`, `CliOptions.cs`, `BridgeClient.cs`, `BridgePaths.cs`, модель CLI TaskRequest и help.
- How: dispatch coord после проверки project identity, но до требования здорового Editor. `capabilities` разделяет CLI и пакет; недоступный пакет не объявлять поддерживающим protocol. Парсить команды/JSON строго: unknown поля внешнего command input и неверные flags дают bad_usage; неизвестные аддитивные поля status сохраняют чтение старым CLI. Мутации подтверждаются общей транзакцией, read/wait не запускают Unity-задач и не создают edit grant. Обновить подачу рабочих запросов новыми flags, но сохранять legacy без них.
- Human-ответ: одна строка состояния, blocker/владелец/RequestId/revision и следующий допустимый шаг при ожидании. Не печатать heartbeat каждые секунды. JSON stdout отделён от stderr.
- Gate реализации: help, parser и normative command table совпадают; нет зависимости coord wait/status/register от health.BridgeReady.
- Отложенные проверки: V1, V2, V5.
- Сбой: capability mismatch явно диагностируется. Не обходить его обращением напрямую к внутренним файлам из навыка.

### U4 — Включение окна в Editor scheduler

- Цель: никто не запускает Unity-команду в чужом окне, в том числе через cache/attach.
- Touch: `Editor/CoordinationGate.cs`, `CoordinationUnityCodec.cs`, `CoordinationEditorAdapter.cs`; `TaskCoordinator.cs`, `CachedResultServer.cs`, `AgentSessionScheduler.cs`, `TaskRequest.cs`, `BridgeStatus.cs`, `BridgeStatusWriter.cs`, `TaskRecord.cs`.
- How: связать scheduler с engine через store. Editor подтверждает готовность окна только после завершения активного task, импорта, compile/reload recovery и PlayMode scene recovery. Gate проверяется до cache serving, attach, session rotation и выполнения. Старый in-flight task дренируется; новая queued команда без token при активных регистрациях получает coordination_required, а не запускается. При отсутствии регистраций остаётся прежнее поведение.
- Запуск Step атомарно резервирует StepId и сохраняет TaskId до начала payload. Повтор того же TaskId присоединяется к существующему выполнению; другой TaskId для потреблённого Step даёт step_consumed. Если отказ происходит до начала исполнения, состояние шага хранит терминальный отказ; повтор требует нового окна, не двусмысленного освобождения.
- Window сохраняет ownership сцен через существующий AgentSessionScheduler. `release` больше не может тайно отдать активное окно: в coordinated-mode возвращает window_active с инструкцией finish; чужой release сохраняет прежний harmless not_holder. Окончание coordination-окна освобождает и scheduler lease этого владельца.
- Domain reload сохраняет token/plan/active task в store. После реального Editor restart сохраняются registrations/scopes/edit grants; окна становятся interrupted/draining до восстановления записанных задач. Не выводить Editor из чужого/manual PlayMode автоматически только ради выдачи окна: диагностировать занятость и использовать прежние согласованные правила хоста.
- Gate реализации: все маршруты запуска проходят gate; query/status не берут Unity lease.
- Отложенные проверки: V3, V4, V5.
- Сбой: спорный owner/active task означает draining/blocked, не forced stop. Не менять существующие scene-safety гарантии ради координации.

### U5 — Достоверность входов и результата

- Цель: test success относится к проверенному состоянию; изменение входов не теряется в служебной ветке кэша.
- Touch: `Editor/EvidenceRecord.cs`, `ValidationInputSnapshot.cs`, `ValidationInputMonitor.cs`, `ValidationEvidence.cs`; `AgentTestRunner.cs`, `CompileTaskExecutor.cs`, `TaskCoordinator.cs`, `TestFingerprint.cs`, `TestRunAttachments.cs`; formatter/classification CLI.
- How: новый InputDigest вычисляет SHA-256 содержимого и нормализованных путей полного набора из нормативного контракта. Старый CompileFingerprint можно оставить для legacy compile cache, но нельзя использовать его как полный InputDigest. Настройки сборки/платформы, версия Unity/пакета и разрешённые внешние локальные package roots входят в снимок. Недоступный root означает unknown. Перенос/удаление файла меняет digest. `.meta` участвуют. Не доверять одному размеру/mtime.
- Перед validation-step: дождаться стабильного импорта; получить снимок; установить monitors на все входные roots; повторить снимок, чтобы закрыть окно между снимком и наблюдением. При различии до запуска переустановить один раз; если проект продолжает меняться — отказ evidence_unavailable без запуска дорогого теста. Длинные хэши считать вне main thread по файловым API, затем на main thread подтвердить неизменность import state. Unity API с worker thread не вызывать.
- Во время теста monitor отмечает изменения, включая change-then-revert; overflow/errors означают unknown. На окончании compare снимок, monitor и cleanup fixtures. Изменение любого реального входа даёт stale_input/Validity=stale, даже если NUnit зелёный. Не выдумывать виновника. Temp/Logs/Library и валидные объявленные временные fixture roots исключены; существующее содержимое fixture root перед run запрещает исключение.
- Для compile валидировать снимок после import, а не объявлять stale из-за собственных ожидаемых импортных метаданных до начала проверяемого состояния. Compile failure остаётся failure. Для последующих tests использовать установленное стабильное состояние.
- Подключённые запросы получают тот же stale/unknown терминальный исход без TestRunAttachments.Requeue-цикла. Существующий живой результат не удалять ради автоматического нового прогона. `ArtifactsPresent` считается по фактическим путям, не по наличию массива в JSON.
- Gate реализации: каждый validation terminal result содержит Evidence; stale/unknown не проходят классификацию как success и не продвигают кэш.
- Отложенные проверки: V1, V3, V4, V5.
- Сбой: нельзя заменить полный digest выборочным фильтром текущей фичи без доказанных зависимостей. При неопределённости unknown честнее кэша.

### U6 — Несколько кэшированных наборов

- Цель: A, затем B, затем A с теми же входами не запускают A повторно.
- Touch: `Editor/TestRunDumpStore.cs`, `TestCacheQuery.cs`, `TestRunDump.cs`, `TestFilterCoverage.cs`, `CachedResultServer.cs`, `TestRunAttachments.cs`, новый `TestCacheIndex.cs`.
- How: отдельные immutable entry-файлы и атомарный индекс в `Library/AgentBridge/TestCacheV2/`. До 32 завершённых entries суммарно; LRU eviction. Entry содержит полные результаты по тестам, filter, InputDigest, validity, source id и сведения о сохранности артефактов. Индекс публикуется после entry; при сбое orphan entry не считается результатом. Повреждённая entry исключается из поиска с диагностикой, не роняет весь Bridge.
- Условие hit: exact input digest и режим/платформа, один entry полностью покрывает запрос, нет invalid/skipped обязательного покрытия. Пустой subset не успех. Сохранить текущую корректную семантику имён, категорий и параметризованных тестов. In-flight attach только к покрывающему совместимому запросу, `Fresh` запрещает cache/attach. Из разных digest результаты не соединять.
- Очистка TaskJournal не должна уничтожать собственную копию счётчиков entries. Не обещать вечное сохранение скриншотов: при обязательных отсутствующих artifacts запрос не считается покрытым. Старые однофайловые caches читать только legacy-путём; не мигрировать их как valid evidence-v1.
- Gate реализации: нет единственного глобального слота per mode на новом пути; eviction не удаляет активный run и не трогает чужие payloads.
- Отложенные проверки: V1, V3, V5.
- Сбой: cache miss запускает один штатный запрос по плану; corruption не вызывает бесконечный rerun.

### U7 — Тестовые сценарии, документация и версии

- Цель: подготовлены проверки и эксплуатационные инструкции для всех новых контрактов.
- Touch: новый `AgentBridgeCoordination.Tests/` (консоль net8 без новых внешних пакетов), тесты `AgentBridgeUnity/Assets/Tests/Editor/AgentBridgeCoordinationTests.cs`, `AgentBridgeEvidenceTests.cs`, `AgentBridgeTestCacheTests.cs`; `Assets/Tests/PlayMode/AgentBridgeCoordinationPlayModeTests.cs`; `scripts/verify-coordination.ps1`; README, CLAUDE, пакетный UNITYAGENT, навыки unity-bridge/unity-ui по применимости и файлы версий.
- How: консольный runner имеет `--group state|store|all`; process-child mode для гонок/обрывов/lock без Unity. Каждый тест assert-ит наблюдаемое поведение, а не текст документа. Engine использует fake clock без sleep. FileStore-тесты запускают реальные процессы; остановка только собственных дочерних процессов.
- `verify-coordination.ps1 -Project <host> -Cli <built exe> -Output <absolute temp path>` запускает конечный интеграционный сценарий V5, работает через CLI, сохраняет ids, проверяет counters/actual results и штатно закрывает свои registrations/grants. Не очищает чужое состояние, не меняет глобальный PATH и не обновляет установленный пакет. Если обнаружена чужая работа — ждёт согласования, не убивает её. Временные test fixtures создавать только в зарезервированном `Assets/AgentBridgeCoordinationFixtures/`, runtime probes снимать через Test Runner; `.meta` создаёт Editor.
- Документация объясняет scopes/edit/window, orphan recovery, unsupported/legacy, новые exit statuses и пределы гарантий внешних файловых записей. Установленный навык не предлагает команды без capability check. В human-формате visibility validity обязательна.
- Поднять minor каждого изменённого компонента от актуальной версии, а не слепо присвоить значения из этой спецификации. Пересобрать unity-bridge-plugin.zip штатным скриптом по CLAUDE. Не устанавливать/публиковать обновление без отдельного запроса.
- Gate реализации: код, целевые fixtures, run script и все соответствующие инструкции существуют; версии повышены; coverage table ниже сопоставлена фактическим тестам.
- Отложенные проверки: V1–V6.
- Сбой: дефект harness исправить локально, не заменять реальный интеграционный сценарий самостоятельной симуляцией/ручным Play Mode.

## Матрица обязательных сценариев

| Id | Наблюдаемая проверка |
|---|---|
| C01 | Два непересекающихся scope и edit grants разрешены; пересекающийся scope отклонён без частичного изменения. |
| C02 | A просит окно после edit-end; новые edits ждут; B закрывает active grant; только затем A получает window. |
| C03 | Повтор UUID с тем же payload даёт тот же request/token; иной payload — request_conflict; повтор Step с новым TaskId не выполняется. |
| C04 | Два процесса одновременно запрашивают/подтверждают window: максимум один grant, revisions не теряются. |
| C05 | Убитый собственный child между temp-write и replace не портит опубликованный state; повреждённый JSON не создаёт пустой координатор. |
| C06 | Истёкший writer становится orphaned, следующий window не выдаётся; старый token после abandon отвергается. |
| C07 | Истёкшее окно с живым task переходит draining, следующая задача не стартует до terminal; idle окно закрывается. |
| C08 | Wait видит событие между подпиской и повторным чтением; timeout не отменяет request и не увеличивает revision. |
| C09 | CLI coord status/register/wait работает при занятом Editor; новое окно не granted без свежего подтверждения Editor. |
| C10 | Domain reload от compile и целевого PlayMode сохраняет token/step; реальный restart инвалидирует окно до recovery. |
| C11 | В active coordination новый legacy payload получает coordination_required; без регистраций старые команды проходят. |
| C12 | Content edit с прежним размером и восстановленным mtime меняет InputDigest. Изменение .asset/.meta/локального пакета учитывается. |
| C13 | Change-then-revert/observer overflow не даёт valid. Обычный Temp-артефакт не инвалидирует доказательство. |
| C14 | Зелёный NUnit при input mutation возвращает stale_input, cache не promoted, attachments не requeue. |
| C15 | A/B/A на одинаковых входах даёт два реальных runs, третий cache hit; subset — cache hit; непокрытый набор — miss. |
| C16 | Удалён обязательный снимок — нет визуального cache acceptance. Нулевой набор не PASS. |
| C17 | In-flight identical request присоединяется без второго фактического run; `Fresh` требует отдельного run. |
| C18 | После 33 entries хранятся 32, вытеснение LRU; active task/свои counters остальных entries целы. |
| C19 | CLI/Unity codecs round-trip общего состояния совпадают; неизвестная schema и stale epoch отклоняются. |
| C20 | Завершение/отмена клиентского ожидания не снимает исполняемую задачу и не освобождает window раньше неё. |

## План проверок

Все команды ниже выполняются из целевого репозитория или с абсолютными путями. `AB_COORD_ACCEPT` — один выбранный уникальный session прогона. Не копировать чужой session из статуса. Если в host-проекте идёт другая работа, сперва согласовать окно по доступному legacy/новому режиму.

| Id | Момент и команда | Ожидаемое доказательство | Бюджет |
|---|---|---|---|
| V1 | После U1–U7: `dotnet run --project AgentBridgeCoordination.Tests/AgentBridgeCoordination.Tests.csproj -c Release -- --group all` | Непустой перечень C01–C08, C12–C13, C15–C20 по применимости; `Coordination: PASS`, ноль failures. | 60 с; 2 исправления причины. |
| V2 | `dotnet build AgentBridgeCli/AgentBridgeCli.csproj -c Release`, затем `dotnet run --project AgentBridgeCli.Tests/AgentBridgeCli.Tests.csproj -c Release` | Build success, существующий `AgentBridgeCli.Tests: PASS`; legacy parsing/classification сохранены. | 120 с; 2 исправления. |
| V3 | `agentbridge tests --project D:/Hobby/Repositories/unitycoworkbridge/AgentBridgeUnity --mode EditMode --test AgentBridgeCoordinationTests --test AgentBridgeEvidenceTests --test AgentBridgeTestCacheTests --session AB_COORD_ACCEPT --format human` | Все непустые новые fixtures зелёные, C09–C13/C15–C19 интегрированы с реальными Unity API. | 120 с исполнения; очередь отдельно. |
| V4 | `agentbridge tests --project D:/Hobby/Repositories/unitycoworkbridge/AgentBridgeUnity --mode PlayMode --test AgentBridgeCoordinationPlayModeTests --session AB_COORD_ACCEPT --format human` | Один короткий непустой fixture; recovery/контекст C10 и жизненный цикл C20; не ручной Play Mode. | 60 с; 2 исправления. |
| V5 | `powershell -NoProfile -ExecutionPolicy Bypass -File scripts/verify-coordination.ps1 -Project D:/Hobby/Repositories/unitycoworkbridge/AgentBridgeUnity -Cli <абсолютный-путь-к-собранному-agentbridge.exe> -Output <host-Temp/AgentBridge/CoordAcceptance>` | JSON/краткий отчёт с реальными TaskIds и counters C02/C04/C09/C11/C14/C15/C17/C20; `coordination_acceptance=PASS`. Проверка invalidity обязана ожидать stale_input как правильный отрицательный исход. | До 8 минут, максимум 2 исправления причины; никакой полной игры. |
| V6 | `powershell -NoProfile -ExecutionPolicy Bypass -File scripts/build-plugin.ps1` | Требования версий, frontmatter и ZIP: `invalid_entries=0`, `zip_validation=PASS`. | 60 с. |

V3/V4 выполняются при отсутствии активных coordination registrations; тем самым проверяется legacy compatibility. Для coordinated запуска внутри V5 script сначала регистрирует себя, получает окно/StepIds, добавляет `--coord-window`/`--coord-step` из реальных ответов. Эти флаги нельзя подставить заранее выдуманным token. V5 не запускает те же fixtures заново целиком: использует короткие dedicated probes для нужных отрицательных и гонковых случаев.

Нужный путь CLI в V5 получить из результата Release build; нельзя подставить системный установленный CLI, если он старый. Script проверяет capabilities обеих сторон и прекращает зависимую проверку при несовпадении. Преднамеренное restart испытание выполнять только в выделенном host-проекте с явным разрешением пользователя на закрытие/открытие Editor; при его отсутствии покрыть process-incarnation recovery unit-тестом и отметить реальный restart gate как ожидающий, не заявлять полноценную live restart проверку. Обычные собственные domain reload через compile/PlayMode входят в обязательный автоматический сценарий.

Windows — обязательная живая платформа приёмки для этой рабочей среды. Для macOS/Linux тот же store/process runner обязан пройти в доступной CI/среде прежде чем заявлять платформенную поддержку coordination-v1; отсутствие такой среды отмечается как непроверенная платформенная поддержка, не как Windows failure. Не добавлять обещание кроссплатформенной проверки по одной сборке C#.

## Документация

Обновить README по пользовательским командам, состояниям и восстановлению; CLAUDE по новым папкам/тестам/командам; UNITYAGENT по внешнему протоколу и диагностике; оба навыка Bridge по необходимости токенов для их Editor-операций. В описании recovery явно различить клиентское ожидание, запущенную задачу, domain reload, restart Editor и пропавшего писателя.

Тематическая документация должна объяснять реальный контракт без необходимости читать данное ТДД. ТДД можно ссылать на документацию, но не превращать его в единственную инструкцию эксплуатации. Зафиксировать стоимость snapshot/cache, выявленную V5: время подготовки digest и фактическое число запусков. Не заявлять ускорение в процентах без измерения.

## Done-condition

U1–U7 реализованы, C01–C20 покрыты обязательными проверками V1–V6 с реальными ids/отчётами для итоговой версии файлов. Windows coordination/evidence/cache capabilities объявляются только при прошедших своих проверках. Документация и версии обновлены, штатный ZIP пересобран и проверен, чужие файлы и WaterWalk сохранены. Все выданные этим исполнителем права завершены после terminal tasks. Непроведённые дополнительные платформенные/restart проверки перечислены отдельно и не объявлены успешными. Не осталось обязательных Windows gates, провалов или пропущенного непустого покрытия.

## Отчёт и продолжение

- Статус `Выполнено` только при Done-condition. Иначе сохранить `В работе`, `Ожидает проверки`, `Ожидает пользователя` или `Заблокировано` с причиной.
- Записать версии, реализованные единицы, фактически покрытые сценарии, TaskIds/SourceTaskIds, артефакты, invalidity отрицательных probes и подтверждение сохранённых чужих изменений.
- Измеренные затраты: реальные compile/EditMode/PlayMode, hits/attachments, повторные запуски и время ожидания. Не считать модели расходы токенов по предположению.
- На остановке сохранить active RequestId/TaskId/epoch/token в собственном защищённом состоянии, не выводить чужие tokens. Не оставлять ложное «свободно», если Unity-задача ещё работает.

## Отчёт исполнения (2026-09-12)

### Версии

| Компонент | Было | Стало |
|---|---|---|
| AgentBridge CLI | 1.15.1 | 1.16.0 |
| Unity-пакет | 0.22.1 | 0.23.0 |
| Плагин | 1.19.1 | 1.20.0 |

ZIP пересобран штатным `scripts/build-plugin.ps1`: `version_check=PASS`, `frontmatter_validation=PASS`,
`invalid_entries=0`, `zip_validation=PASS`, `sha256=12cf0cd8a834437d2416dd7b8069373b4d6099c6abb65cb05141598a3a8cef13`.
Установка и публикация не выполнялись — отдельного запроса не было.

### Реализованные единицы

U1–U7 реализованы. Общий код — `AgentBridgeUnity/Packages/com.elmortem.agentbridge/Editor/Coordination/`
(27 файлов, namespace `AgentBridge.Coordination`, без UnityEngine/UnityEditor); CLI подключает эти же
исходники через `<Compile Include>`, копий реализации нет. Адаптеры вне общей папки:
`CoordinationUnityCodec.cs`, `CoordinationEditorAdapter.cs`, `CoordinationGate.cs` в пакете и
`CoordinationJsonCodec.cs`, `CoordinationCommands.cs`, `CoordinationResultFormatter.cs` в CLI.
Достоверность — `EvidenceRecord.cs`, `ValidationInputSnapshot.cs`, `ValidationInputMonitor.cs`,
`ValidationEvidence.cs`, `EvidenceClassification.cs`. Кэш — `TestCacheIndex.cs`,
`TestCacheEntryInfo.cs`, переписанные `TestRunDumpStore.cs`/`TestCacheQuery.cs`.

### Проверки

| Id | Результат |
|---|---|
| V1 | **PASS.** `Coordination: PASS`, ноль failures. Покрыты C01, C02, C03, C04, C05, C06, C07, C08, C12, C13, C18, C19, C20. |
| V2 | **PASS.** Release build чистый (0 warnings), `AgentBridgeCli.Tests: PASS`; добавлены проверки разбора `coord`/`--coord-*`, классификации `stale_input`/`evidence_unavailable` и вывода `Evidence`. |
| V3 | **PASS.** `Task_20260912_120205_589_0d6357a1`: 20 passed, 0 failed, 0 skipped, 0 inconclusive, `Evidence: valid`, `InputDigest 2ba20708ce713eed`. |
| V4 | **PASS.** `Task_20260912_120209_580_b7cdc23c`, `Task_20260912_120214_864_1112c031`, `Task_20260912_120219_616_a375ab0e`: по 3 passed, `Evidence: valid (observer reinstalled after 1 domain reload(s); both input digests match)`, стабильный `InputDigest fbebed40b3e09fac` во всех трёх прогонах. |
| V5 | **PASS.** `coordination_acceptance=PASS`, все 10 проверок зелёные: CAP, C01, C02a, C02b, C09, C11, C09b, C14, C03, C20. `task_ids=Task_20260912_120234_528_41354f8f`, `real_runs=1 cache_hits=0 refusals=3`, `InputDigest c4af14f17fcc0273`. Отчёт — `AgentBridgeUnity/Temp/AgentBridge/CoordAcceptance/coordination-acceptance.json`. |
| V6 | **PASS.** См. раздел «Версии». |

Windows-гейты пройдены живьём на Unity 2022.3.62f2, пакет 0.23.0, CLI 1.16.0.

Кроссплатформенная поддержка coordination-v1 **не заявляется**: тот же store/process runner на
macOS/Linux не прогонялся, среды не было. Реальный restart редактора живьём **не проверялся** —
разрешения на закрытие/открытие редактора не запрашивалось; смена incarnation покрыта unit-тестом
`AgentBridgeCoordinationTests.EditorRestartInterruptsTheWindowButKeepsRegistrationsAndEdits`, живой
restart остаётся ожидающим гейтом. Обычные собственные domain reload через compile и PlayMode
проверены живьём и входят в пройденные V3/V4/V5.

### Дефекты, найденные и исправленные во время приёмки

1. `File.Move(string, string, bool)` есть в .NET Standard 2.1, но не в Mono-профиле Unity — пакет не
   компилировался. Заменён на `CoordinationFileStore.Rename` (`File.Replace`/`File.Move`) с одним
   повтором; fallback копированием намеренно отсутствует. Сигнал пришёл от пользователя.
2. `verify-coordination.ps1` брал в окно валидации фикстуры V3, которые утверждают «регистраций и
   окна нет» — самопротиворечие. Шаг плана переведён на выделенный probe `AgentBridgeTestCacheTests`.
3. `verify-coordination.ps1` при `ErrorActionPreference = Stop` превращал прогресс CLI на stderr в
   терминальную ошибку и сливал stderr в stdout, ломая разбор JSON. stdout и stderr разделены,
   как того требует контракт; прерванный прогон больше не может закончиться `PASS`.
4. **Ложный `stale_input` на PlayMode.** Unity Test Framework создаёт `Assets/InitTestScene*.unity`
   для PlayMode-прогона, а мост удаляет их после — create-then-delete внутри `Assets/`. Наблюдатель
   честно видел изменение с возвратом и выносил `stale`, то есть мост обвинял в изменении входов
   самого себя, причём флаки: при первом прогоне запись попадала в разрыв domain reload и не
   замечалась. Введён инъектируемый предикат «собственный скрэтч» (`ValidationEvidence.BuildIgnore`,
   чистая перегрузка `SceneSafetyGuard.IsTestScenePath(path, bootstrapScenePath)` без Unity API,
   пригодная для worker-потока и колбэка watcher'а). Один и тот же предикат применяется к стартовому
   снимку, наблюдателю, конечному снимку и поиску в кэше — иначе дайджесты не совпали бы никогда.
   Регрессии: сценарий C13 в консольном раннере и
   `AgentBridgeEvidenceTests.TheBridgesOwnTemporaryPlayModeSceneIsNotAForeignChange`. После
   исправления три подряд PlayMode-прогона дают `valid` с одинаковым дайджестом.

### Сознательные отклонения

- Конечный снимок входов (`ValidationEvidence.Complete`) считается синхронно на главном потоке.
  Стартовый снимок, ради которого агент ждёт, вынесен на worker, как требует ТДД; конечный
  выполняется после того, как прогон уже завершился и редактор простаивает. Стоимость измеряется
  в V5 и пока не измерена.
- `coordination_required` реализован как терминальный `rejected` с причиной в `Logs`, а не как новый
  статус: так задачу корректно завершает и старый CLI. Новых статусов ровно два — `stale_input` и
  `evidence_unavailable`, как и предписано.
- Пересечение областей сравнивается регистронезависимо на всех хостах. Это консервативно: на
  регистрозависимой ФС возможен отказ там, где коллизии не было бы. Обратное направление ошибки
  (два писателя в одном файле) недопустимо.
- PlayMode-fixture написан без ссылки на пакет: сборка `AgentBridge` — Editor-only, и PlayMode-тест
  физически не может на неё ссылаться. Инварианты проверяются через общие файлы моста.
- Известное ограничение, зафиксированное в README, UNITYAGENT и скилле: PlayMode-прогон
  перезагружает домен внутри собственного наблюдения. Наблюдатель переустанавливается, оба
  дайджеста сравниваются сквозь прогон, но изменение с возвратом внутри окна перезагрузки не
  детектируется. У EditMode такого разрыва нет.
- Второе известное ограничение, найденное живой приёмкой: `compile`, которому реально есть что
  импортировать, перезагружается собственным `AssetDatabase.Refresh` до того, как снимок завершён,
  и отдаёт `Evidence: unknown (this result was produced without an input snapshot)`. Это честный
  исход, а не оптимистический PASS: сам результат компиляции верен, вне окна валидации зелёный
  compile остаётся зелёным, а кэш по-прежнему защищён прежним fingerprint. `compile` на уже
  импортированном проекте даёт `valid` (проверено: `Task_20260912_115717_407_c2ff6f77`). Приёмке,
  которой нужен дайджест входов, следует опираться на шаг `tests` — он снимает снимок без refresh
  перед собой. Полноценное решение требует перестройки жизненного цикла compile-задачи и в это ТДД
  не входило.

### Измеренная стоимость снимка и кэша

На хост-проекте AgentBridgeUnity подготовка снимка входов не выделяется из времени задачи:
EditMode-прогон 20 тестов — 0.916 s тестов при общем ожидании клиента около 3–4 s; PlayMode-прогон
3 тестов — 0.046 s тестов, полный цикл со входом и выходом из плей мода около 5 s. Фактическое число
запусков в V5: `real_runs=1`, `cache_hits=0`, `refusals=3`. Ускорение в процентах не заявляется:
проект слишком мал, чтобы измерение было честным, а A/B/A-сценарий с кэшем живьём не прогонялся
(C15/C17 покрыты семантикой в `AgentBridgeTestCacheTests` и индексом в V1).

### Чужая работа и выданные права

Незакоммиченных чужих правок не тронуто: изменены только файлы этой задачи. WaterWalk и
установленный пользовательский Bridge не затрагивались.

V5 создавал собственные сессии `AB_COORD_ACC_A_*` / `AB_COORD_ACC_B_*`, их edit grant и окно. Все
закрыты: после прогонов `agentbridge coord status` отвечает `No registered sessions`, включая
прогон, прерванный на середине, — блок `finally` закрыл свои права и только свои. Чужой работы в
проекте на момент прогонов не было (скрипт проверяет это до любых мутаций и отказался бы ждать).
Проверено, что после снятия регистраций legacy-путь снова работает:
`Task_20260912_115717_407_c2ff6f77` — обычный `compile` без токенов, `success`.

### Затраты

Реальных Unity-прогонов: 3 compile, 2 EditMode-серии (плюс 2 в ходе V5-итераций), 4 PlayMode.
Cache hits — 0, attachments — 0 (все приёмочные прогоны шли с `--fresh` либо по изменившимся
входам). Повторных запусков проверок: V1 — 5, V2 — 4, V3 — 3, V4 — 5, V5 — 3, V6 — 3. Четыре
исправления собственных причин провала перечислены выше; ни одна причина не правилась более двух
раз. Расходы токенов не оцениваю — предположения здесь бесполезны.
