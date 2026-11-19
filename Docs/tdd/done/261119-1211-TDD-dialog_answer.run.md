# 261119-1211-TDD-dialog_answer — ход исполнения

## Единицы

- [x] Факт на хосте: скан читает кнопки `DisplayDialog` (`[OK | Cancel]`)
- [x] `DialogAnswerRequest.cs` (+ .meta), подключение в `AgentBridgeCli.csproj`
- [x] Пакет: `BridgePaths`, `NativeDialogProbe.Press`/`ButtonHandles`, `EditorDialogSnapshot.Answer`
- [x] Пакет: `EditorDialogMonitor` (запрос, `Answer`, лог, телеметрия), capability в `BridgeStatusWriter`
- [x] CLI: `BridgePaths`, `--button`, `ReadSnapshot`/`Answer`/`id` в `Describe`, `DialogCommands`, диспетчер и help
- [x] Тесты: `DialogAnswerTests`, правка `RunFormattingTests`, `ModalDialogProbeTests.AnswerPressesNamedButton`, третий блок `verify-modal-dialog.ps1`
- [x] Документация, версии (CLI 1.30.0, пакет 0.46.0, плагин 1.39.0), сборка плагина

## Решения

- `id` в `EditorDialogReader.Describe` добавляется только при непустом `Id` — строка без id не получает пустое `id `.
- Исход команды собран в объект `Outcome`, отказы до записи запроса (`dialog_not_open`, `button_not_found`, `ambiguous_button`, `dialog_answer_unsupported`) выводятся тем же JSON, что и ответ редактора; `bad_usage` — через `WriteError`.
- `Button` в выводе — метка окна (`Gamma`), а не ввод агента (`gamma`).
- Unity раскладывает кнопки `DisplayDialogComplex` как ok, alt, cancel (`Alpha | Gamma | Beta`) — скрипт сверяет набор кнопок, а не порядок.
- Отвеченное окно, ещё не убранное сканом к концу задачи, в логе описывается как закрытое (`held the editor for`), а не `is still open`.
- Страховочный поток пробника отменяется после возврата окна, чтобы через 60 с не закрыть чужое окно; `CloseAfter` получил необязательный `done`.
- `verify-modal-dialog.ps1`: исправлена старая проверка `BlockedBy` соседа — в Windows PowerShell 5.1 у одиночного `PSCustomObject` нет `.Count`, нужен `@()`.
- Тест «без файла запроса» держит на месте файла каталог: попытка записи провалила бы команду.

## Решения за пользователя

- нет

## Упрощения

- нет

## Проверки

- `dotnet build` CLI Release — 0 предупреждений; `AgentBridgeCli.Tests` — PASS (включая `Dialog answer` и `Editor dialogs`).
- Unity `compile` пакета 0.46.0 — success.
- `scripts/verify-modal-dialog.ps1` на хосте Unity 2022.3.62f2 — 6/6 PASS: `button_not_found`/1 при открытом окне, `answered`/0, `AnswerPressesNamedButton` зелёный (результат 2), сосед исполнен, лог `… answered "Gamma" by <сессия>: verification`.
- `build-plugin.ps1` — `version_check=PASS`, `frontmatter_validation=PASS`, `invalid_entries=0`, `zip_validation=PASS`.

## Замечания

- У пробника `DisplayDialog` скан даёт пустой `message`: текст окна не в `Static`-детях. Детекции и ответу не мешает.
