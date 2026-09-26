using System;
using System.IO;
using UnityEngine;

namespace AgentBridge
{
	public static class CompileCacheStore
	{
		public static bool CanReuse(CompileCacheEntry entry, string fingerprint, bool fresh, DateTime submittedUtc)
		{
			if (entry == null || entry.Version != 2 || entry.Diagnostics == null || string.IsNullOrEmpty(fingerprint) || entry.Fingerprint != fingerprint
				|| (entry.Status != "success" && entry.Status != "compiler_error")) return false;
			// A fresh request can share a cycle which completed while it was waiting.
			return !fresh || (DateTime.TryParse(entry.FinishedAtUtc, null,
				System.Globalization.DateTimeStyles.RoundtripKind, out DateTime finished) && finished.ToUniversalTime() >= submittedUtc.ToUniversalTime());
		}
		public static void Write(CompileCacheEntry entry)
		{
			Coordination.SharedFile.WriteAtomic(FilePath(), JsonUtility.ToJson(entry));
		}

		public static bool TryRead(out CompileCacheEntry entry)
		{
			entry = null;
			string path = FilePath();
			if (!File.Exists(path))
			{
				return false;
			}

			try
			{
				entry = JsonUtility.FromJson<CompileCacheEntry>(File.ReadAllText(path));
			}
			catch
			{
				entry = null;
			}

			return entry != null;
		}

		private static string FilePath()
		{
			return Path.Combine(BridgePaths.WorkingRoot, "compile-cache.json");
		}
	}
}
