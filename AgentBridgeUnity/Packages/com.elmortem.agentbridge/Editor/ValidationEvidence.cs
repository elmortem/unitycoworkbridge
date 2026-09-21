using System;
using System.Collections.Generic;
using System.IO;
using System.Threading.Tasks;
using UnityEditor;
using UnityEngine;

namespace AgentBridge
{
	// Turns a validation run into an honest claim about a known project state.
	//
	// Before the run: settle the import, hash every input off the main thread, install the
	// observers, hash again to close the window between the snapshot and the observation. If the
	// project is still moving, the expensive run is refused instead of producing a result nobody
	// can interpret. After the run: hash once more and compare, with the observers as the second
	// witness for a change that was made and reverted.
	//
	// The observer alone cannot cover a PlayMode run: it dies with the domain and, being Mono's
	// polling watcher, never delivers the last seconds before the reload either. So the run carries
	// a witness that needs no thread — a stat manifest of the same inputs, taken at prepare time,
	// kept in a file across the reload and compared once the run is over.
	[InitializeOnLoad]
	public static class ValidationEvidence
	{
		public const string TaskKey = "AgentBridge_EvidenceTask";
		public const string DigestKey = "AgentBridge_EvidenceDigest";
		public const string ContextKey = "AgentBridge_EvidenceContext";
		public const string RootsKey = "AgentBridge_EvidenceRoots";
		public const string ExcludedKey = "AgentBridge_EvidenceExcluded";
		public const string WindowKey = "AgentBridge_EvidenceWindow";
		public const string ReloadsKey = "AgentBridge_EvidenceReloads";
		public const string EventsKey = "AgentBridge_EvidenceEvents";
		public const string ObserverKey = "AgentBridge_EvidenceObserver";
		public const string ScratchKey = "AgentBridge_EvidenceScratch";
		public const string ManifestKey = "AgentBridge_EvidenceManifest";

		private const char Separator = (char)31;
		private const int MaxRetries = 1;

		private static ValidationInputMonitor _monitor;
		private static Task<ValidationInputSnapshot> _work;
		private static Task<string> _sourceWork;
		private static InputHashJob _job;
		private static Task<ValidationInputSnapshot> _completion;
		private static Task<string> _completionSources;
		private static Task<InputStatManifest> _manifestWork;
		private static Task<InputStatVerdict> _completionManifest;
		private static string _manifestPath = "";
		private static string _completingId;
		public const string SourceKey = "AgentBridge_EvidenceSources";
		public static string PreparedSources { get { return SessionState.GetString(SourceKey, ""); } }
		public static string CompletedSources { get; private set; }
		private static string _preparingTaskId = "";
		private static int _retries;
		private static ValidationInputSnapshot _first;
		private static string[] _roots = new string[0];
		private static string[] _excluded = new string[0];
		private static string _context = "";
		private static string _windowId = "";
		private static Func<string, bool> _ignore;

		static ValidationEvidence()
		{
			if (Application.isBatchMode)
			{
				return;
			}

			// Nothing used to hand the observer's count over before the domain went down, so the
			// events the watcher had already delivered died with the watcher. DisposeMonitor is
			// what carries them, and this is the last moment it can be called.
			AssemblyReloadEvents.beforeAssemblyReload -= CarryAcrossReload;
			AssemblyReloadEvents.beforeAssemblyReload += CarryAcrossReload;

			// A PlayMode run reloads the domain in the middle of its own observation. Reinstalling
			// the observer keeps the rest of the run watched; the gap is disclosed in the reason,
			// never silently treated as "nothing happened".
			string running = SessionState.GetString(TaskKey, "");
			if (string.IsNullOrEmpty(running))
			{
				return;
			}

			_roots = Split(SessionState.GetString(RootsKey, ""));
			_excluded = Split(SessionState.GetString(ExcludedKey, ""));
			_context = SessionState.GetString(ContextKey, "");
			_windowId = SessionState.GetString(WindowKey, "");
			_ignore = BuildIgnore(SessionState.GetString(ScratchKey, ""));
			SessionState.SetInt(ReloadsKey, SessionState.GetInt(ReloadsKey, 0) + 1);
			InstallMonitor();
		}

		// The monitor's verdict is already latched, so it does not matter whether InputWatchHub
		// shuts the watchers down before or after this runs on the same event.
		private static void CarryAcrossReload()
		{
			DisposeMonitor();
		}

		public static bool IsPreparing
		{
			get { return _work != null; }
		}

		public static string PreparingTaskId
		{
			get { return _preparingTaskId; }
		}

		public static bool IsObserving(string taskId)
		{
			return SessionState.GetString(TaskKey, "") == taskId;
		}

		// Main thread. Collects everything that needs a Unity API, then hands the hashing to a
		// worker: no Unity call happens off the main thread from here on.
		public static void BeginPrepare(string taskId, string context, string windowId)
		{
			Cleanup();

			_preparingTaskId = taskId;
			_retries = 0;
			_first = null;
			_context = context ?? "";
			_windowId = windowId ?? "";
			_roots = CollectRoots();
			_excluded = CollectExcludedRoots();

			// Built here, on the main thread, because deciding what counts as the bridge's own
			// scratch needs the bootstrap scene path, and reading that touches Unity.
			string bootstrap = BootstrapScenePath();
			SessionState.SetString(ScratchKey, bootstrap);
			_ignore = BuildIgnore(bootstrap);

			_job = new InputHashJob(_roots, _excluded, _context, _ignore);
			string projectRoot = BridgePaths.ProjectRoot;
			_sourceWork = CompileInputContext.StartCapture(projectRoot);
			_work = _job.Start();
		}

		// The temporary scenes the Unity Test Framework creates for a PlayMode run, and which the
		// bridge deletes again afterwards. They are the bridge's own declared infrastructure: a
		// create-then-delete inside Assets that leaves the digest identical. Counting it as a
		// foreign change would make every PlayMode validation randomly stale.
		public static Func<string, bool> BuildIgnore(string bootstrapScenePath)
		{
			string projectRoot = ValidationInputSnapshot.Normalize(BridgePaths.ProjectRoot).TrimEnd('/') + "/";
			string bootstrap = bootstrapScenePath ?? "";
			// Resolve imported types and population modes on the main thread. The immutable path
			// set can then be shared by the hash worker and FileSystemWatcher callbacks. Dynamic
			// font files contain TMP's generated glyph tables and embedded atlas textures; their
			// .meta and source font files remain inputs. Do not infer font types from filenames.
			var dynamicFonts = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
			foreach (string guid in AssetDatabase.FindAssets("t:TMP_FontAsset"))
			{
				string path = AssetDatabase.GUIDToAssetPath(guid);
				var font = AssetDatabase.LoadAssetAtPath<TMPro.TMP_FontAsset>(path);
				if (font == null || font.atlasPopulationMode == TMPro.AtlasPopulationMode.Static) continue;
				string absolute = Path.Combine(BridgePaths.ProjectRoot, path);
				if (path.StartsWith("Packages/", StringComparison.Ordinal))
				{
					var package = UnityEditor.PackageManager.PackageInfo.FindForAssetPath(path);
					if (package == null || string.IsNullOrEmpty(package.resolvedPath)) continue;
					absolute = Path.Combine(package.resolvedPath, path.Substring(("Packages/" + package.name + "/").Length));
				}
				dynamicFonts.Add(ValidationInputSnapshot.Normalize(Path.GetFullPath(absolute)));
			}

			return delegate(string fullPath)
			{
				string normalized = ValidationInputSnapshot.Normalize(fullPath);
				if (dynamicFonts.Contains(normalized)) return true;
				if (!normalized.StartsWith(projectRoot, StringComparison.OrdinalIgnoreCase))
				{
					return false;
				}

				string relative = normalized.Substring(projectRoot.Length);
				if (relative.EndsWith(".meta", StringComparison.OrdinalIgnoreCase))
				{
					relative = relative.Substring(0, relative.Length - ".meta".Length);
				}

				return SceneSafetyGuard.IsTestScenePath(relative, bootstrap);
			};
		}

		private static string BootstrapScenePath()
		{
			return PlayModeSceneRecovery.BootstrapScenePath();
		}

		// The attempt number is in the name so an abandoned attempt that is still writing cannot
		// land in the file the current one is about to trust.
		private static string ManifestPath(string taskId, int attempt)
		{
			return Path.Combine(BridgePaths.WorkingRoot, "evidence-manifest-" + taskId + "-" + attempt + ".txt");
		}

		// Everything the worker needs is copied here, on the main thread: an abandoned job cannot
		// start walking the next task's roots, exactly like InputHashJob.
		private static Task<InputStatManifest> StartManifest(string path)
		{
			string[] roots = (string[])_roots.Clone();
			string[] excluded = (string[])_excluded.Clone();
			Func<string, bool> ignore = _ignore;
			return Task.Run(() =>
			{
				InputStatManifest manifest = InputStatManifest.Capture(roots, excluded, ignore);
				if (!manifest.Complete)
				{
					return manifest;
				}

				try
				{
					manifest.Save(path);
				}
				catch (Exception exception)
				{
					return InputStatManifest.Incomplete("input stat manifest could not be saved: " + exception.Message);
				}

				return manifest;
			});
		}

		// Main thread poll. Returns false while the worker is still hashing.
		public static bool TryFinishPrepare(out bool ready, out string reason)
		{
			ready = false;
			reason = "";

			if (_work == null)
			{
				ready = false;
				reason = "no evidence preparation is running";
				return true;
			}

			// Fast native hashing can finish before the refresh that queued this preparation.
			// Keep the completed work until Unity settles instead of turning speed into failure.
			if (EditorApplication.isCompiling || EditorApplication.isUpdating) return false;
			if (!_work.IsCompleted || (_sourceWork != null && !_sourceWork.IsCompleted)
				|| (_manifestWork != null && !_manifestWork.IsCompleted))
			{
				return false;
			}

			ValidationInputSnapshot snapshot;
			if (_work.IsFaulted)
			{
				snapshot = ValidationInputSnapshot.Incomplete(
					_work.Exception != null ? _work.Exception.GetBaseException().Message : "snapshot failed");
			}
			else
			{
				snapshot = _work.Result;
			}

			_work = null;

			if (!snapshot.Complete)
			{
				Cleanup();
				reason = snapshot.Reason;
				ready = false;
				return true;
			}

			if (_first == null)
			{
				// First pass: observe, then hash again so nothing can slip through the gap between
				// looking and watching.
				_first = snapshot;
				InstallMonitor();
				_manifestPath = ManifestPath(_preparingTaskId, _retries);
				_manifestWork = StartManifest(_manifestPath);
				_work = _job.Start();
				return false;
			}

			if (_first.Digest != snapshot.Digest)
			{
				if (_retries < MaxRetries)
				{
					_retries++;
					_first = null;
					DisposeMonitor();
					_work = _job.Start();
					return false;
				}

				Cleanup();
				reason = "the project kept changing while the inputs were being captured";
				ready = false;
				return true;
			}

			// Unity's import state must still be settled on the main thread; a snapshot taken
			// while an import is in flight describes a project that does not exist yet.
			if (EditorApplication.isCompiling || EditorApplication.isUpdating)
			{
				Cleanup();
				reason = "the editor was still importing when the inputs were captured";
				ready = false;
				return true;
			}

			// Without the manifest on disk the run would have no witness across the domain reload,
			// and a result nobody can interpret is refused here rather than believed later.
			InputStatManifest manifest = _manifestWork != null && _manifestWork.Status == TaskStatus.RanToCompletion
				? _manifestWork.Result
				: InputStatManifest.Incomplete(_manifestWork != null && _manifestWork.Exception != null
					? "input stat manifest failed: " + _manifestWork.Exception.GetBaseException().Message
					: "the input stat manifest was not taken");
			if (!manifest.Complete)
			{
				Cleanup();
				reason = manifest.Reason;
				ready = false;
				return true;
			}

			SessionState.SetString(ManifestKey, _manifestPath);
			_manifestWork = null;

			SessionState.SetString(SourceKey, _sourceWork != null && _sourceWork.Status == TaskStatus.RanToCompletion ? _sourceWork.Result : "");
			SessionState.SetString(TaskKey, _preparingTaskId);
			SessionState.SetString(DigestKey, snapshot.Digest);
			SessionState.SetString(ContextKey, _context);
			SessionState.SetString(WindowKey, _windowId);
			SessionState.SetString(RootsKey, Join(_roots));
			SessionState.SetString(ExcludedKey, Join(_excluded));
			SessionState.SetInt(ReloadsKey, 0);
			SessionState.SetInt(EventsKey, 0);
			SessionState.SetInt(ObserverKey, _monitor != null && _monitor.Observed ? 1 : 0);

			_preparingTaskId = "";
			ready = true;
			return true;
		}

		// Main thread, after the run finished. Hashes once more and turns the two snapshots plus
		// the observer verdict into the record that travels with the result.
		public static bool TryComplete(string taskId, bool artifactsPresent, out EvidenceRecord result)
		{
			result = null;
			if (_completion == null || _completingId != taskId)
			{
				_completingId = taskId;
				string[] roots = Split(SessionState.GetString(RootsKey, ""));
				string[] excluded = Split(SessionState.GetString(ExcludedKey, ""));
				Func<string, bool> ignore = BuildIgnore(SessionState.GetString(ScratchKey, ""));
				var job = new InputHashJob(roots, excluded, SessionState.GetString(ContextKey, ""), ignore);
				string projectRoot = BridgePaths.ProjectRoot;
				// Read on the main thread, walked on a worker: the closing manifest must describe
				// exactly the inputs the closing digest describes.
				string startPath = SessionState.GetString(ManifestKey, "");
				_completionSources = CompileInputContext.StartCapture(projectRoot);
				_completion = job.Measure("validation_complete", taskId);
				_completionManifest = Task.Run(() => InputStatManifest.Compare(
					InputStatManifest.Load(startPath), InputStatManifest.Capture(roots, excluded, ignore)));
				return false;
			}
			if (!_completion.IsCompleted || !_completionSources.IsCompleted || !_completionManifest.IsCompleted) return false;
			var end = _completion.Status == TaskStatus.RanToCompletion ? _completion.Result
				: ValidationInputSnapshot.Incomplete("final input snapshot failed");
			var stat = _completionManifest.Status == TaskStatus.RanToCompletion
				? _completionManifest.Result
				: new InputStatVerdict
				{
					Reason = _completionManifest.Exception != null
						? "input stat manifest failed: " + _completionManifest.Exception.GetBaseException().Message
						: "the input stat manifest could not be compared"
				};
			CompletedSources = _completionSources.Status == TaskStatus.RanToCompletion ? _completionSources.Result : "";
			_completion = null;
			_completionSources = null;
			_completionManifest = null;
			_completingId = null;
			result = Complete(taskId, artifactsPresent, end, stat);
			return true;
		}

		private static EvidenceRecord Complete(
			string taskId,
			bool artifactsPresent,
			ValidationInputSnapshot end,
			InputStatVerdict stat)
		{
			string observed = SessionState.GetString(TaskKey, "");
			if (observed != taskId)
			{
				return EvidenceRecord.UnknownBecause("this result was produced without an input snapshot");
			}

			string startDigest = SessionState.GetString(DigestKey, "");
			string windowId = SessionState.GetString(WindowKey, "");
			int reloads = SessionState.GetInt(ReloadsKey, 0);
			int events = SessionState.GetInt(EventsKey, 0);
			bool observerOk = SessionState.GetInt(ObserverKey, 0) == 1;
			string observerFailure = "";

			if (_monitor != null)
			{
				if (_monitor.EventCount > 0)
				{
					TelemetryLog.Write("input_changes", "", taskId, new[] {
						TelemetryField.Number("Events", _monitor.EventCount),
						TelemetryField.Text("Paths", string.Join(";", _monitor.Paths)),
						TelemetryField.Text("Source", "observer") });
				}
				events += _monitor.EventCount;
				observerOk = observerOk && _monitor.Observed;
				observerFailure = _monitor.Failure;
			}

			// The witness that was awake across the reload. Its events weigh the same as the
			// observer's: an input that moved and came back is not something a result may ignore.
			if (stat.Complete && stat.Changed > 0)
			{
				TelemetryLog.Write("input_changes", "", taskId, new[] {
					TelemetryField.Number("Events", stat.Changed),
					TelemetryField.Text("Paths", string.Join(";", stat.Paths)),
					TelemetryField.Text("Source", "manifest") });
				events += stat.Changed;
			}

			// The observer remains alive until the worker finishes and we evaluate its events.
			Cleanup();

			var record = new EvidenceRecord
			{
				InputDigest = startDigest,
				EndInputDigest = end.Complete ? end.Digest : "",
				WindowId = windowId,
				ArtifactsPresent = artifactsPresent
			};

			if (string.IsNullOrEmpty(startDigest) || !end.Complete)
			{
				record.Validity = EvidenceRecord.Unknown;
				record.Reason = end.Complete ? "the starting input digest was lost" : end.Reason;
				return record;
			}

			if (startDigest != end.Digest)
			{
				record.Validity = EvidenceRecord.Stale;
				record.Reason = "the inputs changed while the validation ran";
				return record;
			}

			if (events > 0)
			{
				// Same digest, but something touched an input and put it back. The result describes
				// a project that did not hold still, so it is not evidence.
				record.Validity = EvidenceRecord.Stale;
				record.Reason = "an input was modified and restored while the validation ran";
				return record;
			}

			if (!observerOk)
			{
				record.Validity = EvidenceRecord.Unknown;
				record.Reason = string.IsNullOrEmpty(observerFailure)
					? "the inputs could not be observed for the whole run"
					: observerFailure;
				return record;
			}

			// Without the manifest there is nothing that covered the reload gap, and "the observer
			// saw nothing" is not the same claim as "nothing happened".
			if (!stat.Complete)
			{
				record.Validity = EvidenceRecord.Unknown;
				record.Reason = string.IsNullOrEmpty(stat.Reason)
					? "the input stat manifest was lost"
					: stat.Reason;
				return record;
			}

			record.Validity = EvidenceRecord.Valid;
			record.Reason = reloads > 0
				? "observer reinstalled after " + reloads
					+ " domain reload(s); the stat manifest covers the gap; both input digests match"
				: "";
			return record;
		}

		public static void Abort()
		{
			Cleanup();
		}

		public static string ContextOf(string mode, string filter)
		{
			UnityEditor.PackageManager.PackageInfo package =
				UnityEditor.PackageManager.PackageInfo.FindForAssembly(typeof(ValidationEvidence).Assembly);
			return "unity=" + Application.unityVersion
				+ ";package=" + (package != null ? package.version : "unknown")
				+ ";platform=" + EditorUserBuildSettings.activeBuildTarget
				+ ";group=" + EditorUserBuildSettings.selectedBuildTargetGroup
				// Selection is checked against the complete discovery catalog by TestFilterCoverage.
				// It is not an input change: aliases and subsets must share the same content digest.
				+ ";mode=" + (mode ?? "");
		}

		public static string[] CollectRoots()
		{
			string projectRoot = BridgePaths.ProjectRoot;
			var roots = new List<string>
			{
				Path.Combine(projectRoot, "Assets"),
				Path.Combine(projectRoot, "Packages"),
				Path.Combine(projectRoot, "ProjectSettings")
			};

			// A package resolved from outside the project is an input like any other, and an
			// unreachable one is what makes the whole snapshot unknown rather than optimistic.
			try
			{
				foreach (UnityEditor.PackageManager.PackageInfo package
					in UnityEditor.PackageManager.PackageInfo.GetAllRegisteredPackages())
				{
					if (package.source != UnityEditor.PackageManager.PackageSource.Local
						&& package.source != UnityEditor.PackageManager.PackageSource.Embedded)
					{
						continue;
					}

					string resolved = package.resolvedPath;
					if (string.IsNullOrEmpty(resolved))
					{
						continue;
					}

					string normalized = Path.GetFullPath(resolved);
					if (IsUnder(normalized, Path.Combine(projectRoot, "Packages")))
					{
						continue;
					}

					if (!roots.Contains(normalized))
					{
						roots.Add(normalized);
					}
				}
			}
			catch (Exception)
			{
				// Listing packages is best effort here; an unreachable root is caught by Capture.
			}

			return roots.ToArray();
		}

		public static string[] CollectExcludedRoots()
		{
			string projectRoot = BridgePaths.ProjectRoot;
			var excluded = new List<string>
			{
				Path.Combine(projectRoot, "Library"),
				Path.Combine(projectRoot, "Temp"),
				Path.Combine(projectRoot, "Logs"),
				Path.Combine(projectRoot, "obj"),
				Path.Combine(projectRoot, "Build")
			};

			foreach (string fixture in CoordinationGate.DeclaredFixtureRoots())
			{
				if (string.IsNullOrEmpty(fixture))
				{
					continue;
				}

				string absolute = Path.GetFullPath(Path.Combine(projectRoot, fixture));

				// A fixture root that already holds files is not a fixture root: excluding it
				// would hide product files from the digest.
				if (Directory.Exists(absolute) && Directory.GetFileSystemEntries(absolute).Length > 0)
				{
					continue;
				}

				excluded.Add(absolute);
			}

			return excluded.ToArray();
		}

		private static void InstallMonitor()
		{
			DisposeMonitor();
			_monitor = new ValidationInputMonitor(_roots, _excluded, _ignore);
		}

		private static void DisposeMonitor()
		{
			if (_monitor == null)
			{
				return;
			}

			// The count carried across a reload must not be lost with the observer that saw it.
			SessionState.SetInt(EventsKey, SessionState.GetInt(EventsKey, 0) + _monitor.EventCount);
			if (!_monitor.Observed)
			{
				SessionState.SetInt(ObserverKey, 0);
			}

			_monitor.Dispose();
			_monitor = null;
		}

		private static void Cleanup()
		{
			DisposeMonitor();
			_work = null;
			_sourceWork = null;
			_completion = null;
			_completionSources = null;
			_completionManifest = null;
			_manifestWork = null;
			_manifestPath = "";
			_completingId = null;
			_job = null;
			_first = null;
			_preparingTaskId = "";
			SessionState.EraseString(TaskKey);
			SessionState.EraseString(DigestKey);
			SessionState.EraseString(ContextKey);
			SessionState.EraseString(RootsKey);
			SessionState.EraseString(ExcludedKey);
			SessionState.EraseString(WindowKey);
			SessionState.EraseString(ScratchKey);
			SessionState.EraseString(ManifestKey);
			SessionState.EraseInt(ReloadsKey);
			SessionState.EraseInt(EventsKey);
			SessionState.EraseInt(ObserverKey);
			EraseManifests();
		}

		// One run carries evidence in this editor at a time, so every manifest still lying here
		// belongs to a run that is over. Leaving them would let an abandoned attempt pose as the
		// witness of the next one.
		private static void EraseManifests()
		{
			try
			{
				foreach (string file in Directory.GetFiles(BridgePaths.WorkingRoot, "evidence-manifest-*"))
				{
					try
					{
						File.Delete(file);
					}
					catch (Exception)
					{
					}
				}
			}
			catch (Exception)
			{
			}
		}

		private static bool IsUnder(string path, string root)
		{
			string normalizedRoot = ValidationInputSnapshot.Normalize(Path.GetFullPath(root)).TrimEnd('/');
			string normalizedPath = ValidationInputSnapshot.Normalize(path);
			return normalizedPath.StartsWith(normalizedRoot + "/", StringComparison.OrdinalIgnoreCase);
		}

		private static string Join(string[] values)
		{
			return string.Join(Separator.ToString(), values ?? new string[0]);
		}

		private static string[] Split(string value)
		{
			if (string.IsNullOrEmpty(value))
			{
				return new string[0];
			}

			return value.Split(Separator);
		}
	}
}
