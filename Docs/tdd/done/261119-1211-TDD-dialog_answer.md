Status: Выполнено

# Ответ агента на модальное окно

Агент получает команду `agentbridge dialog answer <DialogId> --button <текст> --reason <текст>`: она нажимает названную кнопку нативного модального окна Unity на Windows, пока окно держит главный поток. Запрос приходит файлом, нажимает фоновый поток `EditorDialogMonitor`, результат возвращается в `dialogs.json`. Результат: окно `EditorUtility.DisplayDialogComplex` закрывается с кодом выбранной кнопки без человека, очередь идёт дальше, в логах задачи записано, кто и что нажал. Первая версия — только Windows и только окна, у которых скан прочитал кнопки.

## Общий код пакета и CLI

### `Editor/DialogAnswerRequest.cs` — новый файл

Namespace `AgentBridge`, без `UnityEngine`. В `AgentBridgeCli.csproj` подключить рядом с `TaskCancellationPolicy.cs`: `<Compile Include="../AgentBridgeUnity/Packages/com.elmortem.agentbridge/Editor/DialogAnswerRequest.cs" Link="DialogAnswerRequest.cs" />`.

```csharp
public sealed class DialogAnswerRequest
{
	public const string Capability = "dialog-answer-v1";
	public const int ReasonLimit = 200;
	public const int NotFound = -1;
	public const int Ambiguous = -2;

	public const string Sent = "sent";
	public const string NotBlocked = "not_blocked";
	public const string DialogNotOpen = "dialog_not_open";
	public const string DialogChanged = "dialog_changed";
	public const string ButtonNotFound = "button_not_found";
	public const string AmbiguousButton = "ambiguous_button";
	public const string SendFailed = "send_failed";

	public string RequestId = "";
	public int EditorPid;
	public string DialogId = "";
	public string Title = "";
	public string Button = "";
	public string Session = "";
	public string Reason = "";

	// Семь строк "Имя=значение" в порядке полей, LF; каждое значение проходит OneLine.
	public string Format();
	// Значение — всё после первого '='. null, если пусты RequestId, DialogId или Button
	// либо EditorPid не число.
	public static DialogAnswerRequest Parse(string text);
	// '\r' и '\n' → пробел, затем Trim; null → "".
	public static string OneLine(string value);
	// Сравнение по OneLine без учёта регистра (OrdinalIgnoreCase), '’' и '‘' приравнены к '\''.
	// Индекс единственного совпадения; NotFound; Ambiguous, если совпало больше одной.
	public static int MatchButton(string[] labels, string wanted);
}
```

## Пакет `com.elmortem.agentbridge`

### `Editor/BridgePaths.cs`

- `public static string DialogAnswerFile` → `Path.Combine(WorkingRoot, "dialog-answer.txt")`.

### `Editor/NativeDialogProbe.cs`

- `Window`: поле `public long[] ButtonHandles;`, параллельное `Buttons`. В `Scan` рядом с `buttons.Add(label)` добавлять `child.ToInt64()` в свой список, в результат класть `ToArray()`.
- Новый метод, константа `WmCommand = 0x0111`, импорты `GetDlgCtrlID` и `PostMessageW`:

```csharp
// Не ждёт главный поток: PostMessage только ставит сообщение в очередь модального цикла.
public static bool Press(long window, long button)
{
#if UNITY_EDITOR_WIN
	var buttonHandle = new IntPtr(button);
	int id = GetDlgCtrlID(buttonHandle);
	if (id == 0)
	{
		return false;
	}

	// BN_CLICKED == 0, поэтому wParam целиком — идентификатор кнопки.
	return PostMessage(new IntPtr(window), WmCommand, new IntPtr(id), buttonHandle);
#else
	return false;
#endif
}
```

### `Editor/EditorDialogSnapshot.cs`

- `Build(int editorPid, long updatedUtcMs, long mainThreadIdleMs, IList<EditorDialogInfo> dialogs, string answerRequestId, string answerOutcome, string answerDetail)`. При непустом `answerRequestId` после массива `Dialogs` дописывается `"Answer": {"RequestId": "…", "Outcome": "…", "Detail": "…"}` (через `Escape`, `Detail` ограничен `MessageLimit`).

### `Editor/EditorDialogMonitor.cs`

- Поля под `Gate`: `_handledRequestId`, `_answerRequestId`, `_answerOutcome`, `_answerDetail`; вне блокировки `_answerFile`. В `Tracked`: `AnswerButton`, `AnswerSession`, `AnswerReason`.
- `Start()`: `_answerFile = BridgePaths.DialogAnswerFile`; оставшийся от прошлого домена файл удалить в `try/catch`.
- `ReadRequest()`: нет файла — `null`; иначе `Coordination.SharedFile.TryReadAllText` и `DialogAnswerRequest.Parse`; исключение — `null`.
- `OnTimer`: сохранить `bool scanned = _enabled && stalled && NativeDialogProbe.Supported` и использовать его в условии скана; после скана, до блокировки — `DialogAnswerRequest request = ReadRequest()`. Внутри блокировки вместо пары `Reconcile` / `PublishLocked`:

```csharp
Reconcile(windows, now);
bool answered = false;
if (request != null && request.EditorPid == _processId && request.RequestId != _handledRequestId)
{
	_handledRequestId = request.RequestId;
	_answerRequestId = request.RequestId;
	_answerOutcome = Answer(request, windows, scanned, out _answerDetail);
	answered = true;
}

PublishLocked(now, answered);
```

- `PublishLocked(long now, bool force = false)`: ранний выход становится `Open.Count == 0 && !_published && !force`; в `Build` передаются три поля ответа. `Shutdown` передаёт пустые.
- Новый метод:

```csharp
private static string Answer(DialogAnswerRequest request, List<NativeDialogProbe.Window> windows, bool scanned, out string detail)
{
	detail = "";
	if (!scanned)
	{
		return DialogAnswerRequest.NotBlocked;
	}

	Tracked tracked;
	if (!Open.TryGetValue(request.DialogId, out tracked))
	{
		return DialogAnswerRequest.DialogNotOpen;
	}

	if (DialogAnswerRequest.OneLine(tracked.Info.Title) != DialogAnswerRequest.OneLine(request.Title))
	{
		detail = tracked.Info.Title;
		return DialogAnswerRequest.DialogChanged;
	}

	// Окно из событий Unity (6.7+) не несёт HWND: его нативное окно находится по заголовку.
	bool byHandle = tracked.Info.Source == "win32";
	NativeDialogProbe.Window window = windows.Find(found => found.NativeDialog && (byHandle
		? "win32:" + found.Handle.ToString("x", CultureInfo.InvariantCulture) == request.DialogId
		: string.Equals(found.Title, tracked.Info.Title, StringComparison.OrdinalIgnoreCase)));
	if (window == null)
	{
		return DialogAnswerRequest.DialogNotOpen;
	}

	int index = DialogAnswerRequest.MatchButton(window.Buttons, request.Button);
	if (index < 0)
	{
		detail = string.Join(" | ", window.Buttons);
		return index == DialogAnswerRequest.Ambiguous ? DialogAnswerRequest.AmbiguousButton : DialogAnswerRequest.ButtonNotFound;
	}

	if (!NativeDialogProbe.Press(window.Handle, window.ButtonHandles[index]))
	{
		return DialogAnswerRequest.SendFailed;
	}

	tracked.AnswerButton = window.Buttons[index];
	tracked.AnswerSession = request.Session;
	tracked.AnswerReason = request.Reason;
	return DialogAnswerRequest.Sent;
}
```

- `Describe`: при непустом `AnswerButton` дописать в конец `; answered "<кнопка>" by <сессия или agent>: <причина>`.
- Телеметрия `editor_dialog`: поля `Answer`, `AnswerSession`, `AnswerReason` (пустые без ответа).

### `Editor/BridgeStatusWriter.cs`

- `BuildCapabilities()`: добавить `DialogAnswerRequest.Capability`, если `NativeDialogProbe.Supported`.

## CLI `AgentBridgeCli`

### `BridgePaths.cs`

- `DialogAnswerFile` = `Path.Combine(WorkingRoot, "dialog-answer.txt")`.

### `CliOptions.cs`

- `public string? Button`, флаг `--button` через `TryTakeText`, как `--reason`.

### `EditorDialogReader.cs`

- `Snapshot.Answer` типа `AnswerResult { RequestId, Outcome, Detail }` (строки, по умолчанию `""`), может быть `null`.
- `public static Snapshot? ReadSnapshot(string path)` — разбор файла без отбора, `null` при отсутствии и при ошибке; `Read` использует его.
- `Describe`: в список `details` после длительности добавить `"id " + dialog.Id`.

### `DialogCommands.cs` — новый файл

```csharp
internal static class DialogCommands
{
	internal static int AcknowledgeMs = 5000;
	internal static int ForeignAcknowledgeMs = 15000;
	internal static int CloseMs = 5000;
	private const int PollMs = 200;

	public static async Task<int> RunAsync(string projectRoot, BridgePaths paths, BridgeHealth health,
		string[] arguments, CliOptions options, TelemetryLog telemetry);
}
```

Алгоритм `RunAsync`:

- Разбор: `arguments` = `answer <DialogId>`, `--button` и `--reason` непустые, причина не длиннее `ReasonLimit`. Иначе `bad_usage` через `AgentBridgeApplication.WriteError`.
- В `health.Bridge.Capabilities` нет `DialogAnswerRequest.Capability` → `dialog_answer_unsupported`.
- `target` = окно из `health.BlockedBy` с этим `Id`; нет → `dialog_not_open`.
- `MatchButton(target.Buttons, options.Button)` < 0 → `button_not_found` или `ambiguous_button`, `Detail` — кнопки через ` | ` (или `the bridge cannot read this dialog's buttons` при пустом списке). Файл запроса не создаётся.
- Записать запрос `SharedFile.WriteAtomic(paths.DialogAnswerFile, request.Format())`: `RequestId = Guid.NewGuid().ToString("N")`, `EditorPid = health.Bridge.EditorPid`, `Title = target.Title`, `Session = options.Session ?? ""`.
- Каждые `PollMs` читать `ReadSnapshot`. Пока `Answer?.RequestId` не наш — ждать до `AcknowledgeMs` (`ForeignAcknowledgeMs` при `health.ForeignHost`), затем `answer_unacknowledged`. `Outcome` не `sent` → он и есть код, `Detail` из снимка. При `sent` ждать до `CloseMs`, пока в `Dialogs` снимка есть `Id`; исчез → `answered`, остался → `dialog_still_open`.
- В `finally` удалить файл запроса.
- Вывод через `JsonSupport.Write`: `Ok`, `Code`, `DialogId`, `Title`, `Button`, `Detail`, `BlockedBy` (свежий `BridgeInspector.Inspect(projectRoot).BlockedBy`). В `human`: `dialog: answered "<Title>" with "<Button>"` либо `dialog: <Code> — <Detail>`, затем строки `Blocked by dialog: …` для оставшихся окон.
- Телеметрия `cli_dialog_answer`: `Dialog`, `Button`, `Code`.

| Код | Выход |
|---|---|
| `answered` | 0 |
| `dialog_not_open`, `button_not_found`, `ambiguous_button`, `not_blocked`, `dialog_changed`, `send_failed`, `dialog_still_open` | 1 |
| `answer_unacknowledged` | 2 |
| `bad_usage`, `dialog_answer_unsupported` | 3 |

### `AgentBridgeApplication.cs`

- Перед `switch (command)`: `if (command == "dialog") return await DialogCommands.RunAsync(projectRoot, paths, health, commandArguments, options, telemetry);`.
- `WriteHelp`: строка `dialog answer <DialogId> --button LABEL --reason TEXT`.

## Тесты

- `DialogAnswerTests` (`AgentBridgeCli.Tests`, вызов из `Program.cs` после `EditorDialogTests.RunAsync`):
	- `Format` → `Parse` сохраняет все поля; причина с `=` и переводом строки; `Parse` мусора даёт `null`.
	- `MatchButton`: регистр и пробелы; `Don’t Save` находит `Don't Save`; нет кнопки → `NotFound`; две `OK` → `Ambiguous`.
	- Команда против поддельного редактора (фоновая задача читает `dialog-answer.txt` и переписывает `dialogs.json`; таймауты уменьшены через internal-поля): `answered`/0; `sent` без исчезновения окна → `dialog_still_open`/1; молчание → `answer_unacknowledged`/2; неизвестная кнопка → `button_not_found`/1 без файла запроса; статус без capability → `dialog_answer_unsupported`/3; после каждого исхода файла запроса нет.
- `ModalDialogProbeTests.AnswerPressesNamedButton` (EditMode, `Explicit`, только Windows): `EditorUtility.DisplayDialogComplex("AgentBridge answer probe", …, "Alpha", "Beta", "Gamma")`, страховочный поток `CloseAfter` через 60 с, `Assert.AreEqual(2, result)` — `Gamma` стоит в позиции `alt`.
- Существующие тесты, которые затрагивает изменение: `EditorDialogTests.RunFormattingTests` (в строке `Describe` появляется `id …`).

## Порядок работ

- Снять факт на свободном Windows-хосте существующим `scripts/verify-modal-dialog.ps1`: в `status-blocked.json` у окна пробника `buttons` равно `["OK", "Cancel"]`. Если список пуст — остановиться: скан не читает кнопки `DisplayDialog`.
- `DialogAnswerRequest.cs`, подключение в `AgentBridgeCli.csproj`, чистые случаи `DialogAnswerTests`.
- Пакет: `BridgePaths.cs`, `NativeDialogProbe.cs`, `EditorDialogSnapshot.cs`.
- Пакет: `EditorDialogMonitor.cs`, `BridgeStatusWriter.cs`.
- CLI: `BridgePaths.cs`, `CliOptions.cs`, `EditorDialogReader.cs`, `DialogCommands.cs`, `AgentBridgeApplication.cs`; командные случаи `DialogAnswerTests`, правка `RunFormattingTests`.
- `ModalDialogProbeTests.AnswerPressesNamedButton` и третий блок в `scripts/verify-modal-dialog.ps1`: поставить пробник с `--wait 0`, дождаться `blocked_by_dialog` с заголовком `AgentBridge answer probe` и кнопками `Alpha`, `Beta`, `Gamma`, затем проверить признаки «Приёмки» по порядку: сначала `--button Delta`, потом `--button gamma --reason "verification" --session <сессия>`.
- Документация по разделу ниже; версии CLI, пакета и плагина и пересборка плагина — по правилу 2 `CLAUDE.md`.

## Приёмка

- `DisplayDialogComplex` в пробнике возвращает `2` после `dialog answer --button gamma` — `AnswerPressesNamedButton` зелёный в `verify-modal-dialog.ps1`.
- Неизвестная кнопка: код выхода 1, `button_not_found`, `status` по-прежнему `blocked_by_dialog` — тот же скрипт.
- После ответа `status` не `blocked_by_dialog`, сосед в очереди исполняется — тот же скрипт.
- В `Logs` задачи пробника строка `modal dialog: "AgentBridge answer probe" … answered "Gamma" by <сессия>: verification` — тот же скрипт.
- Коды выхода 0/1/2/3 и уборка файла запроса — `DialogAnswerTests`.

## Документация

- `unity-bridge-plugin/skills/unity-bridge/SKILL.md`: в «Команды CLI» добавить `dialog answer`; в разделе «Модальное окно в Unity» список «Что делать» заменить на:
	1. Не переподавай задачу и не жди в цикле с коротким `--wait`.
	2. Реши сам: ответить на окно или позвать человека. Отвечай сам, когда понимаешь, что произойдёт после нажатия, и это следует из твоей задачи: окно вызвала твоя задача (`taskId` совпадает) либо ответ ничего чужого не теряет (информационное «OK», отмена выбора файла). Зови человека, когда кнопка необратимо теряет или меняет чужую работу (несохранённые правки человека или другого агента, удаление, обновление API или проекта), когда окно появилось без твоей задачи и его смысл неочевиден, когда `buttons` пуст или ответ не сработал. Человеку передай заголовок, текст и кнопки как есть.
	3. Ответ: `agentbridge dialog answer <id> --button "<текст из buttons>" --reason "<почему эта кнопка>" --session <id>`. `id` — поле `id` окна в `blockedBy`. Код 0 и `answered` — окно закрыто; код 1 — отказ, причина в `Code` и `Detail`; код 2 `answer_unacknowledged` — редактор не отозвался, зови человека. Нужны capability `dialog-answer-v1` и редактор на Windows.
	4. Прочитай `BlockedBy` в ответе: следом могло открыться другое окно. Затем продолжи `agentbridge wait <TaskId> --wait <секунды>`.
	5. Иначе окно не закрывай: ни скриптом, ни `ExecuteMenuItem`, ни эмуляцией кликов.
	6. Прежний пункт про совпадение `taskId` и исправление скрипта остаётся.
- `UNITYAGENT.md` пакета: строки про `blocked_by_dialog` — та же команда и правило выбора одним абзацем.
- `README.md`, «Modal Dialogs»: `dialog answer`, таблица кодов, capability, ограничения; строка в «Limitations».
- `Docs/PROJECT_MAP.md`: команда `dialog` и `DialogCommands.cs` в разделе 5; `dialog-answer.txt` в раскладке на диске; в 6.12 — `DialogAnswerRequest.cs`, capability и новая роль монитора; раздел 11 — третий блок `verify-modal-dialog.ps1`.
- `CLAUDE.md`: `dialog` в списке команд CLI.
