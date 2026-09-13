using AgentBridge;
using UnityEditor;
using UnityEditor.Compilation;

var root = Path.Combine(Path.GetTempPath(), "BridgeCompile_" + Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(root);
BridgePaths.ProjectRoot = root;
BridgePaths.WorkingRoot = root;
foreach (var dir in new[] { "Assets", "Packages", "ProjectSettings" }) Directory.CreateDirectory(Path.Combine(root, dir));
var source = Path.Combine(root, "Assets/Game.cs");
File.WriteAllText(source, "class Game {}");
var failures = new List<string>();
int total = 0;
void Check(string name, Action action) { total++; try { action(); Console.WriteLine("PASS " + name); } catch(Exception e) { failures.Add(name); Console.WriteLine("FAIL " + name + ": " + e.GetBaseException().Message); } }
void Expect(bool value, string message) { if (!value) throw new Exception(message); }
bool Completed() => (bool?)typeof(CompileTaskExecutor).GetMethod("HasCompleted")?.Invoke(null, null) == true;
try
{
    Check("compiler errors finish without waiting for reload watchdog", () => {
        CompileTaskExecutor.BeginImport("broken"); CompileTaskExecutor.RequestCompilation();
        CompilationPipeline.Start(); CompilationPipeline.Finish(true);
        Expect(Completed(), "compilationFinished must make the result ready immediately");
        Expect(CompileTaskExecutor.ConsumePending("broken").Status == "compiler_error", "errors retained");
    });
    Check("no completion event must never become success", () => {
        CompileTaskExecutor.BeginImport("missing_event"); CompileTaskExecutor.RequestCompilation();
        EditorApplication.timeSinceStartup += 25;
        Expect(CompileTaskExecutor.ConsumePending("missing_event").Status != "success", "silence is not successful compilation");
    });
    Check("unchanged bytes with touched timestamp reuse fingerprint", () => {
        var before = CompileFingerprint.Capture(root);
        File.SetLastWriteTimeUtc(source, DateTime.UtcNow.AddMinutes(2));
        Expect(before == CompileFingerprint.Capture(root), "touching a source must not request another compile");
    });
    Check("external packages and build context invalidate fingerprint", () => {
        var method = typeof(CompileFingerprint).GetMethod("Capture", new[] { typeof(string), typeof(string[]), typeof(string) });
        Expect(method != null, "fingerprint needs resolved roots and compilation context");
        var package = Path.Combine(root, "external"); Directory.CreateDirectory(package);
        var file = Path.Combine(package, "Local.cs"); File.WriteAllText(file, "class A {}");
        string Hash(string context) => (string)method.Invoke(null, new object[] { root, new[] { Path.Combine(root,"Assets"), package }, context });
        var before = Hash("target=A"); var stamp = File.GetLastWriteTimeUtc(file);
        File.WriteAllText(file, "class B {}"); File.SetLastWriteTimeUtc(file, stamp);
        Expect(before != Hash("target=A"), "same-size package edit must invalidate");
        Expect(Hash("target=A") != Hash("target=B"), "target switch must invalidate");
    });
    Check("fresh overlapping requests share a newly completed cycle", () => {
        var method = typeof(CompileCacheStore).GetMethod("CanReuse");
        Expect(method != null, "fresh overlap reuse policy missing");
        var now = DateTime.UtcNow;
        var entry = new CompileCacheEntry { Fingerprint = "abc", Status = "success", FinishedAtUtc = now.ToString("o") };
        bool Reuse(bool fresh, DateTime submitted, string fingerprint) => (bool)method.Invoke(null, new object[] { entry, fingerprint, fresh, submitted });
        Expect(Reuse(true, now.AddSeconds(-1), "abc"), "waiting fresh request should share completion");
        Expect(!Reuse(true, now.AddSeconds(1), "abc"), "later fresh request must run again");
        Expect(!Reuse(false, now, "changed"), "changed sources must not reuse");
        entry.Status = "compiler_error";
        Expect(Reuse(false, now, "abc"), "unchanged errors must be reusable without reload");
    });
    Check("failed cycle publishes reusable diagnostics without reload", () => {
        CompileTaskExecutor.BeginImport("errors_cached"); CompileTaskExecutor.RequestCompilation();
        CompilationPipeline.Start(); CompilationPipeline.Finish(true); EditorApplication.Tick();
        Expect(CompileCacheStore.TryRead(out var entry) && entry.Status == "compiler_error", "no-reload error must seed cache");
        Expect(entry.Diagnostics.Count == 1 && entry.SourceTaskId == "errors_cached", "cache retains diagnostic and source identity");
        CompileTaskExecutor.ConsumePending("errors_cached");
    });
    Check("automatic cycle supplies ordinary checks", () => {
        CompilationPipeline.Start(); CompilationPipeline.Finish(false); EditorApplication.Tick();
        Expect(CompileCacheStore.TryRead(out var entry) && entry.Status == "success", "automatic compile must populate cache");
        Expect(entry.SourceTaskId.StartsWith("UnityCompile_"), "automatic cycle has distinct identity");
    });
    Check("refresh-triggered cycle is not requested twice", () => {
        CompileTaskExecutor.BeginImport("refresh");
        CompilationPipeline.Start(); CompilationPipeline.Finish(false);
        var count = CompilationPipeline.Requests; CompileTaskExecutor.RequestCompilation();
        Expect(CompilationPipeline.Requests == count, "a completed refresh cycle already checked these sources");
        CompileTaskExecutor.ConsumePending("refresh");
    });
    Check("changing source during cycle cannot seed cache", () => {
        File.Delete(Path.Combine(root, "compile-cache.json"));
        CompilationPipeline.Start(); File.WriteAllText(source, "class Changed {}");
        CompilationPipeline.Finish(false); EditorApplication.Tick();
        Expect(!CompileCacheStore.TryRead(out _), "changed inputs are not cacheable");
    });
    Check("changed-and-restored input cannot seed cache", () => {
        File.Delete(Path.Combine(root, "compile-cache.json"));
        CompilationPipeline.Start(); var original = File.ReadAllText(source);
        File.WriteAllText(source, "class Temporary {}"); Thread.Sleep(100);
        File.WriteAllText(source, original); Thread.Sleep(100);
        CompilationPipeline.Finish(false); EditorApplication.Tick();
        Expect(!CompileCacheStore.TryRead(out _), "observer must reject a restored input");
    });
    Check("source edit after compile cannot return current success", () => {
        CompileTaskExecutor.BeginImport("late_edit"); CompileTaskExecutor.RequestCompilation();
        CompilationPipeline.Start(); CompilationPipeline.Finish(false);
        File.WriteAllText(source, "class Later {}");
        Expect(CompileTaskExecutor.ConsumePending("late_edit").Status == "stale_input", "a source changed before finalization");
    });
}
finally { Directory.Delete(root, true); }
Console.WriteLine($"Compile regression tests: {total - failures.Count}/{total} passed");
return failures.Count == 0 ? 0 : 1;
