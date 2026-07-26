Status: Выполнено

# Unity Agent Bridge — Исправления по итогам ревью движка Roslyn — Agent Execution Spec

## References (not inlined)

- Конвенции кода: глобальный CLAUDE.md пользователя (табы, один тип на файл, без комментариев в коде, сериализуемые поля public с большой буквы).
- Исходный ТДД движка: `Docs/tdd/260725-1855-TDD-agent_bridge_roslyn_engine.md` — контракт протокола (статусы, идемпотентность, клиент) остаётся источником истины, этот документ его чинит, а не меняет.
- Skills: `unity-bridge` — протокол клиента описан там, при изменении поведения статусов обновить.

## Контекст (что нашло ревью)

Ревью подтвердило вживую четыре дефекта поведения и набор мелочей:

- Осиротевшая запись журнала с промежуточным статусом (после убийства редактора) вечно блокирует всю очередь: `TryStartNextTask` каждый скан выбирает её как самую старую, `StartTask` выходит по ветке «уже в работе», ни одна новая задача не стартует. Воспроизведено: новая задача не стартовала, пока сирота не удалена руками.
- Переиспользование `TaskId` с другим содержимым тихо возвращает СТАРЫЙ результат: `TryStartNextTask` отфильтровывает файлы с терминальной записью до проверки хеша, правило `id_conflict` из исходного ТДД для терминальных записей не срабатывает никогда. Воспроизведено: изменённый исходник → мгновенный `success` со старым `ReturnValue`.
- `OnBeforeAssemblyReload` в `TaskCoordinator` исключает только `compile`: задача `tests` в PlayMode будет помечена `interrupted_by_domain_reload` при входе в Play Mode (терминальный статус, клиент выйдет с ошибкой), хотя прогон продолжается и `FinalizeCoordinatorRun` потом перепишет запись на `success`.
- Обычная ошибка компиляции csharp-задачи даёт `rejected` вместо `compiler_error`: `CSharpTaskExecutor.RunAsync` не различает `GuardrailRejected` и провал Emit. Воспроизведено: `CS0103` → `"Status": "rejected"`.
- `AgentBridgeSettingsStore.Load()` читает и парсит JSON с диска при каждом геттере; `EditorTickPump.OnUpdate` зовёт геттеры интервалов каждый тик `EditorApplication.update`, `CheckTimeout` зовёт `GetTaskTimeoutSeconds` каждый тик активной задачи — дисковый I/O сотни раз в секунду.
- `EditorTickPump.HasActiveTask` никем не выставляется — активный интервал 33 мс никогда не включается, фоновые задачи тикают на 500 мс.
- Таймаут не вызывает `EditorUtility.RequestScriptReload()` (исходный ТДД, Unit 13) — сбежавший код задачи продолжает жить после `timeout`.
- `bridge.ps1 wait` без `TaskId` молча ждёт пустой идентификатор до исчерпания `--wait` (bash-версия сразу даёт usage, код 3).
- Генерация `TaskId` в клиентах для `compile`/`tests` имеет секундную гранулярность — два вызова в одну секунду дают одинаковый Id (в связке с багом про терминальные записи — тихий реплей).
- Легаси-остатки: `AgentTestRunner.RequestRun`/`WriteAborted`/`WriteResult`/`TestTaskKey` пишут в удалённый каталог `Assets/Editor/AgentBridge`; `GetAsyncTimeoutSeconds` и поле `AsyncTimeoutSeconds` мертвы; `ProjectSettings/AgentBridge.json` лежит в старом формате; в `.claude/settings.local.json` осталось правило на удалённый `wait-for-result.sh`; `Library/AgentBridge/pending_Task_compile2.json` — осиротевший pending-файл, `Trim` такие не подчищает; в `RoslynResolver.TryLoadAndVerify` неудачная проба источника оставляет `_activeDirectory` на непригодном каталоге.

bridge.ps1 при этом проверен агентом end-to-end против живого редактора (PowerShell 7.4.6): `status`, `csharp`, `ui`, `compile`, `tests`, `wait`, коды выхода 0/1/2/3 — всё совпадает с bridge.sh, кроме зафиксированного выше `wait` без аргумента.

## Предусловия (проверить первым делом, при невыполнении — остановиться и сообщить)

- Unity Editor запущен с проектом `AgentBridgeUnity`: файл `AgentBridgeUnity/Library/EditorInstance.json` существует.
- `bash AgentBridgeUnity/Library/AgentBridge/bridge.sh status` печатает JSON с `RoslynReady: true`.
- Если мост не отвечает — остановиться и написать: «Открой AgentBridgeUnity в Unity и включи мост через Tools → Agent Bridge → Start». Не запускать Unity самостоятельно.

## Foundations (shared, used across units)

- Корень репозитория: `D:\Hobby\Repositories\unitycoworkbridge`; все команды запускаются из него.
- Пакет: `AgentBridgeUnity/Packages/com.elmortem.agentbridge/`.
- Клиент: `bash AgentBridgeUnity/Library/AgentBridge/bridge.sh ...`.
- Терминальные статусы: `success`, `compiler_error`, `runtime_error`, `timeout`, `canceled`, `interrupted_by_domain_reload`, `rejected`.
- После каждой правки кода пакета выполнять `bash AgentBridgeUnity/Library/AgentBridge/bridge.sh compile` и убеждаться в `"Status": "success"` — это и деплой (domain reload подхватывает правку), и smoke-проверка. Считать это частью гейта каждого юнита, где правится C#.
- Пробная csharp-задача для smoke: файл `/tmp/<Id>.cs` c классом `<Id>`, `await Task.Delay(100)`, `return "ok"`; выполнение `bash AgentBridgeUnity/Library/AgentBridge/bridge.sh csharp /tmp/<Id>.cs` → `"Status": "success"`.

## Invariants (must hold throughout)

- Изменяются только файлы внутри корня репозитория; `Docs/tdd/done/**` не изменяется.
- Формат JSON-конверта задач и записей журнала не меняется: ни одно поле не добавляется, не удаляется и не переименовывается.
- Файлы `Ui/UiNodeApplier.cs`, `Ui/UiDumper.cs`, `Ui/UiScreenshot.cs`, `Ui/UiComponentSync.cs` не изменяются.
- В коде нет комментариев, отступы — табы, каждый тип в своём файле.
- Зависимости `package.json` не растут.
- Под `AgentBridgeUnity/Assets/` появляются только файлы PlayMode-пробы из Unit 3.

## Execution Plan

Юниты выполняются строго по порядку.

### Unit 1 — Финализация осиротевших записей при старте координатора

- Goal: запись журнала с промежуточным статусом от мёртвой сессии не блокирует очередь после перезапуска домена или редактора.
- Touch: `TaskCoordinator.cs`.
- How: в `Start()` после `TryFinalizePendingCompileTask()` добавить вызов нового приватного метода `FinalizeOrphanRecords()`. Метод: перечислить `Journal/*.json` через `Directory.GetFiles(BridgePaths.Journal, "*.json")`; для каждого файла `TaskJournal.TryRead`; пропустить записи с терминальным статусом; пропустить запись, чей `Id` равен значению `SessionState.GetString("AgentBridge_CompileTask", "")` или `SessionState.GetString("AgentBridge_CoordinatorTestTask", "")` (их финализируют собственные механизмы); пропустить запись, чей `SessionId` равен `BridgeStatusWriter.Current.SessionId`; остальным поставить `Status = "interrupted_by_domain_reload"`, `FinishedAtUtc = DateTime.UtcNow.ToString("o")`, добавить в `Logs` строку `orphaned record finalized on domain load`, записать через `TaskJournal.Write`. Строковые ключи `SessionState` взять из существующих констант (`CompileTaskExecutor.PendingCompileTaskKey` сделать `public const`, `AgentTestRunner.CoordinatorTestTaskKey` сделать `public const`), не дублировать литералами.
- Gate: три шага. Первый: записать руками `AgentBridgeUnity/Library/AgentBridge/Inbox/Task_orphangate.task.json` с содержимым `{"Id":"Task_orphangate","Kind":"compile","PayloadFile":""}` и `AgentBridgeUnity/Library/AgentBridge/Journal/Task_orphangate.json` с содержимым `{"Id":"Task_orphangate","Kind":"compile","Status":"running","Hash":"<sha256 от task.json>","SessionId":"deadbeef","StartedAtUtc":"2026-07-26T06:00:00.0000000Z","FinishedAtUtc":""}`, где `<sha256 от task.json>` — вывод `sha256sum` от файла задачи. Второй: `bash AgentBridgeUnity/Library/AgentBridge/bridge.sh compile` → `"Status": "success"` (правка кода этого юнита вызывает reload, `Start()` нового домена делает sweep). Третий: `cat AgentBridgeUnity/Library/AgentBridge/Journal/Task_orphangate.json` содержит `interrupted_by_domain_reload`, после чего smoke csharp-задача из Foundations → `success`.
- On failure: ≤3 попытки, затем остановиться и сообщить. Файлы `Task_orphangate.*` удалить в любом исходе.

### Unit 2 — id_conflict для терминальных записей вместо реплея старого результата

- Goal: повторная задача с существующим Id и другим содержимым получает `rejected` c `id_conflict`; с тем же содержимым — прежний результат без выполнения; реплей чужого результата невозможен.
- Touch: `TaskCoordinator.cs`.
- How: добавить в `TaskCoordinator` статический кэш хешей `Dictionary<string, CachedHash>` (новый тип `CachedHash.cs`: `public long TaskFileLength; public long PayloadLength; public string TaskFileWriteUtc; public string PayloadWriteUtc; public string Hash;`), метод `HashOf(taskFilePath, payloadPath)`: если размеры и `File.GetLastWriteTimeUtc().ToString("o")` обоих файлов совпадают с кэшем — вернуть кэшированный хеш, иначе пересчитать `ComputeHash` и обновить кэш. В `TryStartNextTask` изменить фильтр: файл пропускается, только если запись терминальна И `existing.Hash == HashOf(...)`; если запись терминальна и хеш не совпал — файл становится кандидатом. В `StartTask` ветку `hasExisting` заменить на: хеш совпал (любой статус) → `return`; хеш не совпал → `WriteTerminal(id, request.Kind, "rejected", "id_conflict")` и `return`.
- Gate: последовательность из трёх команд. Создать `/tmp/Task_idgate.cs` (класс `Task_idgate`, возвращает `"v1"`), `bash .../bridge.sh csharp /tmp/Task_idgate.cs` → `success`, `ReturnValue` `v1`. Изменить файл, чтобы возвращал `"v2"`, повторить команду → код выхода 1, `"Status": "rejected"`, в `Logs` есть `id_conflict`. Вернуть содержимое v1 без изменений и повторить → код выхода 0 и прежний `ReturnValue` `v1` мгновенно (идемпотентный реплей того же содержимого разрешён).
- On failure: ≤3 попытки, затем остановиться и сообщить.

### Unit 3 — tests переживают domain reload PlayMode

- Goal: задача `tests` с PlayMode-тестами не помечается `interrupted_by_domain_reload` при входе в Play Mode и завершается результатом прогона.
- Touch: `TaskCoordinator.cs`, тестовые файлы пробы в `Assets`.
- How: в `OnBeforeAssemblyReload` рядом с исключением для `"compile"` добавить исключение для `"tests"` — запись не финализировать, `CleanupActive()` выполнить (лог-scope и активные ссылки в новом домене всё равно мертвы), запись остаётся `running`, её финализирует `AgentTestRunner.FinalizeCoordinatorRun` по персистентному ключу `SessionState`. Создать PlayMode-пробу: `AgentBridgeUnity/Assets/Tests/PlayMode/AgentBridgePlayModeProbeTests.cs` с одним проходящим тестом и `AgentBridgeUnity/Assets/Tests/PlayMode/AgentBridge.PlayModeProbeTests.asmdef` (references `UnityEngine.TestRunner`, `UnityEditor.TestRunner` не нужен, `includePlatforms` пустой, `defineConstraints` `UNITY_INCLUDE_TESTS`), по образцу существующей `AgentBridge.ProbeTests`.
- Gate: `bash AgentBridgeUnity/Library/AgentBridge/bridge.sh tests --mode PlayMode --assembly AgentBridge.PlayModeProbeTests --wait 180` → код выхода 0, `"Status": "success"`, `Tests.total` равно 1, `Tests.passed` равно 1; в финальной записи журнала нет `interrupted_by_domain_reload`. Затем EditMode-регрессия: `bash .../bridge.sh tests --mode EditMode --assembly AgentBridge.ProbeTests` → `success`, `Tests.total` 1.
- On failure: ≤3 попытки. Если PlayMode-прогон не стартует по причинам самого Test Runner (`Tests.aborted` true) — зафиксировать причину в отчёте и не считать юнит закрытым; тестовые файлы пробы оставить.

### Unit 4 — compiler_error для ошибок компиляции csharp-задач

- Goal: провал компиляции исходника задачи даёт `compiler_error`; `rejected` остаётся только для guardrail и негодной сигнатуры/типа.
- Touch: `CSharpTaskExecutor.cs`.
- How: в `RunAsync` ветку `!compileResult.Success` разделить: `compileResult.GuardrailRejected` → `Status = "rejected"`; иначе → `Status = "compiler_error"`. Ветка `TaskMethodResolver` не меняется (`rejected`).
- Gate: три команды. Задача с `return undefinedVariable;` → `"Status": "compiler_error"`, в `Diagnostics` есть `CS0103`. Задача с `Thread.Sleep(100)` → `"Status": "rejected"`, в `Logs` есть `guardrail`. Smoke csharp-задача → `success`.
- On failure: ≤3 попытки, затем остановиться и сообщить.

### Unit 5 — Кэш настроек

- Goal: настройки не читаются с диска чаще раза в 2 секунды при любом числе вызовов геттеров.
- Touch: `AgentBridgeSettingsStore.cs`.
- How: добавить статические поля `_cached` (`AgentBridgeSettings`), `_cachedWriteUtc` (`DateTime`), `_lastCheckTime` (`double`). `Load()`: если `_cached != null` и `EditorApplication.timeSinceStartup - _lastCheckTime < 2` — вернуть `_cached`; иначе обновить `_lastCheckTime`, взять `File.GetLastWriteTimeUtc(path)` (для несуществующего файла — `DateTime.MinValue`), при совпадении с `_cachedWriteUtc` и непустом `_cached` вернуть `_cached`, иначе перечитать файл и обновить оба поля. `Save()` после записи сбрасывает `_cached = null`. `using UnityEditor;` добавить.
- Gate: `grep -n "timeSinceStartup\|GetLastWriteTimeUtc" AgentBridgeUnity/Packages/com.elmortem.agentbridge/Editor/AgentBridgeSettingsStore.cs` возвращает не меньше 2 строк; функциональная проба подхвата: csharp-задача возвращает `AgentBridgeSettingsStore.GetKeepCompletedCount().ToString()` → `10`; изменить `KeepCompletedCount` на `11` в `AgentBridgeUnity/ProjectSettings/AgentBridge.json`, подождать 3 секунды, повторная задача → `11`; вернуть `10` в файле.
- On failure: ≤3 попытки, затем остановиться и сообщить.

### Unit 6 — Активный интервал тик-пампа реально включается

- Goal: во время активной задачи `EditorTickPump` тикает с `ActiveTickIntervalMs`, на простое — с `IdleTickIntervalMs`.
- Touch: `TaskCoordinator.cs`.
- How: в `StartTask` сразу после присвоения `_activeTaskId` поставить `EditorTickPump.HasActiveTask = true;`, в `CleanupActive()` — `EditorTickPump.HasActiveTask = false;`.
- Gate: `grep -c "EditorTickPump.HasActiveTask" AgentBridgeUnity/Packages/com.elmortem.agentbridge/Editor/TaskCoordinator.cs` возвращает `2`; smoke csharp-задача с `await Task.Delay(1500)` → `success`.
- On failure: одна попытка, затем остановиться и сообщить.

### Unit 7 — Таймаут добивает сбежавший код перезагрузкой домена

- Goal: после статуса `timeout` у csharp-задачи домен перезагружается и код задачи гарантированно мёртв (по Unit 13 исходного ТДД).
- Touch: `TaskCoordinator.cs`.
- How: добавить статическое поле `_pendingTimeoutReload` (bool). В `CheckTimeout` перед `FinishTask("timeout", ...)`: если `_activeRecord.Kind == "csharp"`, поставить `_pendingTimeoutReload = true`. В `OnUpdate` первым делом: если `_pendingTimeoutReload` и `_activeTaskId == null` — сбросить флаг и вызвать `EditorUtility.RequestScriptReload()`.
- Gate: в `AgentBridgeUnity/ProjectSettings/AgentBridge.json` временно добавить `"TaskTimeoutSeconds":5`; подождать 3 секунды (кэш из Unit 5); задача с `await Task.Delay(60000)` и `--wait 30` → код выхода 1, `"Status": "timeout"`; затем `bash .../bridge.sh status` печатает JSON с новым непустым `SessionId` (домен перезагрузился); вернуть `TaskTimeoutSeconds` в `300`; smoke csharp-задача → `success`.
- On failure: ≤3 попытки; `TaskTimeoutSeconds` вернуть в 300 в любом исходе.

### Unit 8 — Исключения синхронного пути не подвешивают задачу

- Goal: любое исключение, вылетевшее из `RunTask` синхронно, завершает задачу `runtime_error` немедленно, а не таймаутом через 300 секунд.
- Touch: `TaskCoordinator.cs`.
- How: в `StartTask` обернуть вызов `RunTask(request)` в `try/catch (Exception ex)`; в catch вызвать `FinishTask("runtime_error", null, new List<string> { ex.Message }, false)`.
- Gate: `grep -n "catch" AgentBridgeUnity/Packages/com.elmortem.agentbridge/Editor/TaskCoordinator.cs` содержит блок вокруг `RunTask`; регрессия всех видов: smoke csharp → `success`; `bash .../bridge.sh ui /tmp/Task_uigate.ui.json` с payload `{"prefab":"Assets/Prefabs/NoSuch_gate.prefab","actions":[{"action":"dump"}]}` → код выхода 1, `"Status": "runtime_error"`; `bash .../bridge.sh compile` → `success`.
- On failure: ≤3 попытки, затем остановиться и сообщить.

### Unit 9 — Клиенты: wait без аргумента и миллисекунды в TaskId

- Goal: `bridge.ps1 wait` без `TaskId` даёт usage и код 3, как bash; генерируемые Id уникальны при вызовах чаще раза в секунду.
- Touch: `bridge.sh` и `bridge.ps1` в корне пакета; копии в `Library/AgentBridge/` обновить.
- How: в `bridge.ps1` в ветке `"wait"` перед `Assert-Alive` проверить `if (-not $positional -or [string]::IsNullOrEmpty($positional[0]))` → в stderr `usage: bridge.ps1 wait <TaskId>` и `exit 3`. В `bridge.sh` `new_task_id()` заменить формат на `date +"Task_%Y%m%d_%H%M%S_%3N"`; в `bridge.ps1` `New-TaskId` — `Get-Date -Format "yyyyMMdd_HHmmss_fff"`. После правки скопировать оба файла поверх копий в `AgentBridgeUnity/Library/AgentBridge/` (cp; `ClientInstaller` сверяет только размер, при следующем reload расхождения не будет, но копирование руками гарантирует немедленную актуальность).
- Gate: `bash AgentBridgeUnity/Library/AgentBridge/bridge.sh compile` → `success`, `"Id"` в ответе содержит суффикс из трёх цифр миллисекунд (`Task_????????_??????_???`); `diff` копий в `Library` и пакете пуст для обоих файлов. Для ps1 — если в системе доступен `powershell.exe` или `pwsh` из bash: `powershell.exe -NoProfile -File AgentBridgeUnity/Library/AgentBridge/bridge.ps1 wait` → строка usage и код выхода 3, `... bridge.ps1 status` → JSON с `SessionId`; если PowerShell из bash недоступен — зафиксировать в отчёте, что ps1-гейт пройден только статически (diff команд с bridge.sh), и НЕ считать это провалом юнита.
- On failure: ≤3 попытки, затем остановиться и сообщить.

### Unit 10 — Гигиена: легаси-код, мёртвые настройки, мусорные файлы

- Goal: в пакете нет кода, пишущего в `Assets/Editor/AgentBridge`, нет мёртвых полей настроек, осиротевшие pending-файлы подчищаются.
- Touch: `AgentTestRunner.cs`, `AgentBridgeSettings.cs`, `AgentBridgeSettingsStore.cs`, `TaskJournal.cs`, `RoslynResolver.cs`, `AgentBridgeUnity/ProjectSettings/AgentBridge.json`, `.claude/settings.local.json`, файл `AgentBridgeUnity/Library/AgentBridge/pending_Task_compile2.json`.
- How:
  - `AgentTestRunner.cs`: удалить `RequestRun`, `WriteAborted`, `WriteResult`, константу `TestTaskKey` и ветку `legacyTaskId` в `RunFinished`; `using System.IO` убрать, если больше не нужен.
  - `AgentBridgeSettings.cs`: удалить поле `AsyncTimeoutSeconds`; `AgentBridgeSettingsStore.cs`: удалить `GetAsyncTimeoutSeconds()`.
  - `AgentBridgeUnity/ProjectSettings/AgentBridge.json`: переписать в `{"Enabled":true,"KeepCompletedCount":10,"TaskTimeoutSeconds":300}`.
  - `TaskJournal.Trim`: после подрезки записей перечислить `Directory.GetFiles(BridgePaths.WorkingRoot, "pending_*.json")`; для каждого файла извлечь `<Id>` из имени; если в журнале нет записи с этим Id с промежуточным статусом — удалить файл.
  - Удалить `AgentBridgeUnity/Library/AgentBridge/pending_Task_compile2.json`.
  - `.claude/settings.local.json`: удалить строку `"Bash(bash AgentBridgeUnity/Assets/Editor/AgentBridge/wait-for-result.sh:*)"`.
  - `RoslynResolver.TryLoadAndVerify`: запомнить прежний `_activeDirectory` в начале, в `catch` и во всех ветках возврата с `Available = false` восстановить его перед `return`.
- Gate: `grep -rn "testresult_\|TestTaskKey\|AsyncTimeoutSeconds\|RequestRun" AgentBridgeUnity/Packages/com.elmortem.agentbridge/Editor/` возвращает пусто; `ls AgentBridgeUnity/Library/AgentBridge/pending_*.json` возвращает ошибку «нет такого файла»; `grep -c "wait-for-result" .claude/settings.local.json` возвращает `0`; финальная тройка: `bash .../bridge.sh compile` → `success`, smoke csharp → `success`, `bash .../bridge.sh tests --mode EditMode --assembly AgentBridge.ProbeTests` → `success` с `Tests.total` 1.
- On failure: ≤3 попытки. Если после удаления легаси сломались тесты — восстановить удалённый фрагмент, из-за которого сломалось, и остановиться с описанием.

## Done (/goal condition)

Все десять юнитов выполнены, и в транскрипте есть вывод команд, запущенных из корня репозитория:

- гейт Unit 1: `Journal/Task_orphangate.json` показал `interrupted_by_domain_reload` после `compile`;
- гейт Unit 2: повтор `Task_idgate` с изменённым содержимым напечатал `"Status": "rejected"` и `id_conflict`;
- гейт Unit 3: `bridge.sh tests --mode PlayMode --assembly AgentBridge.PlayModeProbeTests` напечатал `"Status": "success"` и `Tests.total` 1;
- гейт Unit 4: задача с `undefinedVariable` напечатала `"Status": "compiler_error"` и `CS0103`;
- гейт Unit 7: задача с `Task.Delay(60000)` напечатала `"Status": "timeout"`, следом `bridge.sh status` показал новый `SessionId`;
- гейты Unit 10: все три grep/ls-проверки чистые;
- финал: `bash AgentBridgeUnity/Library/AgentBridge/bridge.sh compile` → `"Status": "success"`; smoke csharp-задача → `"Status": "success"`; `bash AgentBridgeUnity/Library/AgentBridge/bridge.sh tests --mode EditMode --assembly AgentBridge.ProbeTests` → `"Status": "success"`, `Tests.total` 1.

Ограничения всё время: изменения только внутри корня репозитория; `Docs/tdd/done/**` не тронут; формат JSON-конверта не изменён; Ui-файлы из инвариантов не тронуты.

Остановиться после 120 ходов в любом случае.

## End-of-run report (the agent does this when the goal is met or it stops)

- Поставить `Status` в шапке этого файла в `Выполнено`.
- Сообщить: какие юниты закрыты; какие гейты потребовали повторов и почему; проверялся ли ps1-гейт Unit 9 реальным PowerShell или только статически; на чём остановился, если остановился.
- Пометка, но не действие: уточни у заказчика, нужно ли обновить `unity-bridge` SKILL.md под новый статус `compiler_error` для csharp-задач.
