using System;
using System.Collections.Generic;
using System.IO;
using System.Security.Cryptography;
using System.Text;

namespace AgentBridge
{
	public static class TaskFileHash
	{
		private static readonly Dictionary<string, CachedHash> _hashCache = new Dictionary<string, CachedHash>();

		public static string HashOf(string taskFilePath, string payloadPath)
		{
			long taskFileLength = new FileInfo(taskFilePath).Length;
			long payloadLength = !string.IsNullOrEmpty(payloadPath) && File.Exists(payloadPath)
				? new FileInfo(payloadPath).Length
				: 0;
			string taskFileWriteUtc = File.GetLastWriteTimeUtc(taskFilePath).ToString("o");
			string payloadWriteUtc = !string.IsNullOrEmpty(payloadPath) && File.Exists(payloadPath)
				? File.GetLastWriteTimeUtc(payloadPath).ToString("o")
				: "";
			string cacheKey = taskFilePath + "|" + (payloadPath ?? "");

			CachedHash cached;
			if (_hashCache.TryGetValue(cacheKey, out cached)
				&& cached.TaskFileLength == taskFileLength
				&& cached.PayloadLength == payloadLength
				&& cached.TaskFileWriteUtc == taskFileWriteUtc
				&& cached.PayloadWriteUtc == payloadWriteUtc)
			{
				return cached.Hash;
			}

			string hash = ComputeHash(taskFilePath, payloadPath);
			_hashCache[cacheKey] = new CachedHash
			{
				TaskFileLength = taskFileLength,
				PayloadLength = payloadLength,
				TaskFileWriteUtc = taskFileWriteUtc,
				PayloadWriteUtc = payloadWriteUtc,
				Hash = hash
			};
			return hash;
		}

		private static string ComputeHash(string taskFilePath, string payloadPath)
		{
			using (SHA256 sha = SHA256.Create())
			{
				byte[] taskBytes = File.ReadAllBytes(taskFilePath);
				byte[] combined = taskBytes;

				if (!string.IsNullOrEmpty(payloadPath) && File.Exists(payloadPath))
				{
					byte[] payloadBytes = File.ReadAllBytes(payloadPath);
					combined = new byte[taskBytes.Length + payloadBytes.Length];
					Buffer.BlockCopy(taskBytes, 0, combined, 0, taskBytes.Length);
					Buffer.BlockCopy(payloadBytes, 0, combined, taskBytes.Length, payloadBytes.Length);
				}

				byte[] hashBytes = sha.ComputeHash(combined);
				var builder = new StringBuilder(hashBytes.Length * 2);
				foreach (byte b in hashBytes)
				{
					builder.Append(b.ToString("x2"));
				}

				return builder.ToString();
			}
		}
	}
}
