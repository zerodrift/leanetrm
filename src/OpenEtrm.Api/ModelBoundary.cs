using System.Collections.Immutable;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace OpenEtrm.Api;

// The provider-neutral model boundary (docs/ai-assistant.md §6.1, D06). A call sends normalized
// messages, a system instruction, tool definitions and explicit parameters. It streams text deltas
// followed by exactly one ending, a completion or a failure, each with normalized usage (§6.2).
// Vendor types, message identifiers and continuation data stay inside the adapter.
public sealed record ModelRequest(ModelTarget Model,string System,ImmutableArray<ModelMessage> Messages,ImmutableArray<ModelTool> Tools,ModelParameters Parameters);

// The admitted vendor, the identifier sent and the other served identifiers its model version accepts.
public sealed partial record ModelTarget(string Vendor,string Identifier,ImmutableArray<string> AcceptedServed)
{
    // The rule the database applies when a model is added (assistant.is_model_identifier).
    internal static bool Valid(string identifier)=>Format().IsMatch(identifier);
    [GeneratedRegex(@"^[A-Za-z0-9][A-Za-z0-9._-]{0,127}\z")] private static partial Regex Format();
}

// Every value is explicit. Reasoning names one of the model version's settings and is null only for
// a model that has none; Caching is the agent version's switch.
public sealed record ModelParameters(int MaxOutputTokens,string? Reasoning,bool Caching);

public sealed record ModelTool(string Name,string Description,JsonElement InputSchema);

public abstract record ModelMessage;
public sealed record ModelUserText(string Text):ModelMessage;
public sealed record ModelToolResults(ImmutableArray<ModelToolResult> Results):ModelMessage;
public sealed record ModelToolResult(string RequestId,string Content,bool Failed);

// A reply that stopped for tools, replayed unchanged to the model that wrote it within the same turn.
// Its content belongs to the adapter, signed reasoning included, and lives only in worker memory.
public sealed record ModelReply:ModelMessage
{
    internal ModelReply(string protocol,string model,JsonElement content)=>(Protocol,Model,Content)=(protocol,model,content);
    internal string Protocol {get;}
    internal string Model {get;}
    internal JsonElement Content {get;}
    public override string ToString()=>nameof(ModelReply);
}

public abstract record ModelEvent;
public sealed record ModelTextDelta(string Text):ModelEvent;
// Tool requests and the reply to replay are present exactly when the model stopped to use tools.
public sealed record ModelCompleted(ModelStop Stop,ImmutableArray<ModelToolRequest> ToolRequests,ModelReply? Reply,ModelUsage Usage,string Served):ModelEvent;
public sealed record ModelFailed(ModelFailure Failure,ModelUsage Usage,string? Served):ModelEvent;
// The identifier is the vendor's, used only to answer this request within the turn.
public sealed record ModelToolRequest(string Id,string Name,JsonElement Input);

public enum ModelStop {EndTurn,ToolUse,OutputLimit,ContextWindow,Refusal}
public enum ModelFailure {Authentication,RateLimited,Overloaded,InvalidRequest,ContextWindow,Transport,ResponseTooLarge,ModelMismatch,Cancelled}

// Recognized usage can be priced; unrecognized and unknown usage store no cost, never a partial one.
public enum UsageStatus {Recognized,Unrecognized,Unknown}
public enum TokenSource {Reported,NotBilled,Unknown}

// One dimension's count: reported by the vendor, a sourced structural zero (not billed, or nothing
// was sent), or unknown. An unknown count has no value, so it never becomes zero.
public sealed record TokenCount
{
    TokenCount(TokenSource source,long? count)=>(Source,Count)=(source,count);
    public TokenSource Source {get;}
    public long? Count {get;}
    public static TokenCount Reported(long count)=>count>=0?new(TokenSource.Reported,count):throw new ArgumentOutOfRangeException(nameof(count));
    public static readonly TokenCount NotBilled=new(TokenSource.NotBilled,0),Unknown=new(TokenSource.Unknown,null);
}

// Reasoning is an informational subset of output and never priced.
public sealed record ModelUsage(string Mapping,UsageStatus Status,TokenCount UncachedInput,TokenCount CacheRead,TokenCount CacheWrite,TokenCount Output,TokenCount Reasoning)
{
    public static ModelUsage Unknown(string mapping)=>new(mapping,UsageStatus.Unknown,TokenCount.Unknown,TokenCount.Unknown,TokenCount.Unknown,TokenCount.Unknown,TokenCount.Unknown);
    // Nothing reached the vendor, so nothing was processed or billed.
    public static ModelUsage NothingSent(string mapping)=>new(mapping,UsageStatus.Recognized,TokenCount.NotBilled,TokenCount.NotBilled,TokenCount.NotBilled,TokenCount.NotBilled,TokenCount.NotBilled);
}

// A request the adapter refuses before sending anything: parameter_required, model_identifier_invalid
// or credential_unavailable.
public sealed class ModelCallException(string code,string message):Exception(message)
{
    public string Code {get;}=code;
}

// Provider transport limits from the runtime set (§7.5): the longest wait for the response headers or
// between two reads of the body, and the most response bytes read.
public sealed record ModelTransportLimits(TimeSpan Idle,long ResponseBytes);
