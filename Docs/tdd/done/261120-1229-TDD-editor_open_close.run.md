# 261120-1229-TDD-editor_open_close — ход исполнения

## Единицы

- [x] Факт штатного закрытия `CloseMainWindow` на Windows — снят самим `verify-editor-control.ps1` (`closed`/0, процесс вышел)
- [x] Пакет: `BridgeStatus.EditorPath`, `BridgeStatusWriter.WriteOnLoad`
- [x] CLI: `BridgeStatus`, `CliOptions` (`--force`, `--editor-path`), `EditorLocator.cs`
- [x] CLI: `EditorCommands.cs`, `AgentBridgeApplication.cs` (ветка `editor`, help), `EditorCommandTests`
- [x] `scripts/verify-editor-control.ps1`
- [x] Документация, версии (CLI 1.31.0, пакет 0.47.0, плагин 1.40.0), сборка плагина

## Решения

- Отдельный ручной замер `CloseMainWindow` в PowerShell не делался: скрипт приёмки проверяет то же на реальном процессе.
- Вывод JSON — camelCase (`code`, `pid`, `detail`), как у всех команд через `JsonSupport.Output`; поля ТДД с заглавной — имена C#.
- `editor_control_foreign_host` и `bad_usage` выводятся через `WriteError` (код 3), без полей исхода.
- `EditorLocator.CandidatesHere` — те же кандидаты с реальным окружением, для `Detail` у `editor_not_found`.
- При пустой версии проекта кандидаты Hub не строятся; остаётся только `--editor-path`.
- Пути кандидатов собираются разделителем целевой ОС, а не текущей машины — чтобы проверять порядок macOS на Windows.
- `--force` убивает только процесс редактора (`Kill()`), не дерево: дерево может включать диспетчер очереди.
- Тест `editor_not_found` использует несуществующую версию `0.0.0agentbridge`: на машине разработчика Hub может держать 2022.3.62f2.

## Решения за пользователя

- нет

## Упрощения

- Запуск на macOS/Linux перенаправляет stdio Unity в каналы, которые закрываются с выходом CLI (как у `QueueClient.Start`) — предел: Unity, пишущая в stdout после выхода CLI, может получить SIGPIPE; пересмотреть при первой живой проверке на macOS/Linux.

## Проверки

- `dotnet build` CLI — 0 ошибок, 0 предупреждений.
- `AgentBridgeCli.Tests` — PASS, включая `Editor control`.
- Unity compile пакета 0.47.0 — success (первый прогон stale после reload от смены версии, повтор success); `status.json` содержит `EditorPath`.
- `verify-editor-control.ps1` — все шаги PASS: PID 49364 закрыт штатно, 22180 запущен и убит `--force`, 78580 запущен; задача, принятая при закрытом редакторе, — success.
- `build-plugin.ps1` — `invalid_entries=0`, `zip_validation=PASS`.

## Замечания

- `close-again` (`not_running`) отдаёт `pid` мёртвого редактора из старого `status.json` — безвредно, но может путать.
