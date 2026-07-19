---
name: unity-bridge
description: "Use this skill whenever the user wants Claude to execute anything inside the Unity Editor — listing or modifying assets, scenes, prefabs, components, materials, project settings; running editor-side analysis, refactors, or batch operations; querying the scene hierarchy; or any task that requires running C# code in the Unity Editor context. The skill works via Cowork Bridge: Claude writes a C# Editor script, drops it into a watched folder, Bridge compiles and runs it, and returns logs plus a result string. Trigger this even for casual phrasings like 'check what's in the scene', 'find all prefabs using shader X', 'rename these assets', 'what does this component reference' — anything that needs Unity Editor introspection or modification. Do NOT use for runtime gameplay code, build pipeline tasks unrelated to Editor scripting, or pure C# questions outside of Unity. For uGUI prefab layout and UI screenshots prefer the unity-ui skill."
---

# Unity Bridge

Скилл для выполнения произвольных задач в Unity Editor через Cowork Bridge.

Принцип работы: ты генерируешь C# Editor-скрипт, кладёшь его в `Assets/Editor/CoworkBridge/`, Bridge автоматически подхватывает файл, компилирует и выполняет. Результат (логи + возвращённая строка + статус) появляется в JSON-файле рядом со скриптом.

Для вёрстки uGUI-префабов (создание/правка UI, скриншоты экранов) используй скилл `unity-ui` — декларативные задачи без компиляции. Этот скилл — для логики и всего остального.

## Принципы

- Мост — дорогой канал: каждый таск — это компиляция и domain reload. Используй его для того, что умеет только живой Editor: AssetDatabase, выполнение кода в редакторе, тесты, состояние сцены. Код и текстовые ассеты читай и правь обычными файловыми инструментами — без моста.
- Один таск — один осмысленный шаг работы, а не проверка каждой мелочи. Любой таск попутно компилирует проект — отдельные «проверочные» таски не нужны.
- Тесты — финальная верификация этапа, а не ритуал после каждой правки.
- Файлы тасков не удаляй: успешные чистит Bridge сам, упавшие — чини или оставляй человеку.

## Quick reference

| Шаг | Действие |
|-----|----------|
| 1 | Найти все `UNITYCOWORK.md` в проекте — это описания кастомных API |
| 2 | Сгенерировать имя задачи: `Task_YYYYMMDD_HHMMSS` |
| 3 | Создать `Assets/Editor/CoworkBridge/<TaskName>.cs` по шаблону ниже |
| 4 | Запустить скрипт ожидания результата `wait-for-result.sh` |
| 5 | Прочитать `Assets/Editor/CoworkBridge/result_<TaskName>.json` и действовать по статусу |

## Шаблон C# скрипта

Все генерируемые скрипты обязаны следовать этому шаблону:

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

1. `public static class` с `public static async Task<string> Run()`. Метод обязан возвращать `Task<string>` — другие сигнатуры мост отклоняет с `runtime_error`.
2. Имя класса совпадает с именем файла (без расширения).
3. Метод возвращает строку с описанием того, что было сделано.
4. Для вывода информации использовать `Debug.Log()` — логи перехватываются Bridge и включаются в результат.
5. Не добавлять зависимости от пользовательских сборок проекта — только Unity API и кастомные API из `UNITYCOWORK.md`.
6. **Упавший таск чини, а не удаляй.** `compiler_error` (свой) — исправляй скрипт, до 3 итераций; если не починил — перепиши файл в валидный no-op по шаблону (`Run()` сразу возвращает пометку об отмене), чтобы битый `.cs` не блокировал сборку редактора, и сообщи пользователю. Таски с `runtime_error`/`timeout`/`canceled` скомпилированы и сборку не блокируют — действуй по логике обработки ошибок ниже.
7. Внутри `Run()` разрешён `await` любых async API (пул потоков, `Task.Delay`, Unity async-операции) — мост пишет результат только после завершения задачи, не блокируя editor-поток. Если `await` не нужен, метод всё равно объявляется `async Task<string>` (предупреждение CS1998 компиляцию не ломает).

## Кастомные API проекта

Перед генерацией скрипта найди все файлы `UNITYCOWORK.md` в Unity-проекте (рекурсивный поиск от корня проекта). Каждый такой файл описывает кастомный API, доступный в проекте. Прочитай все найденные файлы и используй описанные в них классы и методы при генерации скрипта, если они подходят для задачи.

Если файлов `UNITYCOWORK.md` не найдено или ни один из описанных API не подходит для задачи — используй стандартное Unity Editor API.

## Протокол выполнения

### Шаг 1. Имя задачи

Формат: `Task_YYYYMMDD_HHMMSS` (например, `Task_20260226_143052`).

### Шаг 2. Поиск кастомных API

Рекурсивный поиск `UNITYCOWORK.md` от корня проекта. Прочитать все найденные. Определить, какие из описанных API применимы к текущей задаче.

### Шаг 3. Генерация скрипта

Создать файл `Assets/Editor/CoworkBridge/<TaskName>.cs` по шаблону. Bridge сам подхватывает новые `.cs` файлы в этой папке и обрабатывает их последовательно — никаких дополнительных файлов задач создавать не нужно, сам скрипт и есть задача.

### Шаг 4. Ожидание результата

Bridge сначала пишет `result_<TaskName>.json` целиком, затем создаёт пустой маркер `result_<TaskName>.done`. Ждать нужно появления `.done`-маркера, а читать — `.json`. Это гарантирует, что JSON не будет прочитан частично записанным.

Скрипт ожидания `wait-for-result.sh` Bridge сам кладёт в наблюдаемую папку `Assets/Editor/CoworkBridge/` при загрузке Editor (если файла там ещё нет), рядом с файлами результатов. Запускать нужно **строго этой командой**, без `cd` и без изменений — иначе не сработает правило разрешений:

```bash
bash Assets/Editor/CoworkBridge/wait-for-result.sh Task_20260226_143052 300
```

Первый аргумент — имя задачи, второй (необязательный) — таймаут в секундах (по умолчанию 300). Скрипт сам дожидается `.done`-маркера и выводит содержимое `result_<TaskName>.json` в stdout; при таймауте печатает JSON со `status: "timeout"` и завершается с кодом 1.

> Чтобы команда не требовала подтверждения при каждом запуске, в настройках Claude Code (`~/.claude/settings.json` или `.claude/settings.local.json` проекта) должно быть разрешение:
> `"Bash(bash Assets/Editor/CoworkBridge/wait-for-result.sh:*)"`

### Шаг 5. Обработка результата

Прочитать `Assets/Editor/CoworkBridge/result_<TaskName>.json` и действовать по логике ниже.

## Логика обработки ошибок

### `status == "success"`

Показать пользователю `logs` и `return_value`.

### `status == "compiler_error"` И `foreign_errors == false`

1. Изучить ошибки компилятора из `compiler_errors`.
2. Исправить сгенерированный скрипт `Assets/Editor/CoworkBridge/<TaskName>.cs`. Bridge подхватит изменённый файл автоматически.
3. Снова запустить скрипт ожидания (см. Шаг 4) и проверить результат.
4. Максимум 3 итерации исправлений.
5. Если после 3 итераций ошибки остались — перепиши таск в валидный no-op (правило 6), покажи ошибки пользователю и остановись.

### `status == "compiler_error"` И `foreign_errors == true`

Остановиться немедленно. Сообщить пользователю:

1. В проекте есть ошибки компиляции в других файлах, не связанных с текущей задачей.
2. Показать какие именно файлы и ошибки.
3. НЕ пытаться исправлять чужие файлы.

### `status == "runtime_error"`

Показать пользователю логи ошибки. При необходимости предложить исправление и перезапустить (Шаги 3–5).

### `status == "timeout"`

Таймаут может прийти двумя путями:

1. **JSON от `wait-for-result.sh`** (мост не ответил вовсе): нет `result_<TaskName>.json` от моста, `status: "timeout"` печатает сам клиентский скрипт. Bridge не ответил за отведённое время. Сообщить пользователю и предложить: проверить, запущен ли Unity Editor с активным Bridge; увеличить таймаут (второй аргумент `wait-for-result.sh`), если задача потенциально долгая.
2. **`result_<TaskName>.json` от моста** (задача выполнялась, но превысила `AsyncTimeoutSeconds`): мост запустил задачу, но она летела дольше лимита `AsyncTimeoutSeconds` (по умолчанию 300 секунд, настройка в `ProjectSettings/CoworkBridge.json`). Очередь моста уже разблокирована. Для заведомо долгих задач заранее увеличь `AsyncTimeoutSeconds` и клиентский таймаут `wait-for-result.sh`. Файл задачи можно поправить и перезапустить — сборку он не блокирует.

### `status == "canceled"`

Задачу отменил человек через меню редактора (**Tools → Cowork Bridge → Cancel Running Task**). Сообщить пользователю и не перезапускать без его явного решения. Файл задачи не трогать.

## Очистка задач

Очистка — не твоя работа. Bridge на простое сам держит последние N успешных тасков (по умолчанию 10, `KeepCompletedCount` в `ProjectSettings/CoworkBridge.json`), удаляя более старые вместе с их `result_*`, `testresult_*` и `Artifacts/<TaskId>/`. Всё остальное, включая упавшие таски, человек чистит через **Tools → Cowork Bridge → Clean Completed / Clean All**.

Пустой файл `Assets/Editor/CoworkBridge/clean.command` удаляет все успешные таски сразу — создавай его только по явной просьбе пользователя и никогда во время прогона тестов.
