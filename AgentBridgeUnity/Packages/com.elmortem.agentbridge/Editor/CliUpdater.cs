using System;
using System.Diagnostics;
using System.IO;
using System.Text;
using UnityEditor;
using UnityEngine;

namespace AgentBridge
{
	public static class CliUpdater
	{
		private const string ScriptsBase = "https://raw.githubusercontent.com/elmortem/unitycoworkbridge/roslyn-cli/scripts";

		private static Process _process;
		private static StringBuilder _output;

		[MenuItem("Tools/Agent Bridge/Update CLI")]
		public static void UpdateCli()
		{
			if (_process != null)
			{
				EditorUtility.DisplayDialog("Agent Bridge", "CLI update is already running.", "OK");
				return;
			}

			bool confirmed = EditorUtility.DisplayDialog(
				"Agent Bridge",
				"Download and install the latest AgentBridge CLI release?",
				"Install",
				"Cancel");

			if (!confirmed)
			{
				return;
			}

			_output = new StringBuilder();
			_process = new Process { StartInfo = CreateStartInfo(), EnableRaisingEvents = true };
			_process.OutputDataReceived += OnOutputReceived;
			_process.ErrorDataReceived += OnOutputReceived;

			try
			{
				_process.Start();
			}
			catch (Exception ex)
			{
				_process.Dispose();
				_process = null;
				UnityEngine.Debug.LogError("[AgentBridge] Could not launch the CLI installer. " + ex.Message);
				EditorUtility.DisplayDialog("Agent Bridge", "Could not launch the CLI installer. See the Console for details.", "OK");
				return;
			}

			_process.BeginOutputReadLine();
			_process.BeginErrorReadLine();

			EditorApplication.update -= Poll;
			EditorApplication.update += Poll;

			UnityEngine.Debug.Log("[AgentBridge] Installing the latest CLI release...");
		}

		[MenuItem("Tools/Agent Bridge/Update CLI", true)]
		private static bool UpdateCliValidate()
		{
			return _process == null;
		}

		private static ProcessStartInfo CreateStartInfo()
		{
			var startInfo = new ProcessStartInfo
			{
				UseShellExecute = false,
				RedirectStandardOutput = true,
				RedirectStandardError = true,
				CreateNoWindow = true,
				WorkingDirectory = BridgePaths.ProjectRoot
			};

			if (Application.platform == RuntimePlatform.WindowsEditor)
			{
				startInfo.FileName = ResolveWindowsShell();
				startInfo.Arguments = "-NoProfile -ExecutionPolicy Bypass -Command \""
					+ "[Net.ServicePointManager]::SecurityProtocol = [Net.SecurityProtocolType]::Tls12; "
					+ "irm " + ScriptsBase + "/install-agentbridge.ps1 | iex\"";
			}
			else
			{
				startInfo.FileName = "/bin/bash";
				startInfo.Arguments = "-c \"curl -fsSL " + ScriptsBase + "/install-agentbridge.sh | bash\"";
			}

			return startInfo;
		}

		private static string ResolveWindowsShell()
		{
			string systemDirectory = Environment.GetFolderPath(Environment.SpecialFolder.System);
			string candidate = Path.Combine(systemDirectory, "WindowsPowerShell", "v1.0", "powershell.exe");
			if (File.Exists(candidate))
			{
				return candidate;
			}

			return "powershell.exe";
		}

		private static void OnOutputReceived(object sender, DataReceivedEventArgs args)
		{
			if (string.IsNullOrEmpty(args.Data))
			{
				return;
			}

			lock (_output)
			{
				_output.AppendLine(args.Data);
			}
		}

		private static void Poll()
		{
			if (_process == null)
			{
				EditorApplication.update -= Poll;
				return;
			}

			if (!_process.HasExited)
			{
				return;
			}

			EditorApplication.update -= Poll;

			int exitCode = _process.ExitCode;
			string log;
			lock (_output)
			{
				log = _output.ToString();
			}

			_process.Dispose();
			_process = null;

			if (exitCode == 0)
			{
				UnityEngine.Debug.Log("[AgentBridge] CLI updated.\n" + log);
				EditorUtility.DisplayDialog(
					"Agent Bridge",
					"AgentBridge CLI updated.\n\nRestart the agent application so it picks up the new binary.",
					"OK");
				return;
			}

			UnityEngine.Debug.LogError("[AgentBridge] CLI update failed with exit code " + exitCode + ".\n" + log);
			EditorUtility.DisplayDialog("Agent Bridge", "CLI update failed. See the Console for details.", "OK");
		}
	}
}
