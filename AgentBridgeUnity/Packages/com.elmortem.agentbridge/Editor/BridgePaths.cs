using System.IO;
using UnityEngine;

namespace AgentBridge
{
	public static class BridgePaths
	{
		public static string ProjectRoot
		{
			get { return Path.GetDirectoryName(Application.dataPath); }
		}

		public static string WorkingRoot
		{
			get { return EnsureDirectory(Path.Combine(ProjectRoot, "Library", "AgentBridge")); }
		}

		public static string Inbox
		{
			get { return EnsureDirectory(Path.Combine(WorkingRoot, "Inbox")); }
		}

		public static string Journal
		{
			get { return EnsureDirectory(Path.Combine(WorkingRoot, "Journal")); }
		}

		public static string ArtifactsRoot
		{
			get { return EnsureDirectory(Path.Combine(WorkingRoot, "Artifacts")); }
		}

		public static string ArtifactsFor(string taskId)
		{
			return EnsureDirectory(Path.Combine(ArtifactsRoot, taskId));
		}

		public static string StatusFile
		{
			get { return Path.Combine(WorkingRoot, "status.json"); }
		}

		public static string HeartbeatFile
		{
			get { return Path.Combine(WorkingRoot, "heartbeat"); }
		}

		public static string ProjectIdFile
		{
			get { return Path.Combine(WorkingRoot, "project-id"); }
		}

		public static string PlayModeSceneStateFile
		{
			get { return Path.Combine(WorkingRoot, "pending-playmode-scene.json"); }
		}

		public static string PlaySessionFile
		{
			get { return Path.Combine(WorkingRoot, "play-session.json"); }
		}

		public static string SchedulerStateFile
		{
			get { return Path.Combine(WorkingRoot, "scheduler-state.json"); }
		}

		public static string CliRoot
		{
			get { return EnsureDirectory(Path.Combine(WorkingRoot, "cli")); }
		}

		public static string LegacyInbox
		{
			get { return Path.Combine(Application.dataPath, "Editor", "AgentBridge"); }
		}

		private static string EnsureDirectory(string path)
		{
			if (!Directory.Exists(path))
			{
				Directory.CreateDirectory(path);
			}

			return path;
		}
	}
}
