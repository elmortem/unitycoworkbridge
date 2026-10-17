# 260924-2022-TDD-lost_test_run_end — ход исполнения

## Единицы

- [x] `TestRunnerCancellation.cs`: убрана проверка объекта `EditModeRunner`
- [x] `AgentTestRunner.cs`: `HasPendingFinalization`, `IErrorCallbacks.OnError`
- [x] `TestRunLifecycle.cs`: `InactiveSince`, `LostRunGraceMs`, `TrackInactivity`, `IsLost`, отмена только для `canceled`, детектор в `Tick`
- [x] Тесты: 2 новых случая в `TestCancellationPolicyTests`, `QueueTimeoutReproTests.FrameworkStopsWithoutRunFinished`
- [x] Скрипты: сценарий `lost`, сирота `EditModeRunner`
- [x] Документация (README, UNITYAGENT, SKILL, NOTE, PROJECT_MAP), пакет 0.34.1, плагин 1.30.1, ZIP пересобран
- [x] Проверки

## Решения

- Предполётная сверка: расхождений ТДД с кодом нет, все файлы, типы и члены на месте.
- `InactiveSince` объявлен в одной строке с `StopRequested` — стиль соседних полей `State`.
- Стабы `AgentBridgeRecovery.Tests`: добавлены `EditorApplication.isCompiling` и `AgentTestRunner.HasPendingFinalization` (проект компилирует настоящий `TestRunLifecycle.cs`). Версию CLI это не задевает: `version_check=PASS`.
- `verify-inert-test-controller.ps1`: поиск типа вынесен в хелпер `Find`, чтобы искать и `PlaymodeTestsController`, и `EditModeRunner`.
- `verify-test-cancellation.ps1`: в `Invoke-Bridge` добавлен один повтор при коде 3 — транзиентный `status_missing` дважды обрывал матрицу до конца.
- `Docs/PROJECT_MAP.md` всё-таки правился: у `TestRunLifecycle.cs` появилась новая ответственность, у обоих verify-скриптов — новое покрытие.

## Решения за пользователя

- нет

## Проверки

- `dotnet run --project AgentBridgeRecovery.Tests -c Release` — PASS, 7/7.
- `compile` в живом редакторе (2022.3.62f2, пакет 0.34.1) — success, foreign errors: no.
- EditMode `TestCancellationPolicyTests` — 11/11, включая 2 новых случая и `ActiveFrameworkRunStillBlocksInEditMode`.
- `verify-inert-test-controller.ps1` — PASS: сирота `EditModeRunner` не делает `RunningReason()` непустым.
- `verify-test-cancellation.ps1` — PASS все 6 сценариев. `lost`: `runtime_error` за 26.5 с, лог `Unity test job ended without RunFinished; no results for 25 s`, следующая задача выполнилась.
- EditMode `ResponsiveTestOutlivesTimeout` без отмены — success за 35.2 с: детектор живой прогон не трогает.
- `verify-scene-recovery.ps1` — PASS, включая продвижение очереди после двух PlayMode-прогонов.
- `build-plugin.ps1` — `invalid_entries=0`, `zip_validation=PASS`, `version_check=PASS`.

## Замечания

- Приёмка по WaterWalk не выполнена: проект тянет пакет с ветки `roslyn-cli` по git URL, поэтому проверка требует пуша этой ветки и переразрешения UPM в чужом живом редакторе. Локально все остальные критерии закрыты.
- Транзиентный `status_missing` при свежем heartbeat ловится дважды за сессию и роняет длинные verify-скрипты. `BridgeStatusWriter.WriteAtomic` использует `File.Replace`, а `File.Exists` на стороне читателя возвращает `false` при кратком конфликте доступа. Дефект вне этого ТДД и моих правок; в CLI (`BridgeInspector`) уместен короткий повтор чтения `status.json` — материал для отдельного ТДД.
- `KeepCompletedCount` вытесняет записи задач быстрее, чем успевает отработать прерванный verify-скрипт: `wait` по TaskId уже завершённой задачи отдаёт `task_not_found`.

## Продолжение

- Код, тесты, скрипты, документация и версии закрыты; все локальные критерии приёмки проверены в живом редакторе. Осталось одно: закоммитить и запушить ветку `roslyn-cli`, после чего в `WaterWalkUnity` переразрешить UPM-пакет до 0.34.1 и убедиться, что `Task_20260924_191012_962_d707abe5` финализируется, `QueueBlockReason` пуст и очередь идёт без рестарта редактора. Пуш — действие вне репозитория и решение заказчика.
