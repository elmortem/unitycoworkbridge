---
name: unity-bridge
description: "Use this skill whenever the user wants Claude to execute anything inside the Unity Editor — listing or modifying assets, scenes, prefabs, components, materials, project settings; running editor-side analysis, refactors, or batch operations; querying the scene hierarchy; or any task that requires running C# code in the Unity Editor context. The skill works via Cowork Bridge: Claude writes a C# Editor script, drops it into a watched folder, Bridge compiles and runs it, and returns logs plus a result string. Trigger this even for casual phrasings like 'check what's in the scene', 'find all prefabs using shader X', 'rename these assets', 'what does this component reference' — anything that needs Unity Editor introspection or modification. Do NOT use for runtime gameplay code, build pipeline tasks unrelated to Editor scripting, or pure C# questions outside of Unity."
---

# Unity Bridge

Скилл для выполнения произвольных задач в Unity Editor через Cowork Bridge.

Принцип работы: ты генерируешь C# Editor-скрипт, кладёшь его в `Assets/Editor/CoworkBridge/`, Bridge автоматически подхватывает файл, компилирует и выполняет. Результат (логи + возвращённая строка + статус) появляется в JSON-файле рядом со скриптом.

## Quick reference

| Шаг | Действие |
|-----|----------|
| 1 | Найти все `UNITYCOWORK.md` в проекте — это описания кастомных API |
| 2 | Сгенерировать имя задачи: `Task_YYYYMMDD_HHMMSS` |
| 3 | Создать `Assets/Editor/CoworkBridge/<TaskName>.cs` по шаблону ниже |
| 4 | Запустить inline-команду ожидания результата |
| 5 | Прочитать `Assets/Editor/CoworkBridge/result_<TaskName>.json` и действовать по статусу |

## Шаблон C# скрипта

Все генерируемые скрипты обязаны следовать этому шаблону:

```csharp
using UnityEngine;
using UnityEditor;

public static class Task_XXX
{
    public static string Run()
    {
        // сгенерированный код
        return "описание результата";
    }
}
```

Правила:

1. `public static class` с `public static string Run()`.
2. Имя класса совпадает с именем файла (без расширения).
3. Метод возвращает строку с описанием того, что было сделано.
4. Для вывода информации использовать `Debug.Log()` — логи перехватываются Bridge и включаются в результат.
5. Не добавлять зависимости от пользовательских сборок проекта — только Unity API и кастомные API из `UNITYCOWORK.md`.
6. **Никогда не удалять файлы задач и результатов.** Скрипт задачи (`<TaskName>.cs`) и файлы результата (`result_<TaskName>.json`, `result_<TaskName>.done`, а также `testresult_*`) удалять нельзя. Очисткой управляет пользователь вручную внутри плагина через меню Unity (**Tools → Cowork Bridge → Clean Completed / Clean All**). Каждая новая задача — это новый файл; старые файлы не трогаем.

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

Запустить через bash:

```bash
TASK=Task_20260226_143052   # подставить имя текущей задачи
TIMEOUT=300                 # секунд
elapsed=0
while [ ! -f "Assets/Editor/CoworkBridge/result_${TASK}.done" ]; do
  sleep 1
  elapsed=$((elapsed + 1))
  if [ $elapsed -ge $TIMEOUT ]; then
    echo '{"status":"timeout","error":"Bridge did not respond within timeout"}'
    exit 1
  fi
done
cat "Assets/Editor/CoworkBridge/result_${TASK}.json"
```

Команда выводит JSON с результатом в stdout.

### Шаг 5. Обработка результата

Прочитать `Assets/Editor/CoworkBridge/result_<TaskName>.json` и действовать по логике ниже.

## Логика обработки ошибок

### `status == "success"`

Показать пользователю `logs` и `return_value`.

### `status == "compiler_error"` И `foreign_errors == false`

1. Изучить ошибки компилятора из `compiler_errors`.
2. Исправить сгенерированный скрипт `Assets/Editor/CoworkBridge/<TaskName>.cs`. Bridge подхватит изменённый файл автоматически.
3. Снова запустить inline-команду ожидания (см. Шаг 4) и проверить результат.
4. Максимум 3 итерации исправлений.
5. Если после 3 итераций ошибки остались — показать их пользователю и остановиться.

### `status == "compiler_error"` И `foreign_errors == true`

Остановиться немедленно. Сообщить пользователю:

1. В проекте есть ошибки компиляции в других файлах, не связанных с текущей задачей.
2. Показать какие именно файлы и ошибки.
3. НЕ пытаться исправлять чужие файлы.

### `status == "runtime_error"`

Показать пользователю логи ошибки. При необходимости предложить исправление и перезапустить (Шаги 3–5).

### `status == "timeout"`

Bridge не ответил за отведённое время. Сообщить пользователю и предложить:

1. Проверить, запущен ли Unity Editor с активным Bridge.
2. Увеличить `TIMEOUT` если задача потенциально долгая.

## Очистка задач

Файлы задач и результатов **удаляет сам пользователь** через меню плагина в Unity Editor:

- **Tools → Cowork Bridge → Clean Completed** — удаляет завершённые задачи (скрипт + результат + маркер).
- **Tools → Cowork Bridge → Clean All** — удаляет все задачи.

Тебе удалять `.cs`-скрипты задач и файлы результатов (`result_*`, `testresult_*`) не нужно и нельзя — это сломает историю и порядок обработки. Просто оставляй файлы на месте; пользователь почистит их сам, когда захочет.
