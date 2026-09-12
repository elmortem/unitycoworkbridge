using System;
using System.Collections.Generic;
using System.IO;
using UnityEditor.TestTools.TestRunner.Api;

namespace AgentBridge
{
	public static class TestNameResolver
	{
		[Serializable]
		public sealed class CatalogData
		{
			public List<Node> Nodes = new List<Node>();
		}

		[Serializable]
		public sealed class Node
		{
			public string Name;
			public string FullName;
			public string Assembly;
			public bool IsSuite;
			public List<string> Ancestors = new List<string>();
			public List<string> Categories = new List<string>();
		}

		public static List<Node> Catalog(ITestAdaptor root)
		{
			var nodes = new List<Node>();
			Collect(root, "", new List<string>(), new List<string>(), nodes);
			return nodes;
		}

		private static void Collect(ITestAdaptor test, string assembly, List<string> ancestors,
			List<string> categories, List<Node> nodes)
		{
			if (test == null) return;
			if (test.IsTestAssembly) assembly = Path.GetFileNameWithoutExtension(test.FullName);
			var inherited = new List<string>(categories);
			if (test.Categories != null) inherited.AddRange(test.Categories);
			nodes.Add(new Node { Name = test.Name, FullName = test.FullName, Assembly = assembly,
				IsSuite = test.IsSuite, Ancestors = new List<string>(ancestors), Categories = inherited });
			var parents = new List<string>(ancestors) { test.FullName };
			if (test.HasChildren)
				foreach (ITestAdaptor child in test.Children) Collect(child, assembly, parents, inherited, nodes);
		}

		public static bool TryResolve(List<Node> nodes, string[] names, string[] assemblies, string[] categories,
			out string[] resolved, out string status, out string message)
		{
			var result = new List<string>();
			resolved = new string[0];
			status = null;
			message = null;
			foreach (string name in names ?? new string[0])
			{
				var exact = new List<Node>();
				var shortNames = new List<Node>();
				foreach (Node node in nodes)
				{
					if (!Includes(assemblies, node.Assembly)) continue;
					if (node.FullName == name) exact.Add(node);
					else if (node.Name == name) shortNames.Add(node);
				}
				List<Node> matches = exact.Count > 0 ? exact : shortNames;
				if (matches.Count != 1)
				{
					status = matches.Count == 0 ? "no_tests_matched" : "ambiguous_test_filter";
					var candidates = new List<string>();
					foreach (Node node in matches) candidates.Add(node.FullName + " [" + node.Assembly + "]");
					candidates.Sort(StringComparer.Ordinal);
					message = "--test '" + name + "': " + (matches.Count == 0
						? "no matching test or fixture. Check the full name, --mode and --assembly."
						: "ambiguous; use a full name and, if needed, --assembly: " + string.Join(", ", candidates));
					return false;
				}
				string fullName = matches[0].FullName;
				bool hasCase = nodes.Exists(node => !node.IsSuite && Includes(assemblies, node.Assembly)
					&& (node.FullName == fullName || node.Ancestors.Contains(fullName)) && HasCategory(categories, node.Categories));
				if (!hasCase)
				{
					status = "no_tests_matched";
					message = "--test '" + name + "' has no test cases matching the assembly/category filters.";
					return false;
				}
				if (!result.Contains(fullName)) result.Add(fullName);
			}
			resolved = result.ToArray();
			return true;
		}

		public static List<Node> SelectCases(List<Node> nodes, string[] resolved, string[] assemblies, string[] categories)
		{
			return nodes.FindAll(node => !node.IsSuite && Includes(assemblies, node.Assembly)
				&& HasCategory(categories, node.Categories)
				&& (resolved == null || resolved.Length == 0 || Array.Exists(resolved,
					name => node.FullName == name || node.Ancestors.Contains(name))));
		}

		private static bool Includes(string[] values, string value)
		{
			return values == null || values.Length == 0 || Array.IndexOf(values, value) >= 0;
		}

		private static bool HasCategory(string[] requested, List<string> categories)
		{
			if (requested == null || requested.Length == 0) return true;
			foreach (string category in requested) if (categories.Contains(category)) return true;
			return false;
		}
	}
}
