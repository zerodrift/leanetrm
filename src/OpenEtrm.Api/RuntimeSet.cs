namespace OpenEtrm.Api;

// The runtime set (docs/ai-assistant.md §7.5): limits that ship with the application, never host configuration.
// Every turn records the version its process applied.
static class RuntimeSet
{
    public static readonly TimeSpan ProviderConnect=TimeSpan.FromSeconds(5);
    public static readonly ModelTransportLimits Provider=new(TimeSpan.FromSeconds(30),4<<20);
    public const int TestOutputTokens=256;
    // Below the request deadline of 15 seconds, leaving time to close the test and answer.
    public static readonly TimeSpan TestDeadline=TimeSpan.FromSeconds(10);
    // An agent version's instructions, in characters, so a save's body stays under 32 KB at four bytes each (§7.5).
    public const int AgentInstructions=6000;
    // The longest grant a version may ask for (§7.1).
    public const int MaxGrantDays=90;
    public const string Turns="turns_v2";
    // A question, in characters, so an ask's body stays under 32 KB at four bytes each beside its page report of 8 KiB.
    public const int QuestionLength=4000;
    // The turns a workspace runs at once.
    public const int WorkspaceTurns=4;
    // Above the request deadline of 15 seconds, so admission never takes an ask still pinning its context for a stuck one.
    public const int AdmissionSeconds=30;
    // The catalog's retained contexts a turn's capture leaves for page captures (§7.2).
    public const int RetainedContextReserve=64;
    // How long streamed text gathers before one publication, and the most UTF-8 bytes one publication carries.
    public static readonly TimeSpan TextCoalescing=TimeSpan.FromMilliseconds(250);
    public const int TextChunkBytes=2048;
    // The most records and UTF-8 bytes one tool result returns; a larger result is refused whole, never truncated (§7.3).
    public const int ToolResultRows=500,ToolResultBytes=64*1024;
    // The connections each scope's agent reads may hold on each login, above its workspace turns, which read one at a time
    // (§7.4), and the longest one read may take, whatever its statements.
    public const int AgentPool=6;
    public static readonly TimeSpan ToolRead=TimeSpan.FromSeconds(5);
    // The wait before a turn's first retry, doubled for each later one.
    public static readonly TimeSpan RetryBackoff=TimeSpan.FromSeconds(1);
    // The longest one worker write may take, and the wait before a write that must land is tried again.
    public static readonly TimeSpan WorkerWrite=TimeSpan.FromSeconds(5),WorkerRetry=TimeSpan.FromSeconds(2);
}
