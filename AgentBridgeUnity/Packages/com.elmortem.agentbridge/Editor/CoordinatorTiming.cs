using System;
using System.Diagnostics;
using UnityEngine.Profiling;

namespace AgentBridge
{
	internal readonly struct CoordinatorTiming : IDisposable
	{
		private readonly string _stage, _task;
		private readonly long _start;
		public CoordinatorTiming(string stage, string task)
		{
			_stage = stage;
			_task = task;
			_start = Stopwatch.GetTimestamp();
			Profiler.BeginSample("AgentBridge." + stage);
		}
		public void Dispose()
		{
			Profiler.EndSample();
			long ms = (Stopwatch.GetTimestamp() - _start) * 1000 / Stopwatch.Frequency;
			if (ms < 50) return;
			TelemetryLog.Write("coordinator_slow_stage", "", _task, new[] {
				TelemetryField.Text("Stage", _stage), TelemetryField.Number("ElapsedMs", ms)
			});
		}
	}
}
