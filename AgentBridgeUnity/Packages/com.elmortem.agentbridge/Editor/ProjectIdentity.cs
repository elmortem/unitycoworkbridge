using System;
using System.IO;

namespace AgentBridge
{
	public static class ProjectIdentity
	{
		public static string Ensure()
		{
			string path = BridgePaths.ProjectIdFile;

			try
			{
				if (File.Exists(path))
				{
					string existing = File.ReadAllText(path).Trim();
					if (existing.Length > 0)
					{
						return existing;
					}
				}
			}
			catch (Exception)
			{
				return "";
			}

			string generated = Guid.NewGuid().ToString("N");

			try
			{
				File.WriteAllText(path, generated);
			}
			catch (Exception)
			{
				return "";
			}

			return generated;
		}
	}
}
