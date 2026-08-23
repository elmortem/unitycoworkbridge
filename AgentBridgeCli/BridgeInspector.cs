using System.Diagnostics;
using System.Text.Json;

namespace AgentBridge.Cli;

internal static class BridgeInspector
{
	public static BridgeHealth Inspect(string projectRoot)
	{
		var paths = new BridgePaths(projectRoot);
		var health = new BridgeHealth
		{
			ProjectPath = projectRoot,
			WorkingRoot = paths.WorkingRoot,
			ScratchDir = paths.Scratch,
			ClientOs = HostPlatform.Current,
			PackageDeclared = IsPackageDeclared(projectRoot),
			StatusFileExists = File.Exists(paths.StatusFile),
			HeartbeatExists = File.Exists(paths.HeartbeatFile)
		};

		if (!health.PackageDeclared)
		{
			health.Problems.Add("package_not_installed");
		}

		if (!health.StatusFileExists)
		{
			health.Problems.Add("status_missing");
		}
		else
		{
			try
			{
				var json = File.ReadAllText(paths.StatusFile);
				health.Bridge = JsonSerializer.Deserialize<BridgeStatus>(json, JsonSupport.Read);
				if (health.Bridge == null)
				{
					health.Problems.Add("status_invalid");
				}
			}
			catch
			{
				health.Problems.Add("status_invalid");
			}
		}

		health.HostOs = health.Bridge?.HostOs?.Trim() ?? "";
		health.ForeignHost = HostPlatform.IsForeign(health.HostOs);
		health.HeartbeatToleranceMs = health.ForeignHost
			? BridgeConstants.MaximumForeignHeartbeatAgeMs
			: BridgeConstants.MaximumHeartbeatAgeMs;

		if (!health.HeartbeatExists)
		{
			health.Problems.Add("heartbeat_missing");
		}
		else
		{
			health.HeartbeatAgeMs = ReadHeartbeatAgeMs(paths.HeartbeatFile);
			if (health.HeartbeatAgeMs == null)
			{
				health.Problems.Add("heartbeat_invalid");
			}
			else if (health.HeartbeatAgeMs > health.HeartbeatToleranceMs)
			{
				health.Problems.Add("heartbeat_stale");
			}
		}

		if (health.Bridge != null)
		{
			health.ProtocolCompatible = health.Bridge.ProtocolVersion == BridgeConstants.ProtocolVersion;
			if (!health.ProtocolCompatible)
			{
				health.Problems.Add("protocol_mismatch");
			}

			health.ProjectMatches = MatchesProject(paths, health.Bridge, out var matchedBy);
			health.ProjectMatchedBy = matchedBy;
			if (!health.ProjectMatches)
			{
				health.Problems.Add("project_mismatch");
			}

			if (health.ForeignHost)
			{
				health.EditorProcessAlive = null;
			}
			else
			{
				health.EditorProcessAlive = IsProcessAlive(health.Bridge.EditorPid);
				if (health.EditorProcessAlive != true)
				{
					health.Problems.Add("editor_process_not_running");
				}
			}

			if (!health.Bridge.Enabled)
			{
				health.Problems.Add("bridge_disabled");
			}

			if (!health.Bridge.RoslynReady)
			{
				health.Problems.Add("roslyn_not_ready");
			}

			if (!health.Bridge.SignalTickAvailable)
			{
				health.Warnings.Add("signal_tick_missing");
			}

			if (!health.Bridge.WakeTimerInstalled
				&& string.Equals(health.HostOs, HostPlatform.Windows, StringComparison.OrdinalIgnoreCase))
			{
				health.Warnings.Add("wake_timer_missing");
			}

			if (IsThrottledInteractionMode(health.Bridge.InteractionMode))
			{
				health.Warnings.Add("interaction_throttled");
			}
		}

		health.BridgeReady = health.PackageDeclared
			&& health.Bridge != null
			&& health.ProtocolCompatible
			&& health.ProjectMatches
			&& (health.ForeignHost || health.EditorProcessAlive == true)
			&& health.Bridge.Enabled
			&& health.HeartbeatAgeMs <= health.HeartbeatToleranceMs;
		health.CSharpReady = health.BridgeReady && health.Bridge!.RoslynReady;
		health.Ok = health.BridgeReady;
		health.Code = health.BridgeReady ? "ready" : FirstOperationalProblem(health.Problems);
		return health;
	}

	private static bool IsThrottledInteractionMode(string? mode)
	{
		if (string.IsNullOrWhiteSpace(mode))
		{
			return false;
		}

		var normalized = mode.Trim();
		return string.Equals(normalized, "Default", StringComparison.OrdinalIgnoreCase)
			|| string.Equals(normalized, "MonitorRefreshRate", StringComparison.OrdinalIgnoreCase)
			|| string.Equals(normalized, "Custom", StringComparison.OrdinalIgnoreCase);
	}

	private static string FirstOperationalProblem(List<string> problems)
	{
		string[] priority =
		{
			"package_not_installed",
			"status_missing",
			"status_invalid",
			"protocol_mismatch",
			"project_mismatch",
			"heartbeat_missing",
			"heartbeat_invalid",
			"heartbeat_stale",
			"editor_process_not_running",
			"bridge_disabled"
		};

		foreach (var problem in priority)
		{
			if (problems.Contains(problem))
			{
				return problem;
			}
		}

		return problems.FirstOrDefault() ?? "unavailable";
	}

	private static bool IsPackageDeclared(string projectRoot)
	{
		var embedded = Path.Combine(projectRoot, "Packages", BridgeConstants.PackageId, "package.json");
		if (File.Exists(embedded))
		{
			return true;
		}

		var manifest = Path.Combine(projectRoot, "Packages", "manifest.json");
		try
		{
			using var document = JsonDocument.Parse(File.ReadAllText(manifest));
			return document.RootElement.TryGetProperty("dependencies", out var dependencies)
				&& dependencies.TryGetProperty(BridgeConstants.PackageId, out _);
		}
		catch
		{
			return false;
		}
	}

	private static long? ReadHeartbeatAgeMs(string path)
	{
		try
		{
			var value = File.ReadAllText(path).Trim();
			if (!long.TryParse(value, out var timestamp))
			{
				return null;
			}

			return Math.Max(0, DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() - timestamp);
		}
		catch
		{
			return null;
		}
	}

	private static bool MatchesProject(BridgePaths paths, BridgeStatus status, out string matchedBy)
	{
		var localId = ReadProjectId(paths.ProjectIdFile);
		var reportedId = status.ProjectId?.Trim() ?? "";
		if (localId.Length > 0 && reportedId.Length > 0)
		{
			matchedBy = "project_id";
			return string.Equals(localId, reportedId, StringComparison.OrdinalIgnoreCase);
		}

		matchedBy = "path";
		return PathsEqual(paths.ProjectRoot, status.ProjectPath);
	}

	private static string ReadProjectId(string path)
	{
		try
		{
			return File.ReadAllText(path).Trim();
		}
		catch
		{
			return "";
		}
	}

	private static bool PathsEqual(string expected, string? actual)
	{
		if (string.IsNullOrWhiteSpace(actual))
		{
			return false;
		}

		try
		{
			var normalizedExpected = Path.TrimEndingDirectorySeparator(Path.GetFullPath(expected));
			var normalizedActual = Path.TrimEndingDirectorySeparator(Path.GetFullPath(actual));
			var comparison = OperatingSystem.IsWindows()
				? StringComparison.OrdinalIgnoreCase
				: StringComparison.Ordinal;
			return string.Equals(normalizedExpected, normalizedActual, comparison);
		}
		catch
		{
			return false;
		}
	}

	private static bool IsProcessAlive(int pid)
	{
		if (pid <= 0)
		{
			return false;
		}

		try
		{
			using var process = Process.GetProcessById(pid);
			return !process.HasExited;
		}
		catch
		{
			return false;
		}
	}
}
