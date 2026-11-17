using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using AgentBridge.Coordination;

namespace AgentBridge
{
	// Content hashes of inputs keyed by their stamp, kept in a file so they outlive the domain.
	// A hash is handed out only while length, write time, change time and file id all match.
	// Pure System.IO: the digests that use it run on worker threads, so every member locks.
	public sealed class InputHashCache
	{
		// A file changed this close to a capture can share its change time with its next edit,
		// so its hash serves that capture and is not remembered.
		public const long RacyWindowTicks = 2 * TimeSpan.TicksPerSecond;
		private const string Header = "input-hash-cache-v1";

		public struct Entry
		{
			public InputFileStamp Stamp;
			public string Hash;
		}

		private readonly object _sync = new object();
		private readonly string _path;
		private Dictionary<string, Entry> _entries;

		public InputHashCache(string path)
		{
			_path = path;
		}

		public bool TryGet(string path, InputFileStamp stamp, out string hash)
		{
			lock (_sync)
			{
				Load();
				Entry entry;
				if (_entries.TryGetValue(ValidationInputSnapshot.Normalize(path), out entry)
					&& entry.Stamp.Length == stamp.Length
					&& entry.Stamp.WriteTicks == stamp.WriteTicks
					&& entry.Stamp.ChangeTicks == stamp.ChangeTicks
					&& entry.Stamp.FileId == stamp.FileId)
				{
					hash = entry.Hash;
					return true;
				}

				hash = null;
				return false;
			}
		}

		// Called after a complete snapshot: whatever lies under its roots and was not seen is gone.
		public void Commit(string[] roots, Dictionary<string, Entry> seen)
		{
			lock (_sync)
			{
				Load();
				bool changed = false;
				var prefixes = new List<string>();
				foreach (string root in roots ?? new string[0])
				{
					if (!string.IsNullOrEmpty(root))
					{
						prefixes.Add(ValidationInputSnapshot.Normalize(root).TrimEnd('/') + "/");
					}
				}

				var stale = new List<string>();
				foreach (string key in _entries.Keys)
				{
					if (seen.ContainsKey(key))
					{
						continue;
					}

					foreach (string prefix in prefixes)
					{
						if (key.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
						{
							stale.Add(key);
							break;
						}
					}
				}

				foreach (string key in stale)
				{
					_entries.Remove(key);
					changed = true;
				}

				foreach (KeyValuePair<string, Entry> pair in seen)
				{
					string key = ValidationInputSnapshot.Normalize(pair.Key);
					Entry existing;
					if (!_entries.TryGetValue(key, out existing) || !Same(existing, pair.Value))
					{
						_entries[key] = pair.Value;
						changed = true;
					}
				}

				if (changed)
				{
					Save();
				}
			}
		}

		private static bool Same(Entry left, Entry right)
		{
			return left.Hash == right.Hash
				&& left.Stamp.Length == right.Stamp.Length
				&& left.Stamp.WriteTicks == right.Stamp.WriteTicks
				&& left.Stamp.ChangeTicks == right.Stamp.ChangeTicks
				&& left.Stamp.FileId == right.Stamp.FileId;
		}

		private void Load()
		{
			if (_entries != null)
			{
				return;
			}

			_entries = new Dictionary<string, Entry>(StringComparer.OrdinalIgnoreCase);
			string text;
			if (!SharedFile.TryReadAllText(_path, out text))
			{
				return;
			}

			string[] lines = text.Split('\n');
			if (lines.Length == 0 || lines[0].TrimEnd('\r') != Header)
			{
				return;
			}

			var loaded = new Dictionary<string, Entry>(StringComparer.OrdinalIgnoreCase);
			for (int i = 1; i < lines.Length; i++)
			{
				string line = lines[i].TrimEnd('\r');
				if (line.Length == 0)
				{
					continue;
				}

				string[] parts = line.Split(new[] { '|' }, 6);
				Entry entry = default(Entry);
				if (parts.Length != 6
					|| !TryLong(parts[0], out entry.Stamp.ChangeTicks)
					|| !TryLong(parts[1], out entry.Stamp.WriteTicks)
					|| !TryLong(parts[2], out entry.Stamp.Length)
					|| !TryLong(parts[3], out entry.Stamp.FileId)
					|| parts[4].Length == 0
					|| parts[5].Length == 0)
				{
					// A torn or foreign file is worth nothing: one full read rebuilds it.
					return;
				}

				entry.Hash = parts[4];
				loaded[parts[5]] = entry;
			}

			_entries = loaded;
		}

		private void Save()
		{
			var builder = new StringBuilder();
			builder.Append(Header).Append('\n');
			foreach (KeyValuePair<string, Entry> pair in _entries)
			{
				InputFileStamp stamp = pair.Value.Stamp;
				builder.Append(stamp.ChangeTicks.ToString(CultureInfo.InvariantCulture)).Append('|')
					.Append(stamp.WriteTicks.ToString(CultureInfo.InvariantCulture)).Append('|')
					.Append(stamp.Length.ToString(CultureInfo.InvariantCulture)).Append('|')
					.Append(stamp.FileId.ToString(CultureInfo.InvariantCulture)).Append('|')
					.Append(pair.Value.Hash).Append('|')
					.Append(pair.Key).Append('\n');
			}

			try
			{
				SharedFile.WriteAtomic(_path, builder.ToString());
			}
			catch (IOException)
			{
				// An unsaved cache costs the next domain one full read, never a wrong digest.
			}
			catch (UnauthorizedAccessException)
			{
				// Same as above.
			}
		}

		private static bool TryLong(string text, out long value)
		{
			return long.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out value);
		}
	}
}
