namespace OpenEtrm.Api;

// The runtime set (docs/ai-assistant.md §7.5): limits that ship with the application, never host configuration.
// These are the ones provider Test uses; turns add theirs.
static class RuntimeSet
{
    public static readonly TimeSpan ProviderConnect=TimeSpan.FromSeconds(5);
    public static readonly ModelTransportLimits Provider=new(TimeSpan.FromSeconds(30),4<<20);
    public const int TestOutputTokens=256;
    // Below the request deadline of 15 seconds, leaving time to close the test and answer.
    public static readonly TimeSpan TestDeadline=TimeSpan.FromSeconds(10);
}
