namespace AgentBridge.Cli;

// One modal dialog that holds the editor's main thread, as the package publishes it in
// Library/AgentBridge/dialogs.json.
internal sealed class EditorDialogInfo
{
	public string Id { get; set; } = "";
	// editor_dialog_events (Unity 6.7+) or win32 (window scan on Windows).
	public string Source { get; set; } = "";
	// native_message_box, managed_custom_window, native_dialog or editor_window.
	public string Kind { get; set; } = "";
	// info, warning, error; empty when the source does not know it.
	public string Level { get; set; } = "";
	public string Title { get; set; } = "";
	public string Message { get; set; } = "";
	public string[] Buttons { get; set; } = Array.Empty<string>();
	public string OpenedAtUtc { get; set; } = "";
	public long OpenMs { get; set; }
	// The task that was executing when the dialog appeared; empty for an idle editor.
	public string TaskId { get; set; } = "";
}
