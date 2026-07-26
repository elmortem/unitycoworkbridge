using UnityEditor;
using UnityEngine;

namespace AgentBridge
{
	public class AgentBridgeSetupWindow : EditorWindow
	{
		private static readonly RoslynSourceKind[] Sources =
		{
			RoslynSourceKind.UnityBuiltin,
			RoslynSourceKind.Project,
			RoslynSourceKind.NuGet,
			RoslynSourceKind.Local
		};

		[MenuItem("Tools/Agent Bridge/Setup...")]
		public static void Open()
		{
			var window = GetWindow<AgentBridgeSetupWindow>(true, "Unity Agent Bridge Setup");
			window.minSize = new Vector2(460, 320);
			window.maxSize = new Vector2(460, 320);
			window.Show();
		}

		private void OnEnable()
		{
			RoslynInstaller.Completed += OnInstallCompleted;
		}

		private void OnDisable()
		{
			RoslynInstaller.Completed -= OnInstallCompleted;
		}

		private void OnGUI()
		{
			EditorGUILayout.LabelField("Roslyn source", EditorStyles.boldLabel);
			EditorGUILayout.Space();

			foreach (RoslynSourceKind kind in Sources)
			{
				DrawRow(kind);
			}

			EditorGUILayout.Space();

			using (new EditorGUI.DisabledScope(RoslynInstaller.IsBusy))
			{
				if (GUILayout.Button("Close"))
				{
					SessionState.SetBool("AgentBridge_SetupDismissed", true);
					Close();
				}
			}
		}

		private void DrawRow(RoslynSourceKind kind)
		{
			RoslynLocation location = RoslynResolver.Probe(kind);

			EditorGUILayout.BeginHorizontal();
			EditorGUILayout.LabelField(kind.ToString(), GUILayout.Width(110));
			EditorGUILayout.LabelField(DescriptionFor(kind));
			EditorGUILayout.LabelField(location.Available ? "Ready" : location.Reason, GUILayout.Width(140));

			using (new EditorGUI.DisabledScope(RoslynInstaller.IsBusy))
			{
				if (location.Available && GUILayout.Button("Use", GUILayout.Width(60)))
				{
					AgentBridgeSettingsStore.SetRoslynSource(kind.ToString());
				}

				if (kind == RoslynSourceKind.NuGet && !location.Available && GUILayout.Button("Download", GUILayout.Width(80)))
				{
					RoslynInstaller.Download();
				}
			}

			EditorGUILayout.EndHorizontal();
		}

		private void OnInstallCompleted(bool success, string message)
		{
			Repaint();
		}

		private static string DescriptionFor(RoslynSourceKind kind)
		{
			switch (kind)
			{
				case RoslynSourceKind.UnityBuiltin:
					return "Roslyn bundled with the Unity Editor";
				case RoslynSourceKind.Project:
					return "Roslyn already referenced by the project";
				case RoslynSourceKind.NuGet:
					return "Download Roslyn from NuGet";
				case RoslynSourceKind.Local:
					return "Roslyn from a local folder";
				default:
					return "";
			}
		}
	}
}
