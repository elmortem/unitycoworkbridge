using System.Runtime.InteropServices;
using UnityEngine;

namespace AgentBridge
{
	public static class HostPlatform
	{
		public static string Name
		{
			get
			{
				switch (Application.platform)
				{
					case RuntimePlatform.WindowsEditor:
						return "windows";
					case RuntimePlatform.OSXEditor:
						return "macos";
					case RuntimePlatform.LinuxEditor:
						return "linux";
					default:
						return "unknown";
				}
			}
		}

		public static string SandboxRuntimeIdentifier
		{
			get
			{
				if (RuntimeInformation.OSArchitecture == Architecture.Arm64)
				{
					return "linux-arm64";
				}

				return "linux-x64";
			}
		}
	}
}
