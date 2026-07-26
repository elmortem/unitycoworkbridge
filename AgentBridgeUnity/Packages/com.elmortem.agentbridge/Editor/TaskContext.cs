using System.Collections.Generic;
using System.Threading;

namespace AgentBridge
{
	public class TaskContext
	{
		public string Id;
		public string Kind;
		public CancellationToken CancellationToken;

		private readonly List<string> _artifacts = new List<string>();

		public IReadOnlyList<string> Artifacts
		{
			get { return _artifacts; }
		}

		public string ArtifactsDirectory
		{
			get { return BridgePaths.ArtifactsFor(Id); }
		}

		public void AddArtifact(string relativePath)
		{
			_artifacts.Add(relativePath);
		}
	}
}
