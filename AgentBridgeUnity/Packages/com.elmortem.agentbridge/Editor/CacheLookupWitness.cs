using System;
using System.Threading.Tasks;

namespace AgentBridge
{
	// Both witnesses of one cache lookup, opened together and closed together.
	//
	// A served result claims the same thing a real run claims: that the project held still while the
	// decision was made. The observer covers the window, and the stat manifest covers what a polling
	// watcher cannot see at all — an input that was edited and put back between the two digests.
	//
	// Opening them is not free: arming the observers keeps the shared hub warm, and the manifest walks
	// every input. So the witness is opened once per scan and only when a candidate entry actually
	// exists; a lookup with nothing to verify never pays for it.
	public sealed class CacheLookupWitness : IDisposable
	{
		private readonly string[] _roots;
		private readonly string[] _excluded;
		private readonly Func<string, bool> _ignore;

		public ValidationInputMonitor Monitor;
		public InputStatManifest Start;

		private CacheLookupWitness(string[] roots, string[] excluded, Func<string, bool> ignore)
		{
			_roots = (string[])roots.Clone();
			_excluded = (string[])excluded.Clone();
			_ignore = ignore;
		}

		// The editor tick pays for neither: the observers arm on workers and the manifest is walked on
		// one more, so the two halves of the window open in parallel.
		public static async Task<CacheLookupWitness> OpenAsync(string[] roots, string[] excluded, Func<string, bool> ignore)
		{
			var witness = new CacheLookupWitness(roots, excluded, ignore);
			Task<ValidationInputMonitor> monitor = ValidationInputMonitor.OpenAsync(witness._roots, witness._excluded, ignore);
			Task<InputStatManifest> start = Task.Run(() => InputStatManifest.Capture(witness._roots, witness._excluded, ignore));
			witness.Monitor = await monitor;
			witness.Start = await start;
			return witness;
		}

		// Immutable inputs cross the thread boundary, exactly like InputHashJob: the worker walks the
		// roots the window was opened on and nothing else.
		public Task<InputStatVerdict> VerifyAsync()
		{
			InputStatManifest start = Start;
			string[] roots = _roots;
			string[] excluded = _excluded;
			Func<string, bool> ignore = _ignore;
			return Task.Run(() => InputStatManifest.Compare(start, InputStatManifest.Capture(roots, excluded, ignore)));
		}

		public void Dispose()
		{
			if (Monitor != null)
			{
				Monitor.Dispose();
			}
		}
	}
}
