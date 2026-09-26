using System;
using System.Collections.Generic;
using System.IO;
using UnityEngine;

namespace AgentBridge
{
	// test-cache-v2: immutable per-set entry files plus one atomic index, up to 32 sets in total.
	// The old single-file-per-mode cache is still readable, but only as legacy diagnostics: it was
	// keyed on paths and timestamps and cannot be re-labelled as evidence-v1.
	public static class TestRunDumpStore
	{
		public static string Root
		{
			get
			{
				string path = Path.Combine(BridgePaths.WorkingRoot, "TestCacheV2");
				if (!Directory.Exists(path))
				{
					Directory.CreateDirectory(path);
				}

				return path;
			}
		}

		public static string IndexPath
		{
			get { return Path.Combine(Root, "index.json"); }
		}

		// ---------------------------------------------------------------- pending run

		public static void WritePending(TestRunDump dump)
		{
			WriteAtomic(PendingPath(dump.Filter.TestMode), JsonUtility.ToJson(dump));
		}

		public static bool TryTakePending(string testMode, out TestRunDump dump)
		{
			dump = ReadDump(PendingPath(testMode));
			DeletePending(testMode);
			return dump != null;
		}

		public static void DeletePending(string testMode)
		{
			string path = PendingPath(testMode);
			if (File.Exists(path))
			{
				File.Delete(path);
			}
		}

		// ---------------------------------------------------------------- v2 entries

		public static TestCacheIndex ReadIndex()
		{
			if (!File.Exists(IndexPath))
			{
				return new TestCacheIndex();
			}

			try
			{
				TestCacheIndex index = JsonUtility.FromJson<TestCacheIndex>(File.ReadAllText(IndexPath));
				if (index == null || index.Version != 2)
				{
					return new TestCacheIndex();
				}

				if (index.Entries == null)
				{
					index.Entries = new List<TestCacheEntryInfo>();
				}

				return index;
			}
			catch (Exception)
			{
				// A damaged index must not take the bridge down with it; the sets it pointed at
				// simply stop being reachable and the next run republishes.
				return new TestCacheIndex();
			}
		}

		public static bool TryLoad(string entryId, out TestRunDump dump)
		{
			dump = ReadDump(EntryPath(entryId));
			return dump != null;
		}

		// Entry first, index second. A crash in between leaves an orphan file that no lookup can
		// reach, which is exactly the failure direction a cache is allowed to have.
		public static string Publish(TestRunDump dump, long nowMs)
		{
			string id = Guid.NewGuid().ToString("N");
			WriteAtomic(EntryPath(id), JsonUtility.ToJson(dump));

			TestCacheIndex index = ReadIndex();
			TestRunResult totals = TestResultAggregator.Aggregate(dump.Entries);
			index.Entries.Add(new TestCacheEntryInfo
			{
				Id = id,
				TestMode = dump.Filter.TestMode,
				InputDigest = dump.InputDigest,
				SourceFingerprint = dump.SourceFingerprint,
				ArtifactVersion = dump.Fingerprint,
				SourceTaskId = dump.SourceTaskId,
				Validity = dump.Validity,
				FinishedAtUtc = dump.FinishedAtUtc,
				LastUsedMs = nowMs,
				Passed = totals.passed,
				Failed = totals.failed,
				Skipped = totals.skipped,
				Inconclusive = totals.inconclusive,
				Total = totals.total
			});

			foreach (string evicted in index.Trim())
			{
				TryDeleteEntry(evicted);
			}

			WriteAtomic(IndexPath, JsonUtility.ToJson(index));
			return id;
		}

		public static void Touch(string entryId, long nowMs)
		{
			TestCacheIndex index = ReadIndex();
			TestCacheEntryInfo entry = index.Find(entryId);
			if (entry == null)
			{
				return;
			}

			entry.LastUsedMs = nowMs;
			WriteAtomic(IndexPath, JsonUtility.ToJson(index));
		}

		public static void Forget(string entryId)
		{
			TestCacheIndex index = ReadIndex();
			TestCacheEntryInfo entry = index.Find(entryId);
			if (entry == null)
			{
				return;
			}

			index.Entries.Remove(entry);
			WriteAtomic(IndexPath, JsonUtility.ToJson(index));
			TryDeleteEntry(entryId);
		}

		// ---------------------------------------------------------------- legacy

		public static bool TryReadLegacy(string testMode, out TestRunDump dump)
		{
			dump = ReadDump(LegacyPath(testMode));
			return dump != null;
		}

		// ---------------------------------------------------------------- paths

		private static string EntryPath(string id)
		{
			return Path.Combine(Root, "entry-" + id + ".json");
		}

		private static string LegacyPath(string testMode)
		{
			return Path.Combine(BridgePaths.WorkingRoot, "test-cache-" + testMode.ToLowerInvariant() + ".json");
		}

		private static string PendingPath(string testMode)
		{
			return LegacyPath(testMode) + ".pending";
		}

		private static TestRunDump ReadDump(string path)
		{
			if (!File.Exists(path))
			{
				return null;
			}

			try
			{
				return JsonUtility.FromJson<TestRunDump>(File.ReadAllText(path));
			}
			catch (Exception)
			{
				return null;
			}
		}

		private static void TryDeleteEntry(string id)
		{
			try
			{
				string path = EntryPath(id);
				if (File.Exists(path))
				{
					File.Delete(path);
				}
			}
			catch (IOException)
			{
			}
		}

		private static void WriteAtomic(string path, string json)
		{
			Coordination.SharedFile.WriteAtomic(path, json);
		}
	}
}
