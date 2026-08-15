using System.Collections.Generic;
using UnityEngine.SceneManagement;

namespace AgentBridge
{
	public class SceneDirtyReport
	{
		public List<Scene> DirtySavedScenes = new List<Scene>();
		public List<Scene> DirtyUntitledScenes = new List<Scene>();
		public List<Scene> TransientScenes = new List<Scene>();
		public List<Scene> DirtyUnloadedScenes = new List<Scene>();
		public List<string> TestScenePaths = new List<string>();
		public int OpenSceneCount;
		public bool PrefabStageDirty;
		public string PrefabStageAssetPath;

		public bool IsClean
		{
			get
			{
				return DirtySavedScenes.Count == 0
					&& DirtyUntitledScenes.Count == 0
					&& TransientScenes.Count == 0
					&& DirtyUnloadedScenes.Count == 0
					&& !PrefabStageDirty;
			}
		}
	}
}
