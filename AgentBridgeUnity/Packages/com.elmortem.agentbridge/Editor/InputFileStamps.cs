using System;
using System.Collections.Generic;
#if UNITY_EDITOR_WIN
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32.SafeHandles;
#endif

namespace AgentBridge
{
	// Stamps of every file in one directory from a single listing: no file is opened, so a stamp
	// costs neither a read nor a handle that could block a save.
	public static class InputFileStamps
	{
		// Null when this platform or volume gives no stamps: the digest then reads every file.
		public static Dictionary<string, InputFileStamp> Read(string directory)
		{
#if UNITY_EDITOR_WIN
			if (Environment.OSVersion.Platform != PlatformID.Win32NT || string.IsNullOrEmpty(directory))
			{
				return null;
			}

			try
			{
				return ReadWindows(directory);
			}
			catch (DllNotFoundException) { return null; }
			catch (EntryPointNotFoundException) { return null; }
#else
			return null;
#endif
		}

#if UNITY_EDITOR_WIN
		private const int FileIdBothDirectoryInfo = 10;
		private const int ErrorNoMoreFiles = 18;
		private const int SkippedAttributes = 0x10 | 0x400;

		private static Dictionary<string, InputFileStamp> ReadWindows(string directory)
		{
			// FILE_LIST_DIRECTORY, share read/write/delete, OPEN_EXISTING, BACKUP_SEMANTICS.
			using (SafeFileHandle handle = CreateFileW(directory, 0x1, 0x7, IntPtr.Zero, 3, 0x02000000, IntPtr.Zero))
			{
				if (handle.IsInvalid)
				{
					return null;
				}

				var stamps = new Dictionary<string, InputFileStamp>(StringComparer.OrdinalIgnoreCase);
				var buffer = new byte[64 * 1024];
				while (true)
				{
					if (!GetFileInformationByHandleEx(handle, FileIdBothDirectoryInfo, buffer, buffer.Length))
					{
						return Marshal.GetLastWin32Error() == ErrorNoMoreFiles ? stamps : null;
					}

					int offset = 0;
					while (true)
					{
						int next = BitConverter.ToInt32(buffer, offset);
						int attributes = BitConverter.ToInt32(buffer, offset + 56);
						// A directory is not an input, and a reparse point's stamp is the link's, not the target's.
						if ((attributes & SkippedAttributes) == 0)
						{
							int nameBytes = BitConverter.ToInt32(buffer, offset + 60);
							string name = Encoding.Unicode.GetString(buffer, offset + 104, nameBytes);
							stamps[name] = new InputFileStamp
							{
								WriteTicks = BitConverter.ToInt64(buffer, offset + 24),
								ChangeTicks = BitConverter.ToInt64(buffer, offset + 32),
								Length = BitConverter.ToInt64(buffer, offset + 40),
								FileId = BitConverter.ToInt64(buffer, offset + 96)
							};
						}

						if (next == 0)
						{
							break;
						}

						offset += next;
					}
				}
			}
		}

		[DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
		private static extern SafeFileHandle CreateFileW(string name, int access, int share, IntPtr security, int disposition, int flags, IntPtr template);

		[DllImport("kernel32.dll", SetLastError = true)]
		private static extern bool GetFileInformationByHandleEx(SafeFileHandle handle, int infoClass, byte[] buffer, int size);
#endif
	}
}
