using System;
using System.Globalization;
using System.Text;

namespace AgentBridge
{
	// dialog-answer-v1: Library/AgentBridge/dialog-answer.txt, written by `agentbridge dialog answer`
	// and read by EditorDialogMonitor on its background timer while a modal dialog holds the main
	// thread. Plain "Name=value" lines, so neither side needs a JSON library that works off the main
	// thread. The CLI compiles this file too.
	public sealed class DialogAnswerRequest
	{
		public const string Capability = "dialog-answer-v1";
		public const int ReasonLimit = 200;
		public const int NotFound = -1;
		public const int Ambiguous = -2;

		public const string Sent = "sent";
		public const string NotBlocked = "not_blocked";
		public const string DialogNotOpen = "dialog_not_open";
		public const string DialogChanged = "dialog_changed";
		public const string ButtonNotFound = "button_not_found";
		public const string AmbiguousButton = "ambiguous_button";
		public const string SendFailed = "send_failed";

		public string RequestId = "";
		public int EditorPid;
		public string DialogId = "";
		public string Title = "";
		public string Button = "";
		public string Session = "";
		public string Reason = "";

		public string Format()
		{
			var builder = new StringBuilder(256);
			Append(builder, "RequestId", RequestId);
			Append(builder, "EditorPid", EditorPid.ToString(CultureInfo.InvariantCulture));
			Append(builder, "DialogId", DialogId);
			Append(builder, "Title", Title);
			Append(builder, "Button", Button);
			Append(builder, "Session", Session);
			Append(builder, "Reason", Reason);
			return builder.ToString();
		}

		public static DialogAnswerRequest Parse(string text)
		{
			if (string.IsNullOrEmpty(text))
			{
				return null;
			}

			var request = new DialogAnswerRequest();
			bool pid = false;
			foreach (string line in text.Split('\n'))
			{
				int equals = line.IndexOf('=');
				if (equals <= 0)
				{
					continue;
				}

				string value = line.Substring(equals + 1).TrimEnd('\r');
				switch (line.Substring(0, equals))
				{
					case "RequestId":
						request.RequestId = value;
						break;
					case "EditorPid":
						pid = int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out request.EditorPid);
						break;
					case "DialogId":
						request.DialogId = value;
						break;
					case "Title":
						request.Title = value;
						break;
					case "Button":
						request.Button = value;
						break;
					case "Session":
						request.Session = value;
						break;
					case "Reason":
						request.Reason = value;
						break;
				}
			}

			if (!pid || request.RequestId.Length == 0 || request.DialogId.Length == 0 || request.Button.Length == 0)
			{
				return null;
			}

			return request;
		}

		public static string OneLine(string value)
		{
			if (value == null)
			{
				return "";
			}

			return value.Replace('\r', ' ').Replace('\n', ' ').Trim();
		}

		// Labels come from the window scan; the agent types what `status` showed it, possibly with a
		// typographic apostrophe where the dialog has a straight one.
		public static int MatchButton(string[] labels, string wanted)
		{
			if (labels == null)
			{
				return NotFound;
			}

			string target = Normalize(wanted);
			int found = NotFound;
			for (int index = 0; index < labels.Length; index++)
			{
				if (!string.Equals(Normalize(labels[index]), target, StringComparison.OrdinalIgnoreCase))
				{
					continue;
				}

				if (found != NotFound)
				{
					return Ambiguous;
				}

				found = index;
			}

			return found;
		}

		private static string Normalize(string value)
		{
			return OneLine(value).Replace('’', '\'').Replace('‘', '\'');
		}

		private static void Append(StringBuilder builder, string name, string value)
		{
			builder.Append(name).Append('=').Append(OneLine(value)).Append('\n');
		}
	}
}
