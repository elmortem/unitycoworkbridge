# 261018-0015-TDD-results_survive_script_edits — ход исполнения

## Единицы

- [x] `CompileFingerprint.IsSource`, `NonSourceEventCount`, `NonSourceChanged`
- [x] `EvidenceRecord.StartStateOnly`, перегрузка `TryComplete`, `ClassifyChange`, `NonSourceEventsKey`
- [x] `AgentTestRunner`: ключи цикла и результатов, `RunFinished`, `OnError`, `TryFinalizeCoordinatorRun`, `promoted`, ветка `Requeue`
- [x] Тесты `AgentBridgeEvidenceTests`
- [x] Версии: пакет 0.39.0→0.40.0, плагин 1.35.0→1.36.0 (от значений рабочего дерева); плагин пересобран
- [x] Документация: SKILL.md, UNITYAGENT.md, README.md

## Решения

- `InputStatManifest` и `ValidationInputMonitor` линкуются в `AgentBridgeCoordination.Tests` → туда добавлен `CompileFingerprint.cs`, в `BatchHost.cs` заглушки `BridgePaths.ProjectRoot` и `CompileInputContext`.
- Новые ключи результатов стираются и в `FinalizeCancellation`, чтобы отменённый прогон не оставлял их.
- Ключ флага — bool, поэтому стирается через `SessionState.EraseBool`.

## Решения за пользователя

- нет

## Упрощения

- нет

## Проверки

- `AgentBridgeCoordination.Tests --group all` — PASS; `AgentBridgeCompile.Tests` — PASS.
- Unity compile — success; EditMode `AgentBridgeEvidenceTests` — 11/11.
- Живой PlayMode `ResponsiveLongRun`, правка и возврат `.cs` посреди прогона → `success`, `valid`, `StartStateOnly: true`, 1/1.
- Тот же прогон, правка и возврат `.txt` в Assets → `stale_input`.
- Повтор `ShortSuccessfulRun` при неизменных входах → `success`, но `Cached: false` (см. Замечания).
- Сценарий WaterWalk (NRE TF 1.6 после reload) в хосте на 2022.3 не воспроизводится; путь `OnError` после `RunFinished` проверен только чтением кода.

## Замечания

- Кэш тестов не отдаёт результат и до этой правки: в телеметрии 2026-10-16 (пакет 0.38) уже `cache_verify` → `Cached: false`, и ни одного `tests`-хита во всех логах хоста. Отказ молчаливый: `CachedResultServer.cs` после `cache_verify` уходит через одно из `continue` без `cache_skip` (повторный `TryServe`, сверка дайджестов/источников, `CanPublish`, `TryReserveCache`). Нужно отдельное ТДД.
