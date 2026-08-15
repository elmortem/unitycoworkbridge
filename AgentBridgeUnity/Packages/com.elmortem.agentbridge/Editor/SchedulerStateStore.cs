using System;
using System.Diagnostics;
using System.IO;
using UnityEngine;

namespace AgentBridge
{
	public static class SchedulerStateStore
	{
		private static SchedulerState _state;

		public static SchedulerState State
		{
			get
			{
				if (_state == null)
				{
					_state = LoadOrCreate();
				}

				return _state;
			}
		}

		public static void Save()
		{
			SchedulerState state = State;

			string path = BridgePaths.SchedulerStateFile;
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

		private static SchedulerState LoadOrCreate()
		{
			SchedulerState state = Read();
			if (state == null)
			{
				state = new SchedulerState();
			}

			if (state.Contexts == null)
			{
				state.Contexts = new System.Collections.Generic.List<SessionContext>();
			}

			int pid = Process.GetCurrentProcess().Id;
			if (state.EditorPid == pid)
			{
				return state;
			}

			// A restarted editor holds no lease: the sessions that owned it are gone with their
			// CLI clients. Accumulated scene contexts stay, they are what a returning session needs.
			state.EditorPid = pid;
			state.HolderAgentSessionId = "";
			state.HolderLastActivityUtc = "";
			state.ContentionStartedUtc = "";
			state.HolderContextRestored = true;

			_state = state;
			Save();
			return state;
		}

		private static SchedulerState Read()
		{
			try
			{
				string path = BridgePaths.SchedulerStateFile;
				if (!File.Exists(path))
				{
					return null;
				}

				return JsonUtility.FromJson<SchedulerState>(File.ReadAllText(path));
			}
			catch
			{
				return null;
			}
		}
	}
}
