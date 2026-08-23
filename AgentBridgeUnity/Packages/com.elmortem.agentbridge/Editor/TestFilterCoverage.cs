using System.Collections.Generic;

namespace AgentBridge
{
	public static class TestFilterCoverage
	{
		public static bool Covers(TestRunDump dump, TaskRequest request)
		{
			TestRunFilter filter = dump.Filter;

			if (IsEmpty(filter.AssemblyNames) && IsEmpty(filter.TestNames) && IsEmpty(filter.CategoryNames))
			{
				return true;
			}

			if (!IsEmpty(request.TestNames) && AllNamesPresent(dump.Entries, request.TestNames))
			{
				return true;
			}

			if (!IsEmpty(filter.AssemblyNames) && IsEmpty(filter.TestNames) && IsEmpty(filter.CategoryNames)
				&& !IsEmpty(request.AssemblyNames) && IsSubset(request.AssemblyNames, filter.AssemblyNames))
			{
				return true;
			}

			return SetsEqual(filter.AssemblyNames, request.AssemblyNames)
				&& SetsEqual(filter.TestNames, request.TestNames)
				&& SetsEqual(filter.CategoryNames, request.CategoryNames);
		}

		public static bool CoversFilterOnly(TestRunFilter filter, TaskRequest request)
		{
			if (IsEmpty(filter.AssemblyNames) && IsEmpty(filter.TestNames) && IsEmpty(filter.CategoryNames))
			{
				return true;
			}

			if (!IsEmpty(filter.AssemblyNames) && IsEmpty(filter.TestNames) && IsEmpty(filter.CategoryNames)
				&& !IsEmpty(request.AssemblyNames) && IsSubset(request.AssemblyNames, filter.AssemblyNames))
			{
				return true;
			}

			return SetsEqual(filter.AssemblyNames, request.AssemblyNames)
				&& SetsEqual(filter.TestNames, request.TestNames)
				&& SetsEqual(filter.CategoryNames, request.CategoryNames);
		}

		public static List<TestCaseResult> Select(List<TestCaseResult> entries, TaskRequest request)
		{
			var selected = new List<TestCaseResult>();
			var assemblies = ToSet(request.AssemblyNames);
			var names = ToSet(request.TestNames);
			var categories = ToSet(request.CategoryNames);

			foreach (TestCaseResult entry in entries)
			{
				if (assemblies != null && !assemblies.Contains(entry.Assembly))
				{
					continue;
				}

				if (names != null && !names.Contains(entry.FullName))
				{
					continue;
				}

				if (categories != null && !HasAnyCategory(entry, categories))
				{
					continue;
				}

				selected.Add(entry);
			}

			return selected;
		}

		private static bool HasAnyCategory(TestCaseResult entry, HashSet<string> categories)
		{
			foreach (string category in entry.Categories)
			{
				if (categories.Contains(category))
				{
					return true;
				}
			}

			return false;
		}

		private static bool AllNamesPresent(List<TestCaseResult> entries, string[] names)
		{
			var present = new HashSet<string>();
			foreach (TestCaseResult entry in entries)
			{
				present.Add(entry.FullName);
			}

			foreach (string name in names)
			{
				if (!present.Contains(name))
				{
					return false;
				}
			}

			return true;
		}

		private static bool IsSubset(string[] inner, string[] outer)
		{
			var outerSet = new HashSet<string>(outer);
			foreach (string item in inner)
			{
				if (!outerSet.Contains(item))
				{
					return false;
				}
			}

			return true;
		}

		private static bool SetsEqual(string[] left, string[] right)
		{
			var leftSet = new HashSet<string>(left ?? new string[0]);
			var rightSet = new HashSet<string>(right ?? new string[0]);
			return leftSet.SetEquals(rightSet);
		}

		private static bool IsEmpty(string[] values)
		{
			return values == null || values.Length == 0;
		}

		private static HashSet<string> ToSet(string[] values)
		{
			if (values == null || values.Length == 0)
			{
				return null;
			}

			return new HashSet<string>(values);
		}
	}
}
