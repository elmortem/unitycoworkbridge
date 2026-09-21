Status: Выполнено

# Общий наблюдатель входов без работы на главном потоке

Вид: производительность. `CachedResultServer.ServeAsync` раз в секунду ставит `ValidationInputMonitor` на главном потоке, пока в очереди ждёт кешируемая задача `tests`/`compile`; установка `FileSystemWatcher` под Mono Unity обходит весь корень синхронно. Текущее значение в DeadliftUnity (7231 файл входов): 410–502 мс на установку для `Assets`, `coordinator_slow_stage Stage=update` 70 раз за 70 секунд прогона, 401–1010 мс. Раскладка: вся цена — `EnableRaisingEvents = true`; снос наблюдателя 0 мс, `BuildIgnore` 14 мс, хеши уже на рабочих потоках. После изменения наблюдатели живут в общем хабе, по одному на корень, и ставятся на рабочем потоке; ежесекундные пути (`CachedResultServer`, `TestRunCoalescer`) не ставят наблюдатель на главном потоке никогда, а во время прогона бесплатно делят наблюдатель самого прогона. Ожидаемое число: вклад установки наблюдателя в `Stage=update` — 0 мс; главный поток на `OpenAsync` — не больше 5 мс при любом размере дерева.

Порядок доказательства не меняется нигде: окно наблюдения открывается в тех же местах, что и сейчас, меняется только то, кто и на каком потоке ставит наблюдатель.

## Хаб наблюдателей в `Packages/com.elmortem.agentbridge/Editor`

Все три типа не зависят от Unity: они линкуются в `AgentBridgeCoordination.Tests` и `AgentBridgeCompile.Tests`.

### `InputWatchRoot.cs` — новый файл

```csharp
using System;
using System.IO;
using System.Threading.Tasks;

namespace AgentBridge
{
	public sealed class InputWatchRoot
	{
		private readonly object _sync = new object();
		private readonly TaskCompletionSource<bool> _armed =
			new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
		private ValidationInputMonitor[] _subscribers = new ValidationInputMonitor[0];
		private FileSystemWatcher _watcher;
		private bool _closed;

		public readonly string Key;
		public readonly string RootPath;
		public int References;
		public long IdleSinceMs;
		public volatile bool Broken;
		public volatile string Failure = "";

		public InputWatchRoot(string key, string rootPath)
		{
			Key = key;
			RootPath = rootPath;
		}

		public Task Armed
		{
			get { return _armed.Task; }
		}

		public void Arm()
		{
			try
			{
				var watcher = new FileSystemWatcher(RootPath);
				watcher.IncludeSubdirectories = true;
				watcher.NotifyFilter = NotifyFilters.LastWrite
					| NotifyFilters.FileName
					| NotifyFilters.DirectoryName
					| NotifyFilters.Size
					| NotifyFilters.CreationTime;
				watcher.InternalBufferSize = 64 * 1024;
				watcher.Changed += OnChanged;
				watcher.Created += OnChanged;
				watcher.Deleted += OnChanged;
				watcher.Renamed += OnRenamed;
				watcher.Error += OnError;
				watcher.EnableRaisingEvents = true;

				bool closed;
				lock (_sync)
				{
					closed = _closed;
					if (!closed)
					{
						_watcher = watcher;
					}
				}

				if (closed)
				{
					watcher.Dispose();
				}
			}
			catch (Exception exception)
			{
				Failure = "could not observe " + RootPath + ": " + exception.Message;
			}
			finally
			{
				_armed.TrySetResult(true);
			}
		}

		public void Close()
		{
			FileSystemWatcher watcher;
			lock (_sync)
			{
				_closed = true;
				watcher = _watcher;
				_watcher = null;
			}

			if (watcher == null)
			{
				return;
			}

			try
			{
				watcher.EnableRaisingEvents = false;
				watcher.Dispose();
			}
			catch (Exception)
			{
			}
		}

		private void OnChanged(object sender, FileSystemEventArgs args)
		{
			if (args.ChangeType == WatcherChangeTypes.Changed && Directory.Exists(args.FullPath))
			{
				return;
			}

			Dispatch(args.FullPath);
		}

		private void OnRenamed(object sender, RenamedEventArgs args)
		{
			Dispatch(args.FullPath);
			Dispatch(args.OldFullPath);
		}

		private void OnError(object sender, ErrorEventArgs args)
		{
			Broken = true;
			foreach (ValidationInputMonitor monitor in _subscribers)
			{
				monitor.MarkOverflowed();
			}
		}

		private void Dispatch(string fullPath)
		{
			foreach (ValidationInputMonitor monitor in _subscribers)
			{
				monitor.Record(fullPath);
			}
		}
	}
}
```

- `public void Subscribe(ValidationInputMonitor monitor)` и `public void Unsubscribe(ValidationInputMonitor monitor)` — под `_sync` заменяют `_subscribers` новым массивом (copy-on-write), чтобы `Dispatch` и `OnError` читали его без блокировки.

### `InputWatchHub.cs` — новый файл

```csharp
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

namespace AgentBridge
{
	public static class InputWatchHub
	{
		public const long LingerMs = 30000;
		private const int SweepPeriodMs = 5000;

		private static readonly object Sync = new object();
		private static readonly Dictionary<string, InputWatchRoot> Roots =
			new Dictionary<string, InputWatchRoot>(StringComparer.Ordinal);
		private static readonly Stopwatch Clock = Stopwatch.StartNew();
		private static Timer _sweeper;

		public static int WatcherCount
		{
			get { lock (Sync) { return Roots.Count; } }
		}

		public static long NowMs
		{
			get { return Clock.ElapsedMilliseconds; }
		}

		public static InputWatchRoot Acquire(string root, bool inline)
		{
			string path = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
			InputWatchRoot entry;
			InputWatchRoot stale = null;
			bool created = false;

			lock (Sync)
			{
				if (Roots.TryGetValue(path, out entry) && (entry.Broken || !string.IsNullOrEmpty(entry.Failure)))
				{
					Roots.Remove(path);
					if (entry.References == 0)
					{
						stale = entry;
					}

					entry = null;
				}

				if (entry == null)
				{
					entry = new InputWatchRoot(path, path);
					Roots[path] = entry;
					created = true;
				}

				entry.References++;
				if (_sweeper == null)
				{
					_sweeper = new Timer(OnSweep, null, SweepPeriodMs, SweepPeriodMs);
				}
			}

			if (stale != null)
			{
				stale.Close();
			}

			if (created)
			{
				if (inline)
				{
					entry.Arm();
				}
				else
				{
					Task.Run(new Action(entry.Arm));
				}
			}

			return entry;
		}

		public static void Release(InputWatchRoot entry)
		{
			bool close = false;
			lock (Sync)
			{
				entry.References--;
				if (entry.References > 0)
				{
					return;
				}

				InputWatchRoot current;
				bool attached = Roots.TryGetValue(entry.Key, out current) && ReferenceEquals(current, entry);
				if (attached)
				{
					entry.IdleSinceMs = NowMs;
				}
				else
				{
					close = true;
				}
			}

			if (close)
			{
				entry.Close();
			}
		}

		public static void Sweep(long nowMs)
		{
			var expired = new List<InputWatchRoot>();
			lock (Sync)
			{
				foreach (InputWatchRoot entry in Roots.Values)
				{
					if (entry.References == 0 && nowMs - entry.IdleSinceMs >= LingerMs)
					{
						expired.Add(entry);
					}
				}

				foreach (InputWatchRoot entry in expired)
				{
					Roots.Remove(entry.Key);
				}

				if (Roots.Count == 0 && _sweeper != null)
				{
					_sweeper.Dispose();
					_sweeper = null;
				}
			}

			foreach (InputWatchRoot entry in expired)
			{
				entry.Close();
			}
		}

		public static void Shutdown()
		{
			var all = new List<InputWatchRoot>();
			lock (Sync)
			{
				all.AddRange(Roots.Values);
				Roots.Clear();
				if (_sweeper != null)
				{
					_sweeper.Dispose();
					_sweeper = null;
				}
			}

			foreach (InputWatchRoot entry in all)
			{
				entry.Close();
			}
		}

		private static void OnSweep(object state)
		{
			Sweep(NowMs);
		}
	}
}
```

### `InputWatchHubLifetime.cs` — новый файл

- `[InitializeOnLoad] public static class InputWatchHubLifetime`; статический конструктор подписывает `InputWatchHub.Shutdown` на `AssemblyReloadEvents.beforeAssemblyReload` и `EditorApplication.quitting` (сначала `-=`, затем `+=`). В тестовые проекты не линкуется.
- `Shutdown` только гасит наблюдатели: вердикты уже открытых мониторов остаются читаемыми, поэтому порядок с `CompileTaskExecutor.CaptureCycleChanges` на том же событии не важен.

### `ValidationInputMonitor.cs`

Монитор перестаёт владеть `FileSystemWatcher` и становится окном над хабом. Публичный контракт (`EventCount`, `Observed`, `Failure`, `Paths`, оба конструктора, `Dispose`) сохраняется; единственное отличие — `Observed` после `Dispose` остаётся таким, каким был в момент закрытия.

- Удалить `_watchers`, `OnChanged`, `OnRenamed`, `OnError`. Поля: `List<InputWatchRoot> _roots`, `int _observedRoots`, `bool _sealed`, прежние `_paths`, `_excludedRoots`, `_ignore`, `_sync`, `_events`, `_overflowed`, `_failure`, `_disposed`.
- Закрытый конструктор `ValidationInputMonitor(Func<string, bool> ignore, string[] excludedRoots)` — только присваивает поля.
- `private void Attach(string[] roots, bool inline)`: для каждого непустого существующего корня — `InputWatchRoot entry = InputWatchHub.Acquire(root, inline); entry.Subscribe(this); _roots.Add(entry);`. Подписка идёт до готовности наблюдателя: события между подпиской и `Seal` считаются.
- `private void Seal()`: под `_sync` для каждого корня: непустой `Failure` → `_failure = entry.Failure`; `Broken` → `_overflowed = true`; затем `_observedRoots = _roots.Count; _sealed = true`.
- Публичный конструктор с тремя аргументами: закрытый конструктор → `Attach(roots, true)` → для каждого корня `entry.Armed.Wait()` → `Seal()`. Холодный хаб ставит наблюдатель на вызывающем потоке, как сейчас; тёплый — мгновенно.
- Новый метод:

```csharp
public static async Task<ValidationInputMonitor> OpenAsync(string[] roots, string[] excludedRoots, Func<string, bool> ignore)
{
	var monitor = new ValidationInputMonitor(ignore, excludedRoots);
	monitor.Attach(roots, false);
	var armed = new List<Task>();
	foreach (InputWatchRoot entry in monitor._roots)
	{
		armed.Add(entry.Armed);
	}

	await Task.WhenAll(armed);
	monitor.Seal();
	return monitor;
}
```

- `Observed`: `_sealed && !_overflowed && string.IsNullOrEmpty(_failure) && _observedRoots > 0`.
- `internal void Record(string fullPath)`: прежнее тело, плюс первым действием под `_sync` выход при `_disposed`. `internal void MarkOverflowed()`: под `_sync` `_overflowed = true`.
- `Dispose`: под `_sync` `_disposed = true`; затем для каждого корня `entry.Unsubscribe(this); InputWatchHub.Release(entry);` и `_roots.Clear()`.

## Ежесекундные пути

### `CachedResultServer.cs`

- `ServeAsync`: строку `using var monitor = new ValidationInputMonitor(roots, excluded, ignore);` заменить на `using var monitor = await ValidationInputMonitor.OpenAsync(roots, excluded, ignore);`. Место и порядок не меняются: окно открыто до первого `CompileInputContext.StartCapture`. `CanPublish` не меняется.

### `TestRunCoalescer.cs`

- В блоке `if (monitor == null)` заменить `new ValidationInputMonitor(...)` на `await ValidationInputMonitor.OpenAsync(...)` с теми же аргументами. Во время прогона корни уже держит `ValidationEvidence._monitor`, окно открывается без установки наблюдателя. Комментарий над `monitor` привести в соответствие.

## Тесты

- `AgentBridgeCoordination.Tests/ObserverHubScenarios.cs` — новый, `internal static class ObserverHubScenarios`, `Run(string root, List<string> covered)`, добавляет `"C22_observer_hub"`; вызвать из `Program.cs` после `HashingScenarios.Run`. В начале и в конце — `InputWatchHub.Shutdown()`. Случаи:
	- два монитора на один корень: `WatcherCount == 1`; изменение файла видят оба; путь, исключённый только у второго, считает только первый;
	- `OpenAsync(...).GetAwaiter().GetResult()` даёт `Observed == true` и видит изменение;
	- после `Dispose` последнего монитора `WatcherCount == 1`; `Sweep(InputWatchHub.NowMs + InputWatchHub.LingerMs)` → `0`; новый монитор на том же корне снова `Observed`;
	- `Shutdown()` при открытом мониторе: `Observed` остаётся `true`, `Dispose` не бросает;
	- закрытый монитор не считает события после `Dispose`;
- В оба csproj (`AgentBridgeCoordination.Tests`, `AgentBridgeCompile.Tests`) добавить `Compile Include` для `InputWatchRoot.cs` и `InputWatchHub.cs`.
- Затрагиваемые существующие: `Scenarios.C13_ObserverVerdict` (должен пройти без правок), `AgentBridgeCompile.Tests` целиком.

## Порядок работ

- Снять базу в хост-проекте `AgentBridgeUnity` задачей `csharp`: сгенерировать вне корней входов `Temp/AgentBridge/ObserverFixture` (8000 мелких файлов в 80 папках), замерить на главном потоке `new ValidationInputMonitor(new[] { fixture }, new string[0])` и задержку от `File.WriteAllText` до `EventCount > 0`. Числа — в заметку.
- `InputWatchRoot.cs`, `InputWatchHub.cs`, переписать `ValidationInputMonitor.cs`, линки в оба csproj; оба консольных набора зелёные.
- `ObserverHubScenarios.cs` с наблюдательными случаями, вызов из `Program.cs`.
- `CachedResultServer.cs`, `TestRunCoalescer.cs`, `InputWatchHubLifetime.cs`.
- Повторить замер на той же фикстуре: время главного потока до возврата `Task` из `OpenAsync` (холодный хаб), время синхронного конструктора на тёплом хабе.
- Версия пакета `0.31.1` → `0.32.0`, сборка плагина по правилам `CLAUDE.md`, документация.

## Приёмка

- Главный поток на холодном `OpenAsync` по фикстуре 8000 файлов — не больше 5 мс; база синхронного конструктора записана рядом. Подтверждается замером из порядка работ.
- Синхронный конструктор на тёплом хабе — не больше 5 мс. Тот же замер.
- Два монитора на один корень держат один наблюдатель; фильтры у каждого свои — `C22_observer_hub`.
- `dotnet run --project AgentBridgeCoordination.Tests -c Release -- --group all` → `Coordination: PASS`, включая `C13`; `AgentBridgeCompile.Tests` зелёный.
- В хост-проекте: `tests` EditMode, затем та же нефрешевая задача повторно — вторая отдаётся из кеша (`Cached = true`, `Evidence.Validity = valid`); во время PlayMode-прогона вторая подходящая задача получает `attached`.

## Документация

- `Docs/PROJECT_MAP.md`: строки `ValidationInputMonitor.cs`, `CachedResultServer.cs`, `TestRunCoalescer.cs`; новые строки для `InputWatchHub.cs`, `InputWatchRoot.cs`, `InputWatchHubLifetime.cs`; пункт «Достоверность» жизненного цикла — про общий наблюдатель и окно.
- `Docs/notes/260921-1758-NOTE-observer-install-cost.md` — новая: причина (синхронный обход корня при установке наблюдателя под Mono), замеры DeadliftUnity из этого ТДД, замеры фикстуры до и после, задержка доставки события.
- `CLAUDE.md`: если в выжимке перечислены файлы достоверности — добавить хаб.
