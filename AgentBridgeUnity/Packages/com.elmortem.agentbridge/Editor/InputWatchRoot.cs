using System;
using System.IO;
using System.Threading.Tasks;

namespace AgentBridge
{
	// One live observer over one input root, shared by every monitor that needs that root. Arming is
	// the expensive half: the watcher Unity's Mono picks walks the whole tree before it reports
	// anything, so it is done once per root and never again while anyone is still watching.
	public sealed class InputWatchRoot
	{
		private readonly object _sync = new object();
		private readonly TaskCompletionSource<bool> _armed =
			new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
		private volatile ValidationInputMonitor[] _subscribers = new ValidationInputMonitor[0];
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
				// Without a watcher on an input root nothing can be claimed about it.
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

		// Copy-on-write: the delivery path reads the array without taking a lock, so a change that
		// arrives while a monitor is opening or closing never waits on the hub.
		public void Subscribe(ValidationInputMonitor monitor)
		{
			lock (_sync)
			{
				ValidationInputMonitor[] current = _subscribers;
				var next = new ValidationInputMonitor[current.Length + 1];
				Array.Copy(current, next, current.Length);
				next[current.Length] = monitor;
				_subscribers = next;
			}
		}

		public void Unsubscribe(ValidationInputMonitor monitor)
		{
			lock (_sync)
			{
				ValidationInputMonitor[] current = _subscribers;
				int index = Array.IndexOf(current, monitor);
				if (index < 0)
				{
					return;
				}

				var next = new ValidationInputMonitor[current.Length - 1];
				Array.Copy(current, 0, next, 0, index);
				Array.Copy(current, index + 1, next, index, current.Length - index - 1);
				_subscribers = next;
			}
		}

		private void OnChanged(object sender, FileSystemEventArgs args)
		{
			// Directory timestamps are not snapshot inputs. Unity touches package directories
			// during reload; the file events and directory create/delete/rename events remain.
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
