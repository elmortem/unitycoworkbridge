using System;
using System.Collections.Generic;
using System.IO;

namespace AgentBridge
{
	// A hit needs the exact input digest, the same mode, one set that covers the whole request on
	// its own, a non-empty selection and every mandatory artifact still on disk. Results from
	// different digests are never joined, and neither are results from different sets.
	public static class TestCacheQuery
	{
		public sealed class Hit
		{
			public string EntryId = "";
			public string SourceTaskId = "";
			public string Status = "";
			public TestRunResult Result;
			public List<string> Artifacts = new List<string>();
		}

		// sourceFingerprint is the cheap path/size/mtime hash: it is a necessary precondition and
		// filters out the common miss before the expensive content digest is ever computed.
		public static bool TryServe(
			TaskRequest request,
			string sourceFingerprint,
			Func<string> inputDigestFactory,
			long nowMs,
			out Hit hit)
		{
			hit = null;
			if (request.Fresh)
			{
				return false;
			}

			string mode = request.TestMode == "PlayMode" ? "PlayMode" : "EditMode";
			TestCacheIndex index = TestRunDumpStore.ReadIndex();
			var candidates = new List<TestCacheEntryInfo>();

			foreach (TestCacheEntryInfo entry in index.Entries)
			{
				if (entry.TestMode != mode)
				{
					continue;
				}

				if (entry.Validity != EvidenceRecord.Valid)
				{
					continue;
				}

				if (string.IsNullOrEmpty(entry.SourceFingerprint) || entry.SourceFingerprint != sourceFingerprint)
				{
					continue;
				}

				candidates.Add(entry);
			}

			if (candidates.Count == 0)
			{
				return false;
			}

			string inputDigest = inputDigestFactory();
			if (string.IsNullOrEmpty(inputDigest))
			{
				return false;
			}

			// Newest first: an older set with the same inputs is still correct, but the freshest
			// one keeps the served diagnostics closest to what the agent just did.
			candidates.Sort(delegate(TestCacheEntryInfo left, TestCacheEntryInfo right)
			{
				return string.CompareOrdinal(right.FinishedAtUtc ?? "", left.FinishedAtUtc ?? "");
			});

			foreach (TestCacheEntryInfo entry in candidates)
			{
				if (entry.InputDigest != inputDigest)
				{
					continue;
				}

				TestRunDump dump;
				if (!TestRunDumpStore.TryLoad(entry.Id, out dump))
				{
					// A damaged or evicted payload is skipped with a note, not treated as a hit.
					TelemetryLog.Write("cache_skip", "", entry.SourceTaskId ?? "", new[]
					{
						TelemetryField.Text("What", "entry_unreadable"),
						TelemetryField.Text("Entry", entry.Id)
					});
					continue;
				}

				if (!TestFilterCoverage.Covers(dump, request))
				{
					continue;
				}

				List<TestCaseResult> selected = TestFilterCoverage.Select(dump.Entries, request);
				if (selected.Count == 0)
				{
					// An empty subset is not a pass.
					continue;
				}

				if (!ArtifactsPresent(dump))
				{
					// A screenshot that no longer exists cannot be handed out as visual acceptance.
					continue;
				}

				TestRunResult result = TestResultAggregator.Aggregate(selected);
				hit = new Hit
				{
					EntryId = entry.Id,
					SourceTaskId = dump.SourceTaskId,
					Status = TestResultAggregator.StatusOf(result),
					Result = result,
					Artifacts = dump.Artifacts ?? new List<string>()
				};
				TestRunDumpStore.Touch(entry.Id, nowMs);
				return true;
			}

			return false;
		}

		public static bool ArtifactsPresent(TestRunDump dump)
		{
			if (dump.Artifacts == null || dump.Artifacts.Count == 0)
			{
				return true;
			}

			foreach (string artifact in dump.Artifacts)
			{
				if (string.IsNullOrEmpty(artifact))
				{
					continue;
				}

				string path = Path.IsPathRooted(artifact)
					? artifact
					: Path.Combine(BridgePaths.WorkingRoot, artifact);
				if (!File.Exists(path))
				{
					return false;
				}
			}

			return true;
		}
	}
}
