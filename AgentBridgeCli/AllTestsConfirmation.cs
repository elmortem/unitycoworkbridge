namespace AgentBridge.Cli;

// A tests run with no --test, --category or --assembly filter selects the whole suite of the mode.
// Some projects hold very many long tests, so such a run is refused until the caller confirms it
// with --confirm-all. The refusal is decided before the editor is contacted: asking costs nothing.
internal static class AllTestsConfirmation
{
	public const string Code = "all_tests_confirmation_required";
	public const string Flag = "--confirm-all";

	public static bool IsUnfiltered(string[] assemblies, string[] tests, string[] categories)
	{
		return assemblies.Length == 0 && tests.Length == 0 && categories.Length == 0;
	}

	public static bool IsRequired(string[] assemblies, string[] tests, string[] categories, bool confirmed)
	{
		return !confirmed && IsUnfiltered(assemblies, tests, categories);
	}

	public static string Message(string mode)
	{
		return "Are you sure you want to run ALL " + mode + " tests of the project? "
			+ "No --test, --category or --assembly filter was given. "
			+ "Some projects have very many long-running tests: a full run can take a very long time "
			+ "and holds the editor for every other agent session. "
			+ "Run the whole suite only with a strong reason, for example when the user explicitly asked for a full run; "
			+ "otherwise select the tests related to your change with --test, --category or --assembly. "
			+ "If a full run is really needed, repeat the same command with " + Flag + ".";
	}
}
