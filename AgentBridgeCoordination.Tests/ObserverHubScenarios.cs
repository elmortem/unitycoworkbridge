using AgentBridge;
using static AgentBridge.Coordination.Tests.Harness;

namespace AgentBridge.Coordination.Tests;

// C22 — the observer hub. Monitors are windows over one shared watcher per root: the expensive
// install happens once, the exclusions stay private to each window, and a root outlives the monitor
// that opened it just long enough for the next one to arrive for free.
internal static class ObserverHubScenarios
{
	public static void Run(string root, List<string> covered)
	{
		InputWatchHub.Shutdown();
		try
		{
			Share(root);
			OpenedAsynchronously(root);
			LingerAndSweep(root);
			ShutdownKeepsVerdicts(root);
			ClosedWindowStopsCounting(root);
			covered.Add("C22_observer_hub");
		}
		finally
		{
			InputWatchHub.Shutdown();
		}
	}

	// Two windows over one root: one watcher, both see the change, and an exclusion belongs to the
	// window that declared it and not to the shared observer.
	private static void Share(string project)
	{
		string assets = NewRoot(project, "share");
		string skipped = Path.Combine(assets, "skipped");
		Directory.CreateDirectory(skipped);
		string script = Path.Combine(assets, "Thing.cs");
		File.WriteAllText(script, "original");

		using (var wide = new ValidationInputMonitor(new[] { assets }, Array.Empty<string>()))
		using (var narrow = new ValidationInputMonitor(new[] { assets }, new[] { skipped }))
		{
			Expect(InputWatchHub.WatcherCount == 1, "two monitors on one root share a single watcher");
			Expect(wide.Observed && narrow.Observed, "both windows observe");

			File.WriteAllText(script, "changed");
			Expect(WaitUntil(() => wide.EventCount > 0 && narrow.EventCount > 0, 5000),
				"a change inside the shared root reaches every window");

			int wideBefore = wide.EventCount;
			int narrowBefore = narrow.EventCount;
			File.WriteAllText(Path.Combine(skipped, "artifact.bin"), "editor output");
			Expect(WaitUntil(() => wide.EventCount > wideBefore, 5000),
				"a path only the second window excludes is still a change for the first");
			Thread.Sleep(200);
			Expect(narrow.EventCount == narrowBefore, "and it is not a change for the window that excluded it");
		}
	}

	// The asynchronous form opens the same window without arming on the caller's thread.
	private static void OpenedAsynchronously(string project)
	{
		string assets = NewRoot(project, "async");
		string script = Path.Combine(assets, "Thing.cs");
		File.WriteAllText(script, "original");

		using (var monitor = ValidationInputMonitor
			.OpenAsync(new[] { assets }, Array.Empty<string>(), null).GetAwaiter().GetResult())
		{
			Expect(monitor.Observed, "an asynchronously opened window observes");
			File.WriteAllText(script, "changed");
			Expect(WaitUntil(() => monitor.EventCount > 0, 5000), "and it sees a change");
		}

		using (var blind = ValidationInputMonitor
			.OpenAsync(new[] { Path.Combine(project, "never-created") }, Array.Empty<string>(), null)
			.GetAwaiter().GetResult())
		{
			Expect(!blind.Observed, "a root that cannot be observed is not claimed as observed");
		}
	}

	// The watcher lingers after the last window closes, so the next lookup a second later is free;
	// the sweeper is what finally puts it down.
	private static void LingerAndSweep(string project)
	{
		// WatcherCount is hub-wide, so the roots the earlier windows left behind are swept first.
		InputWatchHub.Sweep(InputWatchHub.NowMs + InputWatchHub.LingerMs);
		Expect(InputWatchHub.WatcherCount == 0, "every root of the closed windows is idle by now");

		string assets = NewRoot(project, "linger");
		string script = Path.Combine(assets, "Thing.cs");
		File.WriteAllText(script, "original");

		using (var first = new ValidationInputMonitor(new[] { assets }, Array.Empty<string>()))
		{
			Expect(first.Observed, "the first window observes");
		}

		Expect(InputWatchHub.WatcherCount == 1, "the watcher outlives the monitor that opened it");
		InputWatchHub.Sweep(InputWatchHub.NowMs + InputWatchHub.LingerMs);
		Expect(InputWatchHub.WatcherCount == 0, "an idle watcher is swept once its linger is over");

		using (var again = new ValidationInputMonitor(new[] { assets }, Array.Empty<string>()))
		{
			Expect(again.Observed, "a swept root is observed again by the next monitor");
			File.WriteAllText(script, "changed");
			Expect(WaitUntil(() => again.EventCount > 0, 5000), "and the reinstalled watcher delivers");
		}

		InputWatchHub.Sweep(InputWatchHub.NowMs + InputWatchHub.LingerMs);
	}

	// Shutting the hub down at a domain reload closes watchers, not verdicts: what an open window
	// already knew stays readable, which is what the compile cycle reads on that same event.
	private static void ShutdownKeepsVerdicts(string project)
	{
		string assets = NewRoot(project, "shutdown");
		var monitor = new ValidationInputMonitor(new[] { assets }, Array.Empty<string>());
		try
		{
			Expect(monitor.Observed, "the window observes before the shutdown");
			InputWatchHub.Shutdown();
			Expect(InputWatchHub.WatcherCount == 0, "shutdown closes every watcher");
			Expect(monitor.Observed, "an open window keeps the verdict it had when the hub went down");
			Expect(string.IsNullOrEmpty(monitor.Failure), "and it does not invent a failure");
		}
		finally
		{
			monitor.Dispose();
		}
	}

	// A closed window is done counting even while the shared watcher keeps running for others.
	private static void ClosedWindowStopsCounting(string project)
	{
		string assets = NewRoot(project, "closed");
		string script = Path.Combine(assets, "Thing.cs");
		File.WriteAllText(script, "original");

		using (var holder = new ValidationInputMonitor(new[] { assets }, Array.Empty<string>()))
		{
			var closed = new ValidationInputMonitor(new[] { assets }, Array.Empty<string>());
			File.WriteAllText(script, "changed once");
			Expect(WaitUntil(() => closed.EventCount > 0, 5000), "the window counts while it is open");
			int counted = closed.EventCount;
			closed.Dispose();

			int holderBefore = holder.EventCount;
			File.WriteAllText(script, "changed twice");
			Expect(WaitUntil(() => holder.EventCount > holderBefore, 5000), "the shared watcher still serves the open window");
			Thread.Sleep(200);
			Expect(closed.EventCount == counted, "a closed window counts nothing more");
			Expect(closed.Observed, "and still reports the verdict it closed with");
		}
	}

	private static string NewRoot(string project, string name)
	{
		string path = Path.Combine(project, "hub", name);
		Directory.CreateDirectory(path);
		return path;
	}
}
