---
name: unity-bridge
description: "Use whenever the user wants Claude to execute anything inside the Unity Editor — listing or modifying assets, scenes, prefabs, components, materials, project settings; editor-side analysis, refactors, batch operations; querying the scene hierarchy; compiling the project; running tests; or any task needing C# code run in the Editor context. Works via Agent Bridge: Claude writes a C# script and hands it to the `agentbridge` CLI, which compiles it in memory with Roslyn and runs it on the main thread, returning logs plus a result string. Trigger even for casual phrasings like 'check what's in the scene', 'find all prefabs using shader X', 'rename these assets', 'what does this component reference', 'compile the project', 'run the tests'. Do NOT use for runtime gameplay code, build pipeline tasks unrelated to Editor scripting, or pure C# questions outside Unity. Also use for capturing Scene View screenshots of the open scene ('сфоткай сцену', 'скриншот сцены', 'покажи как выглядит уровень') via the declarative sceneshot command. For uGUI prefab layout and UI screenshots prefer the unity-ui skill."
---

# Unity Bridge

Скилл для выполнения произвольных задач в Unity Editor через Agent Bridge.

Принцип работы: ты пишешь C#-скрипт в рабочую папку `Temp/AgentBridge/` внутри Unity-проекта и вызываешь CLI `agentbridge` (где его искать — см. «Где искать CLI»). CLI находит Unity-проект из текущей директории, кладёт задачу в очередь моста и ждёт результата. Мост компилирует скрипт в памяти через Roslyn (без domain reload, без файлов в `Assets`) и выполняет на главном потоке редактора. Результат приходит в stdout: полный JSON по умолчанию или читаемое резюме с `--format human`.

Для вёрстки uGUI-префабов (создание/правка UI, скриншоты экранов) используй скилл `unity-ui` — декларативные задачи без компиляции. Этот скилл — для логики, компиляции проекта и тестов.

## Принципы

- Мост — дорогой канал: каждый C#-таск — это компиляция. Используй его для того, что умеет только живой Editor: AssetDatabase, выполнение кода в редакторе, тесты, состояние сцены. Код и текстовые ассеты читай и правь обычными файловыми инструментами — без моста.
- Один таск — один осмысленный шаг работы, а не проверка каждой мелочи.
- `compile` и `tests` — самостоятельные команды, а не побочный эффект `csharp`-таска.
- Файлы тасков не удаляй: очистка — не твоя работа (см. «Где лежат файлы тасков»).

## Где лежат файлы тасков

Файл таска пиши **только** в `<ProjectRoot>/Temp/AgentBridge/` — это рабочая папка агента внутри Unity-проекта. Абсолютный путь печатает `agentbridge status` (`ScratchDir` в JSON, строка `Task files:` в `--format human`); CLI создаёт эту папку сам.

**Никогда не пиши файлы тасков в `Assets/`** (в том числе в `Assets/Editor/...`). Всё в `Assets/` Unity импортирует и компилирует: каждый таск дёргает пересборку проекта и domain reload, ломает состояние редактора и оставляет мусор с `.meta`-файлами в репозитории. `agentbridge csharp`/`ui` предупредит в stderr, если файл лежит в `Assets/` — увидев это, перенеси файл в `Temp/AgentBridge/`, а тот, что в `Assets/`, удали вместе с его `.meta`.

Почему именно `Temp/`: Unity туда не смотрит (нет импорта, нет `.meta`, нет рекомпиляции), папка вне git, и редактор сам вычищает её при старте и закрытии — никакой уборки за собой делать не надо. Внутри сессии редактора файл никуда не денется, так что упавший таск можно чинить и перезапускать сколько нужно.

`Library/AgentBridge/` — внутренняя кухня CLI и редактора (очередь, журнал, артефакты). Это не твоя зона: не пиши туда и ничего там не чисти. Всё, что нужно от задачи, CLI печатает в stdout; единственное исключение — артефакты своей же задачи (скриншоты, дампы), которые читаешь по путям из поля `Artifacts`.

## Quick reference

| Шаг | Действие |
|-----|----------|
| 1 | Выполнить `agentbridge status`; если cwd вне проекта — повторить с `--project <path>` |
| 2 | Найти все `UNITYAGENT.md` (или устаревшие `UNITYCOWORK.md`) в проекте — описания кастомных API |
| 3 | Сгенерировать уникальное имя задачи: `Task_YYYYMMDD_HHMMSS_fff_<random>` |
| 4 | Написать `.cs` по шаблону ниже в `<ProjectRoot>/Temp/AgentBridge/<TaskName>.cs` — не в `Assets/` |
| 5 | Выполнить `agentbridge csharp <файл>` и обработать результат из stdout |

## Шаблон C# скрипта

```csharp
using System.Threading.Tasks;
using UnityEngine;
using UnityEditor;

public static class Task_XXX
{
	public static async Task<string> Run()
	{
		// сгенерированный код
		return "описание результата";
	}
}
```

Правила:

1. `public static class` с `public static Task<string> Run()` или `public static Task<string> Run(CancellationToken cancellationToken)`. Если есть обе сигнатуры — вызывается вторая. Другой тип возврата — мост отклоняет со статусом `rejected`.
2. Имя класса **обязано** совпадать с именем файла (без расширения) — это же имя станет `TaskName`, клиент берёт его из имени файла.
3. Метод возвращает строку с описанием того, что было сделано.
4. Для вывода информации использовать `Debug.Log()` — логи перехватываются мостом и включаются в результат.
5. Не добавлять зависимости от пользовательских сборок проекта — только Unity API и кастомные API из `UNITYAGENT.md`. Скрипт компилируется изолированно от `Assets`, поэтому доступны только уже загруженные в домен сборки.
6. Запрещены блокирующие конструкции: `.Wait()`, `.GetAwaiter().GetResult()`, `.Result` на task-подобных выражениях, `Thread.Sleep`, `while (true)`/`for (;;)` без `await` внутри. Мост отклоняет такой код до исполнения (`rejected`, причина в `Diagnostics`/`Logs`) — переписывай через `await`.
7. Для смены Editor-сцен используй только `AgentBridge.AgentSceneManager`. Прямые вызовы `EditorSceneManager.OpenScene` / `NewScene` / `CloseScene` / `RestoreSceneManagerSetup` и `SceneManager.LoadScene*` отклоняются guardrail: безопасный API сначала разрешает dirty-состояние без модального save-диалога.
8. **Упавший таск чини, а не удаляй.** Битый `.cs` в `Temp/AgentBridge/` ничего не блокирует (задачи не участвуют в компиляции проекта) — просто исправь файл и запусти `csharp` заново с тем же или новым именем.

### Политика dirty untitled-сцен

`ProjectSettings/AgentBridge.json` содержит `DirtyUntitledScenePolicy`. Значение `Discard` используется по умолчанию и закрывает dirty untitled-сцены без сохранения перед задачей. `Block` оставляет сцену открытой и завершает задачу как `runtime_error`, не показывая диалог. Настройка доступна в Unity через **Tools → Agent Bridge → Setup...**.

## Кастомные API проекта

Перед генерацией скрипта найди файлы `UNITYAGENT.md` в Unity-проекте (рекурсивный поиск от корня проекта); если их нет — поищи устаревшее имя `UNITYCOWORK.md`. Каждый такой файл описывает кастомный API, доступный в проекте. Прочитай все найденные файлы и используй описанные в них классы и методы при генерации скрипта, если они подходят для задачи.

Если файлов не найдено или ни один из описанных API не подходит для задачи — используй стандартное Unity Editor API.

## Где искать CLI

Порядок поиска, сверху вниз — побеждает первый рабочий:

1. `agentbridge` в `PATH`.
2. Стабильный путь установщика: `%LOCALAPPDATA%\AgentBridge\bin\agentbridge.exe` (Windows), `$HOME/.local/bin/agentbridge` (macOS/Linux) — на случай, если GUI-агент не подхватил обновлённый `PATH`.
3. `<ProjectRoot>/Library/AgentBridge/cli/agentbridge` — сборка под Linux для агентов, у которых шелл живёт в отдельной песочнице (Cowork, devcontainer, WSL), а редактор — на хост-машине. Её кладёт туда **Tools → Agent Bridge → Update CLI**.

Не ищи CLI внутри `Library/PackageCache` или каталога UPM-пакета.

Если CLI не найден ни по одному пути — **остановись и скажи пользователю**: «AgentBridge CLI не установлен, поставь его через Tools → Agent Bridge → Update CLI». Не пиши задачи в `Library/AgentBridge/Inbox` руками и не воспроизводи протокол моста самостоятельно: в обход CLI не работают ни health-проверки, ни сверка версии протокола, а из команд остаётся только запуск скриптов — без `status`, `doctor`, `compile`, `tests`, `ui` и артефактов.

## Команды CLI

CLI ищет Unity-проект от `cwd` вверх. Если агент запущен вне проекта, передай `--project <path>`; CLI намеренно не ищет проекты рекурсивно вниз.

Когда клиент и редактор на разных ОС (песочница), `agentbridge status` показывает строку `Host: editor on <os>, client on <os>`. Это нормальный режим: проверка PID редактора отключается, живость определяется по heartbeat, а идентичность проекта — по `Library/AgentBridge/project-id`, а не по абсолютному пути.

```bash
agentbridge <команда> [аргументы] [--project <путь>] [--wait <секунды>] [--format json|human]
```

> Чтобы команда не требовала подтверждения при каждом запуске: `"Bash(agentbridge:*)"` в `.claude/settings.local.json`.

### `csharp <path-to-cs>`

Выполнить C#-таск. `TaskName` — имя файла без расширения, класс внутри обязан называться так же. Ждёт результат, печатает JSON по умолчанию; для простого читаемого результата доступен `--format human`.

```bash
agentbridge csharp Temp/AgentBridge/Task_20260226_143052_871_a3f.cs
```

### `sceneshot <file.sceneshot.json>`

Скриншоты текущей открытой сцены (Scene View) с заданных ракурсов. Декларативный JSON, без компиляции. Файл пиши в `Temp/AgentBridge/<TaskName>.sceneshot.json`.

Формат:

```json
{
  "shots": [
    { "name": "hero", "width": 1280, "height": 720,
      "frame": { "target": "Level/Hero", "margin": 1.1, "rotation": [30, 45, 0] } },
    { "name": "top",
      "pose": { "pivot": [0, 0, 0], "rotation": [90, 0, 0], "size": 40, "orthographic": true } }
  ]
}
```

- Ровно одно из `pose` (явная поза SceneView: pivot/rotation/size/orthographic) или `frame` (автокадрирование объекта по имени или пути `Root/Child`, как клавиша F).
- `width`/`height` — дефолт 1280x720, потолок 1920x1080. Если экран меньше, размер пропорционально уменьшается — фактический указан в `ReturnValue`, факт уменьшения в `Logs`.
- `gizmos` — дефолт `true` (иконки компонентов, гизмо). `grid` — дефолт `false`.
- Снимок делается перерисовкой окна в текстуру, а не с экрана: перекрытие окна Unity, потеря фокуса и свёрнутый редактор на результат не влияют.
- Пути готовых PNG приходят в поле `Artifacts` результата — читай их обычным просмотром изображений.
- Снимается текущая открытая сцена. Нужна другая — сначала открой её отдельным `csharp`-таском.

```bash
agentbridge sceneshot Temp/AgentBridge/Task_20260226_143052_871_a3f.sceneshot.json
```

### `compile`

Заставить Unity скомпилировать проект (переживает вызванный этим domain reload) и вернуть список ошибок.

```bash
agentbridge compile --format human
```

Перед успешным ответом Bridge делает синхронный AssetDatabase refresh и проверяет каждый проектный `.cs`: рядом существует `.meta`, AssetDatabase знает GUID, Unity назначил файлу сборку, а файл присутствует в source inventory скомпилированных assemblies. Нарушения возвращаются как `compiler_error` с кодами `ABIMPORT001`–`ABIMPORT004`; такой результат нельзя считать чистой компиляцией.

Используй для структурной проверки проекта после серии файловых правок (не C#-тасков). `compile: success` доказывает только импорт и компилируемость текущего source inventory. Он не является поведенческим proof и не закрывает работу: после него обязательно запусти релевантные EditMode/PlayMode тесты через `agentbridge tests`.

### `tests [--mode EditMode|PlayMode] [--assembly A] [--test T] [--category C]`

Прогнать тесты. Каждый флаг можно повторять несколько раз. Без `--mode` — `EditMode`.

```bash
agentbridge tests --mode EditMode --assembly MyProject.Tests --format human
```

### `status`

Проверить найденный проект, рабочую папку для файлов тасков (`ScratchDir`), наличие пакета, PID редактора, свежесть heartbeat, версию протокола и готовность моста — без создания задачи. Для подробной диагностики используй `agentbridge doctor`.

### `wait <TaskId>`

До-дождаться уже созданной задачи (например, после того как предыдущий вызов вернул код `2`).

## Формат ответа

По умолчанию CLI печатает один JSON-объект в stdout (`TaskRecord`): `Id`, `Kind`, `Status`, `ReturnValue`, `Logs`, `Diagnostics`, `ForeignErrors`, `Artifacts`, `Tests`, `Timing`, `SessionId`, `StartedAtUtc`, `FinishedAtUtc`.

Для обычной проверки компиляции и тестов используй `--format human`: CLI сам печатает краткий итог, а при ошибке сохраняет логи, диагностики и test failures. Не перенаправляй `2>&1` в JSON-файл и не запускай Python/`jq` только ради строки `success` или счётчиков тестов. Формат JSON оставляй для случаев, когда действительно нужна программная обработка отдельных полей полного `TaskRecord`; stderr с прогрессом держи отдельно от stdout.

Коды выхода: `0` — `Status: "success"`; `1` — любой другой терминальный статус, включая `test_failure`; `2` — таймаут ожидания, задача ещё идёт (`{"Id":"...","Status":"running"}` в stdout, повтори через `wait <TaskId>`); `3` — проект/мост недоступен, несовместим протокол или команда использована неверно.

## Логика обработки ошибок

### `Status == "success"`

Показать пользователю `Logs` и `ReturnValue`.

### `Status == "rejected"`

Причина — в `Diagnostics` (гвардрейл, компиляция) или `Logs`. Для `csharp`: исправь скрипт, запусти `csharp` заново. Максимум 3 итерации, затем показать ошибки пользователю и остановиться.

### `Status == "compiler_error"` И `ForeignErrors == false`

Для `csharp` исправь сгенерированный скрипт и повтори задачу. Для `compile` ошибки принадлежат файлам текущего шага — исправь их и запусти `compile` снова.

### `Status == "compiler_error"` И `ForeignErrors == true`

Остановиться немедленно. Сообщить пользователю: в проекте есть ошибки компиляции в файлах, не связанных с текущей задачей — показать какие именно, не пытаться чинить чужой код.

### `Status == "runtime_error"`

Показать пользователю логи ошибки. При необходимости предложить исправление и перезапустить.

### `Status == "test_failure"`

Тестовый прогон завершён, но есть упавшие или inconclusive тесты. Показать `Tests.failures`; код выхода уже равен `1`, такой прогон нельзя считать успешной проверкой.

### `Status == "timeout"`

Задача выполнялась дольше `TaskTimeoutSeconds` (по умолчанию 300 секунд, настройка в `ProjectSettings/AgentBridge.json`). Очередь моста уже разблокирована. Для заведомо долгих задач заранее увеличь `TaskTimeoutSeconds` и клиентский `--wait`.

### `Status == "canceled"`

Задачу отменил человек через меню редактора (**Tools → Agent Bridge → Cancel Running Task**). Сообщить пользователю и не перезапускать без его явного решения.

### Код выхода `2` (ожидание исчерпано, задача ещё идёт)

Мост жив, но не уложился в `--wait`. Повторно подождать: `agentbridge wait <TaskId> --wait <секунды>`.

### Код выхода `3` (мост недоступен)

Смотри поле `code` в JSON. Для `project_not_found` найди корень Unity-проекта и повтори с `--project`; для `heartbeat_stale`/`editor_process_not_running` сообщи: «Открой проект в Unity»; для `bridge_disabled` — «Включи мост через Tools → Agent Bridge → Start»; для `protocol_mismatch` обнови CLI или пакет; для `project_mismatch` — `status.json` от другого проекта, проверь `--project`. Не пытайся запускать Unity самостоятельно.

Ни один из этих кодов не повод обойти CLI. Если мост недоступен — сообщи пользователю и остановись.
