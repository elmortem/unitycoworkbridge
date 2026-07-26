Status: Предварительный дизайн

# ТДД: HTTP-мост и выполнение C# через Roslyn без импорта в Assets

## Цель

Заменить файловый цикл Cowork Bridge:

```text
запись .cs в Assets → AssetDatabase.Refresh → компиляция Unity →
domain reload → reflection → result-файл → wait-for-result.sh
```

на собственный transport и execution pipeline:

```text
локальный HTTP → очередь → Roslyn compile-to-memory →
запуск в main thread → ожидание Task<string> → единый JSON-ответ
```

Новая система должна работать в Unity 2022.3 и новее. Unity CLI и
`com.unity.pipeline` не являются зависимостями: их исходники используются
только как референс для отдельных решений.

## Зафиксированные решения

- Выполнение возможно только в уже запущенном обычном Unity Editor.
- Мост никогда не запускает Editor самостоятельно.
- Batch mode и Development Player не поддерживаются.
- Сервер доступен только через loopback и защищён случайным session token.
- Сгенерированный C# не попадает в `Assets` и не участвует в Unity Script
  Compilation Pipeline.
- C# компилируется Roslyn в PE/PDB в памяти и загружается через
  `Assembly.Load`.
- Компиляция выполняется в worker thread, вызов `Run()` — в Unity main thread.
- Сохраняется контракт `public static Task<string> Run()`.
- Все виды задач проходят через одну очередь и возвращают один формат ответа.
- Успешные и неуспешные запросы сохраняются мостом для диагностики и
  идемпотентного повторного чтения; агент не управляет очисткой task-файлов.
- Потеря фокуса компенсируется через `EditorApplication.SignalTick`, с
  fallback на текущий Windows wake timer, если API недоступен.

## Что заимствуем у Unity Pipeline

Заимствуем:

- loopback HTTP server и descriptor в `Library`;
- случайный bearer token на editor session;
- очередь команд с маршаллингом в main thread;
- Roslyn compile-to-memory;
- `Application.runInBackground` + `EditorApplication.SignalTick`;
- watchdog listener и измерение времени отдельных стадий.

Не копируем:

- compilation Roslyn непосредственно на main thread;
- пересоздание `MetadataReference` для всех assemblies на каждый eval;
- поддержку Development Player и hot reload методов;
- безусловную загрузку нового eval assembly для служебных команд;
- текущую семантику `eval timeout`.

В `com.unity.pipeline 0.3.1-exp.1` параметр timeout для `eval` проверяется на
диапазон, но не передаётся Roslyn как cancellation token и не прерывает
выполняемый код. В собственной системе timeout должен быть реальным хотя бы
для compilation и кооперативного async-кода, с честно описанным ограничением
для заблокированного main thread.

## Важное ограничение: что считать зависанием

Domain reload не является универсальным способом остановить произвольный код,
уже заблокировавший главный поток.

Если `Run()` сделал `await` и затем навсегда остался незавершённым, main thread
свободен. Мост может обнаружить timeout, записать результат и запросить domain
reload, удалив продолжения и статическое состояние задачи.

Если до первого `await` выполняется `while (true)`, `Thread.Sleep`, `.Wait()` или
другая блокирующая операция на главном потоке, код внутри того же Editor не
может надёжно начать domain reload: сам reload тоже требует main thread.
HTTP-поток сможет определить timeout и сообщить `editor_unresponsive`, но не
сможет безопасно восстановить Editor.

Надёжное автоматическое восстановление из такого состояния потребовало бы
внешнего supervisor-процесса с правом завершить и заново запустить Editor. Это
противоречит текущему требованию «мост не управляет запуском Editor» и в этот
дизайн не входит.

Следствия:

- async timeout восстанавливается через domain reload;
- hard main-thread hang диагностируется, но требует ручного завершения Editor;
- перед компиляцией стоит отклонять очевидные конструкции: синхронные
  `.Wait()`, `.Result`, `GetAwaiter().GetResult()`, `Thread.Sleep` и тривиальные
  бесконечные циклы. Это guardrail, а не sandbox и не полная гарантия.

## Архитектура

```text
Agent / router
      │
      │ HTTP + bearer token
      ▼
BridgeHttpServer ───────► TaskJournal
      │                       │
      ▼                       │
TaskCoordinator ◄─────────────┘
      │
      ├── C# ─► RoslynCompiler (worker thread)
      │                    │
      │                    ▼
      │            MainThreadDispatcher
      │                    │
      │                    ▼
      │              AsyncTaskWatcher
      │
      └── UI ─────────► UiTaskRunner (main thread)
                           │
                           ▼
                 TaskResult + artifacts

EditorTickPump ───────► EditorApplication.SignalTick()
```

### `BridgeBootstrap`

Editor-only `[InitializeOnLoad]` entry point.

Обязанности:

- не запускаться в Asset Import Worker и batch mode;
- прочитать `ProjectSettings/CoworkBridge.json`;
- запустить tick pump и HTTP-сервер, если мост включён;
- подписаться на `beforeAssemblyReload`, `afterAssemblyReload` и
  `EditorApplication.quitting`;
- на reload сначала перевести сервер в `quiescing`, перестать принимать новые
  задачи, закрыть listener и отменить фоновые операции;
- после reload снова поднять сервер и восстановить журнал.

### `BridgeHttpServer`

Основа — `HttpListener`, без постоянного собственного `Thread`.
`GetContextAsync` и обработчики работают через отменяемые `Task`.

Требования:

- bind только на `127.0.0.1`;
- диапазон портов, например `7650–7699`;
- отклонять запросы не с loopback;
- отклонять запросы с заголовком `Origin`;
- проверять `Authorization: Bearer <token>` сравнением constant-time;
- ограничить размер request body и максимальный размер исходника;
- ни один HTTP-handler не обращается к Unity API напрямую;
- `Stop()` закрывает listener и дожидается завершения accept loop перед reload.

### Descriptor

При старте сервер атомарно пишет:

```text
<ProjectRoot>/Library/CoworkBridge/bridge.json
```

Пример:

```json
{
  "protocolVersion": 1,
  "pid": 12345,
  "port": 7650,
  "projectPath": "D:/Project",
  "unityVersion": "2022.3.62f2",
  "mode": "editor",
  "sessionId": "8c7d...",
  "token": "<random 256-bit token>",
  "startedAtUtc": "2026-07-23T09:00:00Z"
}
```

Token создаётся заново на каждую editor session. Файл должен быть доступен
только текущему пользователю, насколько это позволяет ОС.

Клиент обязан проверить PID, `mode`, `projectPath` и выполнить `/v1/status`.
Старый descriptor не считается доказательством, что Editor жив.

### Минимальный HTTP API

```text
GET  /v1/status
GET  /v1/capabilities
POST /v1/tasks
GET  /v1/tasks/{id}
POST /v1/tasks/{id}/cancel
```

`POST /v1/tasks` принимает задачу и по умолчанию держит HTTP-запрос до
терминального результата. При разрыве соединения задача не отменяется:
результат можно повторно получить через `GET`.

Один task id идемпотентен:

- тот же id и тот же hash содержимого возвращают существующее состояние;
- тот же id с другим содержимым возвращает `id_conflict`;
- сервер никогда не запускает одну mutating-задачу повторно только из-за
  сетевого retry.

Предварительный запрос:

```json
{
  "protocolVersion": 1,
  "id": "Task_20260723_110000",
  "kind": "csharp",
  "source": "...",
  "timeoutSeconds": 300
}
```

Для UI вместо `source` передаётся существующий JSON payload. В дальнейшем
можно добавить `sourceFile`, но внутри сервера он всё равно сразу превращается
в текст и hash. Основной протокол не должен зависеть от наблюдения за файлом.

### Единый результат

```json
{
  "protocolVersion": 1,
  "id": "Task_20260723_110000",
  "kind": "csharp",
  "status": "success",
  "returnValue": "Done",
  "logs": [],
  "diagnostics": [],
  "artifacts": [],
  "timing": {
    "queuedMs": 2,
    "compileMs": 84,
    "mainThreadWaitMs": 7,
    "executeMs": 12,
    "totalMs": 105
  },
  "sessionId": "8c7d..."
}
```

Терминальные статусы:

- `success`
- `compiler_error`
- `runtime_error`
- `timeout`
- `canceled`
- `interrupted_by_domain_reload`
- `editor_unresponsive`
- `rejected`

Transport-ошибки (`unauthorized`, `id_conflict`, invalid JSON) возвращаются как
HTTP 4xx и в том же JSON envelope.

## Очередь и состояние

`TaskCoordinator` выполняет задачи строго последовательно. Это сохраняет
нынешнюю семантику и исключает параллельные изменения `AssetDatabase`, сцен и
префабов.

Состояния:

```text
accepted → compiling → waiting_main_thread → running → terminal
```

UI-задача пропускает `compiling`.

Журнал хранится в:

```text
Library/CoworkBridge/Journal/<task-id>.json
Library/CoworkBridge/Sources/<task-id>.cs
Library/CoworkBridge/Artifacts/<task-id>/
```

Записи делаются атомарно через temporary file + replace. Source сохраняется
сервером для диагностики, но не импортируется Unity.

При domain reload:

- `accepted` можно вернуть в очередь;
- `compiling`, `waiting_main_thread` и `running` становятся
  `interrupted_by_domain_reload`;
- mutating-задача автоматически повторно не запускается;
- готовый результат остаётся доступен после рестарта HTTP-сервера.

Retention полностью принадлежит мосту. Агенту не даются команды удаления
тасков. Настройка `KeepCompletedCount` может быть переиспользована для журнала.

## Roslyn

### Поставка

В package добавляется зафиксированный и протестированный набор:

- `Microsoft.CodeAnalysis.dll`
- `Microsoft.CodeAnalysis.CSharp.dll`
- `System.Collections.Immutable.dll`
- `System.Reflection.Metadata.dll`
- `System.Runtime.CompilerServices.Unsafe.dll`

DLL берутся из официальных NuGet-пакетов, а не копируются из
`com.unity.pipeline`. Версии фиксируются, checksums и MIT notices сохраняются в
репозитории.

Roslyn лучше изолировать в отдельный Editor asmdef с `overrideReferences` и
явными `precompiledReferences`. Нужно проверить минимум:

- Unity 2022.3.62f2;
- Unity 6 LTS;
- Mono и IL2CPP здесь не имеют значения, поскольку код работает только в
  Editor.

### References

На старте домена `ReferenceCatalog` строит snapshot ссылок:

- все загруженные нединамические assemblies с валидным `Location`;
- Unity Editor/Engine assemblies;
- скомпилированные project asmdef assemblies;
- стандартные BCL/netstandard assemblies.

Из snapshot исключаются ранее созданные `CoworkTask_*` assemblies, иначе
каждая следующая компиляция будет ссылаться на все предыдущие.

`MetadataReference` кэшируются по нормализованному пути, размеру и
`LastWriteTimeUtc`. Каталог инвалидируется после успешной Unity-компиляции и
после domain reload. Это быстрее, чем заново создавать references ко всем
загруженным assemblies для каждого запроса.

Практический результат: новая задача сможет использовать публичные API
проектных asmdef без compile-time reference из `CoworkBridge.asmdef`.
`internal` API остаются недоступны без `InternalsVisibleTo`.

### Компиляция

- Вход — полный compilation unit с классом задачи.
- Syntax tree получает реальный diagnostic path из сохранённого source.
- Assembly name уникален: `CoworkTask_<id>_<attempt>`.
- Emit выполняется в `MemoryStream`; debug PDB можно включать настройкой.
- Parse, diagnostics и emit получают `CancellationToken`.
- Компиляция идёт не на main thread.
- Диагностика содержит code, severity, message, file, line и column.

Компиляция остаётся возможной при ошибках в других исходниках проекта, пока
нужные ранее собранные assemblies уже загружены в текущий домен. Новые или
изменённые project API, которые Unity не смогла скомпилировать, естественно,
недоступны.

### Жизнь загруженных assemblies

Обычная загруженная assembly не выгружается отдельно от AppDomain. Поэтому
каждая успешная компиляция немного увеличивает память до следующего domain
reload.

Мост должен считать:

- число загруженных task assemblies;
- суммарный размер emitted PE/PDB;
- число выполненных задач за текущий домен.

На первом этапе достаточно показывать эти значения в `/v1/status` и warning
после настраиваемого порога. Автоматический профилактический reload лучше не
включать, пока реальные замеры не покажут необходимость.

## Main thread и async

`MainThreadDispatcher` содержит `ConcurrentQueue<WorkItem>` и обрабатывает её
из `EditorApplication.update`, не более заданного числа элементов за tick.

Вызов `Run()` происходит на main thread. Поддерживаемые сигнатуры:

```csharp
public static Task<string> Run()
```

Возможное совместимое расширение:

```csharp
public static Task<string> Run(CancellationToken cancellationToken)
```

Вторая форма позволяет задачам кооперативно завершаться при cancel/timeout, но
не должна быть обязательной для существующего skill.

Поскольку метод вызывается при активном Unity `SynchronizationContext`,
обычный `await` продолжится на main thread. `Task.Run` и
`ConfigureAwait(false)` могут продолжить работу в worker thread; обращаться
там к Unity API нельзя.

Логи перехватываются только на время активной задачи. Для background-логов
используется `Application.logMessageReceivedThreaded` и потокобезопасный
buffer. Так как очередь последовательная, одновременно существует только один
активный task log scope.

## Timeout, cancel и reload

### Компиляция

Timeout отменяет Roslyn через `CancellationToken`. Editor остаётся рабочим,
результат — `timeout`.

### Async-задача с доступным main thread

При timeout/cancel:

1. записать терминальный результат в журнал;
2. завершить ожидающий HTTP-response;
3. отменить переданный task token, если поддерживается новая сигнатура;
4. на следующем tick запросить `EditorUtility.RequestScriptReload()`;
5. после reload поднять сервер с новым `sessionId`.

Клиент должен переживать разрыв соединения: повторно прочитать descriptor и
запросить тот же task id.

### Заблокированный main thread

HTTP watchdog отмечает `editor_unresponsive`, если main-thread heartbeat не
менялся дольше hard timeout. Он не вызывает Unity API из фонового потока и не
пытается сделать `Thread.Abort`.

## Поддержание Editor update без фокуса

Новый `EditorTickPump`:

- один раз находит `EditorApplication.SignalTick` reflection-ом;
- создаёт `Action` delegate, чтобы не использовать reflection на каждом tick;
- подписывается на `EditorApplication.update`;
- вызывает `SignalTick` с настраиваемым интервалом, начальное значение — 16 мс;
- работает только пока сервер включён;
- устанавливает `Application.runInBackground = true`;
- корректно отписывается перед reload и при остановке.

Поскольку API внутренний, отсутствие или изменение сигнатуры не должно ломать
мост. В этом случае:

- capability `signalTick` становится `false`;
- на Windows временно используется существующий `CoworkEditorWakeTimer`;
- в консоль пишется одно предупреждение;
- HTTP API продолжает работать, но background latency может ухудшиться.

Удалять `CoworkEditorWakeTimer` стоит только после проверки `SignalTick` на
всей поддерживаемой линейке Unity.

## Интеграция существующих UI-задач

`UiTaskRunner` не должен знать про HTTP. Его нужно отделить от файлового
`ResultWriter`:

```text
Execute(payload, TaskContext) → TaskResult
```

`TaskCoordinator` отвечает за запись журнала и HTTP-response. Артефакты UI
переносятся из `Assets/Editor/CoworkBridge/Artifacts` в
`Library/CoworkBridge/Artifacts`.

Для агента C# и UI отличаются только `kind` и payload; transport, ожидание,
ошибки и результат одинаковы.

## Клиентская сторона

Unity CLI не используется. Нужен тонкий клиент собственного протокола,
скрывающий от skill:

- поиск descriptor;
- token;
- port;
- повторное подключение после reload;
- ожидание результата;
- единый stdout JSON.

Конкретная упаковка клиента требует отдельного решения. Варианты:

- небольшой кроссплатформенный .NET executable;
- команда существующего внешнего router;
- временно `curl --data-binary`, пока стабилизируется протокол.

Skill должен знать только одну операцию уровня:

```text
execute(project, kind, payload) → TaskResult
```

Он больше не создаёт файлы в `Assets`, не запускает wait shell script и не
удаляет задачи.

## Изменения существующего кода

После завершения миграции удаляются или существенно меняются:

- `CoworkBridge.cs`: файловое сканирование и compilation callbacks;
- `WaitScriptInstaller.cs` и `wait-for-result.sh`;
- `TaskCleaner.cs`: заменяется retention журнала;
- `ResultWriter.cs`: становится journal/result serializer;
- `TaskRunner.cs`: reflection остаётся, поиск типа по всем assemblies больше
  не нужен — compiler сразу возвращает точную assembly;
- `AsyncTaskWatcher.cs`: становится частью `TaskCoordinator`;
- `CoworkEditorWakeTimer.cs`: остаётся только fallback;
- skill: новый HTTP/client protocol вместо файлового.

Декларативный UI implementation и test runner по возможности сохраняются,
меняется только их вход/выход.

## Порядок реализации

1. Заменить механизм постоянного пробуждения на `EditorTickPump` с fallback,
   не меняя старый файловый pipeline.
2. Добавить и проверить Roslyn dependencies на Unity 2022.3 и Unity 6.
3. Реализовать `ReferenceCatalog` и compile-only тесты.
4. Реализовать `MainThreadDispatcher` и новый executor, оставив старый
   transport для тестового запуска.
5. Добавить HTTP server, descriptor, auth, status и lifecycle reload.
6. Добавить `TaskCoordinator`, journal, idempotency и единый результат.
7. Подключить C# execution, затем существующий UI pipeline.
8. Сделать собственный клиент/router и обновить skill.
9. Удалить файловый watcher, Unity compilation path и wait shell script после
   проверки новой схемы на реальных проектах.

Такой порядок позволяет отдельно проверить самые рискованные части — Roslyn,
reload-safe HTTP lifecycle и async timeout — до удаления рабочего пути.

## Критерии приёмки

- Unity 2022.3 и Unity 6 выполняют один и тот же C# task protocol.
- При выполнении C# не создаются и не изменяются файлы под `Assets`.
- Нет Unity compilation и domain reload перед обычным успешным запуском.
- Публичные API project asmdef доступны task-коду.
- Ошибки Roslyn возвращаются с корректными строками исходника.
- `Run()` стартует на main thread; обычное продолжение после `await` также
  выполняется на main thread.
- Editor в фоне принимает задачу и отвечает без ручного возврата фокуса.
- Повторный POST того же id не выполняет mutating-задачу дважды.
- После случайного domain reload незавершённая mutating-задача не стартует
  повторно и получает `interrupted_by_domain_reload`.
- Async timeout приводит к результату и восстановлению сервера после reload.
- Hard main-thread hang определяется внешним HTTP watchdog как
  `editor_unresponsive`; документация явно не обещает in-process recovery.
- Несколько сотен последовательных задач не приводят к неконтролируемому
  росту metadata references; рост загруженных task assemblies измеряется.
- При Stop/quitting/reload не остаётся выполняющихся accept-loop или Roslyn
  worker tasks, удерживающих старый AppDomain.

## Вопросы, которые нужно закрыть перед финальным ТДД

1. Формат собственного клиента: отдельный .NET binary или функция уже
   планируемого router.
2. Оставляем ли поддержку `Run()` единственной или сразу добавляем
   `Run(CancellationToken)`.
3. Нужен ли PDB/debug mode для сгенерированных задач по умолчанию.
4. Какой retention нужен для source/result/artifacts в `Library`.
5. Допускаем ли task-коду намеренно инициировать compilation/domain reload,
   или считаем такой запрос отдельной командой моста.
