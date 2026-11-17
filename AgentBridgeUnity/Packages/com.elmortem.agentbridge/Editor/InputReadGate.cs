using System;
using System.Threading;

namespace AgentBridge
{
	// Unity saves an asset by writing a temporary file and renaming it over the target. Windows
	// refuses that rename while any handle to the target is open, whatever its share mode, and the
	// editor stops in the modal "Moving file failed: Access is denied" until a person answers it.
	// The input digest and the stat manifest read project files on worker threads, so every such
	// read passes this gate, and a save keeps the gate shut until the save has returned.
	//
	// Pure System.Threading like the digest: no Unity API, so the same source compiles into the tests.
	public static class InputReadGate
	{
		// Bounds, not timings: a gate nobody reopens or a read that never ends may cost one failed
		// save, never a stalled run or a frozen editor.
		private const int MaxClosedMs = 60000;
		private const int MaxDrainMs = 10000;

		private static readonly object Sync = new object();
		private static int _readers;
		private static bool _closed;
		private static int _closer;
		private static DateTime _closedAt;

		// The thread that closed the gate is the one saving, so it never waits for itself.
		public static void Enter()
		{
			lock (Sync)
			{
				int self = Thread.CurrentThread.ManagedThreadId;
				while (_closed && self != _closer)
				{
					int left = MaxClosedMs - (int)(DateTime.UtcNow - _closedAt).TotalMilliseconds;
					if (left <= 0)
					{
						break;
					}

					Monitor.Wait(Sync, left);
				}

				_readers++;
			}
		}

		public static void Exit()
		{
			lock (Sync)
			{
				_readers--;
				if (_readers == 0)
				{
					Monitor.PulseAll(Sync);
				}
			}
		}

		// Waits for the reads already in flight. True when this call shut an open gate: the caller
		// then owns the reopening.
		public static bool Close()
		{
			lock (Sync)
			{
				bool opened = !_closed;
				_closed = true;
				_closer = Thread.CurrentThread.ManagedThreadId;
				_closedAt = DateTime.UtcNow;

				DateTime deadline = _closedAt.AddMilliseconds(MaxDrainMs);
				while (_readers > 0)
				{
					int left = (int)(deadline - DateTime.UtcNow).TotalMilliseconds;
					if (left <= 0)
					{
						break;
					}

					Monitor.Wait(Sync, left);
				}

				return opened;
			}
		}

		public static void Open()
		{
			lock (Sync)
			{
				_closed = false;
				Monitor.PulseAll(Sync);
			}
		}
	}
}
