Status: Реализовано (CLI 1.29.0, пакет 0.41.0, плагин 1.37.0); живая проверка на Windows — `scripts/verify-modal-dialog.ps1`

# Модальные окна редактора: как это сделано в Unity CLI и у нас

## Что сделал Unity CLI

Unity CLI 1.0.0-beta.13 (7 октября 2026): `unity status` при модальном окне (например, запрос
сохранения) оставляет состояние редактора `starting`, но меняет код ошибки `STATUS_NOT_READY` на
`STATUS_BLOCKED_BY_DIALOG` и в `--format json` добавляет поле `blockedBy` с уровнем и заголовком окна.
`unity status --until-ready` прекращает ждать, как только окно заблокировало редактор. Пакет
`com.unity.pipeline` дополнительно собирает окна, появившиеся во время команды (`dialogsDuringExecution`).

Источник данных у Unity — `UnityEditor.EditorDialogEvents` (Unity 6.7): `dialogWillShow` /
`dialogDismissed` типа `Action<DialogEventInfo>`, где `DialogEventInfo` — readonly struct с полями
`Id`, `Title`, `Message`, `Level` (`DialogIconType?`), `ButtonLabels`, `Source`
(`NativeMessageBox` / `ManagedCustomWindow`), `OpenedAtUtc`. Покрыты `DisplayDialog`-семейство и
`ShowModal`/`ShowModalUtility`; нативные файловые диалоги (`OpenFilePanel` и т.п.) — нет.

Источники: [release notes Unity CLI](https://docs.unity.com/en-us/unity-cli/release-notes),
[EditorDialogEvents](https://docs.unity.com/en-us/engine/6000.7/script-reference/unityeditor/editordialogevents.md),
[DialogEventInfo](https://docs.unity.com/en-us/engine/6000.7/script-reference/unityeditor/dialogeventinfo.md).

## Что сделано у нас

- Пакет: `EditorDialogMonitor` публикует `Library/AgentBridge/dialogs.json` с потока таймера, потому что
  главный поток стоит в модальном цикле и не пишет ни heartbeat, ни `status.json`. Источники:
  `EditorDialogEvents` через рефлексию (пакет собирается под 2022.3) и на Windows — скан окон.
- CLI: `status`/`doctor` → `code: blocked_by_dialog` + `blockedBy`; ожидание задачи завершается кодом 2
  с `Reason: editor_blocked_by_dialog`, как `--until-ready` у Unity CLI.
- Окна, открытые во время задачи, попадают строкой `modal dialog: …` в `Logs` этой задачи — аналог
  `dialogsDuringExecution`.

## Почему Windows-скан устроен именно так

Ни одна из установленных у нас версий (2022.3, 6000.3, 6000.4, 6000.6) не имеет `EditorDialogEvents`,
поэтому без скана фича ничего бы не давала. Ложная тревога хуже пропуска: она обрывает ожидания агентов
посреди обычной компиляции. Поэтому окно засчитывается, только если выполнены все условия:

1. главный поток не тикал ≥ 1 с (тикающий редактор ничем не заблокирован);
2. окно принадлежит **главному потоку** редактора — у Unity «Hold on» анимируется своим потоком,
   который качает сообщения, пока главный просто занят;
3. окно видимое, включённое, и его владелец выключен (так Win32 выражает модальность); без владельца
   допускается только нативный диалог `#32770` (task-modal message box), а `#32770` с включённым
   владельцем — немодальный и не считается;
4. в нём нет прогресс-контрола, класс не похож на прогресс/тултип/меню;
5. `SendMessageTimeout(WM_NULL, 200 мс)` проходит — модальный цикл качает сообщения, импорт и
   компиляция нет.

Для окон без `#32770` из всех кандидатов с выключенным владельцем берётся только `GetLastActivePopup(owner)`:
message box выключает лишь владельца, и плавающие окна редактора остаются включёнными. Если найден нативный
диалог, остальные кандидаты отбрасываются.

CLI дополнительно требует `MainThreadIdleMs ≥ 1,5 с` в снимке (часы редактора, без перекоса с клиентом на чужой
ОС): тикающий редактор не заблокирован, даже если событие `dialogDismissed` потерялось.

Текст окна читается `InternalGetWindowText`: `GetWindowText` для окна своего процесса шлёт
`WM_GETTEXT` и повис бы на заблокированном главном потоке.

## Что не проверено и требует живой проверки

- Класс и поток окна прогресса Unity 2022.3/6000.x (`DisplayProgressBar`, «Hold on»). Предположение:
  либо другой поток, либо прогресс-контрол, либо главный поток не отвечает на `WM_NULL`. Проверяется
  `ModalDialogProbeTests.ProgressBarIsNotReportedAsDialog`.
- Что `EditorUtility.DisplayDialog` в 2022.3 на Windows — окно главного потока класса `#32770` и
  закрывается по `WM_CLOSE`. Проверяется `ModalDialogProbeTests.NativeDialogIsReportedWhileOpen`.
- Привязка к `EditorDialogEvents` проверена только на заглушке той же формы, что в документации 6.7.
- macOS/Linux до 6.7 не распознают окна вообще (`DialogDetection: unsupported`).
