using System;
using System.Collections.Generic;
using System.Text;

namespace AgentBridge.Coordination
{
	// What a plan may contain, and whether the payload the editor is about to run is the one the
	// plan declared. A token without a matching step authorises nothing.
	public static class CoordinationPlanRules
	{
		private static readonly string[] ValidationKinds = { "compile", "tests", "sceneshot" };
		private static readonly string[] EditorKinds = { "csharp", "ui", "sceneshot", "compile" };

		public static bool Validate(CoordinationPlan plan, string windowKind, out string error)
		{
			error = "";
			if (plan == null || plan.Steps.Count == 0)
			{
				error = "plan has no steps";
				return false;
			}

			if (plan.Steps.Count > CoordinationLimits.MaxPlanSteps)
			{
				error = "plan has more than " + CoordinationLimits.MaxPlanSteps + " steps";
				return false;
			}

			string[] allowed = windowKind == CoordinationLimits.KindValidation ? ValidationKinds : EditorKinds;
			var ids = new List<string>();

			foreach (CoordinationStep step in plan.Steps)
			{
				if (string.IsNullOrEmpty(step.Id))
				{
					error = "every step needs an Id";
					return false;
				}

				if (ids.Contains(step.Id))
				{
					error = "duplicate step id " + step.Id;
					return false;
				}

				ids.Add(step.Id);

				if (!CoordinationText.Contains(allowed, step.Kind))
				{
					error = "step " + step.Id + " kind '" + step.Kind + "' is not allowed in a " + windowKind + " window";
					return false;
				}

				if (step.Kind == "tests")
				{
					if (step.Mode != "EditMode" && step.Mode != "PlayMode")
					{
						error = "step " + step.Id + " must set Mode to EditMode or PlayMode";
						return false;
					}

					if (step.Assemblies.Length == 0 && step.Tests.Length == 0 && step.Categories.Length == 0)
					{
						error = "step " + step.Id + " must declare an exact non-empty test filter";
						return false;
					}
				}
				else if (step.Kind == "csharp" || step.Kind == "ui" || step.Kind == "sceneshot")
				{
					if (string.IsNullOrEmpty(step.PayloadSha256))
					{
						error = "step " + step.Id + " must declare PayloadSha256";
						return false;
					}
				}
			}

			foreach (string root in plan.FixtureRoots)
			{
				string normalized;
				string scopeError;
				if (!CoordinationScope.TryNormalizeOne(root, out normalized, out scopeError))
				{
					error = "fixture root: " + scopeError;
					return false;
				}

				if (!normalized.EndsWith("/", StringComparison.Ordinal))
				{
					error = "fixture root must be a directory: " + root;
					return false;
				}
			}

			return true;
		}

		public static bool Matches(CoordinationStep planned, CoordinationStep actual, out string error)
		{
			error = "";
			if (actual == null)
			{
				error = "no payload description was supplied for step " + planned.Id;
				return false;
			}

			if (!string.Equals(planned.Kind, actual.Kind, StringComparison.Ordinal))
			{
				error = "step " + planned.Id + " plans kind '" + planned.Kind + "' but the task is '" + actual.Kind + "'";
				return false;
			}

			if (planned.Fresh != actual.Fresh)
			{
				error = "step " + planned.Id + " plans Fresh=" + planned.Fresh + " but the task has Fresh=" + actual.Fresh;
				return false;
			}

			if (planned.Kind == "tests")
			{
				if (!string.Equals(planned.Mode, actual.Mode, StringComparison.Ordinal))
				{
					error = "step " + planned.Id + " plans " + planned.Mode + " but the task runs " + actual.Mode;
					return false;
				}

				if (!SameSet(planned.Assemblies, actual.Assemblies)
					|| !SameSet(planned.Tests, actual.Tests)
					|| !SameSet(planned.Categories, actual.Categories))
				{
					error = "step " + planned.Id + " plans a different test filter than the task requests";
					return false;
				}
			}
			else if (planned.Kind == "csharp" || planned.Kind == "ui" || planned.Kind == "sceneshot")
			{
				if (!string.Equals(planned.PayloadSha256, actual.PayloadSha256, StringComparison.OrdinalIgnoreCase))
				{
					error = "step " + planned.Id + " plans a different payload than the task carries";
					return false;
				}
			}

			return true;
		}

		public static string Digest(string windowKind, int seconds, CoordinationPlan plan)
		{
			var builder = new StringBuilder();
			builder.Append(windowKind).Append('\n').Append(seconds).Append('\n');
			foreach (CoordinationStep step in plan.Steps)
			{
				builder.Append(step.Id).Append('|')
					.Append(step.Kind).Append('|')
					.Append(step.Mode).Append('|')
					.Append(Join(step.Assemblies)).Append('|')
					.Append(Join(step.Tests)).Append('|')
					.Append(Join(step.Categories)).Append('|')
					.Append(step.PayloadSha256).Append('|')
					.Append(step.Fresh ? "1" : "0").Append('\n');
			}

			builder.Append(Join(plan.ArtifactRoots)).Append('\n').Append(Join(plan.FixtureRoots));
			return CoordinationDigest.Sha256(builder.ToString());
		}

		private static string Join(string[] values)
		{
			if (values == null || values.Length == 0)
			{
				return "";
			}

			var sorted = new List<string>(values);
			sorted.Sort(StringComparer.Ordinal);
			return string.Join(",", sorted.ToArray());
		}

		private static bool SameSet(string[] left, string[] right)
		{
			left = left ?? new string[0];
			right = right ?? new string[0];
			if (left.Length != right.Length)
			{
				return false;
			}

			foreach (string item in left)
			{
				if (!CoordinationText.Contains(right, item))
				{
					return false;
				}
			}

			return true;
		}
	}
}
