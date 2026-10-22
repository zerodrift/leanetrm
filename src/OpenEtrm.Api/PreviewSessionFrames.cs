using System.Collections.Immutable;
using System.Security.Cryptography;
using System.Text.Json;
using OpenEtrm.Api;
using OpenEtrm.MarketData;

public sealed record FrameCatalogInfo(string DurableSelection,string SelectionPolicy,int MaximumFrames,int DurableLimit,bool HasMoreDurableCloses);
public sealed record SessionFramePlan(ImmutableArray<InputFrame> Frames,FrameCatalogInfo Catalog);

public sealed partial class PreviewSession
{
    public const int MaximumFrames=128;
    private readonly HashSet<Guid> discoveredFrames=[];
    private Guid aliasNamespace=Guid.NewGuid();
    public FrameCatalogInfo FrameCatalog{get;private set;}=new("exact_recorded_close_pins_v1","preserved_session_plus_latest_visible_closes_v1",MaximumFrames,0,false);
    public ImmutableArray<InputFrame> PersistentFrames=>[..Frames.Values.Where(frame=>!discoveredFrames.Contains(frame.Id)&&!IsLiveFrame(frame.Id))];
    public ImmutableArray<InputFrame> VisibleFrames=>[..Frames.Values.Where(frame=>frame.PpaBundle is null && frame.ContextId==MarketContext?.ContextId).OrderBy(frame=>frame.At).ThenBy(frame=>frame.Id)];
    public ImmutableArray<InputFrame> ContextAliases(ScheduleContext? context)=>Live?.Frame is {} live&&live.ContextId==context?.ContextId?[live]:context is null?[]:
        [..Frames.Values.Where(frame=>discoveredFrames.Contains(frame.Id)&&frame.OriginCloseId is null&&frame.ContextId==context.ContextId).OrderBy(frame=>frame.At).ThenBy(frame=>frame.Id)];
    public ImmutableArray<InputFrame> SelectContextAliases(PreviewData data,ScheduleContext context)
    {
        var end=DateWorkspace.Date(context.EffectiveOn).AddDays(1).ToDateTime(TimeOnly.MinValue,DateTimeKind.Utc);
        var visible=data.Observations.Select(row=>row.Id("id")).ToHashSet();
        var curves=data.Definitions["curves"].Select(row=>row.Text("code")).ToHashSet(StringComparer.Ordinal);
        bool Eligible(InputFrame frame)
        {
            var pins=frame.ObservationIds;
            var frozen=frame.FrozenObservationIds.IsDefault?ImmutableArray<Guid>.Empty:frame.FrozenObservationIds;
            if(pins.IsDefault||pins.Distinct().Count()!=pins.Length||frozen.Distinct().Count()!=frozen.Length||frozen.Any(id=>!pins.Contains(id))||
                frame.CurveContracts.Any(pair=>!curves.Contains(pair.Key)||pair.Value.IsDefault||pair.Value.Distinct().Count()!=pair.Value.Length||pair.Value.Any(id=>!data.HasCurveMember(pair.Key,id))))
                throw new MarketDataException("snapshot_inputs","A saved frame requires distinct exact pins, frozen markers within those pins and declared retained curve membership.");
            return pins.All(visible.Contains);
        }
        return [..InitialFrames.Select(id=>Frames[id]).Where(frame=>frame.ContextId is null&&frame.PpaBundle is null&&frame.At.UtcDateTime<end&&Eligible(frame)).Select(seed=>
        {
            var selected=MarketContextStore.Select(seed,data,context);
            return selected with{Id=AliasId("initial",seed.Id,selected)};
        })];
    }
    // Session-scoped identity only: no financial values, authority or read
    // results are cached, and different resolved selections get different IDs.
    private Guid AliasId(string sourceKind,Guid source,InputFrame frame)
    {
        var identity=JsonSerializer.SerializeToUtf8Bytes(new{kind="session_frame_alias_v1",scope=aliasNamespace,sourceKind,source,frame.ContextId,frame.At,
            observations=frame.ObservationIds.Order().ToArray(),
            frozen=(frame.FrozenObservationIds.IsDefault?ImmutableArray<Guid>.Empty:frame.FrozenObservationIds).Order().ToArray(),
            curves=frame.CurveContracts.OrderBy(pair=>pair.Key,StringComparer.Ordinal).Select(pair=>new{curve=pair.Key,contracts=pair.Value})});
        return new Guid(SHA256.HashData(identity).AsSpan(0,16));
    }
    public int DurableLimit(ImmutableArray<InputFrame> aliases)
    {
        var remaining=MaximumFrames-PersistentFrames.Length-aliases.Length;
        if(remaining<0)throw new MarketDataException("preview_limit","The retained session selections exceed the 128-frame catalog capacity.");
        return remaining;
    }
    // Planning reads existing cache identity only. Apply is called after the
    // authenticated database transaction, including its final checks, commits.
    public SessionFramePlan PlanFrames(MarketCloseFrames stored,ScheduleContext? context,ImmutableArray<InputFrame> aliases)
    {
        if(stored.Limit!=DurableLimit(aliases)||stored.Frames.Length>stored.Limit||aliases.Any(frame=>frame.ContextId!=context?.ContextId))
            throw new MarketDataException("preview_limit","The discovered frame catalog does not match the available session capacity.");
        if(stored.Limit==0&&stored.HasMore)throw new MarketDataException("preview_limit","The session's saved selections fill its 128-frame capacity. Start a new session to discover recent stored closes.");
        var frames=stored.Frames.Select(frame=>
        {
            if(frame.OriginCloseId is not {} origin||frame.Id!=origin||frame.ContextId!=context?.ContextId)
                throw new MarketDataException("pilot_close_inputs","A discovered close requires its exact stored identity and selected context.");
            if(context is null)return frame;
            return frame with{Id=AliasId("stored_close",origin,frame)};
        }).Concat(aliases).ToImmutableArray();
        return new(frames,new("exact_recorded_close_pins_v1","preserved_session_plus_latest_visible_closes_v1",MaximumFrames,stored.Limit,stored.HasMore));
    }
    public void ApplyFrames(SessionFramePlan plan)
    {
        if(PersistentFrames.Length+plan.Frames.Length>MaximumFrames||plan.Frames.Select(frame=>frame.Id).Distinct().Count()!=plan.Frames.Length||
            plan.Frames.Any(frame=>Frames.ContainsKey(frame.Id)&&!discoveredFrames.Contains(frame.Id)))
            throw new MarketDataException("preview_limit","The discovered frame catalog conflicts with a saved session selection.");
        foreach(var id in discoveredFrames)Frames.Remove(id);
        discoveredFrames.Clear();
        foreach(var frame in plan.Frames){Frames.Add(frame.Id,frame);discoveredFrames.Add(frame.Id);}
        FrameCatalog=plan.Catalog;
    }
    public void ForgetDiscoveredFrames(bool resetAliasIdentities=false)
    {
        foreach(var id in discoveredFrames)Frames.Remove(id);
        discoveredFrames.Clear();
        if(resetAliasIdentities)aliasNamespace=Guid.NewGuid();
    }
}
