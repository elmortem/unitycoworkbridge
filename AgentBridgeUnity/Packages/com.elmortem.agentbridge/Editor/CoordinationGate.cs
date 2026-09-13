using System;
using System.Collections.Generic;
using System.IO;
using AgentBridge.Coordination;

namespace AgentBridge
{
	// Every route that can run, serve or join a Unity task goes through here. Without active
	// registrations it answers "allow" for everything and the bridge behaves exactly as before.
	public static class CoordinationGate
	{
		private static readonly string[] StepKinds = { "csharp", "ui", "sceneshot", "compile", "tests" };
		private static readonly string[] FreeKinds = { "release", "stopplay", "cancel" };

		public static bool Coordinated
		{
			get { return CoordinationEditorAdapter.Coordinated; }
		}

		// Refuses what must not run at all and removes it from the queue. Tasks that merely wait
		// for their window are left in place: waiting is not a rejection.
		public static void Admit(List<PendingTaskInfo> pending, Action<PendingTaskInfo, string> reject)
		{
			if (pending == null || pending.Count == 0 || !Coordinated)
			{
				return;
			}

			for (int i = pending.Count - 1; i >= 0; i--)
			{
				PendingTaskInfo task = pending[i];
				TaskRequest request;
				if (!TaskRequestReader.TryRead(task.TaskFilePath, out request))
				{
					continue;
				}

				string reason;
				if (IsAdmitted(request, out reason))
				{
					continue;
				}

				reject(task, reason);
				pending.RemoveAt(i);
			}
		}

		public static bool IsAdmitted(TaskRequest request, out string reason)
		{
			reason = "";
			if (!Coordinated)
			{
				return true;
			}

			string kind = request.Kind ?? "";
			if (CoordinationText.Contains(FreeKinds, kind))
			{
				return true;
			}

			if (kind == "play")
			{
				reason = CoordinationCodes.Required
					+ ": play mode is not part of coordination-v1; take a PlayMode test step instead";
				return false;
			}

			if (!CoordinationText.Contains(StepKinds, kind))
			{
				return true;
			}

			if (string.IsNullOrEmpty(request.CoordinationWindowToken) || string.IsNullOrEmpty(request.CoordinationStepId))
			{
				if (string.IsNullOrEmpty(request.CoordinationWindowToken) && string.IsNullOrEmpty(request.CoordinationStepId)) return true;
				reason = CoordinationCodes.Required
					+ ": this project has active coordination registrations; submit with --coord-window and --coord-step";
				return false;
			}

			CoordinationState state = CoordinationEditorAdapter.Snapshot;
			if (state == null)
			{
				reason = CoordinationCodes.Corrupt + ": the coordination state is unreadable";
				return false;
			}

			CoordinationGrant grant = state.FindGrantByToken(request.CoordinationWindowToken);
			if (grant == null)
			{
				reason = CoordinationCodes.StaleToken + ": no live window matches this token";
				return false;
			}

			return true;
		}

		public static bool CanSchedule(PendingTaskInfo task)
		{
			if (!Coordinated || CoordinationText.Contains(FreeKinds, task.Kind)) return true;
			CoordinationState state = CoordinationEditorAdapter.Snapshot;
			CoordinationGrant window = state == null ? null : state.FindWindowGrant();
			if (window == null) return true;
			TaskRequest request;
			return TaskRequestReader.TryRead(task.TaskFilePath, out request) && request.CoordinationWindowToken == window.Token;
		}

		// Reserves the step and records the task id before any payload runs. The same task id may
		// re-enter after a domain reload; a different one finds the step consumed.
		public static bool TryReserve(TaskRequest request, string taskId, out string reason)
		{
			reason = "";
			if (!Coordinated || request == null)
			{
				return true;
			}

			string kind = request.Kind ?? "";
			if (!CoordinationText.Contains(StepKinds, kind))
			{
				return true;
			}

			if (string.IsNullOrEmpty(request.CoordinationWindowToken))
			{
				CoordinationState state = CoordinationEditorAdapter.Snapshot;
				if (state != null && state.FindWindowGrant() != null)
				{
					reason = CoordinationCodes.Busy;
					return false;
				}
				return true;
			}

			CoordinationCommand command = CoordinationEditorAdapter.NewCommand(CoordinationEngine.OpStepBegin);
			command.Session = request.AgentSessionId ?? "";
			command.Token = request.CoordinationWindowToken;
			command.StepId = request.CoordinationStepId;
			command.TaskId = taskId;
			command.Actual = DescribeActual(request);

			CoordinationReply reply;
			if (!CoordinationEditorAdapter.TryApply(command, out reply))
			{
				// The store is busy this tick. Deferring is correct: nothing was consumed.
				reason = CoordinationCodes.Busy;
				return false;
			}

			if (!reply.Ok)
			{
				reason = reply.Code + ": " + reply.Message;
				return false;
			}

			return true;
		}

		public static void Release(TaskRequest request, string taskId, bool success, string reason)
		{
			if (!Coordinated || request == null || string.IsNullOrEmpty(request.CoordinationWindowToken))
			{
				return;
			}

			CoordinationCommand command = CoordinationEditorAdapter.NewCommand(
				success ? CoordinationEngine.OpStepFinish : CoordinationEngine.OpStepFail);
			command.Session = request.AgentSessionId ?? "";
			command.Token = request.CoordinationWindowToken;
			command.StepId = request.CoordinationStepId;
			command.TaskId = taskId;
			command.Reason = reason ?? "";

			CoordinationReply reply;
			CoordinationEditorAdapter.TryApply(command, out reply);
		}

		public static void ReleaseByRecord(TaskRecord record, bool success, string reason)
		{
			if (record == null || string.IsNullOrEmpty(record.CoordinationWindowToken))
			{
				return;
			}

			var request = new TaskRequest
			{
				Kind = record.Kind,
				AgentSessionId = record.AgentSessionId,
				CoordinationWindowToken = record.CoordinationWindowToken,
				CoordinationStepId = record.CoordinationStepId
			};
			Release(request, record.Id, success, reason);
		}

		// True when a coordinated validation window owns the editor. Evidence only claims to cover
		// a controlled project inside one of these.
		public static bool InValidationWindow(out string windowId)
		{
			windowId = "";
			CoordinationState state = CoordinationEditorAdapter.Snapshot;
			if (state == null)
			{
				return false;
			}

			CoordinationGrant window = state.FindWindowGrant();
			if (window == null || window.Kind != CoordinationLimits.KindValidation)
			{
				return false;
			}

			windowId = window.RequestId;
			return true;
		}

		public static string[] DeclaredFixtureRoots()
		{
			CoordinationState state = CoordinationEditorAdapter.Snapshot;
			if (state == null)
			{
				return new string[0];
			}

			CoordinationGrant window = state.FindWindowGrant();
			if (window == null)
			{
				return new string[0];
			}

			CoordinationRequest request = state.FindRequest(window.RequestId);
			return request != null ? request.Plan.FixtureRoots : new string[0];
		}

		private static CoordinationStep DescribeActual(TaskRequest request)
		{
			var step = new CoordinationStep
			{
				Id = request.CoordinationStepId ?? "",
				Kind = request.Kind ?? "",
				Fresh = request.Fresh
			};

			if (request.Kind == "tests")
			{
				step.Mode = request.TestMode == "PlayMode" ? "PlayMode" : "EditMode";
				step.Assemblies = request.AssemblyNames ?? new string[0];
				step.Tests = request.TestNames ?? new string[0];
				step.Categories = request.CategoryNames ?? new string[0];
			}
			else if (request.Kind == "csharp" || request.Kind == "ui" || request.Kind == "sceneshot")
			{
				step.PayloadSha256 = PayloadDigest(request);
			}

			step.Normalize();
			return step;
		}

		private static string PayloadDigest(TaskRequest request)
		{
			string suffix = request.Kind == "ui" ? ".ui.json" : request.Kind == "sceneshot" ? ".sceneshot.json" : ".cs";
			string path = Path.Combine(BridgePaths.Inbox, request.Id + suffix);
			try
			{
				if (!File.Exists(path))
				{
					return "";
				}

				using (var sha = System.Security.Cryptography.SHA256.Create())
				{
					return CoordinationDigest.Hex(sha.ComputeHash(File.ReadAllBytes(path)));
				}
			}
			catch (Exception)
			{
				return "";
			}
		}
	}
}
