# 261016-1540-TDD-dispatcher_host — ход исполнения

## Единицы

- [x] `QueueFiles.HostCapability`; `QueueRuntime.Artifact`; `DispatcherHostPolicy.cs`
- [x] `BridgeHealth.DispatcherOwner`, присвоение в `Inspect`, строка `Dispatcher:`
- [x] `QueueClient.cs`: `launch.json` только от родного клиента, отложенный запуск, `Reason`
- [x] `QueueServer.cs`: ежесекундный `Inspect`, передача владения, правило уступки, простой
- [x] Пакет: capability, `EnsureServer` из `Tick`, предупреждение о недоступном CLI
- [x] `DispatcherHostTests.cs` и вызов из `Program.cs`
- [x] Версии CLI 1.26.0, пакет 0.37.0, плагин 1.33.1; плагин пересобран и обновлён в Claude Code
- [x] Документация, карта
- [x] Живая проверка: простой 5 минут, EditMode

## Решения

- `QueueRuntime.IsCurrent` бросал исключение на несуществующем пути (launcher другой ОС) и молча ронял сервер → возвращает false, если артефакта нет.
- `EnsureServer` из `Tick` обёрнут в try: исключение `Process.Start` предупреждает один раз за домен, а не каждые 3 с.
- `DispatcherHostPolicy.cs` добавлен в выжимку CLAUDE.md рядом с остальными CLI-файлами.

## Решения за пользователя

- Для живой проверки локальная сборка CLI 1.26.0 установлена в `%LOCALAPPDATA%\AgentBridge\bin`; прежняя 1.24.3 сохранена как `agentbridge-1.24.3-backup-20261016.exe`.

## Упрощения

- нет

## Проверки

- `AgentBridgeCli.Tests`: PASS, включая `Dispatcher host` и затронутые очередные наборы.
- `build-plugin.ps1`: `invalid_entries=0`, `zip_validation=PASS`.
- Компиляция пакета в `AgentBridgeUnity`: success; `status.json` содержит `dispatcher-host-v1`; `DispatcherOwner: any_client`.
- После `queue shutdown` редактор поднял новый сервер через ~1 с, heartbeat свежий без вызовов CLI.
- Простой: тот же сервер (прежний `server.json`) жив через 5,5 мин без задач, heartbeat свежий.
- EditMode `TaskQueueWindowTests` — 3/3, evidence valid.

## Замечания

- `tests --assembly AgentBridge.ProbeTests` захватывает `[Explicit]` repro-тесты `QueueTimeoutReproTests`; их инъекция подменяет итог всего прогона на `runtime_error: Controlled reproduction cleanup` (дважды). Не связано с ТДД; фильтр по сборке не должен включать Explicit-тесты — материал для отдельной правки.
