using System.Text.Json;
namespace UnityEngine
{
    public static class JsonUtility
    {
        static readonly JsonSerializerOptions Options = new() { IncludeFields = true };
        public static string ToJson(object value) => JsonSerializer.Serialize(value, value.GetType(), Options);
        public static T FromJson<T>(string value) => JsonSerializer.Deserialize<T>(value, Options);
    }
    public static class Application { public static bool isBatchMode; }
}
namespace UnityEditor
{
    public class InitializeOnLoadAttribute : Attribute { }
    public static class AssemblyReloadEvents { public static event Action beforeAssemblyReload; public static void Reload() => beforeAssemblyReload?.Invoke(); }
    public static class SessionState
    {
        static readonly Dictionary<string,string> Values = new();
        public static void SetString(string key, string value) => Values[key] = value;
        public static string GetString(string key, string fallback) => Values.GetValueOrDefault(key, fallback);
        public static void EraseString(string key) => Values.Remove(key);
        public static void SetBool(string key, bool value) => SetString(key, value.ToString());
        public static bool GetBool(string key, bool fallback) => bool.Parse(GetString(key, fallback.ToString()));
        public static void EraseBool(string key) => EraseString(key);
    }
    public static class EditorApplication
    {
        public static double timeSinceStartup;
        public static bool isCompiling, isUpdating;
        public static event Action update;
        public static void Tick() => update?.Invoke();
    }
    public enum ImportAssetOptions { ForceSynchronousImport }
    public static class AssetDatabase { public static void Refresh(ImportAssetOptions options) { } }
}
namespace UnityEditor.Compilation
{
    public enum CompilerMessageType { Error, Warning }
    public struct CompilerMessage { public CompilerMessageType type; public string message, file; public int line, column; }
    public static class CompilationPipeline
    {
        public static int Requests;
        public static event Action<string,CompilerMessage[]> assemblyCompilationFinished;
        public static event Action<object> compilationStarted, compilationFinished;
        public static void RequestScriptCompilation() => Requests++;
        public static void Start() { UnityEditor.EditorApplication.isCompiling = true; compilationStarted?.Invoke(null); }
        public static void Finish(bool error)
        {
            assemblyCompilationFinished?.Invoke("Game.dll", error ? new[] { new CompilerMessage { type = CompilerMessageType.Error, message = "CS1002: ; expected", file = "Assets/Game.cs" } } : Array.Empty<CompilerMessage>());
            compilationFinished?.Invoke(null);
            UnityEditor.EditorApplication.isCompiling = false;
        }
    }
}
namespace AgentBridge
{
    public static class BridgePaths { public static string ProjectRoot, WorkingRoot; }
    public class TaskDiagnostic { public string Code, Severity, Message, File; public int Line, Column; }
    public class TaskDiagnosticList { public List<TaskDiagnostic> Items; }
    public class TaskRecordOutcome { public string Status; public List<TaskDiagnostic> Diagnostics; public bool ForeignErrors; }
    public static class SourceImportVerifier { public static List<TaskDiagnostic> ValidateProjectSources() => new(); }
    public static class ValidationEvidence { public static string PreparedSources => CompileFingerprint.Current(); }
    public static class CompileInputContext
    {
        public static string[] Roots => new[] { Path.Combine(BridgePaths.ProjectRoot, "Assets"), Path.Combine(BridgePaths.ProjectRoot, "Packages"), Path.Combine(BridgePaths.ProjectRoot, "ProjectSettings") };
        public static string Context => "test-editor";
    }
}
