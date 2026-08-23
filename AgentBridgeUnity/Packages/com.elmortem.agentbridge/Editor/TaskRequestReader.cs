using System.IO;
using UnityEngine;

namespace AgentBridge
{
	public static class TaskRequestReader
	{
		public static bool TryRead(string taskFilePath, out TaskRequest request)
		{
			request = null;
			if (!File.Exists(taskFilePath))
			{
				return false;
			}

			try
			{
				request = JsonUtility.FromJson<TaskRequest>(File.ReadAllText(taskFilePath));
			}
			catch
			{
				request = null;
			}

			return request != null && !string.IsNullOrEmpty(request.Id);
		}
	}
}
