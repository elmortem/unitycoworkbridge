param(
	[string]$Project = (Join-Path $PSScriptRoot '../AgentBridgeUnity'),
	[switch]$IncludeBlockingBaseline
)
$ErrorActionPreference = 'Stop'
$Project = (Resolve-Path -LiteralPath $Project).Path
$cli = (Get-Command agentbridge -ErrorAction SilentlyContinue).Source
if (!$cli) { $cli = Join-Path $env:LOCALAPPDATA 'AgentBridge/bin/agentbridge.exe' }
$session = 'coordinator-perf-' + [Guid]::NewGuid().ToString('N').Substring(0,8)
$task = 'Task_' + (Get-Date -Format 'yyyyMMdd_HHmmss_fff') + '_coordinator_perf'
$scratch = Join-Path $Project 'Temp/AgentBridge'
[IO.Directory]::CreateDirectory($scratch) | Out-Null
$source = @'
using System;
using System.IO;
using System.Text;
using System.Diagnostics;
using System.Security.Cryptography;
using System.Threading.Tasks;
using UnityEditor;
using AgentBridge;
public static class TASK_NAME
{
    public static async Task<string> Run()
    {
        string root = Path.Combine(BridgePaths.ProjectRoot, "Temp/AgentBridge/CoordinatorPerfFixture");
        await Task.Run(() => {
            Directory.CreateDirectory(root);
            byte[] bytes = new byte[1024 * 1024]; new Random(123).NextBytes(bytes);
            for (int i=0;i<128;i++) File.WriteAllBytes(Path.Combine(root,"asset"+i+".bin"),bytes);
            for (int i=0;i<2000;i++) File.WriteAllText(Path.Combine(root,"source"+i+".cs"),"// deterministic test input "+i);
        });
        string baseline = "skipped";
        if (RUN_BASELINE)
        {
            var timer=Stopwatch.StartNew();
            string first=Legacy(root), second=Legacy(root);
            baseline=timer.ElapsedMilliseconds+"ms digest="+first;
            if (first!=second) throw new Exception("baseline inputs moved");
        }
        var job=new InputHashJob(new[]{root},new string[0],"perf");
        var clock=Stopwatch.StartNew(); long last=0, maxGap=0; int ticks=0;
        EditorApplication.CallbackFunction tick=()=> {
            long now=clock.ElapsedMilliseconds; maxGap=Math.Max(maxGap,now-last); last=now; ticks++;
        };
        EditorApplication.update+=tick;
        try
        {
            var first=await job.Start(); long once=clock.ElapsedMilliseconds;
            var second=await job.Start();
            const string expected="71ce4f2c346b069bfd55d1eb7c7a0dac98f59513b395a0bd1cd3af58bc409553";
            if (!first.Complete || !second.Complete || first.Digest!=expected || second.Digest!=expected)
                throw new Exception("digest differs from the original implementation");
            if(ticks==0) throw new Exception("editor did not update during worker hashing");
            return "files="+first.FileCount+" bytes="+first.TotalBytes+" baselineDouble="+baseline
                +" firstWorkerMs="+once+" doubleWorkerMs="+clock.ElapsedMilliseconds
                +" editorUpdates="+ticks+" maxUpdateGapMs="+maxGap+" digest="+first.Digest;
        }
        finally { EditorApplication.update-=tick; }
    }
    // Original implementation, kept only as an opt-in reproduction of the blocking defect.
    private static string Legacy(string root)
    {
        string[] files=Directory.GetFiles(root); Array.Sort(files,StringComparer.OrdinalIgnoreCase);
        using(var sha=SHA256.Create())
        using(var output=new CryptoStream(Stream.Null,sha,CryptoStreamMode.Write))
        {
            Append(output,"context\nperf\nroot\nr0\n");
            foreach(string file in files)
            {
                byte[] bytes=File.ReadAllBytes(file);
                Append(output,"file\nr0/"+Path.GetFileName(file)+"\n"+bytes.Length+"\n");
                output.Write(bytes,0,bytes.Length);
            }
            output.FlushFinalBlock();
            return BitConverter.ToString(sha.Hash).Replace("-","").ToLowerInvariant();
        }
    }
    private static void Append(Stream stream,string text)
    {
        byte[] bytes=Encoding.UTF8.GetBytes(text);stream.Write(bytes,0,bytes.Length);
    }
}
'@
$source = $source.Replace('TASK_NAME', $task).Replace('RUN_BASELINE', $IncludeBlockingBaseline.IsPresent.ToString().ToLowerInvariant())
$path = Join-Path $scratch ($task + '.cs')
[IO.File]::WriteAllText($path, $source)
& $cli csharp $path --project $Project --session $session --format human --wait 30
$result = $LASTEXITCODE
& $cli release --project $Project --session $session --format human --wait 5
exit $result
