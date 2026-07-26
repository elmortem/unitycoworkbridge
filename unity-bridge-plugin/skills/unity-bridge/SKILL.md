---
name: unity-bridge
description: "Use this skill whenever the user wants Claude to execute anything inside the Unity Editor — listing or modifying assets, scenes, prefabs, components, materials, project settings; running editor-side analysis, refactors, or batch operations; querying the scene hierarchy; forcing a project compile; running tests; or any task that requires running C# code in the Unity Editor context. The skill works via Agent Bridge: Claude writes a C# script and hands it to the cross-platform `agentbridge` CLI, which compiles it in memory with Roslyn and runs it on the main thread, returning logs plus a result string. Trigger this even for casual phrasings like 'check what's in the scene', 'find all prefabs using shader X', 'rename these assets', 'what does this component reference', 'compile the project', 'run the tests' — anything that needs Unity Editor introspection or modification. Do NOT use for runtime gameplay code, build pipeline tasks unrelated to Editor scripting, or pure C# questions outside of Unity. For uGUI prefab layout and UI screenshots prefer the unity-ui skill."
---

# Unity Bridge

Скилл для выполнения произвольных задач в Unity Editor через Agent Bridge.

Принцип работы: ты пишешь C#-скрипт во временный файл и вызываешь установленную в `PATH` команду `agentbridge`. CLI находит Unity-проект из текущей директории, кладёт задачу в очередь моста и ждёт результата. Мост компилирует скрипт в памяти через Roslyn (без domain reload, без файлов в `Assets`) и выполняет на главном потоке редактора. Результат — один JSON в stdout.

Для вёрстки uGUI-префабов (создание/правка UI, скриншоты экранов) используй скилл `unity-ui` — декларативные задачи без компиляции. Этот скилл — для логики, компиляции проекта и тестов.

## Принципы

- Мост — дорогой канал: каждый C#-таск — это компиляция. Используй его для того, что умеет только живой Editor: AssetDatabase, выполнение кода в редакторе, тесты, состояние сцены. Код и текстовые ассеты читай и правь обычными файловыми инструментами — без моста.
- Один таск — один осмысленный шаг работы, а не проверка каждой мелочи.
- `compile` и `tests` — самостоятельные команды, а не побочный эффект `csharp`-таска.
- Файлы тасков не удаляй: очистка старых записей — работа моста (авто-трим по `KeepCompletedCount`).

## Quick reference

| Шаг | Действие |
|-----|----------|
| 1 | Выполнить `agentbridge status`; если cwd вне проекта — повторить с `--project <path>` |
| 2 | Найти все `UNITYAGENT.md` (или устаревшие `UNITYCOWORK.md`) в проекте — описания кастомных API |
| 3 | Сгенерировать уникальное имя задачи: `Task_YYYYMMDD_HHMMSS_fff_<random>` |
| 4 | Написать `.cs` во временный файл `<TaskName>.cs` по шаблону ниже |
| 5 | Выполнить `agentbridge csharp <файл>` и обработать JSON из stdout |

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
7. **Упавший таск чини, а не удаляй.** Битый `.cs` ничего не блокирует (задачи не участвуют в компиляции проекта) — просто исправь файл и запусти `csharp` заново с тем же или новым именем.

## Кастомные API проекта

Перед генерацией скрипта найди файлы `UNITYAGENT.md` в Unity-проекте (рекурсивный поиск от корня проекта); если их нет — поищи устаревшее имя `UNITYCOWORK.md`. Каждый такой файл описывает кастомный API, доступный в проекте. Прочитай все найденные файлы и используй описанные в них классы и методы при генерации скрипта, если они подходят для задачи.

Если файлов не найдено или ни один из описанных API не подходит для задачи — используй стандартное Unity Editor API.

## Команды CLI

`agentbridge` должен быть установлен в `PATH`. Если GUI-агент ещё не подхватил обновлённый `PATH`, используй стабильный путь установщика: `%LOCALAPPDATA%\AgentBridge\bin\agentbridge.exe` в Windows или `$HOME/.local/bin/agentbridge` в macOS/Linux. Не ищи CLI внутри `Library/PackageCache` или каталога UPM-пакета. CLI ищет Unity-проект от `cwd` вверх. Если агент запущен вне проекта, передай `--project <path>`; CLI намеренно не ищет проекты рекурсивно вниз.

```bash
agentbridge <команда> [аргументы] [--project <путь>] [--wait <секунды>]
```

> Чтобы команда не требовала подтверждения при каждом запуске: `"Bash(agentbridge:*)"` в `.claude/settings.local.json`.

### `csharp <path-to-cs>`

Выполнить C#-таск. `TaskName` — имя файла без расширения, класс внутри обязан называться так же. Ждёт результат, печатает JSON в stdout.

```bash
agentbridge csharp /tmp/Task_20260226_143052.cs
```

### `compile`

Заставить Unity скомпилировать проект (переживает вызванный этим domain reload) и вернуть список ошибок.

```bash
agentbridge compile
```

Используй для проверки состояния проекта после серии файловых правок (не C#-тасков), либо когда нужно убедиться, что редактор компилируется чисто.

### `tests [--mode EditMode|PlayMode] [--assembly A] [--test T] [--category C]`

Прогнать тесты. Каждый флаг можно повторять несколько раз. Без `--mode` — `EditMode`.

```bash
agentbridge tests --mode EditMode --assembly MyProject.Tests
```

### `status`

Проверить найденный проект, наличие пакета, PID редактора, свежесть heartbeat, версию протокола и готовность моста — без создания задачи. Для подробной диагностики используй `agentbridge doctor`.

### `wait <TaskId>`

До-дождаться уже созданной задачи (например, после того как предыдущий вызов вернул код `2`).

## Формат ответа

Один JSON-объект в stdout (`TaskRecord`): `Id`, `Kind`, `Status`, `ReturnValue`, `Logs`, `Diagnostics`, `ForeignErrors`, `Artifacts`, `Tests`, `Timing`, `SessionId`, `StartedAtUtc`, `FinishedAtUtc`.

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

Смотри поле `code` в JSON. Для `project_not_found` найди корень Unity-проекта и повтори с `--project`; для `heartbeat_stale`/`editor_process_not_running` сообщи: «Открой проект в Unity»; для `bridge_disabled` — «Включи мост через Tools → Agent Bridge → Start»; для `protocol_mismatch` обнови CLI или пакет. Не пытайся запускать Unity самостоятельно.
