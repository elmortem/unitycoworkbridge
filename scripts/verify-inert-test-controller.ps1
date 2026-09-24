param(
	[string]$Project = (Join-Path $PSScriptRoot '../AgentBridgeUnity'),
	[string]$Cli = (Join-Path $PSScriptRoot '../AgentBridgeCli/bin/Release/net8.0/agentbridge.exe')
)
$ErrorActionPreference = 'Stop'
$Project = [IO.Path]::GetFullPath($Project)
function Invoke-Bridge([string[]]$Arguments) {
	$json = (& $Cli @Arguments --project $Project --format json | Out-String)
	if ($LASTEXITCODE -ne 0) { throw $json }
	return ($json | ConvertFrom-Json)
}
$health = Invoke-Bridge @('status')
if ($health.bridge.activeTaskId -or $health.bridge.isPlaying -or $health.bridge.queuedTasks.Count -gt 0) {
	throw 'Run only in an idle test editor.'
}
# This check must run outside NUnit: an NUnit test itself makes IsRunning true.
$id = 'Task_' + (Get-Date -Format 'yyyyMMdd_HHmmss_fff') + '_inert_controller'
$path = Join-Path $Project "Temp/AgentBridge/$id.cs"
$source = @'
using System;
using System.Linq;
using System.Threading.Tasks;
using AgentBridge;
using UnityEditor;
using UnityEngine;
public static class PROBE_NAME
{
	public static Task<string> Run()
	{
		if (EditorApplication.isPlayingOrWillChangePlaymode || TestRunnerCancellation.IsRunning())
			throw new Exception("Expected idle Edit Mode before probe");
		var type = Find("UnityEngine.TestTools.TestRunner.PlaymodeTestsController");
		var controller = new GameObject("Inert test controller regression") { hideFlags = HideFlags.HideAndDontSave };
		try
		{
			controller.AddComponent(type);
			if (Resources.FindObjectsOfTypeAll(type).Length == 0)
				throw new Exception("Probe did not create a discoverable controller");
			string reason = TestRunnerCancellation.RunningReason();
			if (!string.IsNullOrEmpty(reason)) throw new Exception("Inert controller blocks queue: " + reason);
			controller.SetActive(false);
			if (TestRunnerCancellation.IsRunning()) throw new Exception("Inactive controller blocks queue");
		}
		finally { UnityEngine.Object.DestroyImmediate(controller); }
		// EditModeRunner is disposed only by RunFinished, so error and cancellation paths leak it.
		var runnerType = Find("UnityEditor.TestTools.TestRunner.EditModeRunner");
		var orphan = ScriptableObject.CreateInstance(runnerType);
		orphan.hideFlags = HideFlags.HideAndDontSave;
		try
		{
			if (Resources.FindObjectsOfTypeAll(runnerType).Length == 0)
				throw new Exception("Probe did not create a discoverable EditModeRunner");
			string reason = TestRunnerCancellation.RunningReason();
			if (!string.IsNullOrEmpty(reason)) throw new Exception("Orphan EditModeRunner blocks queue: " + reason);
		}
		finally { UnityEngine.Object.DestroyImmediate(orphan); }
		if (TestRunnerCancellation.IsRunning()) throw new Exception("Probe left a running executor");
		return Task.FromResult("PASS: inert controllers and orphan EditModeRunner do not block Edit Mode");
	}

	private static Type Find(string name)
	{
		return AppDomain.CurrentDomain.GetAssemblies().Select(a => a.GetType(name)).First(t => t != null);
	}
}
'@
[IO.File]::WriteAllText($path, $source.Replace('PROBE_NAME', $id))
$result = Invoke-Bridge @('csharp', $path, '--session', 'cancellation-verifier', '--wait', '20')
if ($result.Status -ne 'success') { throw ($result | ConvertTo-Json -Depth 10) }
Write-Output "$($result.ReturnValue) ($($result.Id))"
