using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using AgentBridge;

namespace AgentBridge.Coordination.Tests;

internal static class HashingScenarios
{
	public static void Run(string root, List<string> covered)
	{
		var data = new byte[1048579];
		new Random(42).NextBytes(data);
		using (var expected = SHA256.Create())
		using (var actual = ContentHash.Create())
		{
			foreach (int length in new[] { 0, 1, 63, 64, 65537, 1048576 })
			{
				if (!expected.ComputeHash(data, 3, length).SequenceEqual(actual.ComputeHash(data, 3, length)))
					throw new Exception("SHA256 differs for length " + length);
			}
			actual.TransformBlock(data, 3, 63, null, 0);
			actual.TransformFinalBlock(data, 66, 512);
			if (!expected.ComputeHash(data, 3, 575).SequenceEqual(actual.Hash))
				throw new Exception("Incremental SHA256 differs");
			Console.WriteLine("SHA256 provider: " + actual.GetType().FullName);
		}
#if !UNITY_EDITOR_WIN
		if (typeof(ContentHash).GetNestedType("WindowsSha256", BindingFlags.NonPublic) != null)
			throw new Exception("Portable builds must not contain the Windows native implementation");
#endif
		string fixture = Path.Combine(root, "hash-format");
		Directory.CreateDirectory(fixture);
		File.WriteAllBytes(Path.Combine(fixture, "data.bin"), data);
		var snapshot = ValidationInputSnapshot.Capture(new[] { fixture }, Array.Empty<string>(), "test");
		using (var sha = SHA256.Create())
		using (var expected = new MemoryStream())
		{
			byte[] header = Encoding.UTF8.GetBytes("context\ntest\nroot\nr0\nfile\nr0/data.bin\n" + data.Length + "\n");
			expected.Write(header);
			expected.Write(data);
			expected.Position = 0;
			if (!snapshot.Complete || snapshot.Digest != Convert.ToHexString(sha.ComputeHash(expected)).ToLowerInvariant())
				throw new Exception("Streaming snapshot changed the persisted digest format");
		}
		covered.Add("C21_hash_platform_and_format");
	}
}
