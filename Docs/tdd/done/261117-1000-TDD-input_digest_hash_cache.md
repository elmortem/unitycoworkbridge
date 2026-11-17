Status: Выполнено

# Дайджест входов перечитывает только изменившиеся файлы

Сейчас каждый `ValidationInputSnapshot.Capture` читает все входы: в xp-gang это 38 тыс. файлов и 2,4 ГБ, `input_hash` в среднем 6,6 с, три дайджеста на задачу `tests`/`compile`. Результат правки: дайджест читает только файлы, у которых сдвинулась отметка — длина, время записи, ChangeTime или FileId. Отметки берутся из листинга каталога без открытия файлов (на xp-gang 38 145 файлов за 92 мс), хеши хранятся в `Library/AgentBridge/input-hash-cache.txt`. У неизменного проекта дайджест укладывается меньше чем в секунду, `ReadBytes` равен сумме длин изменившихся файлов. В поток дайджеста идёт хеш файла вместо его байтов, поэтому старый кэш тестов один раз промахнётся.

Проверено на Windows: правка той же длины с возвратом времени записи сдвигает ChangeTime, замена переименованием меняет FileId, чтение ничего не меняет. Отметки есть только в сборке `UNITY_EDITOR_WIN`, иначе файлы читаются целиком.

## Пакет: `AgentBridgeUnity/Packages/com.elmortem.agentbridge/Editor`

### `InputFileStamp.cs` — новый файл

- `public struct InputFileStamp` с полями `public long Length, WriteTicks, ChangeTicks, FileId;`. Времена хранятся в FILETIME UTC, как их отдаёт система.

### `InputFileStamps.cs` — новый файл

- `public static Dictionary<string, InputFileStamp> Read(string directory)` возвращает `null`, если платформа или том отметок не дают; тогда файлы читаются целиком. Вне `#if UNITY_EDITOR_WIN` всегда `null`, по образцу `ContentHash`.
- P/Invoke `kernel32`: `CreateFileW` (`CharSet.Unicode`, `SetLastError`) → `SafeFileHandle` и `GetFileInformationByHandleEx(SafeFileHandle, int, byte[], int)`.
- Каталог открывается `CreateFileW(directory, 0x1, 0x7, IntPtr.Zero, 3, 0x02000000, IntPtr.Zero)`. Невалидный дескриптор — `null`.
- Цикл: `GetFileInformationByHandleEx(handle, 10, buffer, 65536)`. При `false` с ошибкой 18 вернуть словарь, при иной — `null`.
- Записи идут по `NextEntryOffset` (`int` +0, ноль у последней). Смещения проверены на Windows: атрибуты `int` +56, длина имени в байтах `int` +60, имя UTF-16 +104, `long` LastWriteTime +24, ChangeTime +32, EndOfFile +40, FileId +96.
- Атрибуты `0x10 | 0x400` пропускаются: у симлинка отметка ссылки, а не цели. Ключ — имя файла, `OrdinalIgnoreCase`.

### `InputHashCache.cs` — новый файл

Чистый System.IO, `using AgentBridge.Coordination;` для `SharedFile`. Все методы держат блокировку `_sync`: снимки идут на разных рабочих потоках и на главном.

```csharp
public sealed class InputHashCache
{
	// A file changed this close to a capture can share its change time with its next edit,
	// so its hash serves that capture and is not remembered.
	public const long RacyWindowTicks = 2 * TimeSpan.TicksPerSecond;
	private const string Header = "input-hash-cache-v1";

	public struct Entry
	{
		public InputFileStamp Stamp;
		public string Hash;
	}

	public InputHashCache(string path);
	public bool TryGet(string path, InputFileStamp stamp, out string hash);
	public void Commit(string[] roots, Dictionary<string, Entry> seen);
}
```

- Ключ — `ValidationInputSnapshot.Normalize(file)`, словарь с `OrdinalIgnoreCase`.
- `Load()` срабатывает один раз на экземпляр через `SharedFile.TryReadAllText`. Нет файла, чужой `Header`, неразборчивая строка — словарь пуст.
- Строка файла: `ChangeTicks|WriteTicks|Length|FileId|Hash|Path`, числа в `InvariantCulture`, разбор `Split(new[] { '|' }, 6)`.
- `TryGet` даёт хеш, только если совпали все четыре поля отметки.
- `Commit` выполняется после полного снимка:
  - удалить записи под любым корнем (`Normalize(root).TrimEnd('/') + "/"`), которых нет в `seen`;
  - записать `seen` поверх;
  - если что-то поменялось — `Save()`.
- `Save()` вызывает `SharedFile.WriteAtomic(_path, text)` и глушит `IOException` и `UnauthorizedAccessException` с комментарием: несохранённый кэш стоит следующему домену одного полного чтения.

### `InputFileHasher.cs` — новый файл

Хеширование файлов одного снимка: им пользуются дайджест и отпечаток компиляции (следующим ТДД). Чистый System.IO.

```csharp
public sealed class InputFileHasher : IDisposable
{
	private readonly InputHashCache _cache;
	private readonly long _racyFrom;
	private readonly Dictionary<string, Dictionary<string, InputFileStamp>> _directories = new Dictionary<string, Dictionary<string, InputFileStamp>>(StringComparer.OrdinalIgnoreCase);
	private readonly Dictionary<string, InputHashCache.Entry> _seen = new Dictionary<string, InputHashCache.Entry>(StringComparer.OrdinalIgnoreCase);
	private readonly byte[] _buffer = new byte[64 * 1024];
	private readonly SHA256 _sha = ContentHash.Create();

	public InputFileHasher(InputHashCache cache)
	{
		_cache = cache;
		_racyFrom = DateTime.UtcNow.ToFileTimeUtc() - InputHashCache.RacyWindowTicks;
	}

	public long ReadBytes { get; private set; }

	// Hash of the bytes as they are now. False when the length moved while the file was read.
	// FileStream exceptions reach the caller.
	public bool TryHash(string file, out long length, out string hash)
	{
		string key = ValidationInputSnapshot.Normalize(file);
		InputFileStamp stamp = default(InputFileStamp);
		bool stamped = _cache != null && TryStamp(file, out stamp);
		if (stamped && _cache.TryGet(key, stamp, out hash))
		{
			length = stamp.Length;
			_seen[key] = new InputHashCache.Entry { Stamp = stamp, Hash = hash };
			return true;
		}

		InputReadGate.Enter();
		try
		{
			using (var input = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.Read, _buffer.Length, FileOptions.SequentialScan))
			{
				long expected = input.Length;
				_sha.Initialize();
				length = 0;
				int count;
				while ((count = input.Read(_buffer, 0, _buffer.Length)) != 0)
				{
					_sha.TransformBlock(_buffer, 0, count, null, 0);
					length += count;
				}
				_sha.TransformFinalBlock(_buffer, 0, 0);
				hash = ValidationInputSnapshot.ToHex(_sha.Hash);
				ReadBytes += length;
				if (stamped && stamp.Length == length && stamp.ChangeTicks < _racyFrom)
				{
					_seen[key] = new InputHashCache.Entry { Stamp = stamp, Hash = hash };
				}
				return length == expected;
			}
		}
		finally
		{
			InputReadGate.Exit();
		}
	}

	public void Commit(string[] roots);
	public void Dispose();
}
```

- `Commit` при наличии кэша вызывает `_cache.Commit(roots, _seen)`, `Dispose` освобождает `_sha`.
- `_sha.Initialize()` нужен перед каждым файлом: `WindowsSha256` после `HashFinal` без него непригоден.
- `private bool TryStamp(string file, out InputFileStamp stamp)` берёт листинг каталога файла из `_directories`. Если его там нет, читает через `InputFileStamps.Read` и сохраняет, `null` тоже. Возвращает `true`, если в листинге есть имя файла и `ChangeTicks != 0`.

### `ValidationInputSnapshot.cs`

- Поле `public long ReadBytes;` — байты, прочитанные с диска. `TotalBytes` остаётся суммой длин входов.
- Перегрузка с `ignore` делегирует в новую с `cache = null`. Новая перегрузка — `Capture(string[] roots, string[] excludedRoots, string context, Func<string, bool> ignore, InputHashCache cache)`.
- Буфер и шлюз уходят в `InputFileHasher`, рядом с общим `sha` — `using (var hasher = new InputFileHasher(cache))`. Тело цикла по файлам:

```csharp
string relative = prefix + "/" + RelativeTo(root, file);
long length;
string hash;
try
{
	if (!hasher.TryHash(file, out length, out hash)) return Incomplete("input changed size while reading: " + relative);
}
// три существующих catch (missing / missing / could not be read) без изменений
Append(stream, "file\n" + relative + "\n" + length + "\n" + hash + "\n");
snapshot.FileCount++;
snapshot.TotalBytes += length;
```

- После `snapshot.Digest = ToHex(sha.Hash);`: `snapshot.ReadBytes = hasher.ReadBytes; hasher.Commit(snapshot.Roots);`.
- В шапке класса фразу «hash actual bytes, even when size and time are preserved» заменить: байты перечитываются при сдвинутой отметке, а ChangeTime выдаёт и правку с сохранёнными длиной и временем записи.

### `InputHashJob.cs`

- Добавить поле `_cache`. Конструктор: `InputHashJob(string[] roots, string[] excluded, string context, Func<string, bool> ignore = null, InputHashCache cache = null)`.
- `Start()` вызывает `Capture(_roots, _excluded, _context, _ignore, _cache)`. В `Measure` добавить `TelemetryField.Number("ReadBytes", result.ReadBytes)`.

### `BridgePaths.cs`

- Свойство `InputHashCacheFile` → `Path.Combine(WorkingRoot, "input-hash-cache.txt")`, по образцу `DialogsFile`.

### `ValidationEvidence.cs`

- `public static InputHashCache HashCache` лениво создаёт `new InputHashCache(BridgePaths.InputHashCacheFile)` в `_hashCache`. Только главный поток.
- Передать `HashCache` последним аргументом в `new InputHashJob(...)` в `BeginPrepare` и в `TryComplete`.

### `CachedResultServer.cs`

- В `ServeAsync` рядом с `CollectRoots()`, до первого `await`, добавить `InputHashCache hashCache = ValidationEvidence.HashCache;` и передать его в `new InputHashJob(roots, excluded, context, ignore, hashCache)`.
- Комментарий перед `capturedFingerprint` («a memo keyed on sizes and times…») переписать: ключ памяти — отметка с ChangeTime. `CurrentInputDigest` не трогать.

### `package.json`

- Версия `0.44.0`.

## Тестовые стенды

- В оба тестовых `.csproj` рядом с `ValidationInputSnapshot.cs` добавить `Compile Include` для `InputFileStamp.cs`, `InputFileStamps.cs`, `InputHashCache.cs`, `InputFileHasher.cs`.

## Тесты

- `HashingScenarios.cs`: новый сценарий `C27_digest_rereads_only_changed_files` под `#if UNITY_EDITOR_WIN`, вызывается из `Run` после `SaveDuringCapture`.
  - Фикстура: `a.bin` на 1 МБ и `b.asset` с текстом `value: 1`, пауза `RacyWindowTicks` плюс 300 мс, кэш в `root/hash-cache.txt`. На каждом шаге дайджест с кэшем равен `Capture` без кэша.
  - Первый снимок: `ReadBytes` равен сумме длин. Второй: `ReadBytes == 0`. Новый экземпляр кэша по тому же пути: `ReadBytes == 0`.
  - `b.asset` переписан текстом `value: 2`, время записи возвращено через `File.SetLastWriteTimeUtc`: дайджест сдвинулся, `ReadBytes` равен длине `b.asset`. Сразу ещё снимок — снова длина `b.asset`.
- C21: ожидаемый поток `"context\ntest\nroot\nr0\nfile\nr0/data.bin\n" + data.Length + "\n" + hex(sha256(data)) + "\n"`.
- `AgentBridgeHashPerformanceTests.StreamingSnapshotPreservesExistingDigestFormat` (EditMode): переименовать в `SnapshotDigestHashesEachFile` и ожидать тот же новый формат.
- Затронуты без правок: `AgentBridgeEvidenceTests`, `BackgroundSnapshotYieldsAndOwnsImmutableInputs`.

## Порядок работ

- Добавить четыре новых файла с `.meta` (новые GUID), подключить их в оба тестовых `.csproj`.
- Переписать `ValidationInputSnapshot.Capture` и его шапку.
- Протянуть кэш через `InputHashJob`, `BridgePaths`, `ValidationEvidence`, `CachedResultServer`.
- Обновить C21 и EditMode-тест формата, добавить C27.
- Версия пакета 0.44.0, документация, `scripts/build-plugin.ps1`.

## Приёмка

- `dotnet run --project AgentBridgeCoordination.Tests -c Release -p:HashPlatform=UNITY_EDITOR_WIN -- --group all` выдаёт `PASS`, в перечне есть C27. Без `HashPlatform` — тоже `PASS`.
- `AgentBridgeCompile.Tests` проходят 11 из 11.
- EditMode в AgentBridgeUnity: `AgentBridgeHashPerformanceTests` и `AgentBridgeEvidenceTests` зелёные.
- Две задачи `tests --fresh` подряд на одном EditMode-тесте без правок: у второй `ReadBytes` события `input_hash` в `Logs/AgentBridge-editor-*.jsonl` меньше 1 % `Bytes`, файл `Library/AgentBridge/input-hash-cache.txt` существует.
- `build-plugin.ps1` проходит.

## Документация

- `README.md`, Evidence: перечитываются только файлы со сдвинутой отметкой (ChangeTime выдаёт и правку с прежними длиной и временем), хеши в `input-hash-cache.txt`, вне Windows читается всё.
- `Docs/PROJECT_MAP.md`: строки таблицы достоверности для четырёх новых файлов; `input-hash-cache.txt` в раскладке `Library/AgentBridge/`.
- `CLAUDE.md`, пункт «Достоверность»: дописать `InputFileStamps.cs`, `InputHashCache.cs`, `InputFileHasher.cs`.
