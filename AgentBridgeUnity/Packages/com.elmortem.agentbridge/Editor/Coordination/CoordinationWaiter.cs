using System;
using System.IO;
using System.Threading;

namespace AgentBridge.Coordination
{
	// Waits for the Revision to move. The watcher is only a hint: it is installed before the state
	// is re-read, so a change that lands between the read and the subscription is still caught by
	// the next one-second control read instead of being lost forever.
	public sealed class CoordinationWaiter : IDisposable
	{
		private const int ControlIntervalMs = 1000;

		private readonly CoordinationFileStore _store;
		private readonly ManualResetEventSlim _changed = new ManualResetEventSlim(false);
		private FileSystemWatcher _watcher;
		private bool _disposed;

		public CoordinationWaiter(CoordinationFileStore store)
		{
			_store = store;
			TryInstallWatcher();
		}

		// Returns the first snapshot whose Revision is greater than afterRevision, or null when the
		// timeout expired. Expiry cancels nothing: the request keeps living under its own id.
		public CoordinationState Wait(long afterRevision, TimeSpan timeout, CancellationToken cancellation)
		{
			DateTime deadline = DateTime.UtcNow + timeout;

			while (true)
			{
				_changed.Reset();

				CoordinationState state = TryRead();
				if (state != null && state.Revision > afterRevision)
				{
					return state;
				}

				TimeSpan remaining = deadline - DateTime.UtcNow;
				if (remaining <= TimeSpan.Zero)
				{
					return null;
				}

				int slice = (int)Math.Min(ControlIntervalMs, Math.Max(1, remaining.TotalMilliseconds));
				try
				{
					_changed.Wait(slice, cancellation);
				}
				catch (OperationCanceledException)
				{
					return null;
				}

				if (cancellation.IsCancellationRequested)
				{
					return null;
				}
			}
		}

		private CoordinationState TryRead()
		{
			try
			{
				return _store.Read();
			}
			catch (CoordinationStoreException)
			{
				return null;
			}
			catch (IOException)
			{
				return null;
			}
		}

		private void TryInstallWatcher()
		{
			try
			{
				Directory.CreateDirectory(_store.Root);
				_watcher = new FileSystemWatcher(_store.Root, CoordinationFileStore.StateFileName);
				_watcher.NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.FileName | NotifyFilters.Size;
				_watcher.Changed += OnChanged;
				_watcher.Created += OnChanged;
				_watcher.Renamed += OnRenamed;
				_watcher.Error += OnError;
				_watcher.EnableRaisingEvents = true;
			}
			catch (Exception)
			{
				// Without a watcher the control interval alone still answers, one second later.
				_watcher = null;
			}
		}

		private void OnChanged(object sender, FileSystemEventArgs args)
		{
			_changed.Set();
		}

		private void OnRenamed(object sender, RenamedEventArgs args)
		{
			_changed.Set();
		}

		private void OnError(object sender, ErrorEventArgs args)
		{
			// An overflowed watcher stops being a hint; the poll keeps the wait correct.
			_changed.Set();
		}

		public void Dispose()
		{
			if (_disposed)
			{
				return;
			}

			_disposed = true;
			if (_watcher != null)
			{
				_watcher.EnableRaisingEvents = false;
				_watcher.Dispose();
				_watcher = null;
			}

			_changed.Dispose();
		}
	}
}
