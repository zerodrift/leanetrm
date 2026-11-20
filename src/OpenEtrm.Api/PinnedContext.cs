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
        if(Frame is not null&&Context is {} selected)Frame.Time.RequireDate(DateWorkspace.Date(selected.EffectiveOn));
    }
    // The selected retained context, or null for current working data.
    public ScheduleContext? Context {get;}
    public InputFrame? Frame {get;}
    // Whether a working frame is read as of a context captured earlier, rather than at one of the context's own frames.
    readonly bool rereading;
    public static PinnedContext Current(InputFrame? frame=null)=>new(null,frame);
    public static PinnedContext Retained(ScheduleContext context,InputFrame? frame=null)=>new(context,frame);
    // Working data as of a context captured earlier at its frame's date, as TradingView.Reread reads trades.
    public static PinnedContext Rereading(ScheduleContext context,InputFrame frame)=>frame is {ContextId:null}?new(context,frame,true):throw Missing();
    internal InputFrame? CalculationFrame=>Frame is {} frame?frame with{ContextId=Context?.ContextId}:null;
    public InputFrame RequireFrame()=>CalculationFrame??throw Missing();
    // A frame belongs to exactly one knowledge view; working frames carry no context.
    public InputFrame Require(InputFrame? frame)
    {
        if(frame is null||frame.ContextId!=(rereading?null:Context?.ContextId))throw Missing();
        // History samples retain their own business dates under the selected
        // cutoff. Only the selected valuation root must match its effective date.
        _=frame.Time;
        return frame;
    }
    // Binding a recorded close also requires it to be the close in force now: one replaced after it was offered is refused (D334).
    public async Task Bind(NpgsqlConnection connection,Guid catalog,CancellationToken ct)
    {
        if(Context is not null)await BindExact(connection,catalog,Context,ct);
        await BindClose(connection,catalog,ct);
    }
    // Exact close reconstruction already binds its context before reading the frame; apply the remaining close checks once.
    internal async Task BindClose(NpgsqlConnection connection,Guid catalog,CancellationToken ct)
    {
        if(Frame is {Close:{} session,OriginCloseId:{} close})
        {
            await SelectedClose.RequireInForce(connection,catalog,close,ct);
            await SelectedClose.BindSession(connection,catalog,session.Session,ct);
        }
    }
    // A stored context names its id and every coordinate; none is ever filled in later.
    internal static ScheduleContext Exact(ScheduleContext context)=>context is {ContextId:null} or {EffectiveOn:null} or {KnownAt:null} or {KnownThrough:null}
        ?throw new TemporalException("temporal_context","Pin an exact retained context."):context;
    // Bind the exact coordinates across calculation and disclosure.
    internal static async Task BindExact(NpgsqlConnection connection,Guid catalog,ScheduleContext context,CancellationToken ct)
    {
        if(context.ReadCut is {} cut)
        {
            await cut.Bind(connection,catalog,context,ct);
            return;
        }
        var exact=await new KnowledgeResolver(catalog).Retained(connection,context,ct);
        await MarketContextStore.Bind(connection,catalog,exact,ct);
    }
    internal static MarketDataException Missing()=>new("snapshot_missing","Select a saved input snapshot from this preview session.");
}
