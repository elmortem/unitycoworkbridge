using System;
using System.Diagnostics;
using System.Threading.Tasks;

namespace AgentBridge
{
	// Immutable inputs cross the thread boundary. No Unity APIs or mutable preparation state
	// are read by the worker; an abandoned job cannot start hashing the next task's roots.
	public sealed class InputHashJob
	{
		private readonly string[] _roots, _excluded;
		private readonly string _context;
		private readonly Func<string, bool> _ignore;

		public InputHashJob(string[] roots, string[] excluded, string context, Func<string, bool> ignore = null)
		{
			_roots = (string[])roots.Clone();
			_excluded = (string[])excluded.Clone();
			_context = context;
			_ignore = ignore;
		}

		public Task<ValidationInputSnapshot> Start()
		{
			return Task.Run(() => ValidationInputSnapshot.Capture(_roots, _excluded, _context, _ignore));
		}

		public async Task<ValidationInputSnapshot> Measure(string stage, string taskId)
		{
			var watch = Stopwatch.StartNew();
			ValidationInputSnapshot result;
			try { result = await Start(); }
			catch (Exception error) { result = ValidationInputSnapshot.Incomplete(error.GetBaseException().Message); }
			TelemetryLog.Write("input_hash", "", taskId, new[] {
				TelemetryField.Text("Stage", stage),
				TelemetryField.Number("ElapsedMs", watch.ElapsedMilliseconds),
				TelemetryField.Number("Files", result.FileCount),
				TelemetryField.Number("Bytes", result.TotalBytes)
			});
			return result;
		}
	}
}
