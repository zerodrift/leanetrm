using Npgsql;
using OpenEtrm.MarketData;
using OpenEtrm.Shared.Temporal;

namespace OpenEtrm.Api;

// The exact knowledge view and input frame a page read runs under, resolved by its caller
// before the read starts: a retained context, or current working data, which binds nothing.
// A read binds only what the pin names; it never captures, widens or substitutes a context.
public sealed record PinnedContext
{
    private PinnedContext(ScheduleContext? context,InputFrame? frame,bool rereading=false)
    {
        Context=context is null?null:Exact(context);
        this.rereading=rereading;
        Frame=frame is null?null:Require(frame);
    }
    // The selected retained context, or null for current working data.
    public ScheduleContext? Context {get;}
    public InputFrame? Frame {get;}
    // Whether a working frame is read as of a context captured earlier, rather than at one of the context's own frames.
    readonly bool rereading;
    public static PinnedContext Current(InputFrame? frame=null)=>new(null,frame);
    public static PinnedContext Retained(ScheduleContext context,InputFrame? frame=null)=>new(context,frame);
    // Working data as of a context captured earlier at its frame's date, as TradingView.Reread reads trades: a turn's
    // tool reads bind the context its ask captured and never capture one (docs/ai-assistant.md §7.2).
    public static PinnedContext Rereading(ScheduleContext context,InputFrame frame)=>frame is {ContextId:null}?new(context,frame,true):throw Missing();
    public InputFrame RequireFrame()=>Frame??throw Missing();
    // A frame belongs to exactly one knowledge view; working frames carry no context.
    public InputFrame Require(InputFrame? frame)=>frame is not null && frame.ContextId==(rereading?null:Context?.ContextId)?frame:throw Missing();
    public Task Bind(NpgsqlConnection connection,Guid catalog,CancellationToken ct)=>
        Context is null?Task.CompletedTask:BindExact(connection,catalog,Context,ct);
    // A stored context names its id and every coordinate; none is ever filled in later.
    internal static ScheduleContext Exact(ScheduleContext context)=>context is {ContextId:null} or {EffectiveOn:null} or {KnownAt:null} or {KnownThrough:null}
        ?throw new TemporalException("temporal_context","Pin an exact retained context."):context;
    // Binds a stored context only when its coordinates are exactly those named, so a read never mixes two views.
    internal static async Task BindExact(NpgsqlConnection connection,Guid catalog,ScheduleContext context,CancellationToken ct)
    {
        await new KnowledgeStore(catalog).Retained(connection,context,ct);
        await MarketContextStore.Bind(connection,catalog,context,ct);
    }
    internal static MarketDataException Missing()=>new("snapshot_missing","Select a saved input snapshot from this preview session.");
}
