using System;
using System.Collections.Generic;
using System.IO;

namespace AgentBridge
{
	// Watches every input root for the whole length of a validation run. It reports that inputs
	// moved, not who moved them: the package never attributes a filesystem change to an author it
	// cannot prove.
	public sealed class ValidationInputMonitor : IDisposable
	{
		private const int MaxRecordedPaths = 16;

		private readonly List<FileSystemWatcher> _watchers = new List<FileSystemWatcher>();
		private readonly List<string> _paths = new List<string>();
		private readonly string[] _excludedRoots;
		private readonly Func<string, bool> _ignore;
		private readonly object _sync = new object();
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
		{
			_excludedRoots = excludedRoots ?? new string[0];
			_ignore = ignore;

			foreach (string root in roots ?? new string[0])
			{
				if (string.IsNullOrEmpty(root) || !Directory.Exists(root))
				{
					continue;
				}

				try
				{
					var watcher = new FileSystemWatcher(root);
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
					_watchers.Add(watcher);
				}
				catch (Exception exception)
				{
					// Without a watcher on an input root nothing can be claimed about it.
					_failure = "could not observe " + root + ": " + exception.Message;
				}
			}
		}

		public int EventCount
		{
			get { lock (_sync) { return _events; } }
		}

		// An overflowed or failed observer means unknown, never valid.
		public bool Observed
		{
			get { lock (_sync) { return !_overflowed && string.IsNullOrEmpty(_failure) && _watchers.Count > 0; } }
		}

		public string Failure
		{
			get { lock (_sync) { return _overflowed ? "the file observer overflowed" : _failure; } }
		}

		public string[] Paths
		{
			get { lock (_sync) { return _paths.ToArray(); } }
		}

		private void OnChanged(object sender, FileSystemEventArgs args)
		{
			Record(args.FullPath);
		}

		private void OnRenamed(object sender, RenamedEventArgs args)
		{
			Record(args.FullPath);
			Record(args.OldFullPath);
		}

		private void OnError(object sender, ErrorEventArgs args)
		{
			lock (_sync)
			{
				_overflowed = true;
			}
		}

		private void Record(string fullPath)
		{
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
				_events++;
				if (_paths.Count < MaxRecordedPaths && !_paths.Contains(fullPath))
				{
					_paths.Add(fullPath);
				}
			}
		}

		public void Dispose()
		{
			if (_disposed)
			{
				return;
			}

			_disposed = true;
			foreach (FileSystemWatcher watcher in _watchers)
			{
				try
				{
					watcher.EnableRaisingEvents = false;
					watcher.Dispose();
				}
				catch (Exception)
				{
				}
			}

			_watchers.Clear();
		}
	}
}
