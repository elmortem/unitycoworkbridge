using System;
using System.Collections.Generic;
using System.IO;
using System.Threading.Tasks;

namespace AgentBridge
{
	// Watches every input root for the whole length of a validation run. It reports that inputs
	// moved, not who moved them: the package never attributes a filesystem change to an author it
	// cannot prove.
	//
	// The watchers themselves belong to InputWatchHub: a monitor is a window over the shared
	// observers, with its own exclusions, its own ignore rule and its own event count.
	public sealed class ValidationInputMonitor : IDisposable
	{
		private const int MaxRecordedPaths = 16;

		private readonly List<InputWatchRoot> _roots = new List<InputWatchRoot>();
		private readonly List<string> _paths = new List<string>();
		private readonly string[] _excludedRoots;
		private readonly Func<string, bool> _ignore;
		private readonly object _sync = new object();
		private int _observedRoots;
		private bool _sealed;
		private int _events;
		private bool _overflowed;
		private string _failure = "";
		private bool _disposed;

		public ValidationInputMonitor(string[] roots, string[] excludedRoots)
			: this(roots, excludedRoots, null)
		{
		}

		// ignore is the same predicate the snapshot uses: the bridge's own declared scratch, such as
		// the test framework's temporary PlayMode scene, must not be reported as a foreign change.
		public ValidationInputMonitor(string[] roots, string[] excludedRoots, Func<string, bool> ignore)
			: this(ignore, excludedRoots)
		{
			// A cold hub still arms on the calling thread, exactly as this constructor always did.
			Attach(roots, true);
			foreach (InputWatchRoot entry in _roots)
			{
				entry.Armed.Wait();
			}

			Seal();
		}

		private ValidationInputMonitor(Func<string, bool> ignore, string[] excludedRoots)
		{
			_excludedRoots = excludedRoots ?? new string[0];
			_ignore = ignore;
		}

		// Opens the same window without spending the editor's main thread on it: the roots are armed
		// on worker threads, and a root another monitor already holds costs nothing at all.
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

		// Subscribing happens before the watcher is ready on purpose: a change that lands between the
		// subscription and the verdict is counted, never silently dropped.
		private void Attach(string[] roots, bool inline)
		{
			foreach (string root in roots ?? new string[0])
			{
				if (string.IsNullOrEmpty(root) || !Directory.Exists(root))
				{
					continue;
				}

				InputWatchRoot entry = InputWatchHub.Acquire(root, inline);
				entry.Subscribe(this);
				_roots.Add(entry);
			}
		}

		private void Seal()
		{
			lock (_sync)
			{
				foreach (InputWatchRoot entry in _roots)
				{
					if (!string.IsNullOrEmpty(entry.Failure))
					{
						// Without a watcher on an input root nothing can be claimed about it.
						_failure = entry.Failure;
					}

					if (entry.Broken)
					{
						_overflowed = true;
					}
				}

				_observedRoots = _roots.Count;
				_sealed = true;
			}
		}

		public int EventCount
		{
			get { lock (_sync) { return _events; } }
		}

		// An overflowed or failed observer means unknown, never valid.
		public bool Observed
		{
			get { lock (_sync) { return _sealed && !_overflowed && string.IsNullOrEmpty(_failure) && _observedRoots > 0; } }
		}

		public string Failure
		{
			get { lock (_sync) { return _overflowed ? "the file observer overflowed" : _failure; } }
		}

		public string[] Paths
		{
			get { lock (_sync) { return _paths.ToArray(); } }
		}

		internal void Record(string fullPath)
		{
			lock (_sync)
			{
				if (_disposed)
				{
					return;
				}
			}

			if (ValidationInputSnapshot.IsExcluded(fullPath, _excludedRoots))
			{
				return;
			}

			if (_ignore != null && _ignore(fullPath))
			{
				return;
			}

			lock (_sync)
			{
				if (_disposed)
				{
					return;
				}

				_events++;
				if (_paths.Count < MaxRecordedPaths && !_paths.Contains(fullPath))
				{
					_paths.Add(fullPath);
				}
			}
		}

		internal void MarkOverflowed()
		{
			lock (_sync)
			{
				_overflowed = true;
			}
		}

		// Closing the window keeps its verdict readable: the caller still reads EventCount and
		// Observed after the run to decide what the result is worth.
		public void Dispose()
		{
			lock (_sync)
			{
				if (_disposed)
				{
					return;
				}

				_disposed = true;
			}

			foreach (InputWatchRoot entry in _roots)
			{
				entry.Unsubscribe(this);
				InputWatchHub.Release(entry);
			}

			_roots.Clear();
		}
	}
}
