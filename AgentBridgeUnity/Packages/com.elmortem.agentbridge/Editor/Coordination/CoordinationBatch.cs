using System;
using System.Text;

namespace AgentBridge.Coordination
{
	// A request is executable before it enters FIFO. Source paths are resolved only by the CLI;
	// the editor consumes the frozen content and never reopens the agent's source file.
	public static class CoordinationBatch
	{
		public const string Capability = "coordination-batch-v1";
		public const int MaxPayloadBytes = 4 * 1024 * 1024;

		public static bool Validate(CoordinationPlan plan, out string error)
		{
			error = "";
			long bytes = 0;
			foreach (CoordinationStep step in plan.Steps)
			{
				if (step.Kind != "csharp" && step.Kind != "ui" && step.Kind != "sceneshot") continue;
				bytes += Encoding.UTF8.GetByteCount(step.Payload);
				if (string.IsNullOrWhiteSpace(step.Payload) || !SafeName(step.PayloadName)
					|| !string.IsNullOrEmpty(step.PayloadFile)
					|| !string.Equals(CoordinationDigest.Sha256(step.Payload), step.PayloadSha256, StringComparison.OrdinalIgnoreCase))
				{
					error = "step " + step.Id + " requires frozen Payload, safe PayloadName and matching PayloadSha256; submit the complete package";
					return false;
				}
			}
			if (bytes > MaxPayloadBytes) { error = "batch payload exceeds 4 MiB"; return false; }
			return true;
		}

		public static bool SafeName(string name)
		{
			if (string.IsNullOrEmpty(name) || name.Length > 128) return false;
			foreach (char c in name)
				if (!(c >= 'A' && c <= 'Z') && !(c >= 'a' && c <= 'z') && !(c >= '0' && c <= '9') && c != '_') return false;
			return true;
		}

		public static string TaskId(CoordinationRequest request, int index)
		{
			return "Batch_" + CoordinationDigest.Sha256(request.Token).Substring(0, 24) + "_" + index;
		}

		public static int NextStep(CoordinationRequest request, CoordinationGrant grant)
		{
			for (int i = 0; i < request.Plan.Steps.Count; i++)
			{
				CoordinationStepUse use = grant.FindStep(request.Plan.Steps[i].Id);
				if (use == null || use.State != CoordinationLimits.StepDone) return i;
			}
			return -1;
		}
	}
}
