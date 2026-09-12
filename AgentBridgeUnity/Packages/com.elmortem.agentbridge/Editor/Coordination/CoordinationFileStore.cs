using System;
using System.IO;
using System.Text;

namespace AgentBridge.Coordination
{
	// One persistent lock file, one published snapshot, and a temp-then-replace write. The lock is
	// never deleted "to recover": deleting it is exactly how two processes end up believing they
	// both hold the coordinator.
	public sealed class CoordinationFileStore : ICoordinationStore
	{
		public const string StateFileName = "state.json";
		public const string MarkerFileName = "marker.json";
		public const string LockFileName = "transaction.lock";
		private const string TempPrefix = "state.json.";
		private const string TempSuffix = ".tmp";

		private readonly string _root;
		private readonly ICoordinationCodec _codec;
		private readonly ICoordinationClock _clock;
		private readonly string _projectId;
		private readonly Func<string> _newId;

		public CoordinationFileStore(
			string coordinationRoot,
			ICoordinationCodec codec,
			ICoordinationClock clock,
			string projectId,
			Func<string> newId)
		{
			_root = coordinationRoot;
			_codec = codec;
			_clock = clock;
			_projectId = CoordinationText.Safe(projectId);
			_newId = newId ?? DefaultId;
		}

		public string Root
		{
			get { return _root; }
		}

		public string StatePath
		{
			get { return Path.Combine(_root, StateFileName); }
		}

		public string MarkerPath
		{
			get { return Path.Combine(_root, MarkerFileName); }
		}

		public string LockPath
		{
			get { return Path.Combine(_root, LockFileName); }
		}

		public bool Exists
		{
			get { return File.Exists(MarkerPath) || File.Exists(StatePath); }
		}

		public CoordinationState Read()
		{
			// A concurrent replace can make the file momentarily unavailable; a whole older
			// revision is an acceptable answer, half a file never is.
			for (int attempt = 0; attempt < 20; attempt++)
			{
				try
				{
					return ReadOnce();
				}
				catch (IOException)
				{
					System.Threading.Thread.Sleep(10);
				}
			}

			return ReadOnce();
		}

		public bool TryBegin(out ICoordinationTransaction transaction)
		{
			transaction = null;
			Directory.CreateDirectory(_root);

			FileStream lockStream;
			try
			{
				lockStream = new FileStream(LockPath, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
			}
			catch (IOException)
			{
				return false;
			}
			catch (UnauthorizedAccessException)
			{
				return false;
			}

			try
			{
				CoordinationState state = ReadOrCreate();
				transaction = new Transaction(this, lockStream, state);
				return true;
			}
			catch
			{
				lockStream.Dispose();
				throw;
			}
		}

		// Retry loop for clients. The editor never uses it: its main thread takes one attempt per
		// tick and goes back to update if the lock is busy.
		public ICoordinationTransaction BeginWithRetry(int budgetMs)
		{
			long deadline = _clock.UtcNowMs + budgetMs;
			int delay = 5;
			while (true)
			{
				ICoordinationTransaction transaction;
				if (TryBegin(out transaction))
				{
					return transaction;
				}

				if (_clock.UtcNowMs >= deadline)
				{
					throw new CoordinationStoreException(
						CoordinationCodes.Busy,
						"the coordination store stayed locked for " + budgetMs + "ms; retry with the same uuid");
				}

				System.Threading.Thread.Sleep(delay);
				delay = delay < 80 ? delay * 2 : 80;
			}
		}

		private CoordinationState ReadOnce()
		{
			if (!File.Exists(StatePath))
			{
				if (File.Exists(MarkerPath))
				{
					throw new CoordinationStoreException(
						CoordinationCodes.RecoveryRequired,
						"the coordination state is missing but its marker is present; recover " + StatePath + " explicitly");
				}

				throw new CoordinationStoreException(
					CoordinationCodes.RecoveryRequired,
					"no coordination state in " + _root + "; register a session first");
			}

			string json = ReadAllText(StatePath);
			CoordinationState state;
			try
			{
				state = _codec.Deserialize(json);
			}
			catch (Exception exception)
			{
				throw new CoordinationStoreException(
					CoordinationCodes.Corrupt,
					"the coordination state is not readable: " + exception.Message);
			}

			if (state == null)
			{
				throw new CoordinationStoreException(CoordinationCodes.Corrupt, "the coordination state is empty");
			}

			state.Normalize();
			if (state.SchemaVersion != CoordinationLimits.SchemaVersion)
			{
				throw new CoordinationStoreException(
					CoordinationCodes.SchemaUnsupported,
					"coordination state schema " + state.SchemaVersion + " is not supported by this build");
			}

			if (string.IsNullOrEmpty(state.Epoch) || string.IsNullOrEmpty(state.ProjectId))
			{
				throw new CoordinationStoreException(CoordinationCodes.Corrupt, "the coordination state has no identity");
			}

			if (!string.IsNullOrEmpty(_projectId) && !string.Equals(state.ProjectId, _projectId, StringComparison.Ordinal))
			{
				throw new CoordinationStoreException(
					CoordinationCodes.ProjectMismatch,
					"the coordination state belongs to project " + state.ProjectId);
			}

			return state;
		}

		private CoordinationState ReadOrCreate()
		{
			if (File.Exists(StatePath))
			{
				return ReadOnce();
			}

			if (File.Exists(MarkerPath))
			{
				throw new CoordinationStoreException(
					CoordinationCodes.RecoveryRequired,
					"the coordination state is missing but its marker is present; recover " + StatePath + " explicitly");
			}

			var state = new CoordinationState
			{
				SchemaVersion = CoordinationLimits.SchemaVersion,
				ProjectId = _projectId,
				Epoch = _newId(),
				Revision = 1,
				NextTicket = 1,
				NextRequestNumber = 1
			};
			return state;
		}

		private void Publish(CoordinationState state)
		{
			state.Normalize();
			Directory.CreateDirectory(_root);
			WriteAtomic(StatePath, _codec.Serialize(state));

			if (!File.Exists(MarkerPath))
			{
				var marker = new CoordinationMarker
				{
					ProjectId = state.ProjectId,
					Epoch = state.Epoch,
					CreatedAtMs = _clock.UtcNowMs
				};
				WriteAtomic(MarkerPath, MarkerJson(marker));
			}

			CleanupOwnTemporaries();
		}

		private void WriteAtomic(string destination, string content)
		{
			string temporary = Path.Combine(_root, TempPrefix + _newId() + TempSuffix);
			byte[] bytes = new UTF8Encoding(false).GetBytes(content);

			using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
			{
				stream.Write(bytes, 0, bytes.Length);
				stream.Flush(true);
			}

			try
			{
				Rename(temporary, destination);
			}
			catch (IOException)
			{
				// One retry: a reader holding the file for a moment is the ordinary cause. If the
				// rename fails again the exception travels out of Commit and nothing is published.
				// Copying would write the file in pieces, and half a state is worse than no write.
				System.Threading.Thread.Sleep(20);
				Rename(temporary, destination);
			}
		}

		// Atomic publish, using only what Unity's Mono profile actually implements: Replace when the
		// destination exists, a plain rename when it does not.
		private static void Rename(string temporary, string destination)
		{
			if (File.Exists(destination))
			{
				File.Replace(temporary, destination, null);
			}
			else
			{
				File.Move(temporary, destination);
			}
		}

		// Only our own leftovers, and only in a directory we have already proved is the
		// coordination root. Journal and inbox files belong to another mechanism.
		private void CleanupOwnTemporaries()
		{
			try
			{
				foreach (string file in Directory.GetFiles(_root, TempPrefix + "*" + TempSuffix))
				{
					try
					{
						if (DateTime.UtcNow - File.GetLastWriteTimeUtc(file) > TimeSpan.FromMinutes(5))
						{
							File.Delete(file);
						}
					}
					catch (IOException)
					{
					}
				}
			}
			catch (DirectoryNotFoundException)
			{
			}
		}

		private static string ReadAllText(string path)
		{
			using (var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
			using (var reader = new StreamReader(stream, new UTF8Encoding(false)))
			{
				return reader.ReadToEnd();
			}
		}

		private static string MarkerJson(CoordinationMarker marker)
		{
			return "{\"SchemaVersion\":" + marker.SchemaVersion
				+ ",\"ProjectId\":\"" + marker.ProjectId
				+ "\",\"Epoch\":\"" + marker.Epoch
				+ "\",\"CreatedAtMs\":" + marker.CreatedAtMs + "}";
		}

		private static string DefaultId()
		{
			return Guid.NewGuid().ToString("N");
		}

		private sealed class Transaction : ICoordinationTransaction
		{
			private readonly CoordinationFileStore _store;
			private readonly FileStream _lockStream;
			private bool _committed;
			private bool _disposed;

			public Transaction(CoordinationFileStore store, FileStream lockStream, CoordinationState state)
			{
				_store = store;
				_lockStream = lockStream;
				State = state;
			}

			public CoordinationState State { get; private set; }

			public void Commit()
			{
				if (_disposed)
				{
					throw new InvalidOperationException("the coordination transaction is already closed");
				}

				_store.Publish(State);
				_committed = true;
			}

			public bool Committed
			{
				get { return _committed; }
			}

			public void Dispose()
			{
				if (_disposed)
				{
					return;
				}

				_disposed = true;
				_lockStream.Dispose();
			}
		}
	}
}
