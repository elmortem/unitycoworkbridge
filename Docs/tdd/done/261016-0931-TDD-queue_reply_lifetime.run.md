# Ход исполнения: queue_reply_lifetime

## Единицы
- [x] `QueueFiles.ReleaseReply`
- [x] `BridgeClient`: опрос `wait` раз в секунду в пределах бюджета
- [x] `QueueClient`: освобождение ответа, `AcknowledgeTimeoutMs`, `starts`, `queue_unacknowledged`
- [x] `QueueEditorAdapter.TryCall`: освобождение ответа
- [x] `QueueServer`: фоновая очистка, `SweepReplies`, `SweepAndLog`, `ReportSlow` (heartbeat/request/sync)
- [x] `QueueReplyLifetimeTests.cs` + вызов из `Program.cs`
- [x] Версии: CLI 1.25.0, пакет 0.36.1; плагин пересобран
- [x] README (Replies, телеметрия), PROJECT_MAP (QueueClient, QueueServer)

## Решения
- `unacknowledged_is_logged` разбирает строки журнала через `JsonDocument`, а не ищет подстроку: не зависит от форматирования сериализатора.

## Проверки
- `AgentBridgeCli.Tests`: PASS, включая `Queue reply lifetime` и `RunQueueBudgetTests`.
- `build-plugin.ps1`: `invalid_entries=0`, `zip_validation=PASS`.
- Unity 2022.3.62f2: пакет 0.36.1 компилируется; EditMode 114 passed / 0 failed / 4 skipped.
- После compile + tests через CLI в `Replies/` 4 файла.
- 50 000 ответов возрастом −2 сут: за 3 мин heartbeat не старше 1024 мс, `queue list` ≤ 324 мс, `queue_cleanup` Deleted=50000, ElapsedMs=3441, Host=windows.

## Замечания
- Полный EditMode-прогон вернул `stale_input` (вход изменился и восстановился во время прогона) — к правке не относится.
