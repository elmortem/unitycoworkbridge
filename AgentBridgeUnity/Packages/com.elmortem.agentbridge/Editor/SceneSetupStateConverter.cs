using UnityEditor.SceneManagement;

namespace AgentBridge
{
	public static class SceneSetupStateConverter
	{
		public static SceneSetupState[] ToState(SceneSetup[] setup)
		{
			if (setup == null)
			{
				return new SceneSetupState[0];
			}

			var result = new SceneSetupState[setup.Length];
			for (int i = 0; i < setup.Length; i++)
			{
				result[i] = new SceneSetupState
				{
					Path = setup[i].path,
					IsLoaded = setup[i].isLoaded,
					IsActive = setup[i].isActive,
					IsSubScene = setup[i].isSubScene
				};
			}

			return result;
		}

		public static SceneSetup[] FromState(SceneSetupState[] setup)
		{
			if (setup == null)
			{
				return new SceneSetup[0];
			}

			var result = new SceneSetup[setup.Length];
			for (int i = 0; i < setup.Length; i++)
			{
				result[i] = new SceneSetup
				{
					path = setup[i].Path,
					isLoaded = setup[i].IsLoaded,
					isActive = setup[i].IsActive,
					isSubScene = setup[i].IsSubScene
				};
			}

			return result;
		}
	}
}
