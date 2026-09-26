using System;
using System.IO;
using System.Text;
using System.Threading;

namespace AgentBridge.Coordination
{
	// Bridge files are published by one process with an atomic replace and polled by any number
	// of others: the editor writes status, heartbeat and journal while every CLI waiting on a task
	// reads them. On Windows File.Replace fails with a sharing violation whenever a reader holds
	// the destination without FILE_SHARE_DELETE, so readers here always grant it. The writer still
	// retries briefly, because antivirus and the search indexer open files on their own terms.
	// Compiled by both the package and the CLI, like the rest of this folder.
	public static class SharedFile
	{
		public const int WriteAttempts = 5;
		public const int ReadAttempts = 3;
		public const int RetryDelayMs = 10;

		private static readonly Encoding Utf8NoBom = new UTF8Encoding(false);

		public static string ReadAllText(string path)
		{
			for (int attempt = 1; ; attempt++)
			{
				try
				{
					using (var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
					using (var reader = new StreamReader(stream, Encoding.UTF8, true))
					{
						return reader.ReadToEnd();
					}
				}
				catch (FileNotFoundException)
				{
					throw;
				}
				catch (DirectoryNotFoundException)
				{
					throw;
				}
				catch (IOException) when (attempt < ReadAttempts)
				{
					Thread.Sleep(RetryDelayMs * attempt);
				}
				catch (UnauthorizedAccessException) when (attempt < ReadAttempts)
				{
					Thread.Sleep(RetryDelayMs * attempt);
				}
			}
		}

		public static bool TryReadAllText(string path, out string text)
		{
			try
			{
				text = ReadAllText(path);
				return true;
			}
			catch (Exception)
			{
				text = "";
				return false;
			}
		}

		// Throws the last error once the attempts are spent: the caller decides whether a missed
		// write is fatal (a journal record) or only postponed (a status snapshot).
		public static void WriteAtomic(string path, string content)
		{
			string temporary = path + ".tmp";
			for (int attempt = 1; ; attempt++)
			{
				try
				{
					File.WriteAllText(temporary, content, Utf8NoBom);
					if (File.Exists(path))
					{
						File.Replace(temporary, path, null);
					}
					else
					{
						File.Move(temporary, path);
					}

					return;
				}
				catch (IOException) when (attempt < WriteAttempts)
				{
					Thread.Sleep(RetryDelayMs * attempt);
				}
				catch (UnauthorizedAccessException) when (attempt < WriteAttempts)
				{
					Thread.Sleep(RetryDelayMs * attempt);
				}
			}
		}
	}
}
