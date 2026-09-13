using System;
using System.Collections;
using System.Reflection;
using UnityEditor.TestTools.TestRunner.Api;
using UnityEngine;

namespace AgentBridge
{
	// Framework 1.1 has no public cancellation API. Keep its compatibility path isolated.
	public static class TestRunnerCancellation
	{
		private const BindingFlags Flags = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static | BindingFlags.FlattenHierarchy;
		private static Type Find(string name)
		{
			foreach (Assembly assembly in AppDomain.CurrentDomain.GetAssemblies())
			{
				Type type = assembly.GetType(name);
				if (type != null) return type;
			}
			return null;
		}

		public static bool IsRunning()
		{
			// Unknown framework state is busy: never hand the editor to another writer blindly.
			try
			{
				Type api = typeof(TestRunnerApi);
				MethodInfo probe = api.GetMethod("IsTestRunActive", Flags, null, Type.EmptyTypes, null)
					?? api.GetMethod("IsRunActive", Flags, null, Type.EmptyTypes, null);
				if (probe == null) return true;
				if ((bool)probe.Invoke(null, null)) return true;
				Type holder = Find("UnityEditor.TestTools.TestRunner.TestRun.TestJobDataHolder");
				if (holder != null)
				{
					object instance = holder.GetProperty("instance", Flags).GetValue(null, null);
					IEnumerable jobs = holder.GetField("TestRuns", Flags).GetValue(instance) as IEnumerable;
					if (jobs != null) foreach (object job in jobs)
						if ((bool)job.GetType().GetField("isRunning", Flags).GetValue(job)) return true;
				}
				return HasRunner("UnityEditor.TestTools.TestRunner.EditModeRunner")
					|| HasRunner("UnityEngine.TestTools.TestRunner.PlaymodeTestsController");
			}
			catch { return true; }
		}

		private static bool HasRunner(string name)
		{
			Type type = Find(name);
			return type != null && Resources.FindObjectsOfTypeAll(type).Length > 0;
		}

		public static string Request(string jobId)
		{
			try
			{
				MethodInfo cancel = typeof(TestRunnerApi).GetMethod("CancelTestRun", Flags, null, new[] { typeof(string) }, null);
				if (cancel != null && !string.IsNullOrEmpty(jobId))
				{
					cancel.Invoke(null, new object[] { jobId });
					return "";
				}
				foreach (string name in new[] { "UnityEditor.TestTools.TestRunner.EditModeRunner", "UnityEngine.TestTools.TestRunner.PlaymodeTestsController" })
				{
					Type type = Find(name);
					if (type == null) continue;
					foreach (UnityEngine.Object controller in Resources.FindObjectsOfTypeAll(type))
					{
						object runner = type.GetField("m_Runner", Flags).GetValue(controller);
						if (runner != null) runner.GetType().GetMethod("StopRun", Flags).Invoke(runner, null);
					}
				}
				return "";
			}
			catch (Exception ex) { return ex.GetBaseException().Message; }
		}

		public static bool PlayRunnerStarted()
		{
			Type type = Find("UnityEngine.TestTools.TestRunner.PlaymodeTestsController");
			if (type == null) return false;
			foreach (UnityEngine.Object controller in Resources.FindObjectsOfTypeAll(type))
				if (type.GetField("m_Runner", Flags).GetValue(controller) != null) return true;
			return false;
		}
	}
}
