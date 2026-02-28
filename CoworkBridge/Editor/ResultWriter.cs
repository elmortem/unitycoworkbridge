using System.IO;
using UnityEngine;

namespace CoworkBridge
{
	public static class ResultWriter
	{
		public static void Write(TaskResult result, string coworkPath)
		{
			string resultPath = Path.Combine(coworkPath, "result_" + result.id + ".json");
			string donePath = Path.Combine(coworkPath, "result_" + result.id + ".done");

			string json = JsonUtility.ToJson(result, true);
			File.WriteAllText(resultPath, json);

			File.WriteAllText(donePath, "");

			Debug.Log("[CoworkBridge] Result written: " + resultPath);
		}
	}
}
