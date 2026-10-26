using System.Collections.Immutable;
using System.Globalization;
using System.Text.Json;
using Npgsql;
using OpenEtrm.MarketData;

namespace OpenEtrm.Api;

public sealed record CurveName(string Code,string Name);
public sealed record WorkspaceRead(PreviewData Data,UnitCatalogData? Units,ImmutableArray<CurveName>? CurveNames,
    ReferenceDisplayData? ReferenceDisplay=null,ReferenceDisplayData? UnitReferenceDisplay=null);
public sealed record PriceRead(object Result,ImmutableArray<JsonElement> Observations,WorkspaceRead? Workspace=null);
// KnownAt is the latest certified publication instant among the sample's observations; null while any is not yet certified.
public sealed record HistorySample(DateTimeOffset At,string? Value,Guid SnapshotId,Guid[] ObservationIds,string? UnavailableReason,DateTimeOffset? KnownAt=null);
// How a history read chooses the revision each sample shows (D230); the caller must name one.
public static class HistoryRevisionPolicy
{
    public const string ExactSnapshotPins="exact_snapshot_pins_v1",AsKnownAtCutoff="as_known_at_cutoff_v1";
    public static string Require(string? policy)=>policy is ExactSnapshotPins or AsKnownAtCutoff?policy
        :throw new MarketDataException("history_revision_policy","Choose a revision policy: exact snapshot pins or as known at the cutoff.");

    public static IEnumerable<HistorySample> OnePerInstant(IEnumerable<HistorySample> samples)
    {
        foreach(var instant in samples.GroupBy(s=>s.At))
        {
            if(!Agree(instant))
                throw new MarketDataException("history_conflict",$"Several different prices are known at {instant.Key:O} under this cutoff. Choose one version before reading this history.");
            yield return instant.OrderByDescending(s=>s.KnownAt).First();
        }
    }

    /// <summary>Whether the samples at one instant are one point: the same value and the same availability.</summary>
    public static bool Agree(IEnumerable<HistorySample> instant)
    {
        var first=instant.First();
        return instant.All(s=>s.Value==first.Value&&s.UnavailableReason==first.UnavailableReason);
    }
}
// A contract's delivery period and its exact price at the pinned frame, or the position-specific reason it has none (D220).
public sealed record CurveShapePoint(Guid ContractId,string DeliveryFrom,string DeliveryTo,string? Price,string? UnavailableReason);
// A curve at the pinned frame: its price unit and a point for each contract its view selects, or, with neither, the code its own
// Evaluate read raises when the pinned data cannot read it.
// ContractCount, Front and Availability are D233's summary of Points: how many contracts the view selects, its first priced one, and why
// it has none (no_remaining_contracts when the view selects nothing, no_priced_contract when it selects contracts and none is priced).
public sealed record CurveShape(string Curve,string? Unit,ImmutableArray<CurveShapePoint>? Points,string? UnavailableReason,int? ContractCount,CurveFront? Front,string? Availability);
// The front contract of a curve (D233): the first contract in delivery order that the selected close prices.
public static class CurveSummary{public const string NoRemainingContracts="no_remaining_contracts",NoPricedContract="no_priced_contract";}
public sealed record CurveFront(Guid ContractId,string DeliveryFrom,string DeliveryTo,string Price)
{
    public static CurveFront? First(IEnumerable<CurveShapePoint> points)=>points.Where(p=>p.Price is not null)
        .OrderBy(p=>p.DeliveryFrom,StringComparer.Ordinal).ThenBy(p=>p.DeliveryTo,StringComparer.Ordinal)
        .Select(p=>new CurveFront(p.ContractId,p.DeliveryFrom,p.DeliveryTo,p.Price!)).FirstOrDefault();
}

// Market and reference reads callable without an HTTP request or workspace session. Each
// runs in a transaction already bound to an issued request context and binds its own pin,
// so a transaction runs exactly one read.
public sealed class MarketReads(PreviewDatabase database)
{
    public async Task<PreviewData> Data(NpgsqlConnection connection,PinnedContext pin,CancellationToken ct,
        ImmutableArray<Guid>? observationIds=null,ReferenceReadScope referenceScope=ReferenceReadScope.DefinitionClosure,bool includeCalendarDays=false)
    {
        await pin.Bind(connection,database.Manifest.CatalogId,ct);
        return await database.Read(connection,ct,observationIds:observationIds,referenceScope:referenceScope,includeCalendarDays:includeCalendarDays);
    }

    public async Task<WorkspaceRead> Workspace(NpgsqlConnection connection,PinnedContext pin,bool catalogOnly,CancellationToken ct)
    {
        var data=await Data(connection,pin,ct,catalogOnly?ImmutableArray<Guid>.Empty:null,catalogOnly?ReferenceReadScope.DefinitionClosure:ReferenceReadScope.AllVisible);
        return await WorkspaceMetadata(connection,data,catalogOnly,ct);
    }

    async Task<WorkspaceRead> WorkspaceMetadata(NpgsqlConnection connection,PreviewData data,bool catalogOnly,CancellationToken ct)
    {
        UnitCatalogData? units;
        try { units=await database.ReadUnitCatalog(connection,data,ct); }
        catch(MarketDataException e) when(e.Code=="unit_catalog") { units=null; }
        var display=await ReferenceEditors.Display(connection,data.ReferenceRevision,ct);
        var unitDisplay=units is null?null:units.Revision==data.ReferenceRevision?display:await ReferenceEditors.Display(connection,units.Revision,ct);
        return new(data,units,catalogOnly?await Names(connection,ct):null,display,unitDisplay);
    }

    public async Task<NavigationRead> Navigation(NpgsqlConnection connection,PinnedContext pin,CancellationToken ct)
    {
        await pin.Bind(connection,database.Manifest.CatalogId,ct);
        var pins=await MarketBundle.Require(connection,database.Manifest,ct);
        var curves=await MarketNavigation.ReadCurves(connection,database.Manifest,ct);
        var names=await Names(connection,ct);
        await MarketBundle.RequireReferenceVisibility(connection,database.Manifest,ct);
        var display=await ReferenceEditors.Display(connection,pins.References,ct);
        if(display is not null && display.References.TryGetValue("commodities",out var labels))
        {
            var namesByCode=labels.Where(r=>r.TryGetProperty("name",out _)).ToDictionary(r=>r.Text("code"),r=>r.Text("name"));
            curves=[..curves.Select(c=>c.Category is {} category&&namesByCode.TryGetValue(category.Code,out var name)?c with{Category=category with{Name=name}}:c)];
        }
        return new(pins.Definitions,pins.References,curves,names,display);
    }

    // Stored display names for every curve in this catalog, for screens that show curve codes from any bundle.
    public async Task<ImmutableArray<CurveName>> CurveNames(NpgsqlConnection connection,PinnedContext pin,CancellationToken ct)
    {
        await pin.Bind(connection,database.Manifest.CatalogId,ct);
        return await Names(connection,ct);
    }

    public async Task<ReferencePage> References(NpgsqlConnection connection,PinnedContext pin,string table,int offset,string? search,CancellationToken ct)
    {
        await pin.Bind(connection,database.Manifest.CatalogId,ct);
        return await database.ReadReferencePage(connection,table,offset,search,ct);
    }

    // The pinned frame and the data its own observations select, for the date of the pin's context.
    async Task<(InputFrame Frame,PreviewData Data)> Pinned(NpgsqlConnection connection,PinnedContext pin,CancellationToken ct)
    {
        var frame=pin.RequireFrame();
        if(pin.Context is {} selected && DateOnly.FromDateTime(frame.At.UtcDateTime)!=DateWorkspace.Date(selected.EffectiveOn))
            throw new MarketDataException("market_date","Choose inputs for the selected context date. No earlier market date is substituted.");
        return (frame,await Data(connection,pin,ct,frame.ObservationIds));
    }

    public async Task<PriceRead> Evaluate(NpgsqlConnection connection,PinnedContext pin,string curve,CancellationToken ct,bool includeWorkspace=false)
    {
        var (frame,data)=await Pinned(connection,pin,ct);
        var result=data.Result(frame,curve);
        return new(result,data.Observations,includeWorkspace?await WorkspaceMetadata(connection,data,true,ct):null);
    }

    public const int MaximumShapedCurves=64;

    // Every named curve's contract prices, from one read of the pinned data and one snapshot of its inputs, in request order.
    // A curve the pinned data cannot read states the code its own Evaluate read raises, and does not fail the others. The request
    // fails for what is not one curve's: its own form, authorization, the pin and its context, a pinned observation that cannot
    // be found when the snapshot is assembled, and a catalog inconsistency in a readable curve's price unit. A snapshot the model
    // then refuses fails each curve the same way, with the code its own Evaluate read raises.
    public async Task<ImmutableArray<CurveShape>> CurveShapes(NpgsqlConnection connection,PinnedContext pin,ImmutableArray<string> curves,CancellationToken ct)
    {
        if(curves.IsDefault||curves.IsEmpty||curves.Length>MaximumShapedCurves||curves.Any(string.IsNullOrWhiteSpace)||curves.Distinct().Count()!=curves.Length)
            throw new MarketDataException("curve_shapes",$"Name from 1 to {MaximumShapedCurves} distinct curves.");
        var (frame,data)=await Pinned(connection,pin,ct);
        var snapshot=data.Snapshot(frame);
        return [..curves.Select(curve=>Shape(data,snapshot,frame,curve))];
    }

    // The contracts the frame selects for a curve, each priced as Evaluate prices it: one build of the whole curve. Only when that
    // build fails for a position-specific reason (D220) are the contracts that fail alone isolated against the same snapshot, and
    // each says why it has no price. A curve whose view selects nothing has no points, as Evaluate states. A curve that cannot be
    // read, or whose contracts cannot be isolated, has no unit or points and states what its own Evaluate read raises: the failure
    // of its one build of every contract, whatever isolating them met. A catalog inconsistency in the price unit of a curve that
    // can be read is not the curve's own failure, and fails the read.
    internal static CurveShape Shape(PreviewData data,MarketSnapshot snapshot,InputFrame frame,string curve)
    {
        ImmutableArray<CurveRequest> requests;
        try{requests=data.Requests(frame,curve);}
        catch(MarketDataException error){return new(curve,null,null,error.Code,null,null,null);}
        CurveBuild? build;ImmutableDictionary<CurveRequest,string> unvalued;
        try{(build,unvalued)=ValuationStatus.BuildValued(set=>data.Model.Build(snapshot,set),requests);}
        catch(MarketDataException)
        {
            try{data.Model.Build(snapshot,requests);}
            catch(MarketDataException whole){return new(curve,null,null,whole.Code,null,null,null);}
            throw;
        }
        var prices=(build?.Values??ImmutableArray<BuiltValue>.Empty).ToDictionary(v=>v.Request,v=>v.Price.Value.ToString());
        ImmutableArray<CurveShapePoint> points=[..requests.Select(request=>
        {
            var delivery=data.ContractRows(request.Contract.Id).Single();
            return new CurveShapePoint(request.Contract.Id,Date(delivery.DeliveryFrom),Date(delivery.DeliveryTo),prices.GetValueOrDefault(request),unvalued.GetValueOrDefault(request));
        })];
        var front=CurveFront.First(points);
        return new(curve,data.PriceUnit(curve),points,null,points.Length,front,front is not null?null:points.IsEmpty?CurveSummary.NoRemainingContracts:CurveSummary.NoPricedContract);
    }
    static string Date(DateOnly date)=>date.ToString("yyyy-MM-dd",CultureInfo.InvariantCulture);

    // Rebuilds the given frames of the pinned view; the pinned frame replaces the frame at its instant. Under as_known_at_cutoff_v1
    // each frame follows its pins to their latest revision visible at the pinned context's cutoff (the resolver certified contexts use),
    // then samples at one instant collapse when identical and are refused when they still differ (D229, D230).
    public async Task<ImmutableArray<HistorySample>> History(NpgsqlConnection connection,PinnedContext pin,ImmutableArray<InputFrame> frames,
        string curve,Guid? contractId,Guid? seriesId,string revisionPolicy,CancellationToken ct,Func<PreviewData,ImmutableArray<InputFrame>>? resolveFrames=null)
    {
        var current=pin.RequireFrame();
        var data=await Data(connection,pin,ct);
        var selected=(resolveFrames?.Invoke(data)??frames).Select(pin.Require).Where(frame=>frame.At!=current.At).Append(current).OrderBy(frame=>frame.At).ThenBy(frame=>frame.Id).ToArray();
        var samples=selected.Select(f=>
        {
            try {
                var value=data.HistoryValue(revisionPolicy==HistoryRevisionPolicy.AsKnownAtCutoff?data.SelectPinned(f):f,curve,contractId,seriesId);
                return new HistorySample(f.At,value.Value,f.Id,value.Inputs,null);
            }
            catch(MarketDataException error) when(ValuationStatus.PositionSpecific(error))
            { return new HistorySample(f.At,null,f.Id,[],error.Code); }
        }).ToArray();
        var knowledge=await KnownAt(connection,samples.SelectMany(s=>s.ObservationIds).Distinct().ToArray(),ct);
        var known=samples.Select(s=>s with{KnownAt=s.ObservationIds.Length>0&&s.ObservationIds.All(knowledge.ContainsKey)?s.ObservationIds.Max(id=>knowledge[id]):null});
        return revisionPolicy==HistoryRevisionPolicy.AsKnownAtCutoff?[..HistoryRevisionPolicy.OnePerInstant(known)]:[..known];
    }

    // Certified publication instants of the observations (never receipt audit times); observations not yet certified have none.
    async Task<Dictionary<Guid,DateTimeOffset>> KnownAt(NpgsqlConnection connection,Guid[] observations,CancellationToken ct)
    {
        if(observations.Length==0)return [];
        var rows=await PreviewDatabase.Rows(connection,"""
            SELECT row_to_json(q)::text FROM(SELECT e.observation_id AS id,k.known_from FROM market_data.knowledge_events e
            JOIN market_data.market_knowledge_receipts k ON(k.catalog_id,k.event_order,k.publication_xid)=(e.catalog_id,e.event_order,e.publication_xid)
            WHERE e.catalog_id=$1 AND e.observation_id=ANY($2::uuid[]))q
            """,[database.Manifest.CatalogId,observations],ct);
        return rows.ToDictionary(r=>r.Id("id"),r=>r.Instant("known_from"));
    }

    async Task<ImmutableArray<CurveName>> Names(NpgsqlConnection connection,CancellationToken ct)
    {
        await using var command=ScheduleStore.Command(connection,"SELECT code,name FROM market_data.curves WHERE catalog_id=$1 UNION SELECT code,name FROM market_data.rate_curves WHERE catalog_id=$1 ORDER BY 1,2",database.Manifest.CatalogId);
        await using var reader=await command.ExecuteReaderAsync(ct);var rows=ImmutableArray.CreateBuilder<CurveName>();
        while(await reader.ReadAsync(ct))rows.Add(new(reader.GetString(0),reader.GetString(1)));
        return rows.ToImmutable();
    }
}
