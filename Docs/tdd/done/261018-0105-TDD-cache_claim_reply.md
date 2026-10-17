Status: Выполнено

# Кэш тестов и компиляции дожидается ответа диспетчера на claim

Повтор `tests` или `compile` без изменений входов должен приходить из кэша (`Cached: true`). Сейчас кэш тестов не срабатывает ни разу: в телеметрии хоста за 16–17.10 после `cache_verify` нет ни одного `cache_skip`, а через ~400 мс та же задача стартует обычным прогоном. Обычный старт идёт в `task_start` с `WaitedMs ≈ 960`, например `Task_20261017_211429_212_3547ad4e` и `Task_20261017_211449_144_dca1127a`. После правки кэш ждёт ответа на claim, а каждый молчаливый выход из проверки пишет `cache_skip` с причиной.

Причина в коде. `CachedResultServer.ServeAsync` после всех проверок вызывает `TryReserveCache` → `CoordinationGate.TryReserve` → `QueueEditorAdapter.TryClaim` → `TryCall`. `TryCall` неблокирующий: при первом вызове он пишет запрос диспетчеру и возвращает `false`, потому что ответа ещё нет. `TryClaim` при этом отдаёт `reason = Busy`. `ServeAsync` расценивает это как отказ и делает `continue`. `RunLookup` снимает `Checking`, и `TaskCoordinator.StartTask` (строка ~767) зовёт тот же `TryReserve` с тем же ключом `claim:<id>`. Он находит уже готовый ответ, получает claim и запускает обычный прогон. Сам координатор `Busy` правильно трактует как «повторить на следующем тике». Компиляционная ветка `ServeAsync` вызывает `TryReserveCache` так же и страдает так же.

## Пакет `AgentBridge` (`AgentBridgeUnity/Packages/com.elmortem.agentbridge/Editor/`)

### `QueueEditorAdapter.cs`

- Новое свойство `public static bool LastCallAwaitingReply { get; private set; }`.
- В `TryCall`: перед `return false` на непрочитанном ответе (`!SharedFile.TryReadAllText(...Reply...)`) ставить `LastCallAwaitingReply = true`. На прочитанном ответе и в `catch` — `false`. Значение относится только к последнему вызову и читается синхронно сразу после него на главном потоке.

### `CachedResultServer.cs`

- Новые константы:

```csharp
private const int ClaimReplyPollMs = 50;
private const int ClaimReplyTimeoutMs = 5000;
```

- `TryReserveCache` заменяется асинхронным резервированием, которое дожидается ответа диспетчера. Пока оно ждёт, задача остаётся в `Checking`, и координатор её не трогает (`TaskCoordinator`, `pending.RemoveAll(... IsChecking ...)`):

```csharp
private static async Task<string> ReserveCacheAsync(TaskRequest request, string taskId)
{
	long deadline = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() + ClaimReplyTimeoutMs;
	while (true)
	{
		string reason;
		if (CoordinationGate.TryReserve(request, taskId, out reason))
		{
			return null;
		}
		if (!QueueEditorAdapter.LastCallAwaitingReply)
		{
			return "claim_refused: " + reason;
		}
		if (DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() >= deadline)
		{
			return "claim_reply_timeout";
		}
		await Task.Delay(ClaimReplyPollMs);
	}
}
```

  Возврат `null` значит, что claim получен. Строка — причина отказа. Повторный `TryReserve` идемпотентен: `TryCall` переиспользует отложенный запрос по тому же ключу (`claim:<id>` или `coord:<команда>`), и новый запрос не создаётся. На тайм-ауте отложенный запрос остаётся в `Pending`, и его ответ заберёт обычный старт координатора, как сейчас.
- Новый помощник для молчаливых выходов:

```csharp
private static void Skip(string taskId, string what)
{
	TelemetryLog.Write("cache_skip", "", taskId, new[] { TelemetryField.Text("What", what) });
}
```

- Ветка `tests` после `cache_verify`. Каждый `continue`, который сейчас ничего не пишет, получает `Skip` с причиной:
	- повторный `TestCacheQuery.TryServe` не нашёл запись или сменился контекст → `"entry_gone_on_recheck"`;
	- `!verified.Complete || inputDigest != verified.Digest` → `"digest_moved"`;
	- `sourceFingerprint != verifiedSources` → `"sources_moved"`;
	- `!CanPublish(...)` → `"cannot_publish"`;
	- резервирование: `string reserveError = await ReserveCacheAsync(request, task.Id); if (reserveError != null) { Skip(task.Id, reserveError); continue; }`.

  Составное условие `if (!verified.Complete || inputDigest != verified.Digest || sourceFingerprint != verifiedSources || !CanPublish(...)) continue;` разбивается на три отдельных `if` с телом в фигурных скобках. Существующий `cache_skip` для `stat` не меняется.
- Ветка `compile`: `if (sourceFingerprint != await CompileInputContext.StartCapture(projectRoot) || !CanPublish(...)) continue;` разбивается так же (`"sources_moved"`, `"cannot_publish"`). `TryReserveCache` заменяется тем же `ReserveCacheAsync` со `Skip`. Ранние выходы до кандидата (`CompileCacheStore.TryRead`, `CanReuse`, отсутствие кандидатов у тестов, `Misses.ShouldSkip`) — обычный промах. Они остаются молчаливыми, чтобы не засорять телеметрию на каждом скане.
- Старый метод `TryReserveCache` удаляется, других вызывающих у него нет.

## Тесты

- Нет. Правка — проводка ожидания вокруг файлового обмена с внешним диспетчером. Без живого диспетчера её риск не проверить. Подтверждается живым повтором и телеметрией.

## Порядок работ

- `QueueEditorAdapter.cs`: `LastCallAwaitingReply` в `TryCall`.
- `CachedResultServer.cs`: константы, `ReserveCacheAsync`, `Skip`, разбиение условий и причины в обеих ветках, удаление `TryReserveCache`.
- Поднять patch-версию пакета (`0.40.0 → 0.40.1`). Плагин и CLI не меняются, плагин пересобрать.
- `Docs/PROJECT_MAP.md`: в строке `CachedResultServer.cs` упомянуть ожидание ответа на claim и причины `cache_skip`.

## Приёмка

- В хосте `AgentBridgeUnity` два одинаковых `tests --mode EditMode --test <класс>` подряд без изменений входов: второй приходит с `Cached: true` и `SourceTaskId` первого. Подтверждается живым прогоном.
- То же для `--mode PlayMode`: второй прогон — `Cached: true`, Play Mode не запускается. Подтверждается живым прогоном.
- Два `compile` подряд без изменений: второй — `Cached: true`. Подтверждается живым прогоном.
- Если сразу после первого прогона изменить файл в `Assets/`, повтор идёт обычным прогоном. В `Logs/AgentBridge-editor-*.jsonl` при этом нет `cache_skip` с `claim_*`. Причина промаха видна (`digest_moved`/`sources_moved`, ранний промах или существующий `cache_skip` по `stat`). Подтверждается живым прогоном и телеметрией.

## Документация

- `Docs/PROJECT_MAP.md`, строка `CachedResultServer.cs`: ожидание ответа диспетчера на claim (до 5 с, задача держится в `Checking`) и перечень причин `cache_skip`.
- `README.md`, `SKILL.md`, `UNITYAGENT.md`: нет, обещанное в них поведение кэша не меняется.
