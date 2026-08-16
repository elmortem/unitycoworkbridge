using System.IO;
using UnityEngine;

namespace AgentBridge
{
	public static class PlaySessionStore
	{
		public static bool Exists
		{
			get { return File.Exists(BridgePaths.PlaySessionFile); }
		}

		public static PlaySessionState Read()
		{
			string path = BridgePaths.PlaySessionFile;
			if (!File.Exists(path))
			{
				return null;
			}

			try
			{
				return JsonUtility.FromJson<PlaySessionState>(File.ReadAllText(path));
			}
			catch
			{
				// A half-written or hand-edited file must not wedge the editor in play mode:
				// a null state reads as "no session" and stopplay can still exit the editor.
				return null;
			}
		}

		public static void Write(PlaySessionState state)
		{
			string path = BridgePaths.PlaySessionFile;
			string temporaryPath = path + ".new";
			File.WriteAllText(temporaryPath, JsonUtility.ToJson(state, true));

			if (!File.Exists(path))
			{
				File.Move(temporaryPath, path);
				return;
			}

			try
			{
				File.Replace(temporaryPath, path, null);
			}
			catch
			{
				File.Copy(temporaryPath, path, true);
				File.Delete(temporaryPath);
			}
		}

		public static void Delete()
		{
			string path = BridgePaths.PlaySessionFile;
			if (File.Exists(path))
			{
				File.Delete(path);
			}

			string temporaryPath = path + ".new";
			if (File.Exists(temporaryPath))
			{
				File.Delete(temporaryPath);
			}
		}
	}
}
