using System.Collections.Immutable;
using System.Text.Json.Serialization;
using Npgsql;
using OpenEtrm.MarketData;
using OpenEtrm.MarketData.CustomCurves;
using OpenEtrm.MarketData.Power;
using OpenEtrm.MarketData.Risk;

namespace OpenEtrm.Api;

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record IntervalCurveCatalogRequest([property:JsonRequired] Guid SnapshotId);
// Every member is explicit. KnownAt is the knowledge cutoff for inputs a frame does not pin, a shaped curve's shape or a
// resolved curve's collected actuals, and null otherwise. Resolve prices delivered intervals by their actuals; Aggregate
// ('hour' or 'day', or null) needs Resolve.
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record IntervalCurveValuesRequest([property:JsonRequired] Guid SnapshotId,[property:JsonRequired] string Curve,
    [property:JsonRequired] string From,[property:JsonRequired] string To,[property:JsonRequired] string? KnownAt,
    [property:JsonRequired] bool Resolve,[property:JsonRequired] string? Aggregate,IntervalCurveShock? Shock=null);

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record IntervalCurveShock([property:JsonRequired] Guid SeriesId,[property:JsonRequired] string Quote,
    [property:JsonRequired] string Size,[property:JsonRequired] string Convention,[property:JsonRequired] bool Up);

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record PeriodCurveValuesRequest([property:JsonRequired] Guid SnapshotId,[property:JsonRequired] string Curve,
    [property:JsonRequired] string From,[property:JsonRequired] string To);

// Orchestrates interval-curve reads over a pinned snapshot; selection and pricing live in OpenEtrm.MarketData.
public sealed class IntervalCurvesStore(MarketReads reads,TouProductStore products)
{
    public const string DisplayPolicy="half_even_6dp_display_v1";
    static readonly AmountFormat Display=new(6,CashTieRule.ToEven,false,false);
    // The collected-price reader accepts at most 31 days and one hour per read.
    static readonly TimeSpan ActualReadWindow=TimeSpan.FromDays(31);

    // Known at is the database's clock, the clock every shape and actual read compares its cutoff with. A curve whose
    // definition cannot be resolved is listed as unavailable with its reason, never dropped and never fatal to the rest.
    public async Task<object> Catalog(NpgsqlConnection connection,PinnedContext pin,CancellationToken ct)
    {
        var frame=pin.RequireFrame();
        var data=await reads.Data(connection,pin,ct,frame.ObservationIds,includeCalendarDays:true);
        var listed=(await PreviewDatabase.Rows(connection,"""
            SELECT jsonb_build_object('knownAt',to_char(clock_timestamp() AT TIME ZONE 'UTC','YYYY-MM-DD"T"HH24:MI:SS.US"Z"'),
             'resolvable',(SELECT coalesce(jsonb_agg(curve_code),'[]') FROM (SELECT curve_code FROM market_data.curve_actual_rules WHERE(catalog_id,revision_id)=($1,$2) UNION ALL SELECT curve_code FROM market_data.shaped_curve_actual_rules WHERE(catalog_id,revision_id)=($1,$2)) rules))::text
            """,[data.DefinitionRevision.CatalogId,data.DefinitionRevision.RevisionId],ct)).Single();
        var resolvable=listed.GetProperty("resolvable").EnumerateArray().Select(c=>c.GetString()).ToHashSet();
        var tou=await products.Read(connection,ct);
        static object Listed(string code,string kind,Func<object> entry)
        {
            try { return entry(); }
            catch(MarketDataException e) { return new {code,kind,unavailable=e.Message}; }
        }
        return new {knownAt=listed.Text("knownAt"),curves=IntervalCurveDefinitions.MarkedCodes(data).Select(code=>Listed(code,"marked",()=>
            {var d=IntervalCurveDefinitions.Marked(data,code);return new {code,name=d.Name,kind="marked",quote=d.Quote,clock=d.ClockCode,
                intervalMinutes=d.IntervalMinutes,resolvable=resolvable.Contains(code)};}))
            .Concat(IntervalCurveDefinitions.ShapedCodes(data).Select(code=>Listed(code,"shaped",()=>
            {var s=IntervalCurveDefinitions.Shaped(data,code,tou);return new {code,name=s.Name,kind="shaped",method=Method(s.Definition),quote=s.Quote,
                clock=s.ClockCode,intervalMinutes=s.Definition.Grid.IntervalMinutes,
                blocks=s.Blocks.Select(b=>new {curve=b.Curve,membership=b.Membership,touProduct=b.TouProduct,touVersion=b.TouVersion}),
                shapeSeries=s.Definition.ShapeSeries,resolvable=resolvable.Contains(code)};})))};
    }

    public async Task<object> Values(NpgsqlConnection connection,PinnedContext pin,IntervalCurveValuesRequest request,CancellationToken ct)
    {
        var from=Instant(request.From,IntervalPrices.InvalidWindow);var to=Instant(request.To,IntervalPrices.InvalidWindow);
        var frame=pin.RequireFrame();
        var data=await reads.Data(connection,pin,ct,frame.ObservationIds,includeCalendarDays:true);
        if(request.Aggregate is not null && !request.Resolve)
            throw new MarketDataException("interval_aggregate","Aggregate a resolved interval curve.");
        if(IntervalCurveDefinitions.IsShaped(data,request.Curve))
            return request.Resolve?await ResolvedValues(connection,data,frame,request,from,to,ct):await ShapedValues(connection,data,frame,request,from,to,ct);
        return request.Resolve?await ResolvedValues(connection,data,frame,request,from,to,ct):MarkedValues(data,frame,request,from,to);
    }

    // A marked curve's pinned marks; nothing unpinned is read, so no knowledge cutoff applies.
    private static object MarkedValues(PreviewData data,InputFrame frame,IntervalCurveValuesRequest request,DateTimeOffset from,DateTimeOffset to)
    {
        if(request.KnownAt is not null)throw Cutoff();
        if(request.Shock is not null)throw new MarketDataException("risk_bump","Resolve the curve before applying a quote shock.");
        var definition=IntervalCurveDefinitions.Marked(data,request.Curve);
        var snapshot=data.Snapshot(frame);
        var window=Window(definition,frame,snapshot.ValuationAt,from,to);
        if(window.Any(c=>c.Expiry<=snapshot.ValuationAt))
            throw new MarketDataException("interval_delivered","This window includes delivered intervals; resolve them against collected actuals.");
        return new {curve=request.Curve,kind="marked",at=frame.At,cutoff=snapshot.MarketCutoff,
            numericalPolicy=CurveModel.NumericalPolicy,displayPolicy=DisplayPolicy,
            points=Marks(data,snapshot,definition,window).Select(p=>new {
                interval=IntervalCurveDefinitions.Label(p.Interval),price=Price(p.Price),observationIds=Ids(p.Lineage)})};
    }

    // Block marks come from the frame; the shape comes from its series as known at the request's explicit cutoff.
    private async Task<object> ShapedValues(NpgsqlConnection connection,PreviewData data,InputFrame frame,IntervalCurveValuesRequest request,
        DateTimeOffset from,DateTimeOffset to,CancellationToken ct)
    {
        var knownAt=Instant(request.KnownAt,Cutoff);
        if(request.Shock is not null)throw new MarketDataException("risk_bump","Resolve the curve before applying a quote shock.");
        var shaped=IntervalCurveDefinitions.Shaped(data,request.Curve,await products.Read(connection,ct));
        IntervalPrices.RequireWindow(shaped.Definition.Grid.Clock,shaped.Definition.Grid.IntervalMinutes,from,to);
        var (first,last)=BlockShaping.ShapeWindow(shaped.Definition,from,to);
        var (series,cells)=await ReadShape(connection,shaped.Definition.ShapeSeries,shaped.Definition.Grid.IntervalMinutes,first,last,knownAt,ct);
        var inputs=BlockShaping.Prepare(shaped.Definition,cells,from,to);
        var marks=data.Model.Build(data.Snapshot(frame),inputs.Dependencies);
        if(marks.Evaluations.Any(e=>e.Use==InputUse.FrozenFact))
            throw new MarketDataException("interval_delivered","Resolve frozen-anchor prices to retain their modeled state and missing live market-risk coverage.");
        var build=BlockShaping.Evaluate(inputs,marks);
        return new {curve=request.Curve,kind="shaped",method=Method(shaped.Definition),calibrationPolicy=shaped.Definition.CalibrationPolicy,
            operatorVersion=shaped.Definition.OperatorVersion,at=frame.At,cutoff=marks.Snapshot.MarketCutoff,knownAt,
            numericalPolicy=marks.NumericalPolicy,displayPolicy=DisplayPolicy,shapeSeries=series,
            points=build.Prices.Select(p=>new {interval=IntervalCurveDefinitions.Label(p.Interval),price=Price(p.Price),
                block=new {curve=p.Block.Curve.Code,contractId=p.Block.Contract.Id,observationIds=Ids(p.Lineage)},shapeRevision=p.ShapeRevision})};
    }

    // Delivered intervals take their collected actuals known at the explicit cutoff, under the curve's due rule;
    // live intervals take their pinned marks. The valuation instant is the snapshot's.
    private async Task<object> ResolvedValues(NpgsqlConnection connection,PreviewData data,InputFrame frame,IntervalCurveValuesRequest request,
        DateTimeOffset from,DateTimeOffset to,CancellationToken ct)
    {
        var knownAt=Instant(request.KnownAt,Cutoff);
        var snapshot=data.Snapshot(frame);
        var isShaped=IntervalCurveDefinitions.IsShaped(data,request.Curve);
        var (sourceId,rule)=await ActualRule(connection,data,request.Curve,isShaped,ct);
        var source=await PowerHistory.Source(connection,sourceId,ct);
        PriceCurve curve;PowerClock clock;string clockCode;int minutes;
        object? shapeSeries=null;ImmutableArray<SeriesCell> cells=[];var frozenContexts=new List<Guid>();
        if(isShaped)
        {
            var shaped=IntervalCurveDefinitions.Shaped(data,request.Curve,await products.Read(connection,ct));
            IntervalPrices.RequireWindow(shaped.Definition.Grid.Clock,shaped.Definition.Grid.IntervalMinutes,from,to);
            var shapedPrice=IntervalCurveDefinitions.PriceCurve(data,shaped,rule,snapshot);
            var (frozenKnowledge,frozenContext)=await FrozenKnowledge(connection,data,snapshot,knownAt,ct);
            curve=shapedPrice with {FrozenKnowledge=frozenKnowledge};
            if(frozenContext is {} captured)frozenContexts.Add(captured);
            clock=shaped.Definition.Grid.Clock;clockCode=shaped.ClockCode;minutes=shaped.Definition.Grid.IntervalMinutes;
            var (first,last)=BlockShaping.ShapeWindow(shaped.Definition,from,to);
            (shapeSeries,cells)=await ReadShape(connection,shaped.Definition.ShapeSeries,minutes,first,last,knownAt,ct);
        }
        else
        {
            var definition=IntervalCurveDefinitions.Marked(data,request.Curve);
            Window(definition,frame,snapshot.ValuationAt,from,to);
            curve=IntervalCurveDefinitions.PriceCurve(data,definition,rule);
            clock=definition.Clock;clockCode=definition.ClockCode;minutes=definition.IntervalMinutes;
        }
        if(source.Clock.Code!=clockCode || source.Minutes!=minutes)
            throw new MarketDataException("interval_actual","The curve's collected-price source must share its clock and interval length.");
        var actuals=new List<IntervalActual>();var contexts=new List<Guid>();
        foreach(var (first,last) in Chunks(from,to,ActualReadWindow))
        {
            var history=await PowerHistory.ReadPrices(connection,sourceId,first,last,knownAt,ct);
            actuals.AddRange(history.Rows);contexts.Add(history.ContextId);
        }
        var prepared=PreparedCurve.Prepare(curve,[(from,to)],cells,actuals,snapshot.ValuationAt,knownAt);
        var dependencies=prepared.Months.SelectMany(month=>prepared.Dependencies(month)).Distinct().ToImmutableArray();
        var build=data.Model.Build(snapshot,dependencies);
        var resolver=new IntervalPriceResolver(build,new Dictionary<CurveKey,PreparedCurve>{{curve.Key,prepared}});
        if(request.Shock is {} shock)
        {
            var factor=data.Model.QuoteFactors(snapshot).SingleOrDefault(f=>f.Input.Series.Id==shock.SeriesId && f.Quote.Code==shock.Quote)
                ??throw new MarketDataException("risk_factor","Select the exact live quote series and quote specification.");
            var bump=new QuoteBump(factor.Input,factor.Quote,factor.Unit,ExactDecimal.Parse(shock.Size??""),shock.Convention);
            var pair=data.Model.Scenarios(build,[bump],ct).Single();
            resolver=new IntervalPriceResolver(shock.Up?pair.Up:pair.Down,new Dictionary<CurveKey,PreparedCurve>{{curve.Key,prepared}},resolver);
        }
        var resolved=prepared.Months.SelectMany(month=>resolver.Prices(curve.Key,month)).OrderBy(r=>r.Interval.Start).ToImmutableArray();
        var shapeRevisions=cells.ToDictionary(c=>c.Start,c=>c.Revision);
        return new {curve=request.Curve,kind=isShaped?"shaped":"marked",resolved=true,at=frame.At,valuationAt=snapshot.ValuationAt,cutoff=snapshot.MarketCutoff,knownAt,
            rule=new {anchor=rule.Anchor,offsetMinutes=rule.OffsetMinutes,version=rule.Version},sourceId,knowledgeContextIds=contexts.Concat(frozenContexts).Distinct(),shapeSeries,
            numericalPolicy=CurveModel.NumericalPolicy,displayPolicy=DisplayPolicy,
            points=resolved.Select(r=>new {interval=IntervalCurveDefinitions.Label(r.Interval),state=r.State.ToString().ToLowerInvariant(),dueAt=r.DueAt,
                price=Price(r.Price),estimate=Price(r.Estimate),publication=r.Publication,reason=r.Reason,liveMarketRiskCoverage=r.LiveMarketRiskCoverage,
                anchor=r.Anchor is null?null:new {curve=r.Anchor.Request.Curve.Code,contractId=r.Anchor.Request.Contract.Id,use=r.Anchor.Use.ToString().ToLowerInvariant(),observationIds=Ids(r.Anchor.Lineage)},
                block=r.Anchor is null?null:new {curve=r.Anchor.Request.Curve.Code,contractId=r.Anchor.Request.Contract.Id,observationIds=Ids(r.Anchor.Lineage)},
                shapeRevision=isShaped && r.Actual is null && shapeRevisions.TryGetValue(r.Interval.Start,out var revision)?(int?)revision:null,
                actual=r.Actual is null?null:new {batchId=r.Actual.BatchId,providerRevision=r.Actual.Revision,knownAt=r.Actual.KnownAt},
                forward=r.State==IntervalPriceState.Projected?new {observationIds=Ids(r.ForwardLineage)}:null}),
            aggregates=request.Aggregate is null?null:IntervalPricing.Aggregate(resolved,request.Aggregate switch
                {"hour"=>IntervalAggregation.Hour,"day"=>IntervalAggregation.Day,_=>throw new MarketDataException("interval_aggregate","Aggregate by 'hour' or 'day'.")},clock)
                .Select(a=>new {start=a.Start,end=a.End,policy=IntervalPricing.AggregatePolicy,mean=Price(a.Mean),constituents=a.Constituents.Select(c=>c.Start)})};
    }

    // Certified publication receipts, never observation receipt audit timestamps, bound the frozen-anchor knowledge view.
    internal static async Task<(ImmutableDictionary<ObservationKey,DateTimeOffset> Knowledge,Guid? Context)> FrozenKnowledge(
        NpgsqlConnection connection,PreviewData data,MarketSnapshot snapshot,DateTimeOffset knownAt,CancellationToken ct)
    {
        var selected=snapshot.Selections.Where(s=>s.Input.Use==InputUse.FrozenFact && s.Observation.Role==ObservationRole.FinalContractSettlement)
            .Select(s=>s.Observation.Key.Id).ToArray();
        if(selected.Length==0)return (ImmutableDictionary<ObservationKey,DateTimeOffset>.Empty,null);
        var captured=await new KnowledgeStore(data.DefinitionRevision.CatalogId).Capture(connection,
            new(DateOnly.FromDateTime(snapshot.ValuationAt.UtcDateTime).ToString("yyyy-MM-dd",System.Globalization.CultureInfo.InvariantCulture),knownAt.ToString("O"),"at"),ct);
        var rows=await PreviewDatabase.Rows(connection,"""
            SELECT row_to_json(q)::text FROM(SELECT o.id,k.known_from FROM market_data.observations o
            JOIN market_data.knowledge_events e ON(e.catalog_id,e.revision_id,e.observation_id)=(o.catalog_id,o.definition_revision_id,o.id)
            JOIN market_data.market_knowledge_receipts k ON(k.catalog_id,k.event_order,k.publication_xid)=(e.catalog_id,e.event_order,e.publication_xid)
            JOIN market_data.market_contexts c ON c.catalog_id=e.catalog_id AND c.context_id=$4
            WHERE o.catalog_id=$1 AND o.definition_revision_id=$2 AND o.id=ANY($3::uuid[]) AND e.event_order<=c.known_through AND k.known_from<=$5)q
            """,[data.DefinitionRevision.CatalogId,data.DefinitionRevision.RevisionId,selected,captured.Context.ContextId!.Value,knownAt],ct);
        return (rows.ToImmutableDictionary(r=>new ObservationKey(data.DefinitionRevision.CatalogId,r.Id("id")),r=>r.Instant("known_from")),captured.Context.ContextId);
    }

    // A period consumer reads only observations pinned in the selected frame; no independent shape or actual cutoff applies.
    public async Task<object> PeriodValues(NpgsqlConnection connection,PinnedContext pin,PeriodCurveValuesRequest request,CancellationToken ct)
    {
        var frame=pin.RequireFrame();
        var data=await reads.Data(connection,pin,ct,frame.ObservationIds,includeCalendarDays:false);
        var snapshot=data.Snapshot(frame);var curve=IntervalCurveDefinitions.Period(data,request.Curve);
        var from=Instant(request.From,IntervalPrices.InvalidWindow);var to=Instant(request.To,IntervalPrices.InvalidWindow);
        var prepared=PreparedCurve.Prepare(curve,[(from,to)],[],[],snapshot.ValuationAt,snapshot.MarketCutoff);
        var dependencies=prepared.Months.SelectMany(month=>prepared.Dependencies(month)).Distinct().ToImmutableArray();
        var selected=frame.CurveContracts.GetValueOrDefault(request.Curve,[]).ToHashSet();
        if(dependencies.Any(d=>!selected.Contains(d.Contract.Id)))
            throw new MarketDataException("missing_curve_view","The frame must explicitly select every live period projection.");
        var resolver=new IntervalPriceResolver(data.Model.Build(snapshot,dependencies),new Dictionary<CurveKey,PreparedCurve>{{curve.Key,prepared}});
        var resolved=prepared.Months.SelectMany(month=>resolver.Prices(curve.Key,month)).ToImmutableArray();
        return new {curve=request.Curve,kind="period",at=frame.At,cutoff=snapshot.MarketCutoff,numericalPolicy=CurveModel.NumericalPolicy,displayPolicy=DisplayPolicy,
            points=resolved.Select(r=>new {interval=IntervalCurveDefinitions.Label(r.Interval),
                state=r.State.ToString().ToLowerInvariant(),price=Price(r.Price),publication=r.Publication,observationIds=Ids(r.Anchor?.Lineage??r.ForwardLineage)})};
    }

    // The curve's bound collected-price source and its due rule, in its exact definition version.
    internal static async Task<(Guid Source,IntervalActualRule Rule)> ActualRule(NpgsqlConnection connection,PreviewData data,string curve,bool shaped,CancellationToken ct)
    {
        var sql=shaped?"""
            SELECT row_to_json(q)::text FROM(SELECT source_id,anchor,offset_minutes,rule_version FROM market_data.shaped_curve_actual_rules
            WHERE(catalog_id,revision_id,curve_code)=($1,$2,$3))q
            """:"""
            SELECT row_to_json(q)::text FROM(SELECT g.source_id,r.anchor,r.offset_minutes,r.rule_version FROM market_data.curve_actual_rules r
            JOIN market_data.curve_physical_price_sources g ON(g.catalog_id,g.revision_id,g.curve_code)=(r.catalog_id,r.revision_id,r.curve_code)
            WHERE(r.catalog_id,r.revision_id,r.curve_code)=($1,$2,$3))q
            """;
        var rows=await PreviewDatabase.Rows(connection,sql,[data.DefinitionRevision.CatalogId,data.DefinitionRevision.RevisionId,curve],ct);
        if(rows.Length!=1)throw NoActuals();
        var row=rows[0];
        return (row.Id("source_id"),new(row.Text("anchor"),int.Parse(row.Text("offset_minutes"),System.Globalization.CultureInfo.InvariantCulture),row.Text("rule_version")));
    }

    // The shape over [first, last) through the audited series read, in windows the read accepts.
    internal static async Task<(object Series,ImmutableArray<SeriesCell> Cells)> ReadShape(NpgsqlConnection connection,Guid series,int minutes,
        DateTimeOffset first,DateTimeOffset last,DateTimeOffset knownAt,CancellationToken ct)
    {
        var cells=ImmutableArray.CreateBuilder<SeriesCell>();
        var reads=new List<System.Text.Json.JsonElement>();
        foreach(var (from,to) in Chunks(first,last,TimeSpan.FromMinutes(minutes)*IntervalPrices.MaximumWindowIntervals))
        {
            var read=await IntervalSeriesStore.Values(connection,series,from,to,knownAt,ct);reads.Add(read);
            cells.AddRange(read.GetProperty("rows").EnumerateArray().Select(row=>
                new SeriesCell(row.Instant("start"),row.Instant("end"),ExactDecimal.Parse(row.Text("value")),row.GetProperty("revision").GetInt32())));
        }
        return (new {id=series,code=reads[0].GetProperty("series").Text("code"),knownAt,head=reads[0].GetProperty("head").GetInt32(),
            knowledgeContextIds=reads.Select(r=>r.Id("contextId")).Distinct()},cells.ToImmutable());
    }

    // The curve's interval contracts covering the window. The frame must select each one still live at its valuation
    // instant; an ended interval has no mark to select and comes from the definition.
    private static ImmutableArray<MarketContract> Window(IntervalCurveDefinition definition,InputFrame frame,DateTimeOffset valuationAt,
        DateTimeOffset from,DateTimeOffset to)
    {
        IntervalPrices.RequireWindow(definition.Clock,definition.IntervalMinutes,from,to);
        var window=IntervalPrices.Within(definition.Contracts,definition.IntervalMinutes,from,to);
        var selected=frame.CurveContracts.TryGetValue(definition.Curve.Code,out var view)?view.ToHashSet():[];
        return window.Where(c=>c.Expiry>valuationAt).All(c=>selected.Contains(c.Key.Id))?window
            :throw new MarketDataException("missing_curve_view","An explicit contract selection is required for this market date.");
    }

    // The pinned marks of exactly these contracts, in one build.
    private static ImmutableArray<IntervalPrice> Marks(PreviewData data,MarketSnapshot snapshot,IntervalCurveDefinition definition,
        ImmutableArray<MarketContract> contracts)=>contracts.IsEmpty?[]:
        IntervalPrices.Marked(data.Model.Build(snapshot,[..contracts.Select(c=>new CurveRequest(definition.Curve,c.Key))]),definition.Curve,contracts);

    // [from, to) in consecutive windows of at most the given length.
    internal static IEnumerable<(DateTimeOffset From,DateTimeOffset To)> Chunks(DateTimeOffset from,DateTimeOffset to,TimeSpan size)
    {
        for(var at=from;at<to;at+=size)yield return (at,at+size<to?at+size:to);
    }

    private static object? Price(ExactUnitPrice? price)=>price is null?null:new {numerator=price.Numerator,denominator=price.Denominator,display=price.Format(Display)};
    private static Guid[] Ids(IEnumerable<ObservationKey> lineage)=>[..lineage.Select(k=>k.Id).Order()];
    private static string Method(ShapedCurveDefinition definition)=>definition.Method.ToString().ToLowerInvariant();

    private static MarketDataException Cutoff()=>
        new("interval_curve_cutoff","A shaped or resolved curve needs an explicit knowledge cutoff; a marked curve read takes none.");
    private static MarketDataException NoActuals()=>
        new("interval_actual","This curve has no collected actual-price source and due rule.");

    // Window ends and cutoffs use the zero-offset instant forms the interval series routes accept; a missing one is refused.
    private static DateTimeOffset Instant(string? value,Func<MarketDataException> refusal)
    {
        try { return IntervalSeriesStore.ParseInstant(value); }
        catch(MarketDataException e) when(e.Code=="interval_series_instant") { throw refusal(); }
    }
}
