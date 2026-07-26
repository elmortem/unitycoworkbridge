using System.Security.Cryptography;

namespace AgentBridge.Cli;

internal static class TaskIdGenerator
{
	public static string NewId()
	{
		Span<byte> random = stackalloc byte[4];
		RandomNumberGenerator.Fill(random);
		return "Task_"
			+ DateTime.UtcNow.ToString("yyyyMMdd_HHmmss_fff")
			+ "_"
			+ Convert.ToHexString(random).ToLowerInvariant();
	}
}
