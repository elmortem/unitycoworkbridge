using System.IO;
using UnityEngine;

namespace CoworkBridge
{
	public static class CoworkBridgeSettingsStore
	{
		private const string FileName = "CoworkBridge.json";

		public static bool IsEnabled()
		{
			CoworkBridgeSettings settings = Load();
			return settings.Enabled;
		}

		public static void SetEnabled(bool value)
		{
			CoworkBridgeSettings settings = Load();
			settings.Enabled = value;
			Save(settings);
		}

		private static CoworkBridgeSettings Load()
		{
			string path = GetSettingsPath();

			if (!File.Exists(path))
			{
				return new CoworkBridgeSettings();
			}

			string json = File.ReadAllText(path);
			CoworkBridgeSettings settings = JsonUtility.FromJson<CoworkBridgeSettings>(json);

			if (settings == null)
			{
				return new CoworkBridgeSettings();
			}

			return settings;
		}

		private static void Save(CoworkBridgeSettings settings)
		{
			string path = GetSettingsPath();
			string json = JsonUtility.ToJson(settings, true);
			File.WriteAllText(path, json);
		}

		private static string GetSettingsPath()
		{
			string projectRoot = Path.GetDirectoryName(Application.dataPath);
			string projectSettingsPath = Path.Combine(projectRoot, "ProjectSettings");
			return Path.Combine(projectSettingsPath, FileName);
		}
	}
}
