using UnityEditor;
using UnityEngine;

namespace AgentBridge
{
	public class AgentBridgeSetupWindow : EditorWindow
	{
		private static readonly RoslynSourceKind[] Sources =
		{
			RoslynSourceKind.Vendored,
			RoslynSourceKind.Project,
			RoslynSourceKind.Local
		};

		private Vector2 _scroll;

		[MenuItem("Tools/Agent Bridge/Setup...")]
		public static void Open()
		{
			var window = GetWindow<AgentBridgeSetupWindow>(true, "Unity Agent Bridge Setup");
			window.minSize = new Vector2(560, 340);
			window.Show();
		}

		private void OnGUI()
		{
			EditorGUILayout.LabelField("Scene safety", EditorStyles.boldLabel);

			bool saveDirtyScenes = AgentBridgeSettingsStore.GetSaveDirtyScenes();
			bool nextSaveDirtyScenes = EditorGUILayout.ToggleLeft("Save dirty scenes before agent tasks", saveDirtyScenes);
			if (nextSaveDirtyScenes != saveDirtyScenes)
			{
				AgentBridgeSettingsStore.SetSaveDirtyScenes(nextSaveDirtyScenes);
			}

			EditorGUILayout.HelpBox(
				nextSaveDirtyScenes
					? "Dirty scenes and prefab stages are saved silently before an agent task changes scenes."
					: "Agent tasks stop without opening a save dialog while a dirty scene or prefab stage is open.",
				MessageType.Info);
			EditorGUILayout.Space();

			bool discardUntitledScenes = AgentBridgeSettingsStore.GetDiscardDirtyUntitledScenes();
			bool nextDiscardUntitledScenes = EditorGUILayout.ToggleLeft("Discard dirty untitled scenes", discardUntitledScenes);
			if (nextDiscardUntitledScenes != discardUntitledScenes)
			{
				AgentBridgeSettingsStore.SetDiscardDirtyUntitledScenes(nextDiscardUntitledScenes);
			}

			EditorGUILayout.HelpBox(
				nextDiscardUntitledScenes
					? "Dirty untitled scenes are closed without saving before an agent task changes scenes."
					: "Agent tasks stop without opening a save dialog while a dirty untitled scene is open.",
				MessageType.Info);
			EditorGUILayout.Space();

			EditorGUILayout.LabelField("Multi-agent", EditorStyles.boldLabel);

			int leaseIdleTimeout = AgentBridgeSettingsStore.GetLeaseIdleTimeoutSeconds();
			int nextLeaseIdleTimeout = EditorGUILayout.DelayedIntField("Lease idle timeout (s)", leaseIdleTimeout);
			if (nextLeaseIdleTimeout != leaseIdleTimeout)
			{
				AgentBridgeSettingsStore.SetLeaseIdleTimeoutSeconds(nextLeaseIdleTimeout);
			}

			int contentionSlice = AgentBridgeSettingsStore.GetContentionSliceSeconds();
			int nextContentionSlice = EditorGUILayout.DelayedIntField("Contention slice (s)", contentionSlice);
			if (nextContentionSlice != contentionSlice)
			{
				AgentBridgeSettingsStore.SetContentionSliceSeconds(nextContentionSlice);
			}

			EditorGUILayout.HelpBox(
				"An idle agent session keeps the editor for the lease timeout. While another session waits, "
				+ "the holder keeps working for at most the contention slice, then the queue rotates on a task boundary.",
				MessageType.Info);
			EditorGUILayout.Space();

			EditorGUILayout.LabelField("Roslyn source", EditorStyles.boldLabel);
			EditorGUILayout.Space();

			_scroll = EditorGUILayout.BeginScrollView(_scroll);

			foreach (RoslynSourceKind kind in Sources)
			{
				DrawRow(kind);
			}

			EditorGUILayout.EndScrollView();

			EditorGUILayout.BeginHorizontal();

			if (GUILayout.Button("Refresh"))
			{
				RoslynResolver.ClearProbeCache();
				Repaint();
			}

			if (GUILayout.Button("Close"))
			{
				SessionState.SetBool("AgentBridge_SetupDismissed", true);
				Close();
			}

			EditorGUILayout.EndHorizontal();
		}

		private void DrawRow(RoslynSourceKind kind)
		{
			RoslynLocation location = RoslynResolver.ProbeCached(kind);

			EditorGUILayout.LabelField(kind + " — " + DescriptionFor(kind), EditorStyles.boldLabel);

			EditorGUILayout.BeginHorizontal();
			EditorGUILayout.SelectableLabel(location.Available ? "Ready" : location.Reason, GUILayout.Height(EditorGUIUtility.singleLineHeight));

			using (new EditorGUI.DisabledScope(!location.Available))
			{
				if (GUILayout.Button("Use", GUILayout.Width(60)))
				{
					AgentBridgeSettingsStore.SetRoslynSource(kind.ToString());
				}
			}

			EditorGUILayout.EndHorizontal();
			EditorGUILayout.Space();
		}

		private static string DescriptionFor(RoslynSourceKind kind)
		{
			switch (kind)
			{
				case RoslynSourceKind.Vendored:
					return "Roslyn shipped with the package";
				case RoslynSourceKind.Project:
					return "Roslyn already referenced by the project";
				case RoslynSourceKind.Local:
					return "Roslyn from a local folder";
				default:
					return "";
			}
		}
	}
}
