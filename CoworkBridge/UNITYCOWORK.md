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

Обычная задача Bridge: ты генерируешь `.cs`-скрипт с `Run()`, кладёшь в `Assets/Editor/CoworkBridge/`. Внутри `Run()` запускается прогон тестов через `TestRunnerApi`.

Важно: `TestRunnerApi.Execute()` **возвращает управление сразу** — тесты идут асинхронно. Поэтому метод `Run()` лишь стартует прогон, а итог теста пишется в `RunFinished`-колбэке в ОТДЕЛЬНЫЙ файл:

```
Assets/Editor/CoworkBridge/testresult_<TaskName>.json   ← результат прогона
Assets/Editor/CoworkBridge/testresult_<TaskName>.done   ← маркер готовности
```

Значит ожиданий **два**:
1. Сначала дождись обычного `result_<TaskName>.done` — это значит, что скрипт скомпилировался и прогон стартовал (`status == "success"`). Ошибки компиляции чини как обычно.
2. Потом дождись `testresult_<TaskName>.done` и прочитай `testresult_<TaskName>.json` — это и есть результат тестов.

## Шаблон задачи

Подставь имя задачи в трёх местах (`Task_XXX`). Для PlayMode поменяй `TestMode.EditMode` → `TestMode.PlayMode`.

```csharp
using System.Collections.Generic;
using System.IO;
using UnityEngine;
using UnityEditor;
using UnityEditor.TestTools.TestRunner.Api;

public static class Task_XXX
{
    public static string Run()
    {
        var api = ScriptableObject.CreateInstance<TestRunnerApi>();
        api.RegisterCallbacks(new TestResultWriter("Task_XXX"));

        var filter = new Filter { testMode = TestMode.EditMode };
        // Необязательные фильтры (иначе прогоняются все тесты режима):
        // filter.assemblyNames = new[] { "MyGame.Tests" };
        // filter.testNames     = new[] { "MyGame.Tests.MathTests.Adds_Two" };
        // filter.categoryNames = new[] { "Smoke" };

        api.Execute(new ExecutionSettings(filter));
        return "Test run started";
    }

    private class TestResultWriter : ICallbacks
    {
        private readonly string _name;
        public TestResultWriter(string name) { _name = name; }

        public void RunStarted(ITestAdaptor testsToRun) { }
        public void TestStarted(ITestAdaptor test) { }
        public void TestFinished(ITestResultAdaptor result) { }

        public void RunFinished(ITestResultAdaptor result)
        {
            var run = new TestRunResult
            {
                passed = result.PassCount,
                failed = result.FailCount,
                skipped = result.SkipCount,
                inconclusive = result.InconclusiveCount,
                total = result.PassCount + result.FailCount + result.SkipCount + result.InconclusiveCount,
                duration = result.Duration
            };
            CollectFailures(result, run.failures);

            string dir = Path.Combine(Application.dataPath, "Editor", "CoworkBridge");
            File.WriteAllText(Path.Combine(dir, "testresult_" + _name + ".json"), JsonUtility.ToJson(run, true));
            File.WriteAllText(Path.Combine(dir, "testresult_" + _name + ".done"), "");
            Debug.Log("[Tests] " + _name + ": passed " + run.passed + ", failed " + run.failed);
        }

        private static void CollectFailures(ITestResultAdaptor node, List<TestFailure> failures)
        {
            if (node.HasChildren)
            {
                foreach (var child in node.Children) CollectFailures(child, failures);
                return;
            }
            if (node.TestStatus == TestStatus.Failed || node.TestStatus == TestStatus.Inconclusive)
            {
                failures.Add(new TestFailure
                {
                    name = node.FullName,
                    message = node.Message,
                    stacktrace = node.StackTrace
                });
            }
        }
    }

    [System.Serializable]
    private class TestRunResult
    {
        public int passed;
        public int failed;
        public int skipped;
        public int inconclusive;
        public int total;
        public double duration;
        public List<TestFailure> failures = new List<TestFailure>();
    }

    [System.Serializable]
    private class TestFailure
    {
        public string name;
        public string message;
        public string stacktrace;
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

Логика проверки: `failed == 0 && inconclusive == 0` → тесты зелёные. Иначе покажи пользователю содержимое `failures` (имя теста, message, stacktrace).

## PlayMode: важный нюанс

PlayMode-тесты входят в Play Mode. Если в проекте включён **Reload Domain** (Project Settings → Editor → Enter Play Mode Settings), при входе/выходе из Play Mode домен перезагружается, и колбэк из одноразовой задачи теряется — файл `testresult_*` может не записаться.

- Если **Reload Domain выключен** — шаблон выше работает для PlayMode без изменений.
- Если **Reload Domain включён** (по умолчанию) — для EditMode всё ок, а для PlayMode надёжнее, чтобы пользователь либо выключил Reload Domain на время прогона, либо запустил PlayMode-тесты вручную через **Window → General → Test Runner**. Сообщи об этом пользователю, если `testresult_*` не появился за таймаут.

## Примечания

- Один прогон на задачу. Не запускай несколько `Execute()` в одном `Run()`.
- Имя в `testresult_<TaskName>` всегда совпадает с именем задачи — так результат однозначно сопоставляется с задачей.
- Для запуска тестов нужен пакет **Unity Test Framework** (`com.unity.test-framework`) — он есть в проекте по умолчанию.
- Файлы `testresult_*` удалять не нужно — их, как и обычные файлы задач, чистит пользователь через **Tools → Cowork Bridge → Clean Completed / Clean All**.
