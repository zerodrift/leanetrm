using System.Collections.Immutable;
using OpenEtrm.MarketData.CustomCurves;
using OpenEtrm.MarketData.Trading;

namespace OpenEtrm.MarketData.Power;

public abstract record PriceCurve(CurveKey Key,PriceUnit Unit);
public sealed record FlatIntervalCurve(CurveKey Key,PriceUnit Unit,DeliveryGrid Grid,
    ImmutableArray<IntervalBlockDefinition> Blocks,string Policy):PriceCurve(Key,Unit)
{
    public PublishedRecipeReference? Output { get; init; }
    public IntervalActualRule? ActualRule { get; init; }
    public DeliveryCoverage? ActualPrefix { get; init; }
    public ShapedCurveDefinition? PriceShape { get; init; }
    public const string ReplaceActuals = "flat_native_mark_replace_actual_v1";
}
public sealed record MarkedIntervalCurve(CurveKey Key,PriceUnit Unit,IntervalActualRule Rule,PowerClock Clock,int IntervalMinutes,
    ImmutableArray<MarketContract> Contracts):PriceCurve(Key,Unit);
public sealed record ShapedIntervalCurve(CurveKey Key,PriceUnit Unit,IntervalActualRule Rule,ShapedCurveDefinition Definition):PriceCurve(Key,Unit)
{
    // Exact frame-selected contract identities. Required for overlapping balance definitions.
    public ImmutableHashSet<ContractKey>? SelectedAnchors { get; init; }
    // Certified publication knowledge, supplied by the authorized reader for exact pinned observations.
    public ImmutableDictionary<ObservationKey,DateTimeOffset> FrozenKnowledge { get; init; } = ImmutableDictionary<ObservationKey,DateTimeOffset>.Empty;
}
public sealed record PeriodContractCurve(CurveKey Key,PriceUnit Unit,ReferenceKey SettlementIndex,ImmutableArray<MarketContract> Contracts,
    ImmutableArray<IndexFixing> Fixings):PriceCurve(Key,Unit);

internal sealed record PreparedPriceMonth(ImmutableArray<DeliveryInterval> Intervals,
    ImmutableDictionary<DateTimeOffset,IntervalActual> Actuals,ImmutableArray<CurveRequest> Dependencies,
    ShapedInputs? Shape,ImmutableArray<MarketContract> Contracts,
    ImmutableArray<(CurveRequest Request,MarketContract Contract)> Sources)
{
    internal FlatBlockProjection? Flat { get; init; }
}

// Invariant inputs are copied and compiled once per authorized request. Object identity is deliberately
// the reuse boundary: equal curve codes in another request do not establish equal shape/actual inputs.
public sealed partial class PreparedCurve
{
    internal PriceCurve Curve { get; }
    internal ImmutableDictionary<DateOnly,PreparedPriceMonth> Periods { get; }
    public CurveKey Key=>Curve.Key;
    public PriceUnit Unit=>Curve.Unit;
    public DateTimeOffset ValuationAt { get; }
    public DateTimeOffset KnownAt { get; }
    public ImmutableArray<DateOnly> Months { get; }
    private PreparedCurve(PriceCurve curve,DateTimeOffset valuationAt,DateTimeOffset knownAt,
        ImmutableDictionary<DateOnly,PreparedPriceMonth> periods)
    {Curve=curve;ValuationAt=valuationAt;KnownAt=knownAt;Periods=periods;Months=[..periods.Keys.Order()];}
    public ImmutableArray<CurveRequest> Dependencies(DateOnly month)=>Dependencies(month,null);
    public ImmutableArray<CurveRequest> Dependencies(DateOnly month,PriceSettlementSelection? selection)
    {
        var period=Period(month);
        if(Curve is not PeriodContractCurve curve)
        {
            if(selection is not null)throw Invalid("Interval curves take no period settlement selection.");
            return period.Dependencies;
        }
        selection??=PriceSettlementSelection.CurveTarget(curve.SettlementIndex);
        if(selection.CurveTargetIndex!=curve.SettlementIndex)throw Invalid("The settlement selection must retain the exact period curve target.");
        return [..period.Contracts.Where(contract=>PeriodPriceSelector.RequiresProjection(selection,contract,Fixing(curve,contract,selection),ValuationAt))
            .Select(contract=>new CurveRequest(Key,contract.Key))];
    }
    internal static IndexFixing? Fixing(PeriodContractCurve curve,MarketContract contract,PriceSettlementSelection selection)=>
        curve.Fixings.SingleOrDefault(f=>f.Index==selection.SelectedIndex && f.From==contract.DeliveryFrom && f.To==contract.DeliveryTo);
    internal PreparedPriceMonth Period(DateOnly month)=>Periods.TryGetValue(month,out var period)?period:
        throw new MarketDataException("interval_price_source","The requested month is outside the prepared coverage.");

    public ImmutableArray<(Guid Series,CurveRequest Anchor,ImmutableArray<SeriesCell> Cells)> ShapeInputs=>
        [..Periods.Values.Where(p=>p.Shape is not null).SelectMany(p=>p.Shape!.Blocks.Select(b=>(p.Shape.Definition.ShapeSeries,b.Request,b.Cells.ToImmutableArray()))).DistinctBy(p=>p.Item2)];

    public PreparedCurve WithPriceShape(ShapedCurveDefinition declared,IReadOnlyList<SeriesCell> shape)
    {
        if(Curve is not FlatIntervalCurve{PriceShape:null} flat||declared.Method!=ShapeMethod.Additive||declared.ShapeSeries==Guid.Empty||declared.Code!=flat.Key.Code||
            declared.CalibrationPolicy!=BlockShaping.CalibrationPolicy||declared.OperatorVersion!=BlockShaping.OperatorVersion||
            declared.Grid.Clock.Fingerprint!=flat.Grid.Clock.Fingerprint||declared.Grid.IntervalMinutes!=flat.Grid.IntervalMinutes||
            !declared.Grid.Holidays.SequenceEqual(flat.Grid.Holidays)||declared.Blocks.IsDefaultOrEmpty||
            !declared.Blocks.SequenceEqual(flat.Blocks.Where(b=>b.Membership is BlockMembership.DailyPeak or BlockMembership.DailyOffPeak))||!declared.BalanceBlocks.IsEmpty)
            throw new MarketDataException("interval_flat_policy","The additive shape must retain the exact daily-native blocks and grid; monthly sources remain flat.");
        var declaredRequests=declared.Blocks.SelectMany(b=>b.Contracts.Select(c=>new CurveRequest(b.Curve,c.Key))).ToHashSet();
        var periods=Periods.ToBuilder();
        foreach(var(month,period)in Periods)
        {
            var compiled=period.Flat??throw Invalid("Price shaping requires an existing native-block projection.");
            var live=period.Dependencies.ToHashSet();
            var selected=compiled.Intervals.Select((cell,i)=>(cell,owner:compiled.Owners[i]))
                .Where(p=>p.owner>=0&&!period.Actuals.ContainsKey(p.cell.Start)&&live.Contains(compiled.Anchors[p.owner].Request)&&declaredRequests.Contains(compiled.Anchors[p.owner].Request))
                .ToImmutableDictionary(p=>p.cell.Start,p=>compiled.Anchors[p.owner].Request);
            if(selected.Count>0)periods[month]=period with{Shape=BlockShaping.PrepareSelected(declared,shape,period.Intervals[0].Start,period.Intervals[^1].End,selected)};
        }
        return new(flat with{PriceShape=declared},ValuationAt,KnownAt,periods.ToImmutable());
    }

    public static PreparedCurve Prepare(PriceCurve curve,ImmutableArray<(DateTimeOffset From,DateTimeOffset To)> spans,
        IReadOnlyList<SeriesCell> shape,IReadOnlyList<IntervalActual> actuals,DateTimeOffset valuationAt,DateTimeOffset knownAt)
    {
        ArgumentNullException.ThrowIfNull(curve);ArgumentNullException.ThrowIfNull(shape);ArgumentNullException.ThrowIfNull(actuals);
        if(spans.IsDefaultOrEmpty || valuationAt.Offset!=TimeSpan.Zero || knownAt.Offset!=TimeSpan.Zero ||
            spans.Any(s=>s.From.Offset!=TimeSpan.Zero || s.To.Offset!=TimeSpan.Zero || s.From>=s.To))
            throw Invalid("Supply explicit UTC coverage, valuation and knowledge instants.");
        var coverage=new List<(DateTimeOffset From,DateTimeOffset To)>();
        foreach(var span in spans.OrderBy(s=>s.From))
            if(coverage.Count>0 && span.From<=coverage[^1].To)
                coverage[^1]=(coverage[^1].From,span.To>coverage[^1].To?span.To:coverage[^1].To);
            else coverage.Add(span);
        spans=[..coverage];
        var periods=ImmutableDictionary.CreateBuilder<DateOnly,PreparedPriceMonth>();
        if(curve is FlatIntervalCurve flat)
        {
            if(flat.ActualRule?.ReleaseClock is {} releaseClock&&releaseClock.Fingerprint!=flat.Grid.Clock.Fingerprint)
                throw Invalid("Actual release and delivery must retain the same exact clock rules.");
            var replace=flat.Policy==FlatIntervalCurve.ReplaceActuals;
            if(shape.Count!=0&&flat.PriceShape is null||(!replace&&(actuals.Count!=0||flat.ActualRule is not null))||replace&&flat.ActualRule is null)
                throw new MarketDataException("interval_flat_policy","Select an explicit actual rule for native-mark replacement; forward-only curves take no shape or actuals.");
            if(flat.ActualPrefix is {} prefix&&(!replace||flat.Blocks.IsDefaultOrEmpty||flat.Blocks.Any(b=>b.Contracts.IsDefaultOrEmpty)||
                prefix.To!=flat.Grid.Clock.StartOfDay(flat.Blocks.SelectMany(b=>b.Contracts).Min(c=>c.DeliveryFrom)).ToUniversalTime()))
                throw Invalid("An actual-only prefix must end at this curve's exact forward boundary.");
            var parts=new Dictionary<DateOnly,List<FlatBlockProjection>>();
            foreach(var span in spans)
                for(var from=span.From;from<span.To;)
                {
                    var month=Month(DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(from,flat.Grid.Clock.Zone).DateTime));
                    var to=flat.Grid.Clock.StartOfDay(month.AddMonths(1)).ToUniversalTime();if(to>span.To)to=span.To;
                    if(!parts.TryGetValue(month,out var pieces))parts.Add(month,pieces=[]);
                    if(flat.ActualPrefix is {} historical&&from<historical.To)
                    {
                        if(from<historical.From)throw Invalid("Actual-only demand must stay within its declared prefix.");
                        if(to>historical.To)to=historical.To;
                        IntervalPrices.RequireWindow(flat.Grid.Clock,flat.Grid.IntervalMinutes,from,to);
                        var first=DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(from,flat.Grid.Clock.Zone).DateTime);
                        var last=DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(to.AddTicks(-1),flat.Grid.Clock.Zone).DateTime).AddDays(1);
                        ImmutableArray<PowerInterval> cells=[..PowerHours.Compile(first,last,flat.Grid.Clock,TimeOfUse.All(flat.Grid.IntervalMinutes),[..flat.Grid.Holidays],flat.Grid.IntervalMinutes)
                            .Where(p=>p.Start>=from&&p.End<=to)];
                        pieces.Add(new(cells,[],[..cells.Select(_=>-1)]));
                    }
                    else pieces.Add(replace?BlockShaping.CompileFlat(flat.Grid,flat.Blocks,from,to):
                        BlockShaping.PrepareFlat(flat.Grid,flat.Blocks,from,to,valuationAt,flat.Policy));
                    from=to;
                }
            if(parts.Count>1200)throw Invalid("At most 1,200 operating months can be prepared.");
            foreach(var (month,pieces) in parts)
            {
                var anchors=pieces.SelectMany(p=>p.Anchors).Distinct().ToImmutableArray();
                var indexes=anchors.Select((a,i)=>(a.Request,i)).ToDictionary(p=>p.Request,p=>p.i);
                var compiled=new FlatBlockProjection([..pieces.SelectMany(p=>p.Intervals)],anchors,
                    [..pieces.SelectMany(p=>p.Owners.Select(i=>i<0?-1:indexes[p.Anchors[i].Request]))]);
                ImmutableArray<DeliveryInterval> intervals=[..compiled.Intervals.Select(p=>p.Delivery)];
                var eligible=ImmutableDictionary<DateTimeOffset,IntervalActual>.Empty;
                ImmutableArray<CurveRequest> dependencies=[..anchors.Select(a=>a.Request)];
                if(replace)
                {
                    var byStart=intervals.ToDictionary(i=>i.Start);
                    var relevant=actuals.Where(a=>a.Start<intervals[^1].End&&a.End>intervals[0].Start).ToArray();
                    eligible=IntervalPricing.ValidateInputs(intervals,flat.Unit,relevant,flat.ActualRule!,knownAt)
                        .Where(p=>byStart.TryGetValue(p.Key,out var cell)&&flat.ActualRule!.Eligible(p.Value,cell,valuationAt)).ToImmutableDictionary();
                    var live=new HashSet<CurveRequest>();
                    for(var i=0;i<intervals.Length;i++)
                    {
                        var interval=intervals[i];if(eligible.ContainsKey(interval.Start))continue;
                        if(compiled.Owners[i]<0)
                            throw new MarketDataException("interval_actual_missing",$"The actual-only prefix requires an eligible captured price for {interval.Start:O}.");
                        if(valuationAt>=flat.ActualRule!.DueAt(interval.Start,interval.End))
                            throw new MarketDataException("interval_actual_missing",$"The actual for {interval.Start:O} is due and missing.");
                        if(interval.End<=valuationAt)continue;
                        var anchor=anchors[compiled.Owners[i]];
                        if(anchor.Contract.Expiry<=valuationAt)
                            throw new MarketDataException("interval_flat_delivered","An unfixed live interval requires a live native mark; expired-anchor projection is not admitted.");
                        live.Add(anchor.Request);
                    }
                    dependencies=[..anchors.Where(a=>live.Contains(a.Request)).Select(a=>a.Request)];
                }
                periods.Add(month,new(intervals,eligible,dependencies,null,[],anchors){Flat=compiled});
            }
            var result=new PreparedCurve(flat with{PriceShape=null},valuationAt,knownAt,periods.ToImmutable());
            return flat.PriceShape is {} shapeDefinition?result.WithPriceShape(shapeDefinition,shape):result;
        }
        if(curve is PeriodContractCurve pc)
        {
            if(pc.Contracts.IsDefaultOrEmpty || pc.Fixings.IsDefault || shape.Count!=0 || actuals.Count!=0 ||
                pc.Contracts.Any(c=>c.Interval is not null) || pc.Fixings.GroupBy(f=>(f.Index,f.From,f.To)).Any(g=>g.Count()!=1))
                throw Invalid("Period curves require distinct date-only contracts and explicit qualified fixing schedules.");
            Money.RequireKey(pc.SettlementIndex);
            var selected=pc.Contracts.Where(c=>spans.Any(s=>Utc(c.DeliveryFrom)<s.To && Utc(c.DeliveryTo)>s.From)).ToArray();
            if(selected.Length==0 || selected.Any(c=>!spans.Any(s=>s.From<=Utc(c.DeliveryFrom) && s.To>=Utc(c.DeliveryTo))))
                throw Invalid("Period pricing requires whole declared periods.");
            foreach(var span in spans)
            {
                var cursor=span.From;
                foreach(var contract in selected.Where(c=>Utc(c.DeliveryFrom)>=span.From && Utc(c.DeliveryTo)<=span.To).OrderBy(c=>c.DeliveryFrom))
                {
                    if(Utc(contract.DeliveryFrom)!=cursor)throw Invalid("Period contracts must cover every requested instant exactly once.");
                    cursor=Utc(contract.DeliveryTo);
                }
                if(cursor!=span.To)throw Invalid("Period contracts must cover every requested instant exactly once.");
            }
            if(selected.Length>1200)throw Invalid("At most 1,200 period contracts can be prepared.");
            // Monthly consumers select the exact declared vintage containing their delivery
            // date. An annual contract remains annual; this index does not split its economics.
            var selectedMonths=new Dictionary<DateOnly,List<MarketContract>>();
            foreach(var contract in selected)
                for(var month=Month(contract.DeliveryFrom);month<contract.DeliveryTo;month=month.AddMonths(1))
                {
                    if(!selectedMonths.TryGetValue(month,out var list))selectedMonths.Add(month,list=[]);
                    list.Add(contract);
                    if(selectedMonths.Count>1200)throw Invalid("At most 1,200 period-consumer months can be prepared.");
                }
            foreach(var group in selectedMonths)
            {
                var contracts=group.Value.OrderBy(c=>c.DeliveryFrom).ToImmutableArray();
                if(contracts.Zip(contracts.Skip(1)).Any(p=>p.First.DeliveryTo>p.Second.DeliveryFrom))throw Invalid("Period contracts cannot overlap.");
                // Preparation retains geometry and all qualified definitions.
                // The bound selection owns expiry/fixing admission when its
                // dependencies are requested; another index can already be due.
                periods.Add(group.Key,new([..contracts.Select(c=>new DeliveryInterval(Utc(c.DeliveryFrom),Utc(c.DeliveryTo),c.DeliveryFrom,0,0,1))],
                    ImmutableDictionary<DateTimeOffset,IntervalActual>.Empty,[],null,contracts,[..contracts.Select(c=>(new CurveRequest(pc.Key,c.Key),c))]));
            }
            return new(curve,valuationAt,knownAt,periods.ToImmutable());
        }
        var (clock,minutes,rule)=curve switch
        {
            MarkedIntervalCurve m=>(m.Clock,m.IntervalMinutes,m.Rule),
            ShapedIntervalCurve s=>(s.Definition.Grid.Clock,s.Definition.Grid.IntervalMinutes,s.Rule),
            _=>throw Invalid("Unsupported price curve type.")
        };
        if(curve is MarkedIntervalCurve marked)IntervalContracts.Validate(clock,minutes,marked.Contracts);
        if(curve is ShapedIntervalCurve shaped && !shaped.Definition.BalanceBlocks.IsEmpty && shaped.SelectedAnchors is null)
            throw Invalid("Balance-block resolution requires the exact frame-selected anchors.");
        var byMonth=new Dictionary<DateOnly,List<(DateTimeOffset From,DateTimeOffset To)>>();
        foreach(var span in spans)
        {
            // Split before checking the bounded grid: multi-year consumers never compile a multi-year block.
            for(var from=span.From;from<span.To;)
            {
                var month=Month(DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(from,clock.Zone).DateTime));
                var to=clock.StartOfDay(month.AddMonths(1)).ToUniversalTime();if(to>span.To)to=span.To;
                IntervalPrices.RequireWindow(clock,minutes,from,to);
                if(!byMonth.TryGetValue(month,out var ranges))byMonth[month]=ranges=[];
                ranges.Add((from,to));from=to;
            }
        }
        if(byMonth.Count>1200)throw Invalid("At most 1,200 operating months can be prepared.");
        var shapeData=curve is ShapedIntervalCurve?IntervalData.From(shape,minutes):null;
        foreach(var (month,ranges) in byMonth.OrderBy(p=>p.Key))
        {
            var all=PowerHours.Compile(month,month.AddMonths(1),clock,TimeOfUse.All(minutes),[],minutes)
                .Where(p=>ranges.Any(r=>p.Start>=r.From && p.End<=r.To)).ToArray();
            ImmutableArray<DeliveryInterval> intervals=[..all.Select(p=>p.Delivery)];
            var relevant=actuals.Where(a=>ranges.Any(r=>a.Start<r.To && a.End>r.From)).ToArray();
            var actualMap=IntervalPricing.ValidateInputs(intervals,curve.Unit,relevant,rule,knownAt);
            var eligible=actualMap.Where(p=>intervals.Any(i=>i.Start==p.Key && rule.Eligible(p.Value,i,valuationAt))).ToImmutableDictionary();
            foreach(var interval in intervals)
                if(!eligible.ContainsKey(interval.Start) && valuationAt>=rule.DueAt(interval.Start,interval.End))
                    throw new MarketDataException("interval_actual_missing",$"The actual for {interval.Start:O} is due and missing.");
            ShapedInputs? preparedShape=null;ImmutableArray<MarketContract> contracts;ImmutableArray<CurveRequest> dependencies;
            ImmutableArray<(CurveRequest Request,MarketContract Contract)> sources;
            if(curve is MarkedIntervalCurve m)
            {
                contracts=[..ranges.SelectMany(r=>IntervalPrices.Within(m.Contracts,minutes,r.From,r.To)).DistinctBy(c=>c.Key).OrderBy(c=>c.Interval!.Start)];
                dependencies=[..contracts.Where(c=>c.Expiry>valuationAt && !eligible.ContainsKey(c.Interval!.Start)).Select(c=>new CurveRequest(m.Key,c.Key))];
                sources=[..contracts.Select(c=>(new CurveRequest(m.Key,c.Key),c))];
            }
            else
            {
                var s=(ShapedIntervalCurve)curve;var definition=s.Definition;
                var blocks=definition.Blocks.Select(b=>(Block:b,Balance:false)).Concat(definition.BalanceBlocks.Select(b=>(Block:b,Balance:true)))
                    .SelectMany(b=>b.Block.Contracts.Where(c=>c.DeliveryFrom<month.AddMonths(1) && c.DeliveryTo>month)
                        .Select(c=>(b.Block,b.Balance,Contract:c,Periods:IntervalBlocks.Compile(definition.Grid,b.Block,c)))).ToArray();
                sources=[..blocks.Select(b=>(new CurveRequest(b.Block.Curve,b.Contract.Key),b.Contract))];
                var byStart=blocks.SelectMany(b=>b.Periods.Select(p=>(p.Start,Block:b))).ToLookup(p=>p.Start,p=>p.Block);
                var owners=ImmutableDictionary.CreateBuilder<DateTimeOffset,CurveRequest>();
                foreach(var interval in intervals)
                {
                    var covering=byStart[interval.Start].ToArray();
                    var owning=covering.Count(b=>!b.Balance);
                    if(owning==0)throw new MarketDataException("interval_block_coverage","No period block of the curve covers an interval of the requested window.");
                    if(owning>1)throw new MarketDataException("interval_block_partition","Period blocks must partition the requested grid exactly once.");
                    if(eligible.ContainsKey(interval.Start))continue;
                    var balances=covering.Where(b=>b.Balance && b.Contract.Expiry>valuationAt && s.SelectedAnchors!.Contains(b.Contract.Key)).ToArray();
                    if(balances.Length>1)throw new MarketDataException("interval_anchor_ambiguous","More than one selected live balance prices the same product interval.");
                    var candidates=balances.Length==1?balances:covering.Where(b=>!b.Balance && b.Contract.Expiry>valuationAt).ToArray();
                    if(candidates.Length==0)candidates=covering.Where(b=>b.Contract.Expiry<=valuationAt &&
                        (s.SelectedAnchors is null || s.SelectedAnchors.Contains(b.Contract.Key))).OrderByDescending(b=>b.Contract.Expiry).Take(2).ToArray();
                    if(candidates.Length>1 && candidates[0].Contract.Expiry==candidates[1].Contract.Expiry)
                        throw new MarketDataException("interval_anchor_ambiguous","Frozen anchors must have one latest eligible expiry.");
                    if(candidates.Length==0)
                    {
                        if(interval.End>valuationAt)throw new MarketDataException("interval_anchor_missing","A live interval has no selected anchor, including current-day coverage.");
                        continue;
                    }
                    owners.Add(interval.Start,new(candidates[0].Block.Curve,candidates[0].Contract.Key));
                }
                dependencies=[..owners.Values.Distinct()];
                preparedShape=BlockShaping.PrepareSelected(definition,shapeData!,intervals[0].Start,intervals[^1].End,owners.ToImmutable());
                contracts=[..blocks.Where(b=>dependencies.Any(d=>d.Curve==b.Block.Curve && d.Contract==b.Contract.Key)).Select(b=>b.Contract).DistinctBy(c=>c.Key)];
            }
            periods.Add(month,new(intervals,eligible,dependencies,preparedShape,contracts,sources));
        }
        return new(curve,valuationAt,knownAt,periods.ToImmutable());
    }
    private static DateOnly Month(DateOnly day)=>new(day.Year,day.Month,1);
    private static DateTimeOffset Utc(DateOnly day)=>new(day.ToDateTime(TimeOnly.MinValue,DateTimeKind.Utc));
    private static MarketDataException Invalid(string message)=>new("interval_price_source",message);
}

public sealed class IntervalPriceResolver
{
    private readonly CurveBuild scenario;
    private readonly ImmutableDictionary<CurveKey,PreparedCurve> curves;
    private readonly IntervalPriceResolver? basis;
    private readonly Dictionary<CurveRequest,BuiltValue> values;
    private readonly Dictionary<(CurveKey,DateOnly,PriceSettlementSelection?),ImmutableArray<IntervalPriceResolution>> cache=[];
    private readonly Dictionary<(CurveKey,DateOnly),ShapedCurveBuild> shapes=[];
    public int Resolved { get; private set; }
    public int ReusedMonths { get; private set; }
    public int CalibratedBlocks { get; private set; }
    public int ReusedBlocks { get; private set; }
    public IntervalPriceResolver(CurveBuild scenario,IReadOnlyDictionary<CurveKey,PreparedCurve> curves,IntervalPriceResolver? @base=null)
    {
        ArgumentNullException.ThrowIfNull(scenario);ArgumentNullException.ThrowIfNull(curves);
        this.scenario=scenario;this.curves=curves.ToImmutableDictionary();basis=@base;values=scenario.Values.ToDictionary(v=>v.Request);
        // A product can select no hours in a valid window. Its price context is
        // empty; any attempted lookup still requires an explicitly prepared curve.
        if(curves.Any(p=>p.Key!=p.Value.Key || p.Key.Revision!=scenario.Definitions || p.Value.ValuationAt!=scenario.Snapshot.ValuationAt) ||
            curves.Values.Select(c=>c.KnownAt).Distinct().Count()>1 || scenario.NumericalPolicy!=CurveModel.NumericalPolicy)
            throw Invalid("Every prepared curve must share the exact scenario, valuation and knowledge context.");
        if(@base is not null && (!ReferenceEquals(scenario.Model,@base.scenario.Model) || !scenario.Snapshot.HasSameInputs(@base.scenario.Snapshot) ||
            @base.scenario.Shock is not null || curves.Count!=@base.curves.Count ||
            curves.Any(p=>!@base.curves.TryGetValue(p.Key,out var old) || !ReferenceEquals(p.Value,old))))
            throw Invalid("Reuse requires the same prepared request, immutable snapshot and unshocked base scenario.");
    }
    internal void RequireContext(CurveBuild authority, IReadOnlyDictionary<CurveKey,PreparedCurve> prepared)
    {
        if (authority is null || prepared is null || !ReferenceEquals(authority.Graph, scenario.Graph) ||
            !ReferenceEquals(authority.Model, scenario.Model) || !authority.Snapshot.HasSameInputs(scenario.Snapshot) || prepared.Count != curves.Count ||
            prepared.Any(p => !curves.TryGetValue(p.Key, out var retained) || !ReferenceEquals(p.Value, retained)))
            throw Invalid("Price admission must retain the exact scenario and immutable prepared curve inputs.");
    }
    public ImmutableArray<IntervalPriceResolution> Prices(CurveKey curve,DateOnly month)=>Prices(curve,month,null);
    public ImmutableArray<IntervalPriceResolution> Prices(CurveKey curve,DateOnly month,PriceSettlementSelection? selection)
    {
        if(!curves.TryGetValue(curve,out var prepared))throw new MarketDataException("risk_dependency","The curve was not declared for this request.");
        if(prepared.Curve is PeriodContractCurve periodCurve)selection??=PriceSettlementSelection.CurveTarget(periodCurve.SettlementIndex);
        else if(selection is not null)throw Invalid("Interval curves take no period settlement selection.");
        var key=(curve,month,selection);if(cache.TryGetValue(key,out var cached))return cached;
        var period=prepared.Period(month);
        var dependencies=prepared.Dependencies(month,selection);
        // Observed cells suppress market work, never the validation of their exact source definition.
        foreach(var source in period.Sources)scenario.Model.RequireIntervalPriceSource(source.Request,source.Contract,prepared.Unit);
        foreach(var dependency in dependencies)
        {
            if(!values.ContainsKey(dependency))throw new MarketDataException("risk_dependency","A declared price dependency is absent from the scenario.");
        }
        if(basis is not null && dependencies.All(d=>basis.values.TryGetValue(d,out var prior) && ReferenceEquals(prior,values[d])))
        {
            var reused=basis.Prices(curve,month,selection);cache.Add(key,reused);ReusedMonths++;return reused;
        }
        ImmutableArray<IntervalPriceResolution> result;ShapedCurveBuild? completedShape=null;
        if(period.Flat is {} flat)
        {
            var inputs=flat.Anchors.Where(anchor=>dependencies.Contains(anchor.Request)).ToDictionary(anchor=>anchor.Request,anchor=>
            {
                var mark=values[anchor.Request];
                if(scenario.Evaluations.Single(e=>e.Result.Request==anchor.Request).Use!=InputUse.Variable)
                    throw new MarketDataException("interval_flat_delivered","Flat forward-only construction cannot consume a frozen source transition.");
                return (Price:scenario.Pricing.Price(anchor.Request,default),Anchor:new IntervalAnchor(anchor.Request,InputUse.Variable,mark.Lineage));
            });
            var forward=flat.Intervals.Select((interval,i)=>(interval,owner:flat.Owners[i])).Where(p=>p.owner>=0)
                .Select(p=>(p.interval,anchor:flat.Anchors[p.owner].Request))
                .Where(p=>inputs.ContainsKey(p.anchor)).ToDictionary(p=>p.interval.Start,p=>new IntervalPrice(p.interval.Delivery,inputs[p.anchor].Price,inputs[p.anchor].Anchor.Lineage));
            if(period.Shape is {} shapedInputs)
            {
                var shaped=BlockShaping.Evaluate(shapedInputs,scenario,basis is not null&&basis.shapes.TryGetValue((curve,month),out var old)?old:null);
                completedShape=shaped;
                foreach(var price in shaped.Prices)forward[price.Start]=new(price.Interval,price.Price,price.Lineage);
            }
            result=prepared.Curve is FlatIntervalCurve {ActualRule:{} rule}
                ?IntervalPricing.Resolve(period.Intervals,prepared.Unit,forward,[..period.Actuals.Values],rule,prepared.ValuationAt,prepared.KnownAt)
                :[..flat.Intervals.Select(p=>new IntervalPriceResolution(p.Delivery,IntervalPriceState.Projected,forward[p.Start].Price,forward[p.Start].Lineage,null,null))];
            result=[..result.Select((p,i)=>p with{
                ConstructionSource=flat.Owners[i]<0?null:flat.Anchors[flat.Owners[i]].Request,
                Anchor=p.State==IntervalPriceState.Projected?inputs[flat.Anchors[flat.Owners[i]].Request].Anchor:null})];
        }
        else if(prepared.Curve is PeriodContractCurve pc)
        {
            result=[..period.Contracts.Select((contract,i)=>
            {
                var request=new CurveRequest(pc.Key,contract.Key);scenario.Model.RequireIntervalPriceSource(request,contract,pc.Unit);
                var fixing=PreparedCurve.Fixing(pc,contract,selection!);
                var lookup=scenario.Model.PeriodLookup(request,contract,selection!,fixing);
                var source=PeriodPriceSelector.Admit(scenario,lookup);
                var resolved=PeriodPriceSelector.Resolve(scenario,lookup,r=>scenario.Pricing.Price(r,default),r=>scenario.Pricing.Lineage(r,default));
                return new IntervalPriceResolution(period.Intervals[i],resolved.State==SchedulePriceState.Actual?IntervalPriceState.PublishedFixing:IntervalPriceState.Projected,
                    resolved.Price,source.Projection is null?[]:resolved.Lineage,null,fixing?.FixingAt??contract.Expiry)
                    {PeriodSelection=source,Publication=source.Projection is null?"published_fixing":"mark",Anchor=new(request,source.Projection is null?InputUse.FrozenFact:InputUse.Variable,resolved.Lineage),
                     LiveMarketRiskCoverage=source.Projection is null?"observed":selection!.Mode==PriceSettlementMode.Override?"curve_risk_unmodelled_basis":"live_anchor"};
            })];
        }
        else
        {
            var rule=prepared.Curve is MarkedIntervalCurve m?m.Rule:((ShapedIntervalCurve)prepared.Curve).Rule;
            var forward=new Dictionary<DateTimeOffset,(ExactUnitPrice Price,IntervalAnchor Anchor)>();
            if(period.Shape is {} inputs)
            {
                if(basis is not null)basis.Prices(curve,month);
                var shaped=BlockShaping.Evaluate(inputs,scenario,basis is not null && basis.shapes.TryGetValue((curve,month),out var old)?old:null);
                completedShape=shaped;
                var uses=scenario.Evaluations.ToDictionary(e=>e.Result.Request,e=>e.Use);
                foreach(var cell in shaped.Prices)forward.Add(cell.Start,(cell.Price,new(cell.Block,uses[cell.Block],cell.Lineage)));
            }
            else
                foreach(var contract in period.Contracts.Where(c=>period.Dependencies.Contains(new(curve,c.Key))))
                {var mark=values[new(curve,contract.Key)];forward.Add(contract.Interval!.Start,(ExactUnitPrice.FromPrice(mark.Price),new(mark.Request,InputUse.Variable,mark.Lineage)));}
            result=[..period.Intervals.Select(interval=>
            {
                var due=rule.DueAt(interval.Start,interval.End);
                if(period.Actuals.TryGetValue(interval.Start,out var actual))
                    return new IntervalPriceResolution(interval,IntervalPriceState.Actual,ExactUnitPrice.FromPrice(actual.Price),[],actual,due)
                        {Publication="collected",LiveMarketRiskCoverage="observed"};
                var has=forward.TryGetValue(interval.Start,out var cell);
                var frozen=has && cell.Anchor.Use==InputUse.FrozenFact;
                if(frozen)
                    prepared.RequireFrozenKnowledge(cell.Anchor.Lineage,interval);
                if(interval.End<=prepared.ValuationAt)
                    return new IntervalPriceResolution(interval,IntervalPriceState.Unresolved,null,has?cell.Anchor.Lineage:[],null,due)
                        {Estimate=has?cell.Price:null,Anchor=has?cell.Anchor:null,Reason=new(has?"awaiting_actual":"no_estimate",due),
                            Publication=has?(frozen?"final_settlement_anchor":"mark"):"unavailable",LiveMarketRiskCoverage=frozen?"frozen_anchor_only":"unavailable"};
                if(!has)throw new MarketDataException("interval_anchor_missing","A live interval requires a declared price anchor.");
                return new IntervalPriceResolution(interval,frozen?IntervalPriceState.Modeled:IntervalPriceState.Projected,cell.Price,cell.Anchor.Lineage,null,due)
                    {Anchor=cell.Anchor,Publication=frozen?"final_settlement_anchor":"mark",LiveMarketRiskCoverage=frozen?"frozen_anchor_only":"live_anchor"};
            })];
        }
        if(completedShape is {} completed)
        {shapes.Add((curve,month),completed);CalibratedBlocks+=completed.CalibratedBlocks;ReusedBlocks+=completed.ReusedBlocks;}
        cache.Add(key,result);Resolved++;return result;
    }
    private static MarketDataException Invalid(string message)=>new("interval_price_source",message);
}
