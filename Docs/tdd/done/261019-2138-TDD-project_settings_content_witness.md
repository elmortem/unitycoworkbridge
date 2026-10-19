Status: Выполнено

# Настройки проекта, перезаписанные теми же байтами, не бракуют прогон

Unity сама перезаписывает файлы в `ProjectSettings/` без изменения содержимого. Shader Graph вызывает `Save()` в `OnDisable()` при каждой перезагрузке домена, `QualitySettings.asset` сохраняется посреди прогона и возвращается к прежним байтам. Наблюдатель (`ValidationInputMonitor`) и stat-манифест (`InputStatManifest`) видят событие и новое время записи, и `ValidationEvidence.ClassifyChange` выдаёт `stale_input` с причиной «an input was modified and restored». Результат правки: для файлов под `ProjectSettings/` свидетелем служит содержимое, а не время. Наблюдатель этот корень не считает, манифест хранит SHA-256 этих файлов и признаёт файл неизменным, если длина и хеш совпали. Настоящее изменение настроек, оставшееся к концу прогона, по-прежнему бракует его через дайджест и хеш манифеста. Правило одно для прогона, выдачи из кэша и присоединения к идущему прогону. Файлы, вернувшиеся к прежним байтам, пишутся в телеметрию `input_restored`.

## Свидетель содержимого в `AgentBridgeUnity/Packages/com.elmortem.agentbridge/Editor`

### `InputStatEntry.cs`

- Добавить поле `public string Hash = "";`: SHA-256 содержимого в hex, заполняется только для файлов под корнем содержимого, у остальных пустое.

### `InputStatVerdict.cs`

- Добавить поля:

```csharp
public int Restored;
public List<string> RestoredPaths = new List<string>();
```

### `ValidationInputSnapshot.cs`

- `private static string ToHex(byte[] bytes)` сделать `internal`: манифест хеширует тем же кодом.

### `InputStatManifest.cs`

- Существующий `Capture(string[] roots, string[] excludedRoots, Func<string, bool> ignore)` оставить и делегировать в новую перегрузку с `contentRoot = ""`: на нём стоят тесты и вызовы без корня содержимого.
- Новая перегрузка. Нормализованный корень вычисляется один раз до обхода. Для каждого файла:

```csharp
public static InputStatManifest Capture(string[] roots, string[] excludedRoots, Func<string, bool> ignore, string contentRoot)
{
	string content = ValidationInputSnapshot.Normalize(contentRoot ?? "").TrimEnd('/');
	...
	string path = ValidationInputSnapshot.Normalize(file);
	manifest.Entries.Add(new InputStatEntry
	{
		Path = path,
		Length = info.Length,
		WriteTicks = info.LastWriteTimeUtc.Ticks,
		Hash = IsUnder(path, content) ? HashOf(file) : ""
	});
	...
}

private static bool IsUnder(string path, string root)
{
	if (root.Length == 0)
	{
		return false;
	}

	return path.StartsWith(root + "/", StringComparison.OrdinalIgnoreCase);
}

private static string HashOf(string file)
{
	using (var sha = ContentHash.Create())
	using (var input = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.Read))
	{
		return ValidationInputSnapshot.ToHex(sha.ComputeHash(input));
	}
}
```

- Исключение при хешировании ловит уже существующий `catch` в `Capture`: манифест становится `Incomplete`, как и при любом другом сбое чтения.
- `Save`: строка `ticks|length|hash|path`, у файлов без хеша поле пустое (`ticks|length||path`).
- `Load`: `line.Split(new[] { '|' }, 4)`, при `parts.Length != 4` — `Incomplete("the input stat manifest is unreadable")`, `Hash = parts[2]`, `Path = parts[3]`. Старый трёхпольный формат не поддерживается: манифест живёт только в пределах одного прогона.
- `Compare`, ветка совпавшего пути:

```csharp
if (before.Length != after.Length || before.WriteTicks != after.WriteTicks)
{
	if (before.Hash.Length > 0 && before.Length == after.Length && before.Hash == after.Hash)
	{
		verdict.Restored++;
		if (verdict.RestoredPaths.Count < MaxRecordedPaths)
		{
			verdict.RestoredPaths.Add(after.Path);
		}
	}
	else
	{
		Note(verdict, after.Path);
	}
}
```

### `ValidationEvidence.cs`

- Новые публичные методы:

```csharp
// Main thread: reads BridgePaths.ProjectRoot.
public static string ContentWitnessedRoot()
{
	return Path.Combine(BridgePaths.ProjectRoot, "ProjectSettings");
}

// Pure: safe on any thread.
public static string[] ObserverExcludedRoots(string[] excluded, string contentRoot)
{
	var result = new List<string>(excluded ?? new string[0]);
	result.Add(contentRoot);
	return result.ToArray();
}
```

- `InstallMonitor`: `new ValidationInputMonitor(_roots, ObserverExcludedRoots(_excluded, ContentWitnessedRoot()), _ignore)`.
- `StartManifest`: на главном потоке взять `string contentRoot = ContentWitnessedRoot();` и передать в `InputStatManifest.Capture(roots, excluded, ignore, contentRoot)`.
- `TryComplete`: так же взять `contentRoot` на главном потоке рядом с `startPath` и передать в `Capture` внутри `Task.Run`.
- `Complete`: после блока телеметрии манифеста `input_changes` добавить:

```csharp
if (stat.Complete && stat.Restored > 0)
{
	TelemetryLog.Write("input_restored", "", taskId, new[] {
		TelemetryField.Number("Files", stat.Restored),
		TelemetryField.Text("Paths", string.Join(";", stat.RestoredPaths)) });
}
```

- `ClassifyChange` и остальная логика `Complete` не меняются: `Restored` не входит ни в `events`, ни в `Changed`.

## Выдача из кэша и присоединение

### `CacheLookupWitness.cs`

- Поле `private readonly string _contentRoot;`, конструктор и `OpenAsync` получают параметр `string contentRoot`.
- В `OpenAsync`: наблюдатель открывается с `ValidationEvidence.ObserverExcludedRoots(witness._excluded, witness._contentRoot)`, стартовый манифест — `InputStatManifest.Capture(witness._roots, witness._excluded, ignore, witness._contentRoot)`.
- В `VerifyAsync`: закрывающий `Capture` тоже с `_contentRoot`.

### `CachedResultServer.cs`

- Рядом с `CollectRoots`/`CollectExcludedRoots` в начале скана: `string contentRoot = ValidationEvidence.ContentWitnessedRoot();`.
- `OpenWitness(string[] roots, string[] excluded, Func<string, bool> ignore, string contentRoot, string taskId)` передаёт его в `CacheLookupWitness.OpenAsync`. Оба вызова `OpenWitness` в скане передают `contentRoot`.
- Сразу после `InputStatVerdict stat = await witness.VerifyAsync();` писать ту же строку `input_restored`, что и в `ValidationEvidence.Complete` (`task.Id`), если `stat.Complete && stat.Restored > 0`.

### `TestRunCoalescer.cs`

- Открытие наблюдателя: вторым аргументом `ValidationInputMonitor.OpenAsync` передать `ValidationEvidence.ObserverExcludedRoots(ValidationEvidence.CollectExcludedRoots(), ValidationEvidence.ContentWitnessedRoot())`.

### Не меняется

- `CompileTaskExecutor._cycleMonitor` следит за `CompileInputContext.Roots`, к свидетельству прогона не относится.
- `CachedResultServer.CurrentInputDigest` и дайджест `ValidationInputSnapshot` по-прежнему покрывают `ProjectSettings/` целиком.

## Тесты

- `StatManifestScenarios` (`AgentBridgeCoordination.Tests`, группа `state`): новый сценарий `ASettingsRewriteWithSameBytesIsRestored`, добавить в `Run` перед `covered.Add`. Дерево по образцу `NewTree`, плюс каталог `ProjectSettings` с файлом `QualitySettings.asset`. Корни — оба каталога, `contentRoot` — каталог `ProjectSettings`.
  - Стартовый манифест сохранить через `Save` и прочитать через `Load`: хеш переживает круговой путь.
  - Переписать `QualitySettings.asset` теми же байтами после `Thread.Sleep(50)`: `Changed == 0`, `Restored == 1`, `RestoredPaths[0]` — нормализованный путь файла.
  - Переписать его другими байтами той же длины: `Changed == 1`, `NonSourceChanged == 1`, `Restored == 0`.
- Существующие тесты, которые затрагивает изменение: `StatManifestScenarios` (`AnEditThatWasRevertedIsSeen` должен остаться зелёным: файл вне корня содержимого по-прежнему считается изменением), `AgentBridgeEvidenceTests.StatManifestSeesAChangeThatWasReverted` (трёхаргументная перегрузка), `CacheLookupScenarios` и `ObserverHubScenarios` (конструктор наблюдателя не меняется).

## Порядок работ

- `InputStatEntry.cs`, `InputStatVerdict.cs`, `ValidationInputSnapshot.ToHex` → `internal`.
- `InputStatManifest.cs`: перегрузка `Capture` с хешем, формат `Save`/`Load`, ветка `Restored` в `Compare`.
- `ValidationEvidence.cs`: `ContentWitnessedRoot`, `ObserverExcludedRoots`, проводка в `InstallMonitor`, `StartManifest`, `TryComplete`, телеметрия `input_restored`.
- `CacheLookupWitness.cs`, `CachedResultServer.cs`, `TestRunCoalescer.cs`: проводка `contentRoot` и исключение корня из наблюдателя.
- Сценарий `ASettingsRewriteWithSameBytesIsRestored` в `StatManifestScenarios`.
- Версия пакета `0.42.0` → `0.42.1`, документация по разделу ниже.

## Приёмка

- Перезапись файла в `ProjectSettings/` теми же байтами не даёт изменения в вердикте манифеста и пишется в `Restored` — сценарий `ASettingsRewriteWithSameBytesIsRestored`.
- Изменение содержимого файла настроек при той же длине по-прежнему считается изменением не-исходника — тот же сценарий.
- Перезапись исходника вне `ProjectSettings/` теми же байтами по-прежнему считается изменением — существующий `AnEditThatWasRevertedIsSeen`.
- `dotnet run --project AgentBridgeCoordination.Tests -c Release -- --group all` заканчивается `Coordination: PASS`.
- В живом хосте: PlayMode-прогон, во время которого соседняя задача вызвала перезагрузку домена (Shader Graph пересохраняет `ShaderGraphSettings.asset`), завершается без `stale_input`, а в `Logs/AgentBridge-*.jsonl` есть строка `input_restored` с этим путём.

## Документация

- `README.md`: в описании `stale` (около строки 519) и stat-манифеста (около строки 524) указать, что файлы под `ProjectSettings/` сравниваются по содержимому: перезапись теми же байтами не делает результат `stale`, такие файлы попадают в телеметрию `input_restored`, а наблюдатель этот корень не считает.
- `Docs/PROJECT_MAP.md`: описание манифеста (около строки 104) — для `ProjectSettings/` хранится SHA-256, совпавший хеш даёт `input_restored` вместо `input_changes`.
- `CLAUDE.md`, раздел «Достоверность»: «путь/размер/mtime входов» дополнить «и SHA-256 для `ProjectSettings/`».
