Status: Выполнено

# Время жизни ответов очереди

Сейчас на каждый запрос сервер пишет файл ответа в `Library/AgentBridge/Queue/Replies/`, и удаляет его только сам сервер через сутки. В `OceanariumUnity` за 15.10 накопилось 54 076 файлов (80 МБ), в пике до 1 000 в минуту, около 3/4 — опросы `get` из `wait` каждые 250 мс. Очистка раз в минуту и сразу после старта обходит папку прямо в цикле сервера с отдельным запросом метаданных на файл. Пока она идёт, heartbeat не пишется и запросы не обрабатываются. Из Linux-песочницы через смонтированную папку это 2,4 мс на файл, около 130 с на 54 000 файлов.

Результат: `wait` опрашивает диспетчер раз в секунду. Прочитанный ответ удаляет процесс, который его прочитал (CLI или редактор). Остальное — ответы разовых команд окна редактора и брошенные после обрывов — удаляет фоновая очистка старше суток. Отказы диспетчера и медленные фазы его цикла видны в телеметрии.

## Общий транспорт `Editor/Queueing/` (компилируется в пакет и в CLI)

### `QueueFiles.cs`

- Добавить метод рядом с `Reply`:

```csharp
// The reader owns a reply once the server has finished the transaction: the server deletes
// the request right after publishing the reply. While the request still exists the reply
// guards it against re-handling after a server crash, so it stays for the server's sweep.
public static void ReleaseReply(string project, string id)
{
	try
	{
		if (File.Exists(Request(project, id))) return;
		File.Delete(Reply(project, id));
	}
	catch (IOException) { }
	catch (UnauthorizedAccessException) { }
}
```

## CLI `AgentBridgeCli/`

### `BridgeClient.cs`

- Новая константа класса: `private const int WaitPollIntervalMs = 1000;`.
- В `WaitForTaskAsync` последнюю строку цикла `await Task.Delay(250);` заменить паузой до следующего опроса. Пауза не выходит за остаток бюджета `--wait`, поэтому ожидание не затягивается сверх заказанного:

```csharp
double remainingMs = waitSeconds * 1000.0 - watch.Elapsed.TotalMilliseconds;
await Task.Delay(TimeSpan.FromMilliseconds(Math.Clamp(remainingMs, 1, WaitPollIntervalMs)));
```

- Цикл ожидания авто-stopplay в `StopManualPlayAsync` (тоже `Task.Delay(250)`) не трогать: он читает журнал напрямую, не через диспетчер, и ответов не создаёт.

### `QueueClient.cs`

- Новое свойство вместо литерала `15000` в `CallAsync`: `internal int AcknowledgeTimeoutMs { get; init; } = 15000;`.
- В `CallAsync` после успешного `SharedFile.TryReadAllText(QueueFiles.Reply(...), out var json)` выполнить `await ReleaseReplyAsync(message.Id)`, затем десериализовать и вернуть ответ, как сейчас. Сервер удаляет запрос через микросекунды после публикации ответа. Клиент даёт ему до 200 мс, чтобы не оставлять ответ из-за этой гонки:

```csharp
private async Task ReleaseReplyAsync(string id)
{
	for (int attempt = 0; attempt < 10 && File.Exists(QueueFiles.Request(_project, id)); attempt++) await Task.Delay(20);
	QueueFiles.ReleaseReply(_project, id);
}
```

- Редактор (`QueueEditorAdapter`) не ждёт: его тик не блокируется. Ответ, оставшийся после гонки, удаляет суточная очистка.
- Считать вызовы `Start()` за время ожидания в локальную `int starts`.
- Перед возвратом `dispatcher_unavailable` записать событие:

```csharp
new TelemetryLog(_project, true).Write("queue_unacknowledged", message.Session, message.TaskId, new Dictionary<string, object?>
{
	["Op"] = message.Op,
	["WaitedMs"] = deadline.ElapsedMilliseconds,
	["ServerAlive"] = QueueFiles.Alive(_project),
	["Starts"] = starts,
	["Host"] = HostPlatform.Current
});
```

### `QueueServer.cs`

- Константы и поле:

```csharp
internal const int ReplySweepIntervalMs = 60000;
internal const int SlowPhaseMs = 1000;
internal static readonly TimeSpan ReplyLifetime = TimeSpan.FromDays(1);
private Task? _sweep;
```

- В `Loop()` блок `if (now - lastCleanup > 60000) { ... }` заменить запуском фоновой очистки. Пока предыдущая очистка не закончилась, новую не запускать. Первая очистка по-прежнему запускается сразу после старта, так как `lastCleanup = 0`. При выходе из цикла очистку не ждать: удаление идемпотентно, и обрыв на середине ничего не портит.

```csharp
if (now - lastCleanup > ReplySweepIntervalMs && (_sweep == null || _sweep.IsCompleted))
{
	string replies = Path.Combine(_root, "Replies");
	_sweep = Task.Run(() => SweepAndLog(replies));
	lastCleanup = now;
}
```

- Новые методы:

```csharp
internal readonly record struct ReplySweep(int Deleted, int Failed, int Kept, long ElapsedMs);

// Enumeration carries each file's timestamp (on Windows without a per-file metadata call).
// Covers *.json replies and *.tmp leftovers of interrupted atomic writes.
internal static ReplySweep SweepReplies(string directory, DateTime cutoffUtc)
{
	var watch = Stopwatch.StartNew();
	int deleted = 0, failed = 0, kept = 0;
	try
	{
		if (Directory.Exists(directory))
			foreach (var file in new DirectoryInfo(directory).EnumerateFiles())
			{
				if (file.LastWriteTimeUtc >= cutoffUtc) { kept++; continue; }
				try { file.Delete(); deleted++; }
				catch (Exception error) when (error is IOException or UnauthorizedAccessException) { failed++; }
			}
	}
	catch (Exception error) when (error is IOException or UnauthorizedAccessException) { failed++; }
	return new ReplySweep(deleted, failed, kept, watch.ElapsedMilliseconds);
}

private void SweepAndLog(string directory)
{
	var sweep = SweepReplies(directory, DateTime.UtcNow - ReplyLifetime);
	if (sweep.Deleted > 0 || sweep.Failed > 0 || sweep.ElapsedMs >= SlowPhaseMs)
		_log.Write("queue_cleanup", "", "", new Dictionary<string, object?>
		{
			["Deleted"] = sweep.Deleted, ["Failed"] = sweep.Failed, ["Kept"] = sweep.Kept,
			["ElapsedMs"] = sweep.ElapsedMs, ["Host"] = HostPlatform.Current
		});
}

private void ReportSlow(string phase, long startedAt, string op = "")
{
	long elapsed = Environment.TickCount64 - startedAt;
	if (elapsed >= SlowPhaseMs)
		_log.Write("queue_slow", "", "", new Dictionary<string, object?>
		{
			["Phase"] = phase, ["Op"] = op, ["ElapsedMs"] = elapsed, ["Host"] = HostPlatform.Current
		});
}
```

- Замеры фаз цикла через `ReportSlow`:
  - запись heartbeat: перед `QueueFiles.Write(... "heartbeat" ...)` взять `long started = Environment.TickCount64;`, после записи вызвать `ReportSlow("heartbeat", started)`;
  - один запрос: в `HandleFile` первой строкой `long started = Environment.TickCount64;`, после `File.Delete(path)` перед логированием вызвать `ReportSlow("request", started, message?.Op ?? "")`. Ранний выход при уже существующем ответе не замерять;
  - синхронизация: блок `if (now - lastSync >= 250)` замерить целиком (`_tasks.Synchronize()`, sweep координации, `Catalog`) и вызвать `ReportSlow("sync", started)` после `try/catch`.

## Пакет `Editor/`

### `QueueEditorAdapter.cs`

- В `TryCall` сразу после успешного `SharedFile.TryReadAllText(QueueFiles.Reply(BridgePaths.ProjectRoot, id), out json)` вызвать `QueueFiles.ReleaseReply(BridgePaths.ProjectRoot, id)`. Остальной порядок (`FromJson`, `Pending.Remove`) не меняется. `Post` не меняется: его ответы не читаются и уходят с суточной очисткой.

## Тесты

- `AgentBridgeCli.Tests/QueueReplyLifetimeTests.cs` — новый файл, `internal static class QueueReplyLifetimeTests` с `internal static async Task RunAsync(string root)`. Вызывается из `Program.cs` сразу после `IndependentQueueTests.RunAsync(root)`. Фикстура проекта собирается так же, как в начале `IndependentQueueTests.RunAsync`, под своим именем каталога. Проверки через локальный `Check`. Случаи:
  - `client_removes_reply`: пять вызовов `list` через `QueueClient`. Для каждого `message.Id` файла ответа нет, в `Replies/` нет `*.json`;
  - `reply_kept_while_request_exists`: в отдельном каталоге без сервера `QueueFiles.Write` кладёт запрос и ответ для `held`. После `ReleaseReply` ответ на месте. После удаления запроса и повторного `ReleaseReply` ответа нет;
  - `sweep_deletes_only_expired`: во временном каталоге `old.json`, `old.json.<guid>.tmp` с `File.SetLastWriteTimeUtc(..., now - 2 сут)` и свежий `fresh.json`. `SweepReplies(dir, now - 1 сут)` даёт `Deleted == 2`, `Failed == 0`, `Kept == 1`, `fresh.json` на месте;
  - `sweep_survives_locked_file` (только `OperatingSystem.IsWindows()`): старый `locked.json` открыт тестом с `FileShare.None`. Очистка возвращает `Failed == 1` без исключения. После закрытия повторная очистка удаляет файл;
  - `sweep_missing_directory`: несуществующий каталог даёт нули без исключения;
  - `unacknowledged_is_logged`: в новой фикстуре тест держит `Queue/server.lock` открытым с `FileShare.None`, поэтому сервер стартовать не может. `new QueueClient(project) { AcknowledgeTimeoutMs = 500 }.CallAsync(list)` возвращает `dispatcher_unavailable`. В `Logs/AgentBridge-client-*.jsonl` есть строка с `"E":"queue_unacknowledged"` и `"Op":"list"`.
- Существующие тесты, которые затрагивает изменение: `IndependentQueueTests`, `QueueRuntimeTests` (читает ответ напрямую в обход клиента — поведение сервера для него не меняется), `RunQueueBudgetTests`, `RunStaleSubmissionTests`.

## Порядок работ

- `QueueFiles.ReleaseReply` в `Editor/Queueing/QueueFiles.cs`.
- `BridgeClient.cs`: опрос в `WaitForTaskAsync` раз в секунду. `QueueClient.cs`: освобождение ответа, `AcknowledgeTimeoutMs`, счётчик `starts`, событие `queue_unacknowledged`.
- `QueueEditorAdapter.cs`: освобождение ответа в `TryCall`.
- `QueueServer.cs`: фоновая очистка, `SweepReplies`, `SweepAndLog`, `ReportSlow` и три замера фаз.
- `QueueReplyLifetimeTests.cs` и вызов из `Program.cs`.
- Версии: CLI `1.24.3` → `1.25.0`, пакет `0.36.0` → `0.36.1`. Файлы плагина не меняются, поэтому его версия остаётся прежней, но плагин пересобирается по правилу 2.
- Документация по разделу ниже.
- Живая проверка в хост-проекте `AgentBridgeUnity` по признакам приёмки.

## Приёмка

- `wait` опрашивает раз в секунду и не выходит за бюджет: существующая проверка в `RunQueueBudgetTests` (`--wait 1` возвращает код 2 быстрее 4 с) проходит.
- Прочитанные ответы не остаются: тесты `client_removes_reply`, `reply_kept_while_request_exists`. В хост-проекте после прогона `compile` и `tests --wait` через CLI при открытом редакторе в `Replies/` не больше десяти файлов.
- Очистка удаляет только файлы старше суток и переживает занятый файл: тесты `sweep_*`.
- Очистка не глушит диспетчер: в хост-проекте с 50 000 файлов в `Replies/` с временем изменения −2 суток heartbeat диспетчера за 3 минуты после старта ни разу не старше 1,5 с, а `agentbridge queue list` отвечает быстрее секунды. Файлы удалены, в `Logs/AgentBridge-client-*.jsonl` есть `queue_cleanup` с `Deleted` около 50 000 и `Host`.
- Неподтверждённый запрос виден в телеметрии: тест `unacknowledged_is_logged`.
- `AgentBridgeCli.Tests: PASS`. `build-plugin.ps1` завершается `invalid_entries=0` и `zip_validation=PASS`. Unity-пакет компилируется в хост-проекте.

## Документация

- `README.md`: в дереве `Library/AgentBridge/` строка `Replies/` → «operation acknowledgments; the reader deletes its reply, the dispatcher sweeps leftovers older than 24 h». В таблицу телеметрии добавить строки писателя `client` (сервер пишет в клиентский файл): `queue_unacknowledged` — `Op`, `WaitedMs`, `ServerAlive`, `Starts`, `Host`; `queue_cleanup` — `Deleted`, `Failed`, `Kept`, `ElapsedMs`, `Host`; `queue_slow` — `Phase` (`heartbeat`/`request`/`sync`), `Op`, `ElapsedMs`, `Host`.
- `Docs/PROJECT_MAP.md`: строка `QueueClient.cs` — «…, удаляет прочитанный ответ, пишет `queue_unacknowledged`». Строка `QueueServer.cs` — «…, фоновая очистка `Replies/` старше суток, телеметрия медленных фаз цикла».
- `SKILL.md` и `UNITYAGENT.md` не меняются: контракт для агента прежний.
