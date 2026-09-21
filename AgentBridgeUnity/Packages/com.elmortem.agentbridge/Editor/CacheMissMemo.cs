using System.Collections.Generic;

namespace AgentBridge
{
	// Why a waiting task that just missed is not asked again a second later.
	//
	// The content digest is the expensive half of the lookup, and it is deliberately never memoized:
	// its answer must not survive an edit. What is memoized here is the miss itself, keyed on the two
	// cheap facts the digest was compared under — the source fingerprint and the set of candidate
	// entries. Either of them changing forgets the miss at once, so an agent that edits a file and
	// asks again is answered by a fresh digest; while both hold, the digest is recomputed at most once
	// every RetryMs instead of on every scan.
	//
	// The backstop retry exists because an input can change without moving either key: a texture, a
	// scene or any other asset the fingerprint does not track.
	public sealed class CacheMissMemo
	{
		public const long RetryMs = 10000;

		private readonly Dictionary<string, CacheMissEntry> _entries = new Dictionary<string, CacheMissEntry>();

		public bool ShouldSkip(string taskId, string sourceFingerprint, string candidateKey, long nowMs)
		{
			CacheMissEntry entry;
			if (!_entries.TryGetValue(taskId, out entry))
			{
				return false;
			}

			if (entry.SourceFingerprint != sourceFingerprint || entry.CandidateKey != candidateKey || nowMs >= entry.RetryAtMs)
			{
				_entries.Remove(taskId);
				return false;
			}

			return true;
		}

		public void Record(string taskId, string sourceFingerprint, string candidateKey, long nowMs)
		{
			_entries[taskId] = new CacheMissEntry
			{
				SourceFingerprint = sourceFingerprint,
				CandidateKey = candidateKey,
				RetryAtMs = nowMs + RetryMs
			};
		}

		public void Forget(string taskId)
		{
			_entries.Remove(taskId);
		}

		// A task that left the queue takes its miss with it: the memo never grows past the tasks that
		// are actually waiting, and a reused id cannot inherit an older verdict.
		public void Retain(ICollection<string> liveTaskIds)
		{
			var dead = new List<string>();
			foreach (string id in _entries.Keys)
			{
				if (!liveTaskIds.Contains(id))
				{
					dead.Add(id);
				}
			}

			foreach (string id in dead)
			{
				_entries.Remove(id);
			}
		}
	}
}
