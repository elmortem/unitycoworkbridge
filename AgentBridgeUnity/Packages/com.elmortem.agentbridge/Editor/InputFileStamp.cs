namespace AgentBridge
{
	// What a directory listing says about one input without opening it. Times are FILETIME UTC
	// ticks exactly as the system reports them; ChangeTime moves on any content or metadata write,
	// even when the writer restores the length and the write time.
	public struct InputFileStamp
	{
		public long Length;
		public long WriteTicks;
		public long ChangeTicks;
		public long FileId;
	}
}
