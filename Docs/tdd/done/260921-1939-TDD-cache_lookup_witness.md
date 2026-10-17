Status: Выполнено

# Выдача из кеша: свидетели только при кандидате и откат промахов

Вид: производительность с сохранением доказательства. Сейчас `CachedResultServer.ServeAsync` раз в секунду открывает окно наблюдателя первой же строкой, пока в очереди ждёт любая кешируемая задача, даже если в индексе кеша для неё ничего нет: хаб из-за этого не остывает, и опрашивающий `DefaultWatcher` непрерывно обходит дерево входов на фоновом потоке (на 8000 файлов — около 580 мс работы на каждый круг в 750 мс). Если кандидат по отпечатку источников есть, а digest не совпал, каждую секунду заново считается полный digest входов (4,4 с на 16 004 файла). После изменения свидетели открываются только когда дешёвый кандидат найден, свидетелем окна выдачи становится stat-манифест вокруг обоих замеров digest, а промах по digest не перепроверяется, пока не сменились отпечаток источников или набор кандидатов и не прошло 10 секунд. Ожидаемое: без кандидата — ни одного открытия свидетеля и ни одного `input_hash`; при устойчивом промахе — не больше одного `cache_lookup` в 10 секунд вместо одного в секунду.

Порядок доказательства для `tests`: `F0` (только ключ фильтра, вне окна) → кандидат → свидетель (`M0` и наблюдатель) → `D1` → `TryServe` → `D2` → `F1` → `M1` → требуем `D1 == D2`, `F1 == F0`, `Compare(M0, M1).Changed == 0`, наблюдатель чист. Все чтения, на которых стоит решение, лежат между `M0` и `M1`. Для `compile` манифест не нужен: отпечаток источников сам состоит из размеров и mtime, поэтому `F1 == F0` ловит и правку с возвратом; там откладывается только наблюдатель.

## Свидетель и откат в `Packages/com.elmortem.agentbridge/Editor`

Три типа не зависят от Unity и линкуются в `AgentBridgeCoordination.Tests`.

### `CacheLookupWitness.cs` — новый файл

```csharp
using System;
using System.Threading.Tasks;

namespace AgentBridge
{
	public sealed class CacheLookupWitness : IDisposable
	{
		private readonly string[] _roots;
		private readonly string[] _excluded;
		private readonly Func<string, bool> _ignore;

		public ValidationInputMonitor Monitor;
		public InputStatManifest Start;

		private CacheLookupWitness(string[] roots, string[] excluded, Func<string, bool> ignore)
		{
			_roots = (string[])roots.Clone();
			_excluded = (string[])excluded.Clone();
			_ignore = ignore;
		}

		public static async Task<CacheLookupWitness> OpenAsync(string[] roots, string[] excluded, Func<string, bool> ignore)
		{
			var witness = new CacheLookupWitness(roots, excluded, ignore);
			Task<ValidationInputMonitor> monitor = ValidationInputMonitor.OpenAsync(witness._roots, witness._excluded, ignore);
			Task<InputStatManifest> start = Task.Run(() => InputStatManifest.Capture(witness._roots, witness._excluded, ignore));
			witness.Monitor = await monitor;
			witness.Start = await start;
			return witness;
		}

		public Task<InputStatVerdict> VerifyAsync()
		{
			InputStatManifest start = Start;
			string[] roots = _roots;
			string[] excluded = _excluded;
			Func<string, bool> ignore = _ignore;
			return Task.Run(() => InputStatManifest.Compare(start, InputStatManifest.Capture(roots, excluded, ignore)));
		}

		public void Dispose()
		{
			if (Monitor != null)
			{
				Monitor.Dispose();
			}
		}
	}
}
```

### `CacheMissEntry.cs` — новый файл

```csharp
namespace AgentBridge
{
	public sealed class CacheMissEntry
	{
		public string SourceFingerprint = "";
		public string CandidateKey = "";
		public long RetryAtMs;
	}
}
```

### `CacheMissMemo.cs` — новый файл

```csharp
using System.Collections.Generic;

namespace AgentBridge
{
	public sealed class CacheMissMemo
	{
		public const long RetryMs = 10000;

		private readonly Dictionary<string, CacheMissEntry> _entries = new Dictionary<string, CacheMissEntry>();

		public bool ShouldSkip(string taskId, string sourceFingerprint, string candidateKey, long nowMs)
		{
			CacheMissEntry entry;
			if (!_entries.TryGetValue(taskId, out entry))
			{
				return false;
			}

			if (entry.SourceFingerprint != sourceFingerprint || entry.CandidateKey != candidateKey || nowMs >= entry.RetryAtMs)
			{
				_entries.Remove(taskId);
				return false;
			}

			return true;
		}

		public void Record(string taskId, string sourceFingerprint, string candidateKey, long nowMs)
		{
			_entries[taskId] = new CacheMissEntry
			{
				SourceFingerprint = sourceFingerprint,
				CandidateKey = candidateKey,
				RetryAtMs = nowMs + RetryMs
			};
		}

		public void Forget(string taskId)
		{
			_entries.Remove(taskId);
		}

		public void Retain(ICollection<string> liveTaskIds)
		{
			var dead = new List<string>();
			foreach (string id in _entries.Keys)
			{
				if (!liveTaskIds.Contains(id))
				{
					dead.Add(id);
				}
			}

			foreach (string id in dead)
			{
				_entries.Remove(id);
			}
		}
	}
}
```

## `CachedResultServer.cs`

- Поле `private static readonly CacheMissMemo Misses = new CacheMissMemo();`.
- `ServeAsync`:
	- строку `using var monitor = await ValidationInputMonitor.OpenAsync(roots, excluded, ignore);` заменить на `CacheLookupWitness witness = null;`; всё после неё обернуть в `try { ... } finally { if (witness != null) { witness.Dispose(); } }`;
	- после `nowMs`: собрать `HashSet<string>` идентификаторов `pending` и вызвать `Misses.Retain(...)`;
	- комментарий в начале метода привести в соответствие: свидетели открываются один раз за скан и только при кандидате.
- Приватный помощник, чтобы открытие было одно на скан и попадало в телеметрию:

```csharp
private static async Task<CacheLookupWitness> OpenWitness(string[] roots, string[] excluded, Func<string, bool> ignore, string taskId)
{
	var watch = System.Diagnostics.Stopwatch.StartNew();
	CacheLookupWitness witness = await CacheLookupWitness.OpenAsync(roots, excluded, ignore);
	TelemetryLog.Write("cache_witness", "", taskId, new[] {
		TelemetryField.Number("ElapsedMs", watch.ElapsedMilliseconds),
		TelemetryField.Number("Files", witness.Start.Entries.Count)
	});
	return witness;
}
```

- Ветка `tests`:
	- вместо `bool candidate = ...Exists(...)`: пройти `TestRunDumpStore.ReadIndex().Entries`, собрать `Id` записей с `TestMode == mode`, `Validity == EvidenceRecord.Valid`, `SourceFingerprint == sourceFingerprint`; пусто → `continue`; отсортировать `StringComparer.Ordinal`; `string candidateKey = string.Join(",", ids);`
	- `if (Misses.ShouldSkip(task.Id, sourceFingerprint, candidateKey, nowMs)) { continue; }`;
	- до `job.Measure("cache_lookup", ...)`: `if (witness == null) { witness = await OpenWitness(roots, excluded, ignore, task.Id); }`;
	- при неудаче первого `TestCacheQuery.TryServe`: `Misses.Record(task.Id, sourceFingerprint, candidateKey, nowMs);` и `continue`;
	- после `verifiedSources`, до блока перепроверок: `InputStatVerdict stat = await witness.VerifyAsync();`
	- после существующей проверки `!verified.Complete || inputDigest != verified.Digest || ...` добавить отдельную: `!stat.Complete || stat.Changed != 0` → `TelemetryLog.Write("cache_skip", "", task.Id, ...)` с `What = "inputs moved during the cache lookup"` и `Paths = string.Join(";", stat.Paths)` (при `!stat.Complete` — `What = stat.Reason`), затем `continue`;
	- `CanPublish(task, requestHash, witness.Monitor)`; после `TaskJournal.Write(record)` — `Misses.Forget(task.Id)`.
- Ветка `compile`: после успешного `CompileCacheStore.CanReuse`, до повторного `StartCapture` — тот же блок ленивого `OpenWitness`; `CanPublish(..., witness.Monitor)`. `VerifyAsync` здесь не вызывается.
- `CanPublish` не меняется.

## Тесты

- `AgentBridgeCoordination.Tests/CacheLookupScenarios.cs` — новый, `internal static class CacheLookupScenarios`, `Run(string root, List<string> covered)`, добавляет `"C24_cache_lookup"`; вызвать из `Program.cs` после `StatManifestScenarios.Run`. В начале и в конце — `InputWatchHub.Shutdown()`. Случаи:
	- `CacheLookupWitness.OpenAsync` на неизменном дереве: `Monitor.Observed`, `Start.Complete`, `VerifyAsync` → `Complete`, `Changed == 0`;
	- файл переписан тем же содержимым через 50 мс после открытия: `VerifyAsync` → `Changed == 1`, путь назван;
	- файл под `excluded` и файл, отсечённый `ignore`, не считаются;
	- недостижимый корень: `Start.Complete == false`, `VerifyAsync` → `Complete == false` с причиной;
	- после `Dispose` свидетеля `Monitor` не считает события;
	- `CacheMissMemo`: пропуск внутри `RetryMs`; сброс при другом отпечатке, при другом `candidateKey`, по времени; `Forget`; `Retain` убирает записи снятых задач.
- В `AgentBridgeCoordination.Tests.csproj` добавить `Compile Include` для `CacheLookupWitness.cs`, `CacheMissEntry.cs`, `CacheMissMemo.cs`.
- Затрагиваемые существующие: `C13`, `C22_observer_hub`, `C23_stat_manifest`, EditMode `AgentBridgeEvidenceTests`.

## Порядок работ

- База по телеметрии в хост-проекте: открыть play-сессию, поставить в очередь нефрешевую задачу `tests` при существующей валидной записи кеша с тем же отпечатком источников, но изменённом несходном входе (файл под `Assets`, не `.cs`), подождать 30 секунд; записать число `input_hash Stage=cache_lookup` для этой задачи. Числа — в заметку.
- `CacheMissEntry.cs`, `CacheMissMemo.cs`, `CacheLookupWitness.cs`, линки, `CacheLookupScenarios.cs`.
- `CachedResultServer.cs`: ленивый свидетель, манифест в ветке `tests`, откат промахов, телеметрия `cache_witness` и `cache_skip`.
- Повторить сценарий базы; отдельно — тот же сценарий без кандидата (индекс кеша без подходящей записи), 30 секунд.
- Версия пакета `0.33.0` → `0.34.0`, сборка плагина по правилам `CLAUDE.md`, документация.

## Приёмка

- `Coordination: PASS` со всеми группами, включая `C24_cache_lookup`.
- EditMode `AgentBridgeEvidenceTests`: `Evidence: valid`; повтор той же задачи — `Cached: true`, в телеметрии одно `cache_witness` перед выдачей.
- Ожидание без кандидата 30 секунд: ноль событий `cache_witness` и ноль `input_hash` для ждущей задачи.
- Устойчивый промах 30 секунд: `input_hash Stage=cache_lookup` для задачи — не больше 4 (база записана рядом).
- Смена mtime входного файла между `cache_lookup` и выдачей не даёт `Cached: true`: в телеметрии `cache_skip` с `inputs moved during the cache lookup` и этим путём. Подтверждается `C24_cache_lookup` на уровне свидетеля.
- `compile` по-прежнему отдаётся из кеша при совпадающих источниках (`compile_reuse` в логах записи).

## Документация

- `Docs/PROJECT_MAP.md`: строка `CachedResultServer.cs` и пункт жизненного цикла про выдачу из кеша — свидетели только при кандидате, stat-манифест вокруг замеров, откат промахов; новые строки для `CacheLookupWitness.cs`, `CacheMissMemo.cs`, `CacheMissEntry.cs`.
- `Docs/notes/260921-1939-NOTE-cache-lookup-cost.md` — новая: числа базы и после, цена `cache_witness`.
- `README.md`, `UNITYAGENT.md`, `SKILL.md` — только если там описано поведение выдачи из кеша, которое изменилось.
