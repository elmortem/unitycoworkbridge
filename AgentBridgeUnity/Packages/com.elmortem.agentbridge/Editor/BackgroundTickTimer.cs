using System;
using System.Threading;

#nullable disable

namespace AgentBridge
{
	// No persistent worker, synchronization-context posts, IO or main-thread waits.
	// The only production callback is Unity's [ThreadSafe] EditorApplication.SignalTick.
	internal sealed class BackgroundTickTimer : IDisposable
	{
		private readonly object _gate = new object();
		private readonly Action _signal;
		private readonly Timer _timer;
		private bool _disposed;
		private long _signalCount;
		private string _error;

		public BackgroundTickTimer(Action signal, int intervalMs)
		{
			_signal = signal ?? throw new ArgumentNullException(nameof(signal));
			_timer = new Timer(OnTimer, null, Timeout.Infinite, Timeout.Infinite);
			_timer.Change(intervalMs, intervalMs);
		}

		public long SignalCount { get { lock (_gate) return _signalCount; } }
		public string Error { get { lock (_gate) return _error; } }

		private void OnTimer(object state)
		{
			lock (_gate)
			{
				if (_disposed || _error != null) return;
				try
				{
					_signal();
					_signalCount++;
				}
				catch (Exception exception)
				{
					// Never let an exception escape a ThreadPool callback. Report on the next
					// editor update, where logging and status IO are safe.
					_error = exception.GetType().Name + ": " + exception.Message;
					_timer.Change(Timeout.Infinite, Timeout.Infinite);
				}
			}
		}

		public void Dispose()
		{
			lock (_gate)
			{
				if (_disposed) return;
				_disposed = true;
				_timer.Dispose();
				// An in-flight signal has finished before this lock is acquired. Already
				// queued callbacks see _disposed and cannot enter Unity after shutdown.
			}
		}
	}
}
