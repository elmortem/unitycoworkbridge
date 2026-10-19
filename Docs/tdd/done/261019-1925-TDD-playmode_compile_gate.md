Status: Выполнено

# PlayMode-тесты отказывают при красной компиляции

Unity не входит в Play Mode, пока в скриптах есть ошибки компиляции, а Test Framework ждёт этого входа бесконечно. В waterwalk 18–19.10 из-за красной `MemePaint.cs` соседней сессии каждый PlayMode-прогон висел от 20 минут до 5,2 часа, пока его не снимали вручную. Результат: задача `tests --mode PlayMode` при `EditorUtility.scriptCompilationFailed` сразу, без запуска Test Framework, завершается со статусом `compiler_error`, `ForeignErrors: true`, а файлы и строки из последнего цикла компиляции попадают в `Diagnostics`. EditMode-тесты не меняются. Скилл агента дополняется правилом форс-мажора: если владелец ошибки недоступен или не чинит её 15 минут, агент чинит сам.

## Пакет `com.elmortem.agentbridge`

### `Editor/CompileTaskExecutor.cs`

- Добавить публичный метод рядом с `LastCycleFinishedUtc`. Он читает уже сохраняемый `CycleErrorsKey`, то есть ошибки последнего завершившегося цикла, включая чужой цикл без задачи:

```csharp
public static List<TaskDiagnostic> LastCycleErrors()
{
	try
	{
		var list = JsonUtility.FromJson<TaskDiagnosticList>(SessionState.GetString(CycleErrorsKey, ""));
		if (list == null || list.Items == null)
		{
			return new List<TaskDiagnostic>();
		}

		return list.Items;
	}
	catch
	{
		return new List<TaskDiagnostic>();
	}
}
```

### `Editor/TaskCoordinator.cs`

- В `RunTask`, ветка `case "tests":`, перед `BeginValidationPrepare(request, "tests")`:

```csharp
case "tests":
	if (request.TestMode == "PlayMode" && EditorUtility.scriptCompilationFailed)
	{
		RefusePlayModeOnCompileErrors(request);
		break;
	}
	BeginValidationPrepare(request, "tests");
	break;
```

`EditorApplication.isCompiling` отдельно не проверяется: скан очереди перед `TryStartNextTask` выходит при `EditorApplication.isCompiling`. Значит, в `RunTask` флаг `scriptCompilationFailed` отражает уже завершившийся цикл.

- Новый приватный метод рядом с `StartResolvedTestsTask`. Отказ повторяет ветку `!started` из `StartResolvedTestsTask`: Test Framework запуск не получил, поэтому наблюдатель сцен снимается здесь же. Снимок входов (`ValidationEvidence`) ещё не начат, поэтому `Abort` не нужен.

```csharp
private static void RefusePlayModeOnCompileErrors(TaskRequest request)
{
	List<TaskDiagnostic> errors = CompileTaskExecutor.LastCycleErrors();
	string message = PlayModeCompileGateMessage(errors);
	SceneDirtyWatcher.Disarm(request.Id);
	_activeRecord.Diagnostics = errors;
	_activeRecord.Tests = new TestRunResult { aborted = true, message = message };
	FinishTask("compiler_error", null, new List<string> { message }, true);
}

private static string PlayModeCompileGateMessage(List<TaskDiagnostic> errors)
{
	var files = new List<string>();
	foreach (TaskDiagnostic error in errors)
	{
		if (!string.IsNullOrEmpty(error.File) && !files.Contains(error.File))
		{
			files.Add(error.File);
		}
	}

	if (files.Count == 0)
	{
		return "playmode_blocked_by_compile_errors: Unity cannot enter Play Mode while scripts have compile errors; no diagnostics from the last compile cycle, run 'agentbridge compile' to list them";
	}

	return "playmode_blocked_by_compile_errors: Unity cannot enter Play Mode while scripts have compile errors in " + string.Join(", ", files);
}
```

- `using UnityEditor;` в файле уже есть: `EditorApplication` используется.

### `package.json`

- Поднять `version` на minor: новое поведение.

## Плагин `unity-bridge-plugin`

### `skills/unity-bridge/SKILL.md`

- В раздел `### tests ...` после абзаца о `--test` добавить: «`--mode PlayMode` при ошибках компиляции проекта не запускается и не ждёт: задача сразу возвращает `compiler_error` с `ForeignErrors: true`, файлами и строками из последнего цикла компиляции в `Diagnostics` и строкой `playmode_blocked_by_compile_errors` в `Logs`. Код выхода `1`. Поступай по разделу `compiler_error` + `ForeignErrors == true`. EditMode при красной компиляции исполняется на последних успешно собранных сборках».
- В раздел `### Status == "compiler_error" И ForeignErrors == true`, после пункта 5, добавить пункт 6:

  «6. Форс-мажор: самостоятельное исправление. Если владелец ошибки не отвечает по межагентскому каналу или не зарегистрирован в координации, либо через 15 минут после уведомления диагностика не изменилась, исправь ошибку сам, чтобы вернуть компиляцию и продолжить свою работу. Это исключение из запрета менять чужой код без согласования. Ради него не нужно ждать пользователя: агенты могут работать без присмотра. Правка может затронуть несколько файлов. Восстанавливай намерение автора по соседнему коду, истории `git log -p` и его ТДД. Если намерение не восстановить, верни затронутый код к последнему компилирующемуся состоянию. Не рефакторь и не дописывай чужую фичу сверх того, что нужно для компиляции. Работай через `edit-begin`/`edit-end`, затем выполни `compile`. После исправления сообщи владельцу по межагентскому каналу и запиши в свой отчёт: какие файлы и строки изменены, почему и как это откатить».

### `.claude-plugin/plugin.json`

- Поднять `version` на minor.

## Тесты

- Нет. Ветка опирается на флаг Unity, который в EditMode-тесте без настоящей красной компиляции не поднять. Поведение подтверждает живая приёмка.
- Существующие тесты не затрагиваются: путь EditMode и PlayMode при зелёной компиляции не меняется.

## Порядок работ

- `CompileTaskExecutor.cs`: добавить `LastCycleErrors()`.
- `TaskCoordinator.cs`: проверка в `RunTask`, методы `RefusePlayModeOnCompileErrors` и `PlayModeCompileGateMessage`.
- `SKILL.md`: абзац в `tests` и пункт 6 в разделе `ForeignErrors == true`.
- `README.md` и `UNITYAGENT.md`: по разделу «Документация».
- Поднять версии пакета и плагина, пересобрать плагин `scripts/build-plugin.ps1`.
- Живая приёмка в `AgentBridgeUnity`.

## Приёмка

- В хост-проект `AgentBridgeUnity` положен временный `Assets/CompileGateProbe.cs` с ошибкой (`int x = "a";`), выполнен `agentbridge compile` → `compiler_error` с этим файлом. После этого `agentbridge tests --mode PlayMode --test <любой PlayMode-тест хоста> --format json` завершается за секунды кодом выхода `1`. В ответе `Status: compiler_error`, `ForeignErrors: true`, в `Diagnostics` есть `CompileGateProbe.cs` с номером строки, в `Logs` строка `playmode_blocked_by_compile_errors ... CompileGateProbe.cs`.
- В том же состоянии `agentbridge tests --mode EditMode --test <EditMode-тест хоста>` исполняется как раньше.
- После удаления `CompileGateProbe.cs` и зелёного `compile` тот же PlayMode-прогон выполняется и возвращает результаты тестов.
- Следующая задача из очереди после отказа стартует сразу: в `Logs/AgentBridge-editor-*.jsonl` у отказанной задачи `task_finish` со `Status: compiler_error`, следом `task_start` следующей задачи без паузы.
- `scripts/build-plugin.ps1` заканчивается `invalid_entries=0` и `zip_validation=PASS`.

## Документация

- `README.md`, раздел `## Play Mode`: абзац о том, что PlayMode-тесты при ошибках компиляции отказывают сразу со статусом `compiler_error` и файлами в `Diagnostics`, а не ждут входа в Play Mode.
- `UNITYAGENT.md`, раздел `## PlayMode`: то же для агента в чужом проекте, плюс короткое правило форс-мажора из пункта 6 `SKILL.md`, сформулированное под этого читателя.
- `Docs/PROJECT_MAP.md`: нет. Файлы, команды и коды выхода не меняются.
