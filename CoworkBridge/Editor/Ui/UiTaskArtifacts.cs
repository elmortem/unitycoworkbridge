using System;
using System.IO;

namespace CoworkBridge.Ui
{
	public static class UiTaskArtifacts
	{
		public const string RootDirectoryName = "Artifacts";

		public static string GetRootDirectory(string coworkPath)
		{
			return Path.Combine(coworkPath, RootDirectoryName);
		}

		public static string GetTaskDirectory(string coworkPath, string taskId)
		{
			if (string.IsNullOrEmpty(taskId) || taskId != Path.GetFileName(taskId))
			{
				throw new ArgumentException("Invalid task id: " + taskId, "taskId");
			}

			return Path.Combine(GetRootDirectory(coworkPath), taskId);
		}

		public static string GetDumpPath(string coworkPath, string taskId)
		{
			return Path.Combine(EnsureTaskDirectory(coworkPath, taskId), "uidump.json");
		}

		public static string GetScreenshotPath(string coworkPath, string taskId, string outputName)
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

			return Path.Combine(EnsureTaskDirectory(coworkPath, taskId), fileName);
		}

		private static string EnsureTaskDirectory(string coworkPath, string taskId)
		{
			string directory = GetTaskDirectory(coworkPath, taskId);
			Directory.CreateDirectory(directory);
			return directory;
		}
	}
}
