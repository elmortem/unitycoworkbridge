using System;
using System.IO;
using System.Text;
using UnityEngine;
using AgentBridge.Coordination;

namespace AgentBridge
{
	// Runs only when the coordinator confirms that execution AND scene recovery have stopped.
	// No client process is needed after submission. Stable task ids make publication recoverable.
	public static class CoordinationBatchPump
	{
		public static void Tick()
		{
			CoordinationState state = CoordinationEditorAdapter.Snapshot;
			CoordinationGrant grant = state == null ? null : state.FindWindowGrant();
			if (grant == null || grant.State != CoordinationLimits.GrantActive) return;
			CoordinationRequest batch = state.FindRequest(grant.RequestId);
			if (batch == null || !batch.Automatic) return;
			if (state.InputRepairPending && batch.Kind == CoordinationLimits.KindValidation && grant.ActiveTaskIds.Length == 0)
			{
				Finish(grant, "failed:compiler_error:input_repair_pending");
				return;
			}
			try
			{
				int index = CoordinationBatch.NextStep(batch, grant);
				if (index < 0) { Finish(grant, "completed"); return; }
				CoordinationStep step = batch.Plan.Steps[index];
				CoordinationStepUse use = grant.FindStep(step.Id);
				if (use != null && use.State == CoordinationLimits.StepFailed)
				{
					Finish(grant, "failed:" + step.Id + ":" + use.Reason);
					return;
				}
				string id = CoordinationBatch.TaskId(batch, index);
				string journal = Path.Combine(BridgePaths.Journal, id + ".json");
				if (File.Exists(journal))
				{
					TaskRecord record = JsonUtility.FromJson<TaskRecord>(File.ReadAllText(journal));
					if (record == null || string.IsNullOrEmpty(record.FinishedAtUtc)) return;
					// Reconcile a terminal journal if the completion transaction lost a lock race.
					if (use == null) { Finish(grant, "failed:" + step.Id + ":" + record.Status); return; }
					CoordinationCommand done = CoordinationEditorAdapter.NewCommand(record.Status == "success" ? CoordinationEngine.OpStepFinish : CoordinationEngine.OpStepFail);
					done.Session = grant.Session; done.Token = grant.Token; done.StepId = step.Id; done.TaskId = id; done.Reason = record.Status;
					CoordinationReply result;
					CoordinationEditorAdapter.TryApply(done, out result);
					return;
				}
				if (use != null) return;
				string taskFile = Path.Combine(BridgePaths.Inbox, id + ".task.json");
				if (File.Exists(taskFile)) return;
				var task = new TaskRequest
				{
					Id = id, Kind = step.Kind, AgentSessionId = batch.Session,
					Note = "batch " + batch.Id + " step " + step.Id,
					CoordinationWindowToken = grant.Token, CoordinationStepId = step.Id,
					TestMode = step.Mode, AssemblyNames = step.Assemblies, TestNames = step.Tests,
					CategoryNames = step.Categories, Fresh = step.Fresh, EntryPointName = step.PayloadName
				};
				if (!string.IsNullOrEmpty(step.Payload))
				{
					task.PayloadFile = id + (step.Kind == "csharp" ? ".cs" : step.Kind == "ui" ? ".ui.json" : ".sceneshot.json");
					Publish(Path.Combine(BridgePaths.Inbox, task.PayloadFile), step.Payload);
				}
				Publish(taskFile, JsonUtility.ToJson(task));
			}
			catch (Exception error)
			{
				Finish(grant, "batch_dispatch_failed:" + error.Message);
			}
		}

		private static void Finish(CoordinationGrant grant, string reason)
		{
			CoordinationCommand finish = CoordinationEditorAdapter.NewCommand(CoordinationEngine.OpFinish);
			finish.Session = grant.Session; finish.Token = grant.Token; finish.Reason = reason;
			CoordinationReply reply;
			CoordinationEditorAdapter.TryApply(finish, out reply);
		}

		private static void Publish(string path, string content)
		{
			if (File.Exists(path))
			{
				if (File.ReadAllText(path) != content) throw new IOException("batch publication conflict: " + Path.GetFileName(path));
				return;
			}
			string temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
			File.WriteAllText(temporary, content, new UTF8Encoding(false));
			File.Move(temporary, path);
		}
	}
}
