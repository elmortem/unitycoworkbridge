using System;
using System.Collections.Generic;
using System.IO;
using System.Security.Cryptography;

namespace AgentBridge
{
	// Hashes the files of one snapshot. A file whose stamp has not moved since it was last read
	// takes its hash from the cache; every other file is read through the save gate. Pure System.IO.
	public sealed class InputFileHasher : IDisposable
	{
		private readonly InputHashCache _cache;
		private readonly long _racyFrom;
		private readonly Dictionary<string, Dictionary<string, InputFileStamp>> _directories = new Dictionary<string, Dictionary<string, InputFileStamp>>(StringComparer.OrdinalIgnoreCase);
		private readonly Dictionary<string, InputHashCache.Entry> _seen = new Dictionary<string, InputHashCache.Entry>(StringComparer.OrdinalIgnoreCase);
		private readonly byte[] _buffer = new byte[64 * 1024];
		private readonly SHA256 _sha = ContentHash.Create();

		public InputFileHasher(InputHashCache cache)
		{
			_cache = cache;
			_racyFrom = DateTime.UtcNow.ToFileTimeUtc() - InputHashCache.RacyWindowTicks;
		}

		public long ReadBytes { get; private set; }

		// Hash of the bytes as they are now. False when the length moved while the file was read.
		// FileStream exceptions reach the caller.
		public bool TryHash(string file, out long length, out string hash)
		{
			string key = ValidationInputSnapshot.Normalize(file);
			InputFileStamp stamp = default(InputFileStamp);
			bool stamped = _cache != null && TryStamp(file, out stamp);
			if (stamped && _cache.TryGet(key, stamp, out hash))
			{
				length = stamp.Length;
				_seen[key] = new InputHashCache.Entry { Stamp = stamp, Hash = hash };
				return true;
			}

			InputReadGate.Enter();
			try
			{
				using (var input = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.Read, _buffer.Length, FileOptions.SequentialScan))
				{
					long expected = input.Length;
					// WindowsSha256 is unusable after HashFinal until it is initialized again.
					_sha.Initialize();
					length = 0;
					int count;
					while ((count = input.Read(_buffer, 0, _buffer.Length)) != 0)
					{
						_sha.TransformBlock(_buffer, 0, count, null, 0);
						length += count;
					}

					_sha.TransformFinalBlock(_buffer, 0, 0);
					hash = ValidationInputSnapshot.ToHex(_sha.Hash);
					ReadBytes += length;
					// The stamp was listed before this read, so it describes these bytes only if the
					// length agrees and the file has been still long enough for its next edit to
					// move the change time.
					if (stamped && stamp.Length == length && stamp.ChangeTicks < _racyFrom)
					{
						_seen[key] = new InputHashCache.Entry { Stamp = stamp, Hash = hash };
					}

					return length == expected;
				}
			}
			finally
			{
				InputReadGate.Exit();
			}
		}

		// After a complete snapshot only: what lies under the roots and was not hashed is gone.
		public void Commit(string[] roots)
		{
			if (_cache != null) _cache.Commit(roots, _seen);
		}

		public void Dispose()
		{
			_sha.Dispose();
		}

		// Each directory is listed once per snapshot; a directory without stamps is remembered as
		// null so its files are simply read.
		private bool TryStamp(string file, out InputFileStamp stamp)
		{
			stamp = default(InputFileStamp);
			string directory = Path.GetDirectoryName(file) ?? "";
			Dictionary<string, InputFileStamp> listing;
			if (!_directories.TryGetValue(directory, out listing))
			{
				listing = InputFileStamps.Read(directory);
				_directories[directory] = listing;
			}

			return listing != null
				&& listing.TryGetValue(Path.GetFileName(file), out stamp)
				&& stamp.ChangeTicks != 0;
		}
	}
}
