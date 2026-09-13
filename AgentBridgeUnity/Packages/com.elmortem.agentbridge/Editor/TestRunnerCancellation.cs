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
			return !string.IsNullOrEmpty(RunningReason());
		}

		public static string RunningReason()
		{
			// Unknown framework state is busy: never hand the editor to another writer blindly.
			try
			{
				Type api = typeof(TestRunnerApi);
				MethodInfo probe = api.GetMethod("IsTestRunActive", Flags, null, Type.EmptyTypes, null)
					?? api.GetMethod("IsRunActive", Flags, null, Type.EmptyTypes, null);
				if (probe == null) return "Unity test activity API unavailable";
				if ((bool)probe.Invoke(null, null)) return "Unity test job still active";
				Type holder = Find("UnityEditor.TestTools.TestRunner.TestRun.TestJobDataHolder");
				if (holder != null)
				{
					object instance = holder.GetProperty("instance", Flags).GetValue(null, null);
					IEnumerable jobs = holder.GetField("TestRuns", Flags).GetValue(instance) as IEnumerable;
					if (jobs != null) foreach (object job in jobs)
						if ((bool)job.GetType().GetField("isRunning", Flags).GetValue(job)) return "Unity persisted test job still running: " + job.GetType().GetField("guid", Flags).GetValue(job);
				}
				if (HasRunner("UnityEditor.TestTools.TestRunner.EditModeRunner")) return "Unity EditMode runner object remains";
				if (HasRunner("UnityEngine.TestTools.TestRunner.PlaymodeTestsController")) return "Unity PlayMode controller object remains";
				return "";
			}
			catch (Exception ex) { return "Cannot inspect Unity test state: " + ex.GetBaseException().Message; }
		}

		private static bool HasRunner(string name)
		{
			Type type = Find(name);
			return type != null && Resources.FindObjectsOfTypeAll(type).Length > 0;
		}

		// A task adopted after a package upgrade has no saved Execute() return value.
		// Recover only an unambiguous active job; never cancel every framework job.
		public static string RecoverJobId()
		{
			Type holder = Find("UnityEditor.TestTools.TestRunner.TestRun.TestJobDataHolder");
			if (holder == null) return "";
			object instance = holder.GetProperty("instance", Flags).GetValue(null, null);
			return UniqueRunningJobId(holder.GetField("TestRuns", Flags).GetValue(instance) as IEnumerable);
		}

		public static string UniqueRunningJobId(IEnumerable jobs)
		{
			string id = "";
			if (jobs == null) return id;
			foreach (object job in jobs)
			{
				if (!(bool)job.GetType().GetField("isRunning", Flags).GetValue(job)) continue;
				if (!string.IsNullOrEmpty(id)) throw new InvalidOperationException("Multiple Unity test jobs are active; cannot recover the canceled task's job ID unambiguously.");
				id = (string)job.GetType().GetField("guid", Flags).GetValue(job);
				if (string.IsNullOrEmpty(id)) throw new InvalidOperationException("Active Unity test job has no ID.");
			}
			return id;
		}

		public static string Request(string jobId)
		{
			try
			{
				MethodInfo cancel = typeof(TestRunnerApi).GetMethod("CancelTestRun", Flags, null, new[] { typeof(string) }, null);
				if (cancel != null && string.IsNullOrEmpty(jobId)) jobId = RecoverJobId();
				if (cancel != null && !string.IsNullOrEmpty(jobId))
				{
					object accepted = cancel.Invoke(null, new object[] { jobId });
					return accepted is bool && !(bool)accepted ? "Unity CancelTestRun did not accept cancellation (job " + jobId + "); waiting for framework cleanup." : "";
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
