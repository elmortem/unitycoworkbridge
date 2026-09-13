using System;
#if UNITY_EDITOR_WIN
using System.Runtime.InteropServices;
#endif
using System.Security.Cryptography;

namespace AgentBridge
{
	// Unity's Mono selects SHA256Managed on Windows. CNG computes the identical digest
	// using the OS implementation; other platforms retain the portable implementation.
	public static class ContentHash
	{
		public static SHA256 Create()
		{
#if UNITY_EDITOR_WIN
			if (Environment.OSVersion.Platform == PlatformID.Win32NT)
			{
				try { return new WindowsSha256(); }
				catch (DllNotFoundException) { }
				catch (EntryPointNotFoundException) { }
				catch (CryptographicException) { }
			}
#endif
			return SHA256.Create();
		}

#if UNITY_EDITOR_WIN
		private sealed class WindowsSha256 : SHA256
		{
			private IntPtr _algorithm, _hash;
			~WindowsSha256() { Dispose(false); }
			public WindowsSha256()
			{
				HashSizeValue = 256;
				try
				{
					Check(BCryptOpenAlgorithmProvider(out _algorithm, "SHA256", null, 0));
					Initialize();
				}
				catch { Dispose(); throw; }
			}

			public override void Initialize()
			{
				if (_hash != IntPtr.Zero) BCryptDestroyHash(_hash);
				_hash = IntPtr.Zero;
				Check(BCryptCreateHash(_algorithm, out _hash, IntPtr.Zero, 0, IntPtr.Zero, 0, 0));
			}

			protected override void HashCore(byte[] array, int offset, int count)
			{
				if (count == 0) return;
				var pinned = GCHandle.Alloc(array, GCHandleType.Pinned);
				try { Check(BCryptHashData(_hash, IntPtr.Add(pinned.AddrOfPinnedObject(), offset), count, 0)); }
				finally { pinned.Free(); }
			}

			protected override byte[] HashFinal()
			{
				var result = new byte[32];
				Check(BCryptFinishHash(_hash, result, result.Length, 0));
				return result;
			}

			protected override void Dispose(bool disposing)
			{
				if (_hash != IntPtr.Zero) BCryptDestroyHash(_hash);
				if (_algorithm != IntPtr.Zero) BCryptCloseAlgorithmProvider(_algorithm, 0);
				_hash = _algorithm = IntPtr.Zero;
				base.Dispose(disposing);
			}

			private static void Check(int status)
			{
				if (status < 0) throw new CryptographicException("CNG SHA256 failed: " + status.ToString("x8"));
			}

			[DllImport("bcrypt.dll", CharSet = CharSet.Unicode)]
			private static extern int BCryptOpenAlgorithmProvider(out IntPtr algorithm, string id, string implementation, int flags);
			[DllImport("bcrypt.dll")]
			private static extern int BCryptCreateHash(IntPtr algorithm, out IntPtr hash, IntPtr storage, int storageSize, IntPtr secret, int secretSize, int flags);
			[DllImport("bcrypt.dll")]
			private static extern int BCryptHashData(IntPtr hash, IntPtr input, int size, int flags);
			[DllImport("bcrypt.dll")]
			private static extern int BCryptFinishHash(IntPtr hash, byte[] output, int size, int flags);
			[DllImport("bcrypt.dll")]
			private static extern int BCryptDestroyHash(IntPtr hash);
			[DllImport("bcrypt.dll")]
			private static extern int BCryptCloseAlgorithmProvider(IntPtr algorithm, int flags);
		}
#endif
	}
}
