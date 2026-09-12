using System;
using System.Collections.Generic;

namespace AgentBridge
{
	// Published after its entries, never before: an entry without an index row is an orphan and is
	// not a result, while an index row without its entry would be a promise the cache cannot keep.
	[Serializable]
	public class TestCacheIndex
	{
		public const int MaxEntries = 32;

		public int Version = 2;
		public List<TestCacheEntryInfo> Entries = new List<TestCacheEntryInfo>();

		public TestCacheEntryInfo Find(string id)
		{
			foreach (TestCacheEntryInfo entry in Entries)
			{
				if (string.Equals(entry.Id, id, StringComparison.Ordinal))
				{
					return entry;
				}
			}

			return null;
		}

		// Least recently used loses. Returns the ids whose payload files may be deleted.
		public List<string> Trim()
		{
			var evicted = new List<string>();
			if (Entries.Count <= MaxEntries)
			{
				return evicted;
			}

			Entries.Sort(delegate(TestCacheEntryInfo left, TestCacheEntryInfo right)
			{
				return left.LastUsedMs.CompareTo(right.LastUsedMs);
			});

			while (Entries.Count > MaxEntries)
			{
				evicted.Add(Entries[0].Id);
				Entries.RemoveAt(0);
			}

			return evicted;
		}
	}
}
