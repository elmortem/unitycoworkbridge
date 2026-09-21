Status: Выполнено

# Stat-манифест входов как свидетель прогона без разрывов

Вид: новая функция в существующем коде. `ValidationEvidence` теряет события наблюдателя при domain reload: `DisposeMonitor` умеет переносить счётчик в `SessionState`, но перед reload его никто не вызывает, а между `InputWatchHub.Shutdown` и новой установкой входы не наблюдает никто. Под Unity наблюдатель — опрашивающий `System.IO.DefaultWatcher`, поэтому не доставляются и события последних секунд перед reload. Результат: PlayMode-прогон получает `Valid` с причиной `observer reinstalled after N domain reload(s)` даже если вход был изменён и возвращён в этом окне. После изменения у прогона появляется второй свидетель — stat-манифест (путь, размер, mtime) по тем же корням с теми же `excluded` и `ignore`: он снимается на рабочем потоке при подготовке, переживает reload в файле и сравнивается с манифестом на завершении. Любое расхождение — событие с путями в `input_changes`; потерянный манифест — `Unknown`. Счётчик наблюдателя теперь переносится через reload.

## Манифест в `Packages/com.elmortem.agentbridge/Editor`

Три типа не зависят от Unity и линкуются в `AgentBridgeCoordination.Tests`.

### `ValidationInputSnapshot.cs`

- `TryCollect` сделать `internal static` — манифест обязан обходить входы ровно так же, как digest.

### `InputStatEntry.cs` — новый файл

```csharp
namespace AgentBridge
{
	public sealed class InputStatEntry
	{
		public string Path = "";
		public long Length;
		public long WriteTicks;
	}
}
```

### `InputStatVerdict.cs` — новый файл

```csharp
using System.Collections.Generic;

namespace AgentBridge
{
	public sealed class InputStatVerdict
	{
		public bool Complete;
		public string Reason = "";
		public int Changed;
		public List<string> Paths = new List<string>();
	}
}
```

### `InputStatManifest.cs` — новый файл

```csharp
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;

namespace AgentBridge
{
	public sealed class InputStatManifest
	{
		public const int MaxRecordedPaths = 16;

		public bool Complete;
		public string Reason = "";
		public List<InputStatEntry> Entries = new List<InputStatEntry>();

		public static InputStatManifest Incomplete(string reason)
		{
			return new InputStatManifest { Complete = false, Reason = reason ?? "" };
		}

		public static InputStatManifest Capture(string[] roots, string[] excludedRoots, Func<string, bool> ignore)
		{
			var manifest = new InputStatManifest();
			try
			{
				foreach (string root in roots ?? new string[0])
				{
					if (string.IsNullOrEmpty(root))
					{
						continue;
					}

					List<string> files;
					string error;
					if (!ValidationInputSnapshot.TryCollect(root, excludedRoots ?? new string[0], ignore, out files, out error))
					{
						return Incomplete(error);
					}

					foreach (string file in files)
					{
						var info = new FileInfo(file);
						if (!info.Exists)
						{
							continue;
						}

						manifest.Entries.Add(new InputStatEntry
						{
							Path = ValidationInputSnapshot.Normalize(file),
							Length = info.Length,
							WriteTicks = info.LastWriteTimeUtc.Ticks
						});
					}
				}
			}
			catch (Exception exception)
			{
				return Incomplete("input stat manifest failed: " + exception.Message);
			}

			manifest.Entries.Sort(delegate(InputStatEntry left, InputStatEntry right)
			{
				return string.CompareOrdinal(left.Path, right.Path);
			});
			manifest.Complete = true;
			return manifest;
		}

		public void Save(string path)
		{
			var lines = new List<string>(Entries.Count);
			foreach (InputStatEntry entry in Entries)
			{
				lines.Add(entry.WriteTicks.ToString(CultureInfo.InvariantCulture)
					+ "|" + entry.Length.ToString(CultureInfo.InvariantCulture)
					+ "|" + entry.Path);
			}

			string temporary = path + ".tmp";
			File.WriteAllLines(temporary, lines);
			if (File.Exists(path))
			{
				File.Delete(path);
			}

			File.Move(temporary, path);
		}

		public static InputStatManifest Load(string path)
		{
			if (string.IsNullOrEmpty(path) || !File.Exists(path))
			{
				return Incomplete("the input stat manifest was lost");
			}

			try
			{
				var manifest = new InputStatManifest();
				foreach (string line in File.ReadAllLines(path))
				{
					string[] parts = line.Split(new[] { '|' }, 3);
					if (parts.Length != 3)
					{
						return Incomplete("the input stat manifest is unreadable");
					}

					manifest.Entries.Add(new InputStatEntry
					{
						WriteTicks = long.Parse(parts[0], CultureInfo.InvariantCulture),
						Length = long.Parse(parts[1], CultureInfo.InvariantCulture),
						Path = parts[2]
					});
				}

				manifest.Complete = true;
				return manifest;
			}
			catch (Exception exception)
			{
				return Incomplete("the input stat manifest is unreadable: " + exception.Message);
			}
		}

		public static InputStatVerdict Compare(InputStatManifest start, InputStatManifest end)
		{
			var verdict = new InputStatVerdict();
			if (!start.Complete)
			{
				verdict.Reason = start.Reason;
				return verdict;
			}

			if (!end.Complete)
			{
				verdict.Reason = end.Reason;
				return verdict;
			}

			int left = 0;
			int right = 0;
			while (left < start.Entries.Count || right < end.Entries.Count)
			{
				int order;
				if (left >= start.Entries.Count)
				{
					order = 1;
				}
				else if (right >= end.Entries.Count)
				{
					order = -1;
				}
				else
				{
					order = string.CompareOrdinal(start.Entries[left].Path, end.Entries[right].Path);
				}

				if (order < 0)
				{
					Note(verdict, start.Entries[left].Path);
					left++;
				}
				else if (order > 0)
				{
					Note(verdict, end.Entries[right].Path);
					right++;
				}
				else
				{
					InputStatEntry before = start.Entries[left];
					InputStatEntry after = end.Entries[right];
					if (before.Length != after.Length || before.WriteTicks != after.WriteTicks)
					{
						Note(verdict, after.Path);
					}

					left++;
					right++;
				}
			}

			verdict.Complete = true;
			return verdict;
		}

		private static void Note(InputStatVerdict verdict, string path)
		{
			verdict.Changed++;
			if (verdict.Paths.Count < MaxRecordedPaths)
			{
				verdict.Paths.Add(path);
			}
		}
	}
}
```

## `ValidationEvidence.cs`

- Константа `public const string ManifestKey = "AgentBridge_EvidenceManifest";`. Поля `private static Task<InputStatManifest> _manifestWork;`, `private static string _manifestPath = "";`, `private static Task<InputStatVerdict> _completionManifest;`.
- Статический конструктор: сразу после проверки `Application.isBatchMode`, до чтения `TaskKey`, подписать `AssemblyReloadEvents.beforeAssemblyReload -= CarryAcrossReload;` и `+= CarryAcrossReload;`.
- `private static void CarryAcrossReload()` — вызывает `DisposeMonitor()`. Тот уже складывает `EventCount` в `EventsKey` и сбрасывает `ObserverKey` при `!Observed`; вердикт монитора защёлкнут, порядок с `InputWatchHub.Shutdown` на том же событии не важен.
- `private static string ManifestPath(string taskId, int attempt)` → `Path.Combine(BridgePaths.WorkingRoot, "evidence-manifest-" + taskId + "-" + attempt + ".txt")`. Номер попытки в имени нужен, чтобы брошенная задача предыдущей попытки не писала в файл текущей.
- `private static Task<InputStatManifest> StartManifest(string path)`: скопировать `_roots`, `_excluded` (через `Clone`) и `_ignore` в локальные переменные; `Task.Run`: `Capture`; при `Complete` — `Save(path)` в `try`, исключение → `Incomplete("input stat manifest could not be saved: " + message)`; вернуть манифест.
- `TryFinishPrepare`:
	- в условие ожидания добавить `|| (_manifestWork != null && !_manifestWork.IsCompleted)`;
	- в ветке первого прохода (`_first = snapshot; InstallMonitor(); _work = _job.Start();`) добавить `_manifestPath = ManifestPath(_preparingTaskId, _retries); _manifestWork = StartManifest(_manifestPath);` — манифест снимается параллельно второму проходу хеша, уже под наблюдателем;
	- после проверки `isCompiling/isUpdating`, до записи ключей: результат `_manifestWork` (при `IsFaulted` — `Incomplete` с сообщением исключения); если `!Complete` → `Cleanup(); reason = manifest.Reason; ready = false; return true;`. Иначе `SessionState.SetString(ManifestKey, _manifestPath); _manifestWork = null;`.
- `TryComplete`: вместе с `_completion` запустить `_completionManifest`. На главном потоке прочитать `string startPath = SessionState.GetString(ManifestKey, "")` и те же `roots`, `excluded`, `ignore`, что переданы в `InputHashJob`; в `Task.Run`: `InputStatManifest.Compare(InputStatManifest.Load(startPath), InputStatManifest.Capture(roots, excluded, ignore))`. В условие ожидания добавить `!_completionManifest.IsCompleted`. Результат (при сбое задачи — `new InputStatVerdict { Reason = сообщение }`) передать в `Complete` четвёртым аргументом; `_completionManifest = null`.
- `Complete(string taskId, bool artifactsPresent, ValidationInputSnapshot end, InputStatVerdict stat)`:
	- в существующую телеметрию `input_changes` наблюдателя добавить поле `TelemetryField.Text("Source", "observer")`;
	- до `Cleanup()`: если `stat.Complete && stat.Changed > 0` — `TelemetryLog.Write("input_changes", "", taskId, ...)` с `Events = stat.Changed`, `Paths = string.Join(";", stat.Paths)`, `Source = "manifest"`, и `events += stat.Changed`;
	- после проверки `!observerOk` добавить: `if (!stat.Complete)` → `Validity = Unknown`, `Reason = string.IsNullOrEmpty(stat.Reason) ? "the input stat manifest was lost" : stat.Reason`;
	- причина `Valid` при `reloads > 0`: `"observer reinstalled after " + reloads + " domain reload(s); the stat manifest covers the gap; both input digests match"`.
- `Cleanup`: `_manifestWork = null; _completionManifest = null; _manifestPath = "";`, `SessionState.EraseString(ManifestKey)`, и удалить в `BridgePaths.WorkingRoot` все файлы `evidence-manifest-*` (каждое удаление в `try/catch`). Прогон с доказательством в редакторе один, чужих манифестов там нет.

## Тесты

- `AgentBridgeCoordination.Tests/StatManifestScenarios.cs` — новый, `internal static class StatManifestScenarios`, `Run(string root, List<string> covered)`, добавляет `"C23_stat_manifest"`; вызвать из `Program.cs` после `ObserverHubScenarios.Run`. Случаи:
	- два снимка неизменного дерева: `Compare` даёт `Complete`, `Changed == 0`;
	- файл переписан тем же содержимым через 50 мс: `Changed == 1`, путь назван; `ValidationInputSnapshot`-digest при этом равен — это и есть случай «изменили и вернули»;
	- добавленный и удалённый файл считаются по одному;
	- файл под `excludedRoots` и файл, отсечённый `ignore`, не считаются;
	- `Save` → `Load` → `Compare` с исходным: `Changed == 0`; `Load` несуществующего пути и файла с битой строкой: `Complete == false`, `Compare` возвращает `Complete == false` с причиной;
	- 40 изменённых файлов: `Changed == 40`, `Paths.Count == 16`;
	- недостижимый корень: `Capture` → `Complete == false`.
- В `AgentBridgeCoordination.Tests.csproj` добавить `Compile Include` для `InputStatEntry.cs`, `InputStatVerdict.cs`, `InputStatManifest.cs`.
- `AgentBridgeEvidenceTests` (EditMode): новый `StatManifestSeesAChangeThatWasReverted` — на временной папке внутри `Temp` под Mono: снимок, перезапись тем же содержимым, снимок, `Changed == 1`.
- Затрагиваемые существующие: `AgentBridgeEvidenceTests`, `AgentBridgeHashPerformanceTests`, `C12`, `C13`.

## Порядок работ

- Замер в хост-проекте на фикстуре `Temp/AgentBridge/ObserverFixture` (8000 файлов): время `InputStatManifest.Capture` на рабочем потоке и время главного потока за тот же интервал. Числа — в заметку.
- `InputStatEntry.cs`, `InputStatVerdict.cs`, `InputStatManifest.cs`, `TryCollect` → `internal`, линки, `StatManifestScenarios.cs`.
- `ValidationEvidence.cs`: перенос через reload (`CarryAcrossReload`), манифест в подготовке, сравнение в завершении, `Cleanup`.
- `AgentBridgeEvidenceTests.StatManifestSeesAChangeThatWasReverted`.
- Живая проверка разрыва: во время PlayMode-прогона из PowerShell сменить только mtime входного файла (`(Get-Item <файл>).LastWriteTimeUtc = [DateTime]::UtcNow`), результат прогона и телеметрия — в заметку.
- Версия пакета `0.32.0` → `0.33.0`, сборка плагина по правилам `CLAUDE.md`, документация.

## Приёмка

- `Coordination: PASS` со всеми группами, включая `C12`, `C13`, `C22_observer_hub`, `C23_stat_manifest`.
- EditMode `AgentBridgeEvidenceTests` + `AgentBridgeHashPerformanceTests` зелёные, `Evidence: valid`; повтор той же задачи — `Cached: true`.
- PlayMode-набор хост-проекта: `Evidence: valid`, причина содержит `the stat manifest covers the gap`.
- Смена одного mtime во время PlayMode-прогона: `Evidence: stale`, в телеметрии `input_changes` с `Source=manifest` и этим путём.
- После завершения и после `Abort` в `Library/AgentBridge` нет файлов `evidence-manifest-*`.
- Если чистый PlayMode-прогон становится `stale` из-за файлов, которые пишет сам Unity или Test Framework, `ignore` не расширяется: пути из `input_changes` заносятся в заметку и в замечания к ТДД, решение принимает пользователь.

## Документация

- `Docs/PROJECT_MAP.md`: пункт «Достоверность» жизненного цикла — два свидетеля (наблюдатель и stat-манифест), перенос счётчика через reload; строка `ValidationEvidence.cs`; новые строки для `InputStatManifest.cs`, `InputStatEntry.cs`, `InputStatVerdict.cs`.
- `Docs/notes/260921-1854-NOTE-evidence-reload-gap.md` — новая: дефект (счётчик не переносился, разрыв не наблюдался, задержка опроса), замер `Capture`, результат живой проверки с mtime.
