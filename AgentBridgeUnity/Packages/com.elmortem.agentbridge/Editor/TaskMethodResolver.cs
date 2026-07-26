using System;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;

namespace AgentBridge
{
	public static class TaskMethodResolver
	{
		public static bool TryResolve(Assembly assembly, string taskId, out MethodInfo method, out bool needsCancellationToken, out string error)
		{
			method = null;
			needsCancellationToken = false;
			error = null;

			Type type = assembly.GetType(taskId);
			if (type == null)
			{
				error = "Class not found: " + taskId;
				return false;
			}

			MethodInfo withToken = type.GetMethod("Run", BindingFlags.Public | BindingFlags.Static, null, new[] { typeof(CancellationToken) }, null);
			MethodInfo withoutToken = type.GetMethod("Run", BindingFlags.Public | BindingFlags.Static, null, Type.EmptyTypes, null);

			MethodInfo chosen = withToken != null ? withToken : withoutToken;
			if (chosen == null)
			{
				error = "Method Run not found in class " + taskId;
				return false;
			}

			if (chosen.ReturnType != typeof(Task<string>))
			{
				error = "Run must have signature: public static Task<string> Run()";
				return false;
			}

			method = chosen;
			needsCancellationToken = chosen == withToken;
			return true;
		}
	}
}
