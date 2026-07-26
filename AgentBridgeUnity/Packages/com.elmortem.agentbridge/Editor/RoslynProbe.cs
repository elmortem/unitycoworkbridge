using System.Text;

namespace AgentBridge
{
	public static class RoslynProbe
	{
		public static string Run()
		{
			var builder = new StringBuilder();
			AppendResult(builder, RoslynSourceKind.UnityBuiltin);
			AppendResult(builder, RoslynSourceKind.Project);
			AppendResult(builder, RoslynSourceKind.NuGet);
			AppendResult(builder, RoslynSourceKind.Local);
			return builder.ToString();
		}

		private static void AppendResult(StringBuilder builder, RoslynSourceKind kind)
		{
			if (builder.Length > 0)
			{
				builder.Append("; ");
			}

			RoslynLocation location = RoslynResolver.Probe(kind);
			builder.Append(kind);
			builder.Append('=');
			builder.Append(location.Available ? "ok" : location.Reason);
		}
	}
}
