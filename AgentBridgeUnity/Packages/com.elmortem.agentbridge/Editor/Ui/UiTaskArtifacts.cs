using System;
using System.IO;

namespace AgentBridge.Ui
{
	public static class UiTaskArtifacts
	{
		public static string GetDumpPath(TaskContext context)
		{
			return Path.Combine(EnsureDirectory(context), "uidump.json");
		}

		public static string GetScreenshotPath(TaskContext context, string outputName)
		{
			string fileName = string.IsNullOrWhiteSpace(outputName) ? "shot.png" : outputName.Trim();
			if (Path.IsPathRooted(fileName) || fileName.IndexOf('/') >= 0 || fileName.IndexOf('\\') >= 0)
			{
				throw new ArgumentException("UI shot 'output' must be a file name without a directory: " + outputName, "outputName");
			}

			if (fileName.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0)
			{
				throw new ArgumentException("UI shot 'output' contains invalid file name characters: " + outputName, "outputName");
			}

			if (!fileName.EndsWith(".png", StringComparison.OrdinalIgnoreCase))
			{
				throw new ArgumentException("UI shot 'output' must use the .png extension: " + outputName, "outputName");
			}

			return Path.Combine(EnsureDirectory(context), fileName);
		}

		private static string EnsureDirectory(TaskContext context)
		{
			string directory = context.ArtifactsDirectory;
			Directory.CreateDirectory(directory);
			return directory;
		}
	}
}
