using System;
using System.Diagnostics;
using System.Reflection;
using System.Text;
using AgentBridge;
using NUnit.Framework;

[Platform("Win")]
public class CliUpdaterTests
{
	[TestCase("success", 0)]
	[TestCase("download-failure", 1)]
	[TestCase("installer-failure", 1)]
	public void WindowsUpdatePropagatesProjectAndFailures(string scenario, int expectedExit)
	{
		const string project = "C:\\Unity projects\\O'Brien $build `literal Проект";
		var method = typeof(CliUpdater).GetMethod("WindowsUpdateScript", BindingFlags.NonPublic | BindingFlags.Static);
		Assert.NotNull(method);
		string command = (string)method.Invoke(null, new object[] { project });
		string installer = scenario == "installer-failure"
			? "param([string]$Project) Write-Error 'Linux installation failed'"
			: "param([string]$Project) [Console]::Out.WriteLine('PROJECT:' + $Project)";
		string mock = "function Invoke-WebRequest { param($Uri, $OutFile, [switch]$UseBasicParsing); "
			+ "if (-not $UseBasicParsing) { throw 'Basic parsing required' }; "
			+ (scenario == "download-failure" ? "throw 'Download failed'; "
				: "[IO.File]::WriteAllText($OutFile, '" + installer.Replace("'", "''") + "'); ")
			+ "}; ";
		var createStart = typeof(CliUpdater).GetMethod("CreateStartInfo", BindingFlags.NonPublic | BindingFlags.Static);
		var start = (ProcessStartInfo)createStart.Invoke(null, null);
		start.StandardOutputEncoding = Encoding.UTF8;
		start.StandardErrorEncoding = Encoding.UTF8;
		// Make the child's output encoding explicit as Windows PowerShell otherwise uses OEM.
		string arguments = start.Arguments.Substring(0, start.Arguments.IndexOf("-EncodedCommand ", StringComparison.Ordinal) + 16);
		start.Arguments = arguments + Convert.ToBase64String(Encoding.Unicode.GetBytes(
			"[Console]::OutputEncoding = New-Object Text.UTF8Encoding($false); " + mock + command));
		using (var process = Process.Start(start))
		{
			try
			{
				var output = process.StandardOutput.ReadToEndAsync();
				var errors = process.StandardError.ReadToEndAsync();
				Assert.IsTrue(process.WaitForExit(15000), "Windows updater did not exit");
				string stderr = errors.GetAwaiter().GetResult();
				Assert.AreEqual(expectedExit, process.ExitCode, stderr);
				string stdout = output.GetAwaiter().GetResult();
				if (scenario == "success") Assert.AreEqual("PROJECT:" + project, stdout.Trim());
				else
				{
					Assert.IsFalse(stdout.Contains("PROJECT:"), "Failure must not continue into installation");
					StringAssert.Contains(scenario == "download-failure" ? "Download failed" : "Linux installation failed", stderr);
				}
			}
			finally { if (!process.HasExited) { process.Kill(); process.WaitForExit(); } }
		}
	}

	[Test]
	public void WindowsMenuUsesEncodedNoninteractiveInstaller()
	{
		var method = typeof(CliUpdater).GetMethod("CreateStartInfo", BindingFlags.NonPublic | BindingFlags.Static);
		var start = (ProcessStartInfo)method.Invoke(null, null);
		Assert.IsTrue(start.Arguments.Contains("-EncodedCommand "));
		Assert.IsTrue(start.Arguments.Contains("-NonInteractive"));
		Assert.IsFalse(start.UseShellExecute);
		Assert.IsTrue(start.CreateNoWindow);
		Assert.AreEqual(BridgePaths.ProjectRoot, start.WorkingDirectory);
		string script = Encoding.Unicode.GetString(Convert.FromBase64String(start.Arguments.Substring(start.Arguments.IndexOf("-EncodedCommand ", StringComparison.Ordinal) + 16)));
		Assert.IsTrue(script.Contains("& $installer -Project '"));
		Assert.IsFalse(script.Contains("& $installer -Rid"), "The installer must own both builds as one operation");
	}
}
