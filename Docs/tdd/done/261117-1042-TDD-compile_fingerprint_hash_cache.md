Status: Выполнено

# Отпечаток компиляции берёт хеши файлов из кэша отметок

`CompileFingerprint.Capture` при каждом вызове открывает и хеширует все `.cs`, `.asmdef`, `.asmref`, `.rsp`, `.dll`, `package.json` и их `.meta`, все `ProjectSettings/` и манифесты пакетов. В xp-gang это 5 720 файлов и 20,5 МБ. Отпечаток считается на каждом скане `CachedResultServer`, при подготовке и завершении прогона, в `TestRunCoalescer`, а через `CompileFingerprint.Current()` — на главном потоке в `CompileTaskExecutor` и `TestFingerprint`. Результат правки: отпечаток хеширует файлы через `InputFileHasher` с общим кэшем `ValidationEvidence.HashCache` из ТДД 261117-1000. Файл с неизменной отметкой не открывается, а хеш одного и того же `.cs` общий у дайджеста и отпечатка. Формат строки отпечатка меняется (hex вместо base64), поэтому `compile-cache.json` один раз промахнётся.

Строится на `InputHashCache`, `InputFileHasher` и `ValidationEvidence.HashCache` в том виде, как их задаёт ТДД 261117-1000. `Commit` с пустым набором корней ничего не удаляет и только дописывает: отпечаток видит лишь часть файлов под корнями дайджеста.

## Пакет: `AgentBridgeUnity/Packages/com.elmortem.agentbridge/Editor`

### `CompileFingerprint.cs`

- Перегрузку `Capture(string projectRoot, string[] roots, string context)` оставить: её по сигнатуре ищет `Compile regression tests`. Она делегирует в новую с `cache = null`. `Capture(string projectRoot)` не меняется.
- `Current()` вызывает `Capture(BridgePaths.ProjectRoot, CompileInputContext.Roots, CompileInputContext.Context, CompileInputContext.HashCache)`.
- Новая перегрузка `Capture(string projectRoot, string[] roots, string context, InputHashCache cache)`. Сбор и сортировка файлов прежние. Цикл заменяется таким:

```csharp
var builder = new StringBuilder("compile-v3|" + context + "\n");
using (var hasher = new InputFileHasher(cache))
{
	foreach (string file in files)
	{
		long length;
		string hash;
		hasher.TryHash(file, out length, out hash);
		builder.Append(file.Replace('\\', '/')).Append('|').Append(length).Append('|').Append(hash).Append('\n');
	}

	hasher.Commit(new string[0]);
}
```

- Результат `TryHash` не проверяется: длина и хеш относятся к одним и тем же прочитанным байтам. Исключения `FileStream` по-прежнему доходят до вызывающего. `FileInfo` и прямое `File.OpenRead` убираются, шлюз чтения теперь внутри `InputFileHasher`.
- Комментарий «Deliberately never memoized…» переписать: хеш файла переиспользуется, только пока не сдвинулась его отметка, включая ChangeTime. Правка сдвигает ChangeTime, а файл, изменённый в пределах `InputHashCache.RacyWindowTicks` до снимка, в кэш не попадает. Комментарий «Timestamps and sizes can be preserved…» удалить вместе с кодом, к которому он относится.

### `CompileInputContext.cs`

- Свойство `public static InputHashCache HashCache { get { return ValidationEvidence.HashCache; } }` — только главный поток, как и `Roots`.
- `StartCapture`: рядом с `roots` и `context` взять `InputHashCache cache = HashCache;` и вызвать `CompileFingerprint.Capture(projectRoot, roots, context, cache)` внутри `Task.Run`.

### `package.json`

- Версия `0.45.0`.

## Тестовые стенды

- `AgentBridgeCompile.Tests/Stubs.cs`, заглушка `CompileInputContext`: добавить `public static InputHashCache HashCache => null;`.
- `AgentBridgeCoordination.Tests/BatchHost.cs`, заглушка `CompileInputContext`: то же.

## Тесты

- `HashingScenarios.C27_digest_rereads_only_changed_files` дополнить. В фикстуру добавить `c.cs` с текстом `class C {}`; суммы `ReadBytes` первого снимка учитывают его. Сразу после шага «новый экземпляр кэша по тому же пути» и до правки `b.asset`:
  - `CompileFingerprint.Capture(root, new[] { fixture }, "ctx", cache)` равен тому же вызову с `cache = null`;
  - следующий дайджест тем же кэшем по-прежнему даёт `ReadBytes == 0`, то есть `Commit` отпечатка не вычеркнул записи дайджеста.
- Затронуты без правок: `Compile regression tests` (без кэша), `AgentBridgeEvidenceTests`.

## Порядок работ

- `CompileFingerprint`: новая перегрузка с `InputFileHasher`, `Current()` с кэшем, комментарии.
- `CompileInputContext.HashCache` и `StartCapture`, заглушки в обоих стендах.
- Дополнить C27.
- Версия пакета 0.45.0, документация, `scripts/build-plugin.ps1`.

## Приёмка

- `dotnet run --project AgentBridgeCoordination.Tests -c Release -p:HashPlatform=UNITY_EDITOR_WIN -- --group all` выдаёт `PASS`, C27 проходит с шагами отпечатка. Без `HashPlatform` — тоже `PASS`.
- `AgentBridgeCompile.Tests` проходят 11 из 11.
- EditMode в AgentBridgeUnity зелёные.
- После задачи `compile` в хост-проекте `Library/AgentBridge/input-hash-cache.txt` содержит строки для `.cs` пакета `com.elmortem.agentbridge`.
- `build-plugin.ps1` проходит.

## Документация

- `README.md`, абзац «The compile fingerprint hashes source contents…»: хеши файлов берутся из того же кэша отметок, что и дайджест; файл с неизменной отметкой не перечитывается.
- `Docs/PROJECT_MAP.md`, строка `CompileFingerprint.cs`: хеши файлов через `InputFileHasher` и общий кэш отметок.
