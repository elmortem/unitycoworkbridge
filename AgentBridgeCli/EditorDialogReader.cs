using System.Text.Json;
using AgentBridge.Coordination;

namespace AgentBridge.Cli;

// The client half of dialog-detection-v1. A modal dialog stops the editor's main thread, and with
// it the heartbeat and status.json; the package keeps refreshing dialogs.json from a background
// thread instead, so a fresh file is the only evidence that the editor is held by a dialog rather
// than busy, asleep or gone.
internal static class EditorDialogReader
{
	public const string Code = "blocked_by_dialog";
	public const string WaitReason = "editor_blocked_by_dialog";
	// The package rewrites the file every second while a dialog stays open.
	public const long MaximumAgeMs = 5000;
	// Same allowance as the heartbeat: a foreign client reads through a shared folder.
	public const long MaximumForeignAgeMs = BridgeConstants.MaximumForeignHeartbeatAgeMs;
	// A wait gives up only on a dialog that has stayed open this long, not on one that closes itself.
	public const long WaitGraceMs = 2000;
	// A dialog blocks only while the main thread has stopped ticking (idle ticks come every 500 ms).
	// Measured by the editor itself, so a foreign client's clock skew does not matter.
	public const long MinimumMainThreadIdleMs = 1500;

	internal sealed class Snapshot
	{
		public int EditorPid { get; set; }
		public long UpdatedUtcMs { get; set; }
		public long MainThreadIdleMs { get; set; }
		public EditorDialogInfo[] Dialogs { get; set; } = Array.Empty<EditorDialogInfo>();
	}

	public static EditorDialogInfo[] Read(string path, BridgeStatus? status, bool foreignHost, long nowUtcMs)
	{
		if (status == null || !File.Exists(path))
		{
			return Array.Empty<EditorDialogInfo>();
		}

		try
		{
			var snapshot = JsonSerializer.Deserialize<Snapshot>(SharedFile.ReadAllText(path), JsonSupport.Read);
			return Select(snapshot, status.EditorPid, foreignHost, nowUtcMs);
		}
		catch
		{
			return Array.Empty<EditorDialogInfo>();
		}
	}

	// A snapshot counts only for the editor status.json describes, only while it keeps moving (a
	// crashed editor leaves its last dialogs behind) and only while the main thread is not ticking:
	// an editor that still runs its updates is not held by any window it shows.
	public static EditorDialogInfo[] Select(Snapshot? snapshot, int editorPid, bool foreignHost, long nowUtcMs)
	{
		if (snapshot == null || snapshot.Dialogs == null || snapshot.Dialogs.Length == 0
			|| snapshot.EditorPid <= 0 || snapshot.EditorPid != editorPid
			|| snapshot.MainThreadIdleMs < MinimumMainThreadIdleMs)
		{
			return Array.Empty<EditorDialogInfo>();
		}

		var tolerance = foreignHost ? MaximumForeignAgeMs : MaximumAgeMs;
		var age = nowUtcMs - snapshot.UpdatedUtcMs;
		if (age > tolerance || age < -tolerance)
		{
			return Array.Empty<EditorDialogInfo>();
		}

		var dialogs = new List<EditorDialogInfo>();
		foreach (var dialog in snapshot.Dialogs)
		{
			if (dialog == null)
			{
				continue;
			}

			dialog.Id ??= "";
			dialog.Source ??= "";
			dialog.Kind ??= "";
			dialog.Level ??= "";
			dialog.Title ??= "";
			dialog.Message ??= "";
			dialog.Buttons ??= Array.Empty<string>();
			dialog.OpenedAtUtc ??= "";
			dialog.TaskId ??= "";
			dialog.OpenMs = Math.Max(0, dialog.OpenMs) + Math.Max(0, age);
			dialogs.Add(dialog);
		}

		return dialogs.ToArray();
	}

	public static bool ShouldInterruptWait(BridgeHealth health)
	{
		return health.BlockedBy.Any(dialog => dialog.OpenMs >= WaitGraceMs);
	}

	// One line for people: [warning] "Scene(s) Have Been Modified" (native_message_box, 12s, task X) — message [Save | Don't Save].
	public static string Describe(EditorDialogInfo dialog)
	{
		var level = dialog.Level.Length > 0 ? "[" + dialog.Level + "] " : "";
		var details = new List<string> { dialog.Kind.Length > 0 ? dialog.Kind : "dialog", dialog.OpenMs / 1000 + "s" };
		if (dialog.TaskId.Length > 0)
		{
			details.Add("task " + dialog.TaskId);
		}

		var line = level + "\"" + dialog.Title + "\" (" + string.Join(", ", details) + ")";
		var message = dialog.Message.Replace('\r', ' ').Replace('\n', ' ').Trim();
		if (message.Length > 160)
		{
			message = message[..160] + "…";
		}

		if (message.Length > 0)
		{
			line += " — " + message;
		}

		if (dialog.Buttons.Length > 0)
		{
			line += " [" + string.Join(" | ", dialog.Buttons) + "]";
		}

		return line;
	}
}
