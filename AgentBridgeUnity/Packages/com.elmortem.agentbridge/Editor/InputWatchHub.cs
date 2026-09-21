using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

namespace AgentBridge
{
	// One observer per input root for the whole editor, not one per monitor. A root stays armed for a
	// short while after the last monitor lets it go, so the per-second cache lookup and the coalescer
	// share the observer a real run already paid for instead of installing their own.
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

		// inline arms the watcher on the calling thread, which is what the synchronous constructor
		// has always done. Everything that runs on the editor tick asks for the off-thread form.
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
					// A broken observer is not shared any further: the next monitor gets a fresh one
					// and decides for itself, exactly as it would have with its own watcher.
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
