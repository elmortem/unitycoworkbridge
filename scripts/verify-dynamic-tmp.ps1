param(
	[string]$Project = (Join-Path $PSScriptRoot '../AgentBridgeUnity'),
	[string]$Cli = 'agentbridge'
)
$ErrorActionPreference = 'Stop'
$Project = [IO.Path]::GetFullPath($Project)
$taskName = 'Task_' + (Get-Date -Format 'yyyyMMdd_HHmmss_fff') + '_TmpInputs'
$scratch = Join-Path $Project 'Temp/AgentBridge'
New-Item -ItemType Directory -Force $scratch | Out-Null
$taskPath = Join-Path $scratch ($taskName + '.cs')
$source = @'
using System;
using System.IO;
using System.Threading.Tasks;
using AgentBridge;
using TMPro;
using UnityEditor;
using UnityEngine;

public static class TASK_NAME
{
	public static async Task<string> Run()
	{
		string folder = "Assets/BridgeTmpInputs_" + Guid.NewGuid().ToString("N");
		AssetDatabase.CreateFolder("Assets", folder.Substring("Assets/".Length));
		try
		{
			var dynamicFont = ScriptableObject.CreateInstance<TMP_FontAsset>();
			dynamicFont.atlasPopulationMode = AtlasPopulationMode.Dynamic;
			AssetDatabase.CreateAsset(dynamicFont, folder + "/Dynamic.asset");
			var atlas = new Texture2D(4, 4);
			AssetDatabase.AddObjectToAsset(atlas, dynamicFont);
			var staticFont = ScriptableObject.CreateInstance<TMP_FontAsset>();
			staticFont.atlasPopulationMode = AtlasPopulationMode.Static;
			AssetDatabase.CreateAsset(staticFont, folder + "/Static.asset");
			AssetDatabase.SaveAssets();
			string root = Path.Combine(BridgePaths.ProjectRoot, folder);
			var ignore = ValidationEvidence.BuildIgnore("");
			string dynamicPath = Path.Combine(root, "Dynamic.asset");
			if (!ignore(dynamicPath) || ignore(dynamicPath + ".meta")
				|| ignore(Path.Combine(root, "Static.asset")) || ignore(Path.Combine(root, "Source.ttf"))
				|| ignore(Path.Combine(root, "Source.otf")) || ignore(Path.Combine(root, "Other.asset")))
				throw new Exception("TMP exclusion classification failed");
			var roots = new[] { root };
			var excluded = new string[0];
			var before = ValidationInputSnapshot.Capture(roots, excluded, "tmp-regression", ignore);
			using (var monitor = new ValidationInputMonitor(roots, excluded, ignore))
			{
				atlas.SetPixel(0, 0, Color.red);
				atlas.Apply();
				EditorUtility.SetDirty(atlas);
				EditorUtility.SetDirty(dynamicFont);
				AssetDatabase.SaveAssets();
				await Task.Delay(300);
				var after = ValidationInputSnapshot.Capture(roots, excluded, "tmp-regression", ValidationEvidence.BuildIgnore(""));
				if (!before.Complete || !after.Complete || before.Digest != after.Digest
					|| !monitor.Observed || monitor.EventCount != 0)
					throw new Exception("Dynamic atlas invalidated inputs: " + string.Join(",", monitor.Paths));
				staticFont.name = "Changed static font";
				EditorUtility.SetDirty(staticFont);
				AssetDatabase.SaveAssets();
				for (int i = 0; i < 20 && monitor.EventCount == 0; i++) await Task.Delay(50);
				var changed = ValidationInputSnapshot.Capture(roots, excluded, "tmp-regression", ignore);
				if (!changed.Complete || changed.Digest == before.Digest || monitor.EventCount == 0)
					throw new Exception("Static font edits were hidden");
			}
			dynamicFont.atlasPopulationMode = AtlasPopulationMode.Static;
			EditorUtility.SetDirty(dynamicFont);
			AssetDatabase.SaveAssets();
			if (ValidationEvidence.BuildIgnore("")(dynamicPath)) throw new Exception("Dynamic-to-static change remained excluded");
			return "PASS: saved dynamic atlas preserves digest and observer; static edits invalidate both; meta, source fonts and unrelated assets remain tracked; static mode restores tracking";
		}
		finally { AssetDatabase.DeleteAsset(folder); }
	}
}
'@
[IO.File]::WriteAllText($taskPath, $source.Replace('TASK_NAME', $taskName))
& $Cli csharp $taskPath --project $Project --format human --wait 30
if ($LASTEXITCODE -ne 0) { throw "Dynamic TMP verification failed (exit $LASTEXITCODE)" }
