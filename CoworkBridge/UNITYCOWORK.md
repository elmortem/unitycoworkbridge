# Запуск тестов Unity через Bridge

Как прогнать EditMode (юнит) и PlayMode тесты прямо из задачи Bridge и получить результат (passed/failed + детали падений). Используется стандартный `UnityEditor.TestTools.TestRunner.Api` — никаких сторонних зависимостей.

## Домен

- запусти тесты, прогони тесты, проверь тесты
- запусти юнит-тесты / EditMode тесты
- запусти PlayMode тесты / игровые тесты
- проверь, что тесты проходят, не сломал ли я тесты
- прогони тесты сборки X, тесты класса Y, тест с категорией Z
- после правок кода — убедись, что всё зелёное

## Как это работает

Таск не запускает тесты напрямую и не регистрирует колбэки. Он вызывает API моста `CoworkBridge.CoworkTestRunner.RequestRun(...)`, который владеет персистентным колбэком (переживает domain reload при входе/выходе из Play Mode) и сам пишет результат в:

```
Assets/Editor/CoworkBridge/testresult_<TaskName>.json
Assets/Editor/CoworkBridge/testresult_<TaskName>.done
```

Ожиданий два:
1. Дождись обычного `result_<TaskName>.done` (`status == "success"`) — скрипт скомпилировался и прогон стартовал.
2. Дождись `testresult_<TaskName>.done` и прочитай `testresult_<TaskName>.json` — это результат тестов.

## Шаблон задачи

Подставь имя задачи в двух местах. Для PlayMode поменяй `"EditMode"` → `"PlayMode"`. Фильтры передаются массивами строк (или `null`, если не нужны).

```csharp
using UnityEngine;
using UnityEditor;

public static class Task_XXX
{
    public static string Run()
    {
        return CoworkBridge.CoworkTestRunner.RequestRun(
            "Task_XXX",
            "EditMode",
            null,   // assemblyNames, например new[] { "MyGame.Tests" }
            null,   // testNames,     например new[] { "MyGame.Tests.MathTests.Adds_Two" }
            null);  // categoryNames, например new[] { "Smoke" }
    }
}
```

## Ожидание результата тестов

После того как обычный `result_<TaskName>.json` показал `success`, дождись файла с результатами тестов (PlayMode может идти долго — таймаут побольше):

```bash
TASK=Task_XXX
TIMEOUT=600
elapsed=0
while [ ! -f "Assets/Editor/CoworkBridge/testresult_${TASK}.done" ]; do
  sleep 2
  elapsed=$((elapsed + 2))
  if [ $elapsed -ge $TIMEOUT ]; then
    echo '{"status":"timeout","error":"Tests did not finish within timeout"}'
    exit 1
  fi
done
cat "Assets/Editor/CoworkBridge/testresult_${TASK}.json"
```

Логика проверки `testresult_<TaskName>.json`:
- `aborted == true` → прогон не стартовал; покажи `message` пользователю (как правило — «выйди из Play Mode и перезапусти»).
- иначе `failed == 0 && inconclusive == 0` → тесты зелёные; иначе покажи `failures`.

## PlayMode

PlayMode-прогон надёжен при любой настройке Enter Play Mode (Reload Domain включён или выключен) — колбэк моста персистентный. Перед PlayMode-прогоном Bridge сохраняет открытые сцены (`SaveOpenScenes`), чтобы тест-фреймворк не падал на сохранении. Если на момент запроса редактор уже в Play Mode, прогон не стартует — в `testresult` будет `aborted: true`.

## Примечания

- Один прогон на задачу. Не вызывай несколько `RequestRun()` в одном `Run()`.
- Имя в `testresult_<TaskName>` всегда совпадает с именем задачи — так результат однозначно сопоставляется с задачей.
- Для запуска тестов нужен пакет **Unity Test Framework** (`com.unity.test-framework`) — он есть в проекте по умолчанию.
- Файлы `testresult_*` чистит Bridge автоматически вместе с таском (авто-очистка последних N успешных и команда `clean.command`). Отдельно их не трогай.
