using System.Diagnostics;
using UnityEditor;

namespace AgentBridge
{
	// Two fingerprints guard a cached test run, because one value cannot do both jobs.
	//
	// Current() is the cache key: it answers "is the project in the same state as when these
	// results were produced". GlobalArtifactDependencyVersion catches any asset change, which is
	// exactly what a reader needs, but it is also bumped by the run itself — tests that create,
	// save or delete scenes and prefabs move it several times before they finish. Keying on the
	// value taken at run start would therefore never match again, so the key is stamped at
	// promotion, once the run and any PlayMode scene recovery have settled.
	//
	// Sources() is the other half of the key, and the run guard. GlobalArtifactDependencyVersion
	// only ever reflects what Unity has already imported, so a source file edited on disk and not
	// yet picked up leaves it untouched — and that is the ordinary case, since an agent edits code
	// and immediately asks for tests. Hashing the files on disk closes that hole, and unlike the
	// artifact version it is not moved by tests that create or delete assets while they run.
	public static class TestFingerprint
	{
		private static readonly string ProcessStamp;

		static TestFingerprint()
		{
			Process process = Process.GetCurrentProcess();
			ProcessStamp = process.Id + "-" + process.StartTime.ToUniversalTime().Ticks;
		}

		public static string Current()
		{
			return ProcessStamp + "-" + AssetDatabase.GlobalArtifactDependencyVersion;
		}

		public static string Sources()
		{
			return CompileFingerprint.Current();
		}
	}
}
