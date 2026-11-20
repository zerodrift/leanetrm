using System.Globalization;
using Npgsql;

namespace OpenEtrm.Api;

// The dated cutoff a trading read binds and the frame it reads at. Trading facts are always read
// as of a context: current knowledge captured for this read over a working frame, the only view
// that may be edited; a context captured earlier, re-read over a working frame; or a historical
// view's retained context. Page views use PinnedContext.
public sealed record TradingView
{
    // Views are made by Capture, Reread or Retained; the checks alone rebuild a page's own view directly.
    internal TradingView(InputFrame? frame,ScheduleContext context,bool working)
    {
        Frame=frame??throw PinnedContext.Missing();Context=PinnedContext.Exact(context);Working=working;
        Frame.Time.RequireDate(DateWorkspace.Date(Context.EffectiveOn));
    }
    public InputFrame Frame {get;}
    public InputFrame CalculationFrame=>Frame with{ContextId=Context.ContextId};
    public ScheduleContext Context {get;}
    // The context was captured from current knowledge for this read over a working frame.
    public bool Working {get;}
    // Working data as of current knowledge, captured now at the working frame's date.
    public static async Task<TradingView> Capture(NpgsqlConnection connection,KnowledgeResolver store,InputFrame? frame,CancellationToken ct)
    {
        if(frame is not {ContextId:null})throw PinnedContext.Missing();
        var on=WorkingDate(frame).ToString("yyyy-MM-dd",CultureInfo.InvariantCulture);
        return new(frame,(await store.Capture(connection,new(on,null,"current"),ct)).Context,true);
    }
    // Working data as of current knowledge that a transaction of its own captured for this read a moment earlier
    // (TradingReadSelection.CaptureKnowledge). The read's transaction reads that context again under its own authority
    // and refuses one whose stored coordinates differ from the captured ones.
    internal static async Task<TradingView> Captured(NpgsqlConnection connection,KnowledgeResolver store,InputFrame? frame,ScheduleContext knowledge,CancellationToken ct)=>
        frame is {ContextId:null}?new(frame,await store.Retained(connection,knowledge,ct),true):throw PinnedContext.Missing();
    // The date working data is captured at for a frame: its instant's UTC date.
    public static DateOnly WorkingDate(InputFrame frame)=>PreviewSession.BusinessDay(frame);
    // Working data as of a context captured earlier. The read refuses a context effective on another date.
    public static TradingView Reread(InputFrame? frame,ScheduleContext context)=>
        frame is {ContextId:null}?new(frame,context,false):throw PinnedContext.Missing();
    // A historical view: its retained context, read at one of its own frames.
    public static TradingView Retained(InputFrame? frame,ScheduleContext context)=>
        frame is not null && frame.ContextId==context.ContextId?new(frame,context,false):throw PinnedContext.Missing();
    public Task Bind(NpgsqlConnection connection,Guid catalog,CancellationToken ct)=>PinnedContext.BindExact(connection,catalog,Context,ct);
}
