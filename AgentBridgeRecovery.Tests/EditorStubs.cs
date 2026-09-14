// Minimal external dependencies for executing the real recovery implementation.
// No recovery decisions are duplicated here; tests drive callbacks and observe effects.
using System.Text.Json;

namespace UnityEngine
{
	public static class Application { public static bool isBatchMode; }
	public static class Debug { public static void LogException(Exception ex) { } }
	public static class JsonUtility
	{
		static readonly JsonSerializerOptions Options = new() { IncludeFields = true };
		public static T FromJson<T>(string text) => JsonSerializer.Deserialize<T>(text, Options);
		public static string ToJson<T>(T value, bool pretty = false) => JsonSerializer.Serialize(value, Options);
	}
}
namespace UnityEngine.SceneManagement
{
	public struct Scene { public string path; }
	public static class SceneManager { public static Scene GetActiveScene() => new() { path = "Original.unity" }; }
}
namespace UnityEditor
{
	public enum PlayModeStateChange { ExitingPlayMode, EnteredEditMode }
	public static class SessionState
	{
		static readonly Dictionary<string, string> Values = new();
		public static string GetString(string key, string fallback) => Values.TryGetValue(key, out var value) ? value : fallback;
		public static void SetString(string key, string value) => Values[key] = value;
		public static void EraseString(string key) => Values.Remove(key);
		public static void Clear() => Values.Clear();
	}
	public static class EditorApplication
	{
		public static bool isPlaying, isPlayingOrWillChangePlaymode;
		public static event Action<PlayModeStateChange> playModeStateChanged;
		public static event Action update, delayCall;
		public static void ExitPlaymode() { isPlaying = isPlayingOrWillChangePlaymode = false; playModeStateChanged?.Invoke(PlayModeStateChange.EnteredEditMode); }
		public static void Frame() { update?.Invoke(); var delayed = delayCall; delayCall = null; delayed?.Invoke(); }
		public static void Reset() { isPlaying = isPlayingOrWillChangePlaymode = false; update = delayCall = null; playModeStateChanged = null; }
	}
}
namespace UnityEditor.SceneManagement
{
	public struct SceneSetup { }
	public enum NewSceneSetup { DefaultGameObjects }
	public enum NewSceneMode { Single }
	public static class EditorSceneManager
	{
		public static int Restores;
		public static Action OnRestore;
		public static SceneSetup[] GetSceneManagerSetup() => new SceneSetup[1];
		public static void RestoreSceneManagerSetup(SceneSetup[] setup) { Restores++; OnRestore?.Invoke(); }
		public static void NewScene(NewSceneSetup setup, NewSceneMode mode) { Restores++; OnRestore?.Invoke(); }
	}
}
namespace AgentBridge
{
	public class TestRunResult { public bool aborted; public string message; }
	public class TaskRecord { public string Status; public List<string> Logs = new(); }
	public static class TaskJournal
	{
		public static TaskRecord Owner;
		public static bool TryRead(string id, out TaskRecord record) { record = Owner; return record != null; }
		public static void Write(TaskRecord record) => Owner = record;
	}
	public static class TaskCoordinator { public static bool IsTerminal(string status) => status is "success" or "canceled" or "runtime_error"; }
	public static class TestRunnerCancellation
	{
		public static bool Running;
		public static int Requests;
		public static bool IsRunning() => Running;
		public static bool PlayRunnerStarted() => Running;
		public static string Request(string id) { Requests++; return "requested"; }
		public static string RunningReason() => "test executor";
	}
	public static class BridgePaths { public static string PlayModeSceneStateFile; }
	public static class AgentTestRunner
	{
		public const string CoordinatorTestTaskKey = "test";
		public static Action OnFinalize;
		public static int CancellationFinalizations;
		public static string CancellationReason;
		public static void FinalizeRecoveredPlayModeRun(string id, TestRunResult result, string error) => OnFinalize?.Invoke();
		public static void FinalizeCancellation(string id, string outcome, string reason)
		{
			CancellationFinalizations++; CancellationReason = reason; TaskJournal.Owner.Status = outcome;
		}
	}
	public static class SceneDirtyWatcher { public static void Disarm(string id) { } }
	public static class SceneSafetyGuard
	{
		public static bool TryPrepareForTask(out string error) { error = null; return true; }
		public static void ClearOpenSceneDirtiness() { }
		public static bool IsTestScenePath(string path) => false;
		public static void DeleteTestSceneAsset(string path) { }
		public static void DeleteAllTestSceneAssets() { }
	}
	public static class SceneSetupStateConverter
	{
		public static SceneSetupState[] ToState(UnityEditor.SceneManagement.SceneSetup[] setup) => new[] { new SceneSetupState { Path = "Original.unity", IsLoaded = true, IsActive = true } };
		public static UnityEditor.SceneManagement.SceneSetup[] FromState(SceneSetupState[] setup) => new UnityEditor.SceneManagement.SceneSetup[setup.Length];
	}
}
