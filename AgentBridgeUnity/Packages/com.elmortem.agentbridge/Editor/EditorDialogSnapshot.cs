using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;

namespace AgentBridge
{
	// Content of Library/AgentBridge/dialogs.json. Written from a background thread while the main
	// thread is stuck in the dialog, so the JSON is built by hand: no Unity API runs off the main
	// thread. An empty Dialogs array means "nothing blocks"; the CLI also ignores a file whose
	// UpdatedUtcMs stopped moving, because a dialog that is still open is refreshed every second.
	public static class EditorDialogSnapshot
	{
		public const int MessageLimit = 500;
		public const int ButtonLimit = 8;

		// mainThreadIdleMs: how long EditorApplication.update has not run. A dialog is blocking only while
		// it grows; the CLI ignores a snapshot whose main thread still ticks.
		public static string Build(int editorPid, long updatedUtcMs, long mainThreadIdleMs, IList<EditorDialogInfo> dialogs)
		{
			var builder = new StringBuilder(256);
			builder.Append("{\n\t\"EditorPid\": ").Append(editorPid.ToString(CultureInfo.InvariantCulture));
			builder.Append(",\n\t\"UpdatedUtcMs\": ").Append(updatedUtcMs.ToString(CultureInfo.InvariantCulture));
			builder.Append(",\n\t\"MainThreadIdleMs\": ").Append(Math.Max(0, mainThreadIdleMs).ToString(CultureInfo.InvariantCulture));
			builder.Append(",\n\t\"Dialogs\": [");
			for (int index = 0; index < dialogs.Count; index++)
			{
				EditorDialogInfo dialog = dialogs[index];
				builder.Append(index == 0 ? "\n\t\t{" : ",\n\t\t{");
				AppendText(builder, "Id", dialog.Id, true);
				AppendText(builder, "Source", dialog.Source, false);
				AppendText(builder, "Kind", dialog.Kind, false);
				AppendText(builder, "Level", dialog.Level, false);
				AppendText(builder, "Title", dialog.Title, false);
				AppendText(builder, "Message", Limit(dialog.Message, MessageLimit), false);
				builder.Append(", \"Buttons\": [");
				int buttons = dialog.Buttons == null ? 0 : Math.Min(dialog.Buttons.Length, ButtonLimit);
				for (int button = 0; button < buttons; button++)
				{
					if (button > 0)
					{
						builder.Append(", ");
					}

					builder.Append('"').Append(Escape(dialog.Buttons[button])).Append('"');
				}

				builder.Append(']');
				AppendText(builder, "OpenedAtUtc", dialog.OpenedAtUtc, false);
				builder.Append(", \"OpenMs\": ").Append(dialog.OpenMs.ToString(CultureInfo.InvariantCulture));
				AppendText(builder, "TaskId", dialog.TaskId, false);
				builder.Append('}');
			}

			builder.Append(dialogs.Count == 0 ? "]\n}\n" : "\n\t]\n}\n");
			return builder.ToString();
		}

		public static string Limit(string value, int length)
		{
			if (string.IsNullOrEmpty(value))
			{
				return "";
			}

			return value.Length <= length ? value : value.Substring(0, length);
		}

		private static void AppendText(StringBuilder builder, string name, string value, bool first)
		{
			if (!first)
			{
				builder.Append(", ");
			}

			builder.Append('"').Append(name).Append("\": \"").Append(Escape(value)).Append('"');
		}

		// Unlike TelemetryJson.Escape this keeps the whole value: the caller has already limited it.
		private static string Escape(string value)
		{
			if (string.IsNullOrEmpty(value))
			{
				return "";
			}

			var builder = new StringBuilder(value.Length + 8);
			foreach (char symbol in value)
			{
				switch (symbol)
				{
					case '"':
						builder.Append("\\\"");
						break;
					case '\\':
						builder.Append("\\\\");
						break;
					case '\n':
						builder.Append("\\n");
						break;
					case '\r':
						builder.Append("\\r");
						break;
					case '\t':
						builder.Append("\\t");
						break;
					default:
						if (symbol < ' ')
						{
							builder.Append("\\u").Append(((int)symbol).ToString("x4", CultureInfo.InvariantCulture));
						}
						else
						{
							builder.Append(symbol);
						}

						break;
				}
			}

			return builder.ToString();
		}
	}
}
