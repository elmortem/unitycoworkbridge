using System;

namespace AgentBridge
{
	// One modal dialog that holds the editor's main thread. Published in dialogs.json for the CLI
	// and turned into a task log line once the dialog closes.
	[Serializable]
	public class EditorDialogInfo
	{
		// Stable while the dialog stays open: "unity:<id>" from EditorDialogEvents, "win32:<hwnd>"
		// from the window scan.
		public string Id;
		// editor_dialog_events or win32.
		public string Source;
		// native_message_box / managed_custom_window from Unity, native_dialog / editor_window from
		// the window scan.
		public string Kind;
		// info, warning, error; empty when the source does not know it.
		public string Level;
		public string Title;
		public string Message;
		public string[] Buttons;
		public string OpenedAtUtc;
		// How long the dialog has been seen open, measured by the editor's own clock.
		public long OpenMs;
		// The task that was executing when the dialog appeared; empty for an idle editor.
		public string TaskId;
	}
}
