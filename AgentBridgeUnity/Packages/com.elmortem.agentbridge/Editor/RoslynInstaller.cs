using System;
using System.IO;
using System.IO.Compression;
using UnityEngine.Networking;

namespace AgentBridge
{
	public static class RoslynInstaller
	{
		private struct Package
		{
			public readonly string Id;
			public readonly string Version;

			public Package(string id, string version)
			{
				Id = id;
				Version = version;
			}
		}

		private static readonly Package[] Packages =
		{
			new Package("microsoft.codeanalysis.common", "4.9.2"),
			new Package("microsoft.codeanalysis.csharp", "4.9.2"),
			new Package("system.collections.immutable", "8.0.0"),
			new Package("system.reflection.metadata", "8.0.0"),
			new Package("system.runtime.compilerservices.unsafe", "6.0.0"),
			new Package("system.memory", "4.5.5"),
			new Package("system.buffers", "4.5.1"),
			new Package("system.numerics.vectors", "4.5.0"),
			new Package("system.threading.tasks.extensions", "4.5.4"),
			new Package("system.text.encoding.codepages", "8.0.0")
		};

		public static bool IsBusy { get; private set; }
		public static event Action<bool, string> Completed;

		public static void Download()
		{
			if (IsBusy)
			{
				return;
			}

			IsBusy = true;
			DownloadNext(0);
		}

		private static void DownloadNext(int index)
		{
			if (index >= Packages.Length)
			{
				IsBusy = false;
				RaiseCompleted(true, "installed");
				return;
			}

			Package package = Packages[index];
			string url = string.Format("https://api.nuget.org/v3-flatcontainer/{0}/{1}/{0}.{1}.nupkg", package.Id, package.Version);

			UnityWebRequest request = UnityWebRequest.Get(url);
			request.downloadHandler = new DownloadHandlerBuffer();

			UnityWebRequestAsyncOperation operation = request.SendWebRequest();
			operation.completed += _ => OnDownloadCompleted(request, package, index);
		}

		private static void OnDownloadCompleted(UnityWebRequest request, Package package, int index)
		{
			if (request.result != UnityWebRequest.Result.Success)
			{
				IsBusy = false;
				RaiseCompleted(false, package.Id + ": " + request.error);
				request.Dispose();
				return;
			}

			try
			{
				ExtractPackage(request.downloadHandler.data);
			}
			catch (Exception ex)
			{
				IsBusy = false;
				RaiseCompleted(false, package.Id + ": " + ex.Message);
				request.Dispose();
				return;
			}

			request.Dispose();
			DownloadNext(index + 1);
		}

		private static void ExtractPackage(byte[] nupkgData)
		{
			string targetDirectory = BridgePaths.Roslyn;

			using (var stream = new MemoryStream(nupkgData))
			using (var archive = new ZipArchive(stream, ZipArchiveMode.Read))
			{
				foreach (ZipArchiveEntry entry in archive.Entries)
				{
					string normalized = entry.FullName.Replace('\\', '/');
					if (normalized.IndexOf("lib/netstandard2.0/", StringComparison.OrdinalIgnoreCase) < 0)
					{
						continue;
					}

					if (!normalized.EndsWith(".dll", StringComparison.OrdinalIgnoreCase))
					{
						continue;
					}

					string fileName = Path.GetFileName(normalized);
					string destination = Path.Combine(targetDirectory, fileName);
					entry.ExtractToFile(destination, true);
				}
			}
		}

		private static void RaiseCompleted(bool success, string message)
		{
			Action<bool, string> handler = Completed;
			if (handler != null)
			{
				handler(success, message);
			}
		}
	}
}
