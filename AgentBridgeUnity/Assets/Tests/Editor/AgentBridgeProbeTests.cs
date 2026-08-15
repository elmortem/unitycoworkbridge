using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
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
	private const string PrefabProbePath = "Assets/AgentBridgePrefabStageProbe.prefab";
	private const string WatcherLogsKey = SceneDirtyWatcher.OwnerTaskKey + "_Logs";

	private SceneSetup[] _originalSetup;
	private string _originalWatcherOwner;
	private string _originalWatcherLogs;

	[SetUp]
	public void SetUp()
	{
		_originalSetup = EditorSceneManager.GetSceneManagerSetup();
		_originalWatcherOwner = SessionState.GetString(SceneDirtyWatcher.OwnerTaskKey, "");
		_originalWatcherLogs = SessionState.GetString(WatcherLogsKey, "");

		AgentBridgeSettingsStore.SetDiscardDirtyUntitledScenes(true);
		AgentBridgeSettingsStore.SetSaveDirtyScenes(true);
		SceneSafetyGuard.ClearOpenSceneDirtiness();
		EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);
	}

	[TearDown]
	public void TearDown()
	{
		AgentBridgeSettingsStore.SetDiscardDirtyUntitledScenes(true);
		AgentBridgeSettingsStore.SetSaveDirtyScenes(true);
		CloseProbePrefabStage();
		SceneSafetyGuard.ClearOpenSceneDirtiness();
		RestoreOriginalSetup();
		AssetDatabase.DeleteAsset(SavedScenePath);
		AssetDatabase.DeleteAsset(TestScenePath);
		AssetDatabase.DeleteAsset(PrefabProbePath);
		string projectRoot = Path.GetDirectoryName(Application.dataPath);
		File.Delete(Path.Combine(projectRoot, MissingMetaSourcePath));
		File.Delete(Path.Combine(projectRoot, MissingMetaSourcePath + ".meta"));

		RestoreWatcherState();
	}

	private void RestoreOriginalSetup()
	{
		// RestoreSceneManagerSetup rejects entries without a path, so an untitled original
		// setup falls back to a fresh scene. No interactive save API is used either way.
		bool restorable = _originalSetup != null
			&& _originalSetup.Length > 0
			&& _originalSetup.All(setup => !string.IsNullOrEmpty(setup.path) && File.Exists(setup.path));

		if (restorable)
		{
			EditorSceneManager.RestoreSceneManagerSetup(_originalSetup);
			return;
		}

		EditorSceneManager.NewScene(NewSceneSetup.DefaultGameObjects, NewSceneMode.Single);
	}

	private void RestoreWatcherState()
	{
		SessionState.EraseString(SceneDirtyWatcher.OwnerTaskKey);
		SessionState.EraseString(WatcherLogsKey);

		if (!string.IsNullOrEmpty(_originalWatcherOwner))
		{
			SceneDirtyWatcher.Arm(_originalWatcherOwner);
		}

		if (!string.IsNullOrEmpty(_originalWatcherLogs))
		{
			SessionState.SetString(WatcherLogsKey, _originalWatcherLogs);
		}
	}

	private static void PumpWatcherUpdate()
	{
		// EditorApplication.update cannot be invoked wholesale from inside a test: the Test
		// Framework job runner is subscribed to it and would re-enter the running job.
		MethodInfo update = typeof(SceneDirtyWatcher).GetMethod("OnUpdate", BindingFlags.NonPublic | BindingFlags.Static);
		Assert.IsNotNull(update, "SceneDirtyWatcher must keep its editor update handler.");
		update.Invoke(null, null);
	}

	private static bool IsWatcherSubscribedToUpdate()
	{
		FieldInfo field = typeof(EditorApplication).GetField("update",
			BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static);
		if (field == null)
		{
			return false;
		}

		Delegate handler = field.GetValue(null) as Delegate;
		if (handler == null)
		{
			return false;
		}

		return handler.GetInvocationList().Any(entry => entry.Method.DeclaringType == typeof(SceneDirtyWatcher));
	}

	private static Scene CreateDirtySavedScene()
	{
		Scene scene = SceneManager.GetActiveScene();
		Assert.IsTrue(EditorSceneManager.SaveScene(scene, SavedScenePath));
		SceneManager.SetActiveScene(scene);
		new GameObject("PersistedChange");
		EditorSceneManager.MarkSceneDirty(scene);
		Assert.IsTrue(scene.isDirty);
		return scene;
	}

	private static void CloseProbePrefabStage()
	{
		PrefabStage stage = PrefabStageUtility.GetCurrentPrefabStage();
		if (stage == null)
		{
			return;
		}

		// Leaving a dirty prefab stage is what opens Unity's own save prompt, so the stage is
		// brought to a clean state by the guard itself before the main stage is restored.
		string error;
		SceneSafetyGuard.TryPrepareForTask(out error);
		StageUtility.GoToMainStage();
	}

	private static PrefabStage OpenDirtyProbePrefabStage()
	{
		var root = new GameObject("AgentBridgePrefabStageProbe");
		PrefabUtility.SaveAsPrefabAsset(root, PrefabProbePath);
		UnityEngine.Object.DestroyImmediate(root);

		PrefabStage stage = PrefabStageUtility.OpenPrefab(PrefabProbePath);
		Assert.IsNotNull(stage, "The probe prefab stage must open.");

		var child = new GameObject("StageChange");
		child.transform.SetParent(stage.prefabContentsRoot.transform);
		EditorSceneManager.MarkSceneDirty(stage.scene);
		Assert.IsTrue(stage.scene.isDirty);
		return stage;
	}

	private static void AssertGuardrailRejects(string className, string body)
	{
		string source = "public static class " + className + "\n{\n\tpublic static void Run()\n\t{\n\t\t" + body + "\n\t}\n}";
		CompileResult result = RoslynCompiler.Compile(source, className + ".cs", className, CancellationToken.None);
		Assert.IsTrue(result.GuardrailRejected, className + " must be rejected by the guardrail.");
		Assert.AreEqual("guardrail", result.Diagnostics[0].Code);
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
	public void SceneSafetyGuard_BlocksDirtyPersistedSceneWhenConfigured()
	{
		AgentBridgeSettingsStore.SetSaveDirtyScenes(false);
		Scene scene = CreateDirtySavedScene();

		string error;
		Assert.IsFalse(SceneSafetyGuard.TryPrepareForTask(out error));
		StringAssert.Contains(SavedScenePath, error);
		Assert.IsTrue(scene.isDirty, "Policy Block must leave the scene untouched.");
	}

	[Test]
	public void SceneSafetyGuard_VerifyCleanFollowsSceneState()
	{
		Scene scene = CreateDirtySavedScene();

		string error;
		Assert.IsFalse(SceneSafetyGuard.TryVerifyClean(out error));
		StringAssert.Contains(SavedScenePath, error);

		Assert.IsTrue(SceneSafetyGuard.TryPrepareForTask(out error), error);
		Assert.IsTrue(SceneSafetyGuard.TryVerifyClean(out error), error);
	}

	[Test]
	public void SceneDirtyScanner_ClassifiesOpenScenes()
	{
		Scene savedScene = CreateDirtySavedScene();
		SceneDirtyReport savedReport = SceneDirtyScanner.Scan();
		Assert.AreEqual(1, savedReport.DirtySavedScenes.Count);
		Assert.AreEqual(SavedScenePath, savedReport.DirtySavedScenes[0].path);
		Assert.AreEqual(0, savedReport.DirtyUntitledScenes.Count);
		Assert.IsFalse(savedReport.IsClean);

		string error;
		Assert.IsTrue(SceneSafetyGuard.TryPrepareForTask(out error), error);
		Assert.IsFalse(savedScene.isDirty);

		SceneSafetyGuard.ClearOpenSceneDirtiness();
		EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);
		Scene untitledScene = SceneManager.GetActiveScene();
		new GameObject("UnsavedChange");
		EditorSceneManager.MarkSceneDirty(untitledScene);

		SceneDirtyReport untitledReport = SceneDirtyScanner.Scan();
		Assert.AreEqual(1, untitledReport.DirtyUntitledScenes.Count);
		Assert.AreEqual(0, untitledReport.DirtySavedScenes.Count);
		Assert.AreEqual(1, untitledReport.TransientScenes.Count,
			"Discard policy must offer the untitled scene as transient.");

		SceneSafetyGuard.ClearOpenSceneDirtiness();
		EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);
		Scene testScene = SceneManager.GetActiveScene();
		Assert.IsTrue(EditorSceneManager.SaveScene(testScene, TestScenePath));

		SceneDirtyReport testReport = SceneDirtyScanner.Scan();
		Assert.AreEqual(1, testReport.TransientScenes.Count);
		CollectionAssert.Contains(testReport.TestScenePaths, TestScenePath);
		Assert.AreEqual(0, testReport.DirtySavedScenes.Count);
	}

	[Test]
	public void SceneSafetyGuard_SavesDirtyPrefabStage()
	{
		PrefabStage stage = OpenDirtyProbePrefabStage();

		string error;
		Assert.IsTrue(SceneSafetyGuard.TryPrepareForTask(out error), error);
		Assert.IsFalse(stage.scene.isDirty, "A saved prefab stage must not stay dirty.");

		GameObject asset = AssetDatabase.LoadAssetAtPath<GameObject>(PrefabProbePath);
		Assert.IsNotNull(asset);
		Assert.IsNotNull(asset.transform.Find("StageChange"), "The stage edit must reach the prefab asset.");
	}

	[Test]
	public void SceneSafetyGuard_BlocksDirtyPrefabStageWhenConfigured()
	{
		AgentBridgeSettingsStore.SetSaveDirtyScenes(false);
		PrefabStage stage = OpenDirtyProbePrefabStage();

		string error;
		Assert.IsFalse(SceneSafetyGuard.TryPrepareForTask(out error));
		StringAssert.Contains(PrefabProbePath, error);
		Assert.IsTrue(stage.scene.isDirty, "Policy Block must leave the prefab stage untouched.");
	}

	[Test]
	public void SceneDirtyWatcher_NormalizesSceneDirtiedDuringTask()
	{
		Scene scene = SceneManager.GetActiveScene();
		Assert.IsTrue(EditorSceneManager.SaveScene(scene, SavedScenePath));
		SceneDirtyWatcher.DrainLogs();

		SceneDirtyWatcher.Arm("AgentBridgeProbeWatcher");
		try
		{
			Assert.IsTrue(SceneDirtyWatcher.IsArmed);

			SceneManager.SetActiveScene(scene);
			new GameObject("DirtiedDuringTask");
			EditorSceneManager.MarkSceneDirty(scene);
			Assert.IsTrue(scene.isDirty);

			PumpWatcherUpdate();

			Assert.IsFalse(scene.isDirty, "The watcher must leave the editor clean on the next tick.");
			List<string> logs = SceneDirtyWatcher.DrainLogs();
			Assert.IsTrue(logs.Count > 0, "The watcher must record what it did.");
			StringAssert.Contains(SavedScenePath, logs[0]);
			StringAssert.Contains("source:", logs[0]);
			Assert.AreEqual(0, SceneDirtyWatcher.DrainLogs().Count, "Drained logs must not repeat.");
		}
		finally
		{
			SceneDirtyWatcher.Disarm("AgentBridgeProbeWatcher");
		}
	}

	[Test]
	public void SceneDirtyWatcher_IgnoresDisarmFromAnotherTask()
	{
		SceneDirtyWatcher.Arm("AgentBridgeProbeOwner");
		try
		{
			SceneDirtyWatcher.Disarm("AgentBridgeProbeIntruder");

			Assert.IsTrue(SceneDirtyWatcher.IsArmed, "A foreign task must not disarm the watcher.");
			Assert.IsTrue(IsWatcherSubscribedToUpdate(), "The editor update subscription must survive a foreign disarm.");
		}
		finally
		{
			SceneDirtyWatcher.Disarm("AgentBridgeProbeOwner");
		}

		Assert.IsFalse(SceneDirtyWatcher.IsArmed);
	}

	[Test]
	public void SettingsStore_TreatsMissingDirtyScenePolicyAsSave()
	{
		string settingsPath = Path.Combine(Path.GetDirectoryName(Application.dataPath), "ProjectSettings", "AgentBridge.json");
		string original = File.ReadAllText(settingsPath);

		try
		{
			AgentBridgeSettingsStore.SetSaveDirtyScenes(false);
			Assert.IsFalse(AgentBridgeSettingsStore.GetSaveDirtyScenes());

			File.WriteAllText(settingsPath, "{\n\t\"Enabled\": true\n}");
			ResetSettingsCache();
			Assert.IsTrue(AgentBridgeSettingsStore.GetSaveDirtyScenes(),
				"A missing DirtyScenePolicy must be read as Save.");
		}
		finally
		{
			File.WriteAllText(settingsPath, original);
			ResetSettingsCache();
		}
	}

	private static void ResetSettingsCache()
	{
		FieldInfo cache = typeof(AgentBridgeSettingsStore).GetField("_cached", BindingFlags.NonPublic | BindingFlags.Static);
		Assert.IsNotNull(cache, "AgentBridgeSettingsStore must keep its settings cache field.");
		cache.SetValue(null, null);
	}

	[Test]
	public void SourceGuardrail_RejectsModalAndInteractiveEditorApi()
	{
		AssertGuardrailRejects("ModalSaveScenes",
			"UnityEditor.SceneManagement.EditorSceneManager.SaveCurrentModifiedScenesIfUserWantsTo();");
		AssertGuardrailRejects("ModalEnterPlaymode", "UnityEditor.EditorApplication.EnterPlaymode();");
		AssertGuardrailRejects("ModalIsPlayingAssignment", "UnityEditor.EditorApplication.isPlaying = true;");
		AssertGuardrailRejects("ModalDisplayDialog",
			"UnityEditor.EditorUtility.DisplayDialog(\"title\", \"message\", \"ok\");");
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
