using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using UnityEngine;

namespace AgentBridge
{
	public class TaskLogScope : IDisposable
	{
		private static TaskLogScope _active;

		private readonly ConcurrentQueue<string> _buffer = new ConcurrentQueue<string>();
		private readonly Application.LogCallback _handler;

		private TaskLogScope()
		{
			_handler = OnLogMessage;
		}

		public static TaskLogScope Begin()
		{
			if (_active != null)
			{
				_active.Dispose();
			}

			var scope = new TaskLogScope();
			_active = scope;
			Application.logMessageReceivedThreaded += scope._handler;
			return scope;
		}

		private void OnLogMessage(string condition, string stackTrace, LogType type)
		{
			_buffer.Enqueue(condition);
		}

		public List<string> Drain()
		{
			var list = new List<string>();
			string line;
			while (_buffer.TryDequeue(out line))
			{
				list.Add(line);
			}

			return list;
		}

		public void Dispose()
		{
			Application.logMessageReceivedThreaded -= _handler;
			if (_active == this)
			{
				_active = null;
			}
		}
	}
}
