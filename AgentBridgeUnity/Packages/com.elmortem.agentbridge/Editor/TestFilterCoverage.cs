using System.Collections.Generic;

namespace AgentBridge
{
	public static class TestFilterCoverage
	{
		public static bool Covers(TestRunDump dump, TaskRequest request)
		{
			List<TestNameResolver.Node> expected;
			if (!TrySelectCatalog(dump, request, out expected) || expected.Count == 0) return false;
			var remaining = new Dictionary<string, int>();
			foreach (TestCaseResult entry in dump.Entries)
			{
				string key = Key(entry.Assembly, entry.FullName);
				int count;
				remaining.TryGetValue(key, out count);
				remaining[key] = count + 1;
			}
			foreach (TestNameResolver.Node test in expected)
			{
				string key = Key(test.Assembly, test.FullName);
				int count;
				if (!remaining.TryGetValue(key, out count) || count == 0) return false;
				remaining[key] = count - 1;
			}
			return true;
		}

		private static bool TrySelectCatalog(TestRunDump dump, TaskRequest request, out List<TestNameResolver.Node> expected)
		{
			expected = null;
			// Old records have no complete discovery catalog and cannot prove selection coverage.
			if (dump.Catalog == null || dump.Catalog.Nodes == null) return false;
			string[] resolved;
			string status;
			string message;
			if (!TestNameResolver.TryResolve(dump.Catalog.Nodes, request.TestNames, request.AssemblyNames,
				request.CategoryNames, out resolved, out status, out message)) return false;
			expected = TestNameResolver.SelectCases(dump.Catalog.Nodes, resolved, request.AssemblyNames, request.CategoryNames);
			return true;
		}

		public static List<TestCaseResult> Select(TestRunDump dump, TaskRequest request)
		{
			List<TestNameResolver.Node> expected;
			if (!TrySelectCatalog(dump, request, out expected)) return new List<TestCaseResult>();
			var keys = new HashSet<string>();
			foreach (TestNameResolver.Node test in expected) keys.Add(Key(test.Assembly, test.FullName));
			return dump.Entries.FindAll(entry => keys.Contains(Key(entry.Assembly, entry.FullName)));
		}

		private static string Key(string assembly, string fullName)
		{
			assembly = assembly ?? "";
			return assembly.Length + ":" + assembly + fullName;
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

	}
}
