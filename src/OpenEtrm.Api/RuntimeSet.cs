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
    // turns_v7 bounds a tool read by its agent version's time per read and its turn's deadline (D245); turns_v9 adds Save curve
    // draft, whose step is recorded in its write's transaction (D260); turns_v10 has Read current page carry the Blotter's
    // layout its page report states (D270). turns_v8 is reserved for the installation limits (P42).
    public const string Turns="turns_v10";
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
    // (§7.4), and the most time per read a version may set (0120), which the database also enforces (D245).
    public const int AgentPool=6,MaxReadSeconds=30;
    // The wait before a turn's first retry, doubled for each later one.
    public static readonly TimeSpan RetryBackoff=TimeSpan.FromSeconds(1);
    // The longest one worker write may take, and the wait before a write that must land is tried again.
    public static readonly TimeSpan WorkerWrite=TimeSpan.FromSeconds(5),WorkerRetry=TimeSpan.FromSeconds(2);
    // The live event stream (§8): the longest an open stream stays silent before a comment, below the hosted gateway's
    // read timeout of 20 seconds (§11); the most of its turn's events one read sends, its replay window, above a cut
    // subscriber's buffer and the events a reconnect's wait adds; and the signals one subscriber buffers before it is cut.
    public static readonly TimeSpan StreamHeartbeat=TimeSpan.FromSeconds(15);
    public const int ReplayEvents=256,SubscriberEvents=64;
}
