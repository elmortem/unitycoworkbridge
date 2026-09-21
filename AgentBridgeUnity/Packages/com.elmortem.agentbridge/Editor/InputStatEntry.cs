namespace AgentBridge
{
	// One input file as the stat manifest remembers it: where it was, how long it was, and when it
	// was last written. Cheap enough to take for every input, and enough to notice an edit that the
	// observer slept through.
	public sealed class InputStatEntry
	{
		public string Path = "";
		public long Length;
		public long WriteTicks;
	}
}
