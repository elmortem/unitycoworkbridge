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
		var type = AppDomain.CurrentDomain.GetAssemblies()
			.Select(a => a.GetType("UnityEngine.TestTools.TestRunner.PlaymodeTestsController"))
			.First(t => t != null);
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
		if (TestRunnerCancellation.IsRunning()) throw new Exception("Probe left a running executor");
		return Task.FromResult("PASS: active and inactive inert controllers do not block Edit Mode");
	}
}
'@
[IO.File]::WriteAllText($path, $source.Replace('PROBE_NAME', $id))
$result = Invoke-Bridge @('csharp', $path, '--session', 'cancellation-verifier', '--wait', '20')
if ($result.Status -ne 'success') { throw ($result | ConvertTo-Json -Depth 10) }
Write-Output "$($result.ReturnValue) ($($result.Id))"
