using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;

namespace AgentBridge
{
	// The second witness of a validation run, beside the observer.
	//
	// Under Unity the observer is Mono's polling DefaultWatcher, and it does not survive a domain
	// reload: between InputWatchHub.Shutdown and the next install nobody watches the inputs, and the
	// events of the last seconds before the reload are never delivered either. The manifest closes
	// that gap without a running thread: path, length and write time of every input, taken on a
	// worker while the run is prepared, written to a file that outlives the reload, and compared
	// against the same walk once the run is over. It is cheaper than the digest and catches exactly
	// what the digest cannot — a file that was edited and put back.
	//
	// Pure System.IO like the digest: no Unity API, so the same source compiles into the tests.
	public sealed class InputStatManifest
	{
		public const int MaxRecordedPaths = 16;

		public bool Complete;
		public string Reason = "";
		public List<InputStatEntry> Entries = new List<InputStatEntry>();

		public static InputStatManifest Incomplete(string reason)
		{
			return new InputStatManifest { Complete = false, Reason = reason ?? "" };
		}

		// Walks the inputs exactly as the digest does — same roots, same exclusions, same ignore —
		// so the two witnesses can never disagree about what counts as an input.
		public static InputStatManifest Capture(string[] roots, string[] excludedRoots, Func<string, bool> ignore)
		{
			return Capture(roots, excludedRoots, ignore, "");
		}

		// Files under contentRoot are witnessed by their bytes, not by their write time: Unity rewrites
		// ProjectSettings with unchanged content on its own, and such a rewrite is not an edit.
		public static InputStatManifest Capture(string[] roots, string[] excludedRoots, Func<string, bool> ignore, string contentRoot)
		{
			string content = ValidationInputSnapshot.Normalize(contentRoot ?? "").TrimEnd('/');
			var manifest = new InputStatManifest();
			try
			{
				foreach (string root in roots ?? new string[0])
				{
					if (string.IsNullOrEmpty(root))
					{
						continue;
					}

					List<string> files;
					string error;
					if (!ValidationInputSnapshot.TryCollect(root, excludedRoots ?? new string[0], ignore, out files, out error))
					{
						return Incomplete(error);
					}

					foreach (string file in files)
					{
						var info = new FileInfo(file);
						if (!info.Exists)
						{
							continue;
						}

						string path = ValidationInputSnapshot.Normalize(file);
						manifest.Entries.Add(new InputStatEntry
						{
							Path = path,
							Length = info.Length,
							WriteTicks = info.LastWriteTimeUtc.Ticks,
							Hash = IsUnder(path, content) ? HashOf(file) : ""
						});
					}
				}
			}
			catch (Exception exception)
			{
				return Incomplete("input stat manifest failed: " + exception.Message);
			}

			manifest.Entries.Sort(delegate(InputStatEntry left, InputStatEntry right)
			{
				return string.CompareOrdinal(left.Path, right.Path);
			});
			manifest.Complete = true;
			return manifest;
		}

		// The observer cannot tell a same-bytes rewrite from an edit, so it skips the content root
		// and leaves that root to the manifest, which can.
		public static string[] ObserverExcludedRoots(string[] excluded, string contentRoot)
		{
			var result = new List<string>(excluded ?? new string[0]);
			result.Add(contentRoot);
			return result.ToArray();
		}

		private static bool IsUnder(string path, string root)
		{
			if (root.Length == 0)
			{
				return false;
			}

			return path.StartsWith(root + "/", StringComparison.OrdinalIgnoreCase);
		}

		private static string HashOf(string file)
		{
			using (var sha = ContentHash.Create())
			using (var input = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.Read))
			{
				return ValidationInputSnapshot.ToHex(sha.ComputeHash(input));
			}
		}

		public void Save(string path)
		{
			var lines = new List<string>(Entries.Count);
			foreach (InputStatEntry entry in Entries)
			{
				lines.Add(entry.WriteTicks.ToString(CultureInfo.InvariantCulture)
					+ "|" + entry.Length.ToString(CultureInfo.InvariantCulture)
					+ "|" + entry.Hash
					+ "|" + entry.Path);
			}

			string temporary = path + ".tmp";
			File.WriteAllLines(temporary, lines);
			if (File.Exists(path))
			{
				File.Delete(path);
			}

			File.Move(temporary, path);
		}

		public static InputStatManifest Load(string path)
		{
			if (string.IsNullOrEmpty(path) || !File.Exists(path))
			{
				return Incomplete("the input stat manifest was lost");
			}

			try
			{
				var manifest = new InputStatManifest();
				foreach (string line in File.ReadAllLines(path))
				{
					string[] parts = line.Split(new[] { '|' }, 4);
					if (parts.Length != 4)
					{
						return Incomplete("the input stat manifest is unreadable");
					}

					manifest.Entries.Add(new InputStatEntry
					{
						WriteTicks = long.Parse(parts[0], CultureInfo.InvariantCulture),
						Length = long.Parse(parts[1], CultureInfo.InvariantCulture),
						Hash = parts[2],
						Path = parts[3]
					});
				}

				manifest.Complete = true;
				return manifest;
			}
			catch (Exception exception)
			{
				return Incomplete("the input stat manifest is unreadable: " + exception.Message);
			}
		}

		// Both sides are sorted by path, so one merge pass names every appearance, disappearance and
		// edit. An incomplete side is reported as it is: a missing witness, not an empty one.
		public static InputStatVerdict Compare(InputStatManifest start, InputStatManifest end)
		{
			var verdict = new InputStatVerdict();
			if (!start.Complete)
			{
				verdict.Reason = start.Reason;
				return verdict;
			}

			if (!end.Complete)
			{
				verdict.Reason = end.Reason;
				return verdict;
			}

			int left = 0;
			int right = 0;
			while (left < start.Entries.Count || right < end.Entries.Count)
			{
				int order;
				if (left >= start.Entries.Count)
				{
					order = 1;
				}
				else if (right >= end.Entries.Count)
				{
					order = -1;
				}
				else
				{
					order = string.CompareOrdinal(start.Entries[left].Path, end.Entries[right].Path);
				}

				if (order < 0)
				{
					Note(verdict, start.Entries[left].Path);
					left++;
				}
				else if (order > 0)
				{
					Note(verdict, end.Entries[right].Path);
					right++;
				}
				else
				{
					InputStatEntry before = start.Entries[left];
					InputStatEntry after = end.Entries[right];
					if (before.Length != after.Length || before.WriteTicks != after.WriteTicks)
					{
						if (before.Hash.Length > 0 && before.Length == after.Length && before.Hash == after.Hash)
						{
							verdict.Restored++;
							if (verdict.RestoredPaths.Count < MaxRecordedPaths)
							{
								verdict.RestoredPaths.Add(after.Path);
							}
						}
						else
						{
							Note(verdict, after.Path);
						}
					}

					left++;
					right++;
				}
			}

			verdict.Complete = true;
			return verdict;
		}

		private static void Note(InputStatVerdict verdict, string path)
		{
			verdict.Changed++;
			if (!CompileFingerprint.IsSource(path))
			{
				verdict.NonSourceChanged++;
			}
			if (verdict.Paths.Count < MaxRecordedPaths)
			{
				verdict.Paths.Add(path);
			}
		}
	}
}
