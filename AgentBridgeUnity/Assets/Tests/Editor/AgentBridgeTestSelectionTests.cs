using System.Collections.Generic;
using AgentBridge;
using NUnit.Framework;

namespace AgentBridge.Tests
{
	public class AgentBridgeTestSelectionTests
	{
		private static List<TestNameResolver.Node> Catalog()
		{
			return new List<TestNameResolver.Node>
			{
				new TestNameResolver.Node { Name = "Fixture", FullName = "One.Fixture", Assembly = "A", IsSuite = true },
				new TestNameResolver.Node { Name = "Case(1)", FullName = "One.Fixture.Case(1)", Assembly = "A",
					Ancestors = new List<string> { "One.Fixture" }, Categories = new List<string> { "Fast" } },
				new TestNameResolver.Node { Name = "Case(2)", FullName = "One.Fixture.Case(2)", Assembly = "A",
					Ancestors = new List<string> { "One.Fixture" }, Categories = new List<string> { "Slow" } }
			};
		}

		[TestCase("Fixture")]
		[TestCase("One.Fixture")]
		public void FixtureResolvesWithoutCountingMethods(string name)
		{
			Assert.IsTrue(TestNameResolver.TryResolve(Catalog(), new[] { name }, null, null,
				out var resolved, out var status, out var message), message);
			CollectionAssert.AreEqual(new[] { "One.Fixture" }, resolved);
		}

		[Test]
		public void EveryRequestedNameMustMatch()
		{
			Assert.IsFalse(TestNameResolver.TryResolve(Catalog(), new[] { "Fixture", "Typo" }, null, null,
				out _, out var status, out var message));
			Assert.AreEqual("no_tests_matched", status);
			StringAssert.Contains("Typo", message);
		}

		[Test]
		public void AmbiguityListsCandidatesAndAssemblyCanDisambiguate()
		{
			var catalog = Catalog();
			catalog.Add(new TestNameResolver.Node { Name = "Fixture", FullName = "Two.Fixture", Assembly = "B", IsSuite = true });
			Assert.IsFalse(TestNameResolver.TryResolve(catalog, new[] { "Fixture" }, null, null, out _, out var status, out var message));
			Assert.AreEqual("ambiguous_test_filter", status);
			StringAssert.Contains("One.Fixture [A]", message);
			StringAssert.Contains("Two.Fixture [B]", message);
			Assert.IsTrue(TestNameResolver.TryResolve(catalog, new[] { "Fixture" }, new[] { "A" }, null, out _, out _, out _));
			Assert.IsTrue(TestNameResolver.TryResolve(catalog, new[] { "One.Fixture" }, null, null, out _, out _, out _));
		}

		[Test]
		public void CategoryIntersectionMustContainACaseForEachName()
		{
			Assert.IsFalse(TestNameResolver.TryResolve(Catalog(), new[] { "Fixture" }, null, new[] { "Absent" }, out _, out var status, out _));
			Assert.AreEqual("no_tests_matched", status);
			Assert.IsTrue(TestNameResolver.TryResolve(Catalog(), new[] { "Fixture" }, null, new[] { "Fast" }, out _, out _, out _));
		}

		[Test]
		public void EmptyRunIsNotSuccess()
		{
			Assert.AreEqual("no_tests_matched", TestResultAggregator.StatusOf(TestResultAggregator.Aggregate(new List<TestCaseResult>())));
			Assert.AreEqual("success", TestResultAggregator.StatusOf(new TestRunResult { total = 1, passed = 1 }));
			Assert.AreEqual("test_failure", TestResultAggregator.StatusOf(new TestRunResult { total = 1, failed = 1 }));
		}

		[Test]
		public void BroadCacheCannotHideOneMissingSelector()
		{
			var dump = new TestRunDump { Catalog = new TestNameResolver.CatalogData { Nodes = Catalog() } };
			dump.Entries.Add(new TestCaseResult { FullName = "One.Fixture.Case(1)", Assembly = "A",
				Status = "Passed" });
			dump.Entries.Add(new TestCaseResult { FullName = "One.Fixture.Case(2)", Assembly = "A", Status = "Passed" });
			var request = new TaskRequest { TestNames = new[] { "One.Fixture", "One.Typo" } };
			Assert.IsFalse(TestFilterCoverage.Covers(dump, request));
			request.TestNames = new[] { "One.Fixture" };
			Assert.IsTrue(TestFilterCoverage.Covers(dump, request));
			Assert.AreEqual(2, TestFilterCoverage.Select(dump, request).Count);
			request.TestNames = new[] { "Fixture" };
			Assert.IsTrue(TestFilterCoverage.Covers(dump, request), "complete catalog resolves short names without a run");
			Assert.AreEqual(2, TestFilterCoverage.Select(dump, request).Count);
			dump.Catalog.Nodes.Add(new TestNameResolver.Node { Name = "Fixture", FullName = "Two.Fixture", Assembly = "B", IsSuite = true });
			Assert.IsFalse(TestFilterCoverage.Covers(dump, request), "ambiguity must include fixtures absent from the results");
			request.AssemblyNames = new[] { "A" };
			Assert.IsTrue(TestFilterCoverage.Covers(dump, request));
		}

		[Test]
		public void LeafOnlyCacheCannotClaimWholeFixtureCoverage()
		{
			var dump = new TestRunDump { Catalog = new TestNameResolver.CatalogData { Nodes = Catalog() } };
			dump.Filter.TestNames = new[] { "One.Fixture.Case(1)" };
			dump.Entries.Add(new TestCaseResult { FullName = "One.Fixture.Case(1)", Assembly = "A" });
			Assert.IsFalse(TestFilterCoverage.Covers(dump, new TaskRequest { TestNames = new[] { "One.Fixture" } }));
			Assert.IsTrue(TestFilterCoverage.Covers(dump, new TaskRequest { TestNames = new[] { "Case(1)" } }));
		}

		[Test]
		public void OldCacheWithoutCatalogCannotClaimCoverage()
		{
			var dump = new TestRunDump { Version = 2 };
			dump.Entries.Add(new TestCaseResult { FullName = "One.Fixture.Case(1)", Assembly = "A" });
			Assert.IsFalse(TestFilterCoverage.Covers(dump, new TaskRequest { TestNames = new[] { "One.Fixture.Case(1)" } }));
		}

		[Test]
		public void FilterAliasesShareTheInputContext()
		{
			Assert.AreEqual(ValidationEvidence.ContextOf("EditMode", "Fixture"), ValidationEvidence.ContextOf("EditMode", "One.Fixture"));
			Assert.AreNotEqual(ValidationEvidence.ContextOf("EditMode", "Fixture"), ValidationEvidence.ContextOf("PlayMode", "Fixture"));
		}
	}
}
