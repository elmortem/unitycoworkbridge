# Ход исполнения: cache_claim_reply

## Единицы

- [x] `QueueEditorAdapter.cs`: `LastCallAwaitingReply` в `TryCall`
- [x] `CachedResultServer.cs`: константы, `ReserveCacheAsync`, `Skip`, причины в обеих ветках, `TryReserveCache` удалён
- [x] Пакет 0.40.0 → 0.40.1, плагин пересобран
- [x] `Docs/PROJECT_MAP.md`: строка `CachedResultServer.cs`

## Решения

- `CoordinationGate.TryReserve` может вернуть `Busy` без вызова `TryCall` (активное окно координации), поэтому флаг устаревал бы от чужого вызова. `TryCall` сбрасывает флаг на входе, а `ReserveCacheAsync` сбрасывает его перед каждой попыткой (`internal set`).

## Проверки

- compile: success; повторный compile — `Cached: true`, источник — первая задача.
- EditMode `AgentBridgeTestSelectionTests`: 14/14; повтор — `Cached: true`, `SourceTaskId` первого прогона.
- PlayMode `AgentBridgeTestSelectionPlayModeTests`: 1/1; повтор — `Cached: true`, Play Mode не запускался.
- Новый файл в `Assets/` → обычный прогон 14/14 (ранний промах по дайджесту `cache_lookup`), `cache_skip` с `claim_*` нет.
- build-plugin: `version_check=PASS`, `zip_validation=PASS`.
