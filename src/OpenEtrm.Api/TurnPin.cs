using System.Collections.Immutable;
using System.Text.Json;

namespace OpenEtrm.Api;

// The knowledge and As of frame a turn's tool reads run at (docs/ai-assistant.md §7.2), rebuilt from the turn's
// pinned-context record and never captured, widened or substituted. The record keeps the frame's exact pins, from which
// every read rebuilds the frame the page showed. Working data is read as of the context its ask captured at the frame's
// date, a historical view at its retained context, and current-only definitions (TOU products, power hours) only over
// working data, with no context bound.
public sealed class TurnPin
{
    TurnPin(bool working,Guid context,DateTimeOffset cutoff,InputFrame? frame)=>(Working,ContextId,Cutoff,Frame)=(working,context,cutoff,frame);
    // Working data, whose ask captured the context, or a historical view's retained context.
    public bool Working {get;}
    public Guid ContextId {get;}
    public DateTimeOffset Cutoff {get;}
    public InputFrame? Frame {get;}

    // The record as start_turn answers it: the turn's context and its frame's pins, for this scope's catalog. A record the
    // worker cannot read is a defect, never a reason to read another context.
    public static TurnPin From(JsonElement context,JsonElement pins,Guid catalog,PreviewManifest manifest)
    {
        try
        {
            var working=context.GetProperty("mode").GetString() switch{"current"=>true,"retained"=>false,_=>throw Unreadable()};
            var id=context.GetProperty("contextId").GetGuid();
            var frameId=context.GetProperty("frameId") is {ValueKind:JsonValueKind.String} named?named.GetGuid():(Guid?)null;
            var frame=pins.ValueKind==JsonValueKind.Null?null:Rebuild(pins,working?null:id);
            if(catalog!=manifest.CatalogId||frame?.Id!=frameId||working&&frame is null)throw Unreadable();
            return new(working,id,context.GetProperty("cutoff").GetDateTimeOffset(),frame);
        }
        catch(Exception e) when(e is KeyNotFoundException or FormatException or InvalidOperationException){throw Unreadable();}
    }

    // The pins an ask records for its As of frame (§7.2), which a turn's reads rebuild.
    internal static string Record(InputFrame frame)=>JsonSerializer.Serialize(new{at=frame.At,curveContracts=frame.CurveContracts,frameId=frame.Id,
        frozenObservationIds=frame.FrozenObservationIds.IsDefault?[]:frame.FrozenObservationIds,name=frame.Name,observationIds=frame.ObservationIds,
        resolvesLatestCorrection=frame.ResolvesLatestCorrection,swapMarketSelections=frame.SwapMarketSelections});

    static InputFrame Rebuild(JsonElement pins,Guid? context)=>new(pins.GetProperty("frameId").GetGuid(),pins.GetProperty("name").GetString()!,
        pins.GetProperty("at").GetDateTimeOffset(),Ids(pins.GetProperty("observationIds")),
        pins.GetProperty("curveContracts").EnumerateObject().ToImmutableDictionary(p=>p.Name,p=>Ids(p.Value)),context,Ids(pins.GetProperty("frozenObservationIds")))
    {
        ResolvesLatestCorrection=pins.GetProperty("resolvesLatestCorrection").GetBoolean(),
        SwapMarketSelections=pins.GetProperty("swapMarketSelections") is {ValueKind:JsonValueKind.Object} selected
            ?selected.EnumerateObject().ToImmutableDictionary(p=>Guid.Parse(p.Name),p=>Ids(p.Value)):null
    };

    static ImmutableArray<Guid> Ids(JsonElement ids)=>[..ids.EnumerateArray().Select(id=>id.GetGuid())];

    // The context a read binds, as the knowledge store reads it: exactly the record's, at its recorded cutoff.
    public ScheduleContext Exact(ScheduleContext stored)=>
        stored.ContextId==ContextId&&stored.KnownAt is {} at&&SchedulePreview.ParseAt(at)==Cutoff?stored:throw Unreadable();

    // The page views: working data as of the context its ask captured, a historical view at its retained context.
    public PinnedContext Page(ScheduleContext context)=>Working?PinnedContext.Rereading(Exact(context),RequireFrame()):PinnedContext.Retained(Exact(context),Frame);
    // Current-only definitions run only over working data, binding no context.
    public PinnedContext CurrentOnly()=>Working?PinnedContext.Current(Frame):throw ScheduleReads.Historical();
    // Trades and parties read the frame as of the context: the capture of a working-data ask, or a historical view's own.
    public TradingView Trading(ScheduleContext context)=>
        Working?TradingView.Reread(RequireFrame(),Exact(context)):TradingView.Retained(RequireFrame(),Exact(context));
    // Reads that consume a frame fail without one (§7.2).
    public InputFrame RequireFrame()=>Frame??throw PinnedContext.Missing();

    static InvalidOperationException Unreadable()=>new("The turn's pinned-context record cannot be read.");
}
