using System.IO;
using System.Linq;
using System.Threading;
using AgentBridge;
using NUnit.Framework;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;

public class AgentBridgeProbeTests
{
	private const string SavedScenePath = "Assets/AgentBridgeSceneSafetyTest.unity";
	private const string TestScenePath = "Assets/InitTestSceneAgentBridgeSafety.unity";
	private const string MissingMetaSourcePath = "Assets/AgentBridgeMissingMetaProbe.cs";

	[SetUp]
	public void SetUp()
	{
		AgentBridgeSettingsStore.SetDiscardDirtyUntitledScenes(true);
		SceneSafetyGuard.ClearOpenSceneDirtiness();
		EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);
	}

	[TearDown]
	public void TearDown()
	{
		AgentBridgeSettingsStore.SetDiscardDirtyUntitledScenes(true);
		SceneSafetyGuard.ClearOpenSceneDirtiness();
		EditorSceneManager.NewScene(NewSceneSetup.DefaultGameObjects, NewSceneMode.Single);
		AssetDatabase.DeleteAsset(SavedScenePath);
		AssetDatabase.DeleteAsset(TestScenePath);
		string projectRoot = Path.GetDirectoryName(Application.dataPath);
		File.Delete(Path.Combine(projectRoot, MissingMetaSourcePath));
		File.Delete(Path.Combine(projectRoot, MissingMetaSourcePath + ".meta"));
	}

	[Test]
	public void PassingTest()
	{
		Assert.AreEqual(2, 1 + 1);
	}

	[Test]
	public void SceneSafetyGuard_SavesDirtyPersistedScene()
	{
		Scene scene = SceneManager.GetActiveScene();
		Assert.IsTrue(EditorSceneManager.SaveScene(scene, SavedScenePath));

		SceneManager.SetActiveScene(scene);
		new GameObject("PersistedChange");
		EditorSceneManager.MarkSceneDirty(scene);
		Assert.IsTrue(scene.isDirty);

		string error;
		Assert.IsTrue(SceneSafetyGuard.TryPrepareForTask(out error), error);
		Assert.IsFalse(scene.isDirty);
		Assert.IsFalse(string.IsNullOrEmpty(AssetDatabase.AssetPathToGUID(SavedScenePath)));
	}

	[Test]
	public void SceneSafetyGuard_DiscardsDirtyUntitledSceneByDefault()
	{
		AgentBridgeSettingsStore.SetDiscardDirtyUntitledScenes(true);
		Scene scene = SceneManager.GetActiveScene();
		SceneManager.SetActiveScene(scene);
		new GameObject("UnsavedChange");
		EditorSceneManager.MarkSceneDirty(scene);

		string error;
		Assert.IsTrue(SceneSafetyGuard.TryPrepareForTask(out error), error);
		Assert.IsTrue(!scene.IsValid() || !scene.isLoaded);
	}

	[Test]
	public void SceneSafetyGuard_BlocksDirtyUntitledSceneWhenConfigured()
	{
		AgentBridgeSettingsStore.SetDiscardDirtyUntitledScenes(false);
		Scene scene = SceneManager.GetActiveScene();
		SceneManager.SetActiveScene(scene);
		new GameObject("UnsavedChange");
		EditorSceneManager.MarkSceneDirty(scene);

		string error;
		Assert.IsFalse(SceneSafetyGuard.TryPrepareForTask(out error));
		StringAssert.Contains("dirty untitled scene", error);
		Assert.IsTrue(scene.IsValid() && scene.isLoaded && scene.isDirty);
	}

	[Test]
	public void SceneSafetyGuard_DiscardsUnityTestFrameworkScene()
	{
		Scene scene = SceneManager.GetActiveScene();
		Assert.IsTrue(EditorSceneManager.SaveScene(scene, TestScenePath));
		SceneManager.SetActiveScene(scene);
		new GameObject("TestChange");
		EditorSceneManager.MarkSceneDirty(scene);

		string error;
		Assert.IsTrue(SceneSafetyGuard.TryPrepareForTask(out error), error);
		Assert.IsTrue(!scene.IsValid() || !scene.isLoaded);
		string projectRoot = Path.GetDirectoryName(Application.dataPath);
		Assert.IsFalse(File.Exists(Path.Combine(projectRoot, TestScenePath)));
		Assert.DoesNotThrow(() => SceneSafetyGuard.DeleteTestSceneAsset(TestScenePath),
			"Recovery deletion must be idempotent while AssetDatabase still caches the deleted GUID.");
	}

	[Test]
	public void SourceGuardrail_RejectsDirectEditorSceneTransition()
	{
		const string source = @"using UnityEditor.SceneManagement;
public static class DirectSceneTransition
{
	public static void Run()
	{
		EditorSceneManager.OpenScene(""Assets/Scene.unity"");
	}
}";

		CompileResult result = RoslynCompiler.Compile(source, "DirectSceneTransition.cs", "DirectSceneTransition", CancellationToken.None);
		Assert.IsTrue(result.GuardrailRejected);
		Assert.AreEqual("guardrail", result.Diagnostics[0].Code);
	}

	[Test]
	public void SourceGuardrail_AllowsAgentSceneManager()
	{
		const string source = @"using AgentBridge;
public static class SafeSceneTransition
{
	public static void Run()
	{
		AgentSceneManager.OpenScene(""Assets/Scene.unity"");
	}
}";

		CompileResult result = RoslynCompiler.Compile(source, "SafeSceneTransition.cs", "SafeSceneTransition", CancellationToken.None);
		Assert.IsFalse(result.GuardrailRejected);
	}

	[Test]
	public void SourceImportVerifier_RejectsSourceWithoutMeta()
	{
		string projectRoot = Path.GetDirectoryName(Application.dataPath);
		string fullPath = Path.Combine(projectRoot, MissingMetaSourcePath);
		File.WriteAllText(fullPath, "public static class AgentBridgeMissingMetaProbe {}");

		try
		{
			TaskDiagnostic diagnostic = SourceImportVerifier.ValidateProjectSources()
				.FirstOrDefault(item => item.Code == "ABIMPORT001" && item.File == MissingMetaSourcePath);
			Assert.IsNotNull(diagnostic, "A source file without .meta must make compile verification fail.");
		}
		finally
		{
			File.Delete(fullPath);
			File.Delete(fullPath + ".meta");
		}
	}
}
