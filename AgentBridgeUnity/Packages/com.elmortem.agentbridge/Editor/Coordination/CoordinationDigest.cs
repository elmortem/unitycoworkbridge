using System.Security.Cryptography;
using System.Text;

namespace AgentBridge.Coordination
{
	public static class CoordinationDigest
	{
		public static string Sha256(string text)
		{
			using (SHA256 sha = SHA256.Create())
			{
				byte[] hash = sha.ComputeHash(Encoding.UTF8.GetBytes(text ?? ""));
				return Hex(hash);
			}
		}

		public static string Hex(byte[] bytes)
		{
			var builder = new StringBuilder(bytes.Length * 2);
			foreach (byte value in bytes)
			{
				builder.Append(value.ToString("x2"));
			}

			return builder.ToString();
		}
	}
}
