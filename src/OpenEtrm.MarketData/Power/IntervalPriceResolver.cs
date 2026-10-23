using System.Collections.Immutable;
using OpenEtrm.MarketData.CustomCurves;
using OpenEtrm.MarketData.Trading;

namespace OpenEtrm.MarketData.Power;

public abstract record PriceCurve(CurveKey Key,PriceUnit Unit);
public sealed record MarkedIntervalCurve(CurveKey Key,PriceUnit Unit,IntervalActualRule Rule,PowerClock Clock,int IntervalMinutes,
    ImmutableArray<MarketContract> Contracts):PriceCurve(Key,Unit);
public sealed record ShapedIntervalCurve(CurveKey Key,PriceUnit Unit,IntervalActualRule Rule,ShapedCurveDefinition Definition):PriceCurve(Key,Unit)
{
    // Exact frame-selected contract identities. Required for overlapping balance definitions.
    public ImmutableHashSet<ContractKey>? SelectedAnchors { get; init; }
    // Certified publication knowledge, supplied by the authorized reader for exact pinned observations.
    public ImmutableDictionary<ObservationKey,DateTimeOffset> FrozenKnowledge { get; init; } = ImmutableDictionary<ObservationKey,DateTimeOffset>.Empty;
}
public sealed record PeriodContractCurve(CurveKey Key,PriceUnit Unit,ImmutableArray<MarketContract> Contracts,
    ImmutableArray<IndexFixing> Fixings):PriceCurve(Key,Unit);

internal sealed record PreparedPriceMonth(ImmutableArray<DeliveryInterval> Intervals,
    ImmutableDictionary<DateTimeOffset,IntervalActual> Actuals,ImmutableArray<CurveRequest> Dependencies,
    ShapedInputs? Shape,ImmutableArray<MarketContract> Contracts,
    ImmutableArray<(CurveRequest Request,MarketContract Contract)> Sources);

// Invariant inputs are copied and compiled once per authorized request. Object identity is deliberately
// the reuse boundary: equal curve codes in another request do not establish equal shape/actual inputs.
public sealed class PreparedCurve
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
    public ImmutableArray<CurveRequest> Dependencies(DateOnly month)=>Period(month).Dependencies;
    internal PreparedPriceMonth Period(DateOnly month)=>Periods.TryGetValue(month,out var period)?period:
        throw new MarketDataException("interval_price_source","The requested month is outside the prepared coverage.");

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
        if(curve is PeriodContractCurve pc)
        {
            if(pc.Contracts.IsDefaultOrEmpty || pc.Fixings.IsDefault || shape.Count!=0 || actuals.Count!=0 ||
                pc.Contracts.Any(c=>c.Interval is not null) || pc.Fixings.GroupBy(f=>(f.From,f.To)).Any(g=>g.Count()!=1))
                throw Invalid("Period curves require distinct date-only contracts and explicit qualified fixing schedules.");
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
                var dependencies=contracts.Where(c=>pc.Fixings.SingleOrDefault(f=>f.From==c.DeliveryFrom && f.To==c.DeliveryTo) is not {} fixing || valuationAt<fixing.FixingAt)
                    .Select(c=>new CurveRequest(pc.Key,c.Key)).ToImmutableArray();
                if(contracts.Any(c=>dependencies.Any(d=>d.Contract==c.Key) && c.Expiry<=valuationAt))
                    throw new MarketDataException("source_expired","A period without a due qualified fixing requires its live mark.");
                periods.Add(group.Key,new([..contracts.Select(c=>new DeliveryInterval(Utc(c.DeliveryFrom),Utc(c.DeliveryTo),c.DeliveryFrom,0,0,1))],
                    ImmutableDictionary<DateTimeOffset,IntervalActual>.Empty,dependencies,null,contracts,[..contracts.Select(c=>(new CurveRequest(pc.Key,c.Key),c))]));
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
        var shapeIndex=curve is ShapedIntervalCurve?IntervalAlignment.ByStart(shape,c=>c.Start,c=>c.End,
            "interval_shape_coverage","Shape intervals must have distinct exact UTC boundaries."):null;
        foreach(var (month,ranges) in byMonth.OrderBy(p=>p.Key))
        {
            var all=PowerHours.Compile(month,month.AddMonths(1),clock,TimeOfUse.All(minutes),[],minutes)
                .Where(p=>ranges.Any(r=>p.Start>=r.From && p.End<=r.To)).ToArray();
            ImmutableArray<DeliveryInterval> intervals=[..all.Select(p=>p.Delivery)];
            var relevant=actuals.Where(a=>ranges.Any(r=>a.Start<r.To && a.End>r.From)).ToArray();
            var actualMap=IntervalPricing.ValidateInputs(intervals,curve.Unit,relevant,rule,knownAt);
            var eligible=actualMap.Where(p=>intervals.Any(i=>i.Start==p.Key && IntervalPricing.Eligible(p.Value,i,valuationAt))).ToImmutableDictionary();
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
                    if(covering.Count(b=>!b.Balance)!=1)throw new MarketDataException("interval_block_partition","Period blocks must partition the requested grid exactly once.");
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
                var selectedRequests=dependencies.ToHashSet();
                var selectedCells=blocks.Where(b=>selectedRequests.Contains(new(b.Block.Curve,b.Contract.Key))).SelectMany(b=>b.Periods)
                    .DistinctBy(p=>p.Start).Select(p=>shapeIndex!.TryGetValue(p.Start,out var cell)?cell:
                        throw new MarketDataException("interval_shape_coverage","Every selected anchor needs its complete shape period.")).ToArray();
                preparedShape=BlockShaping.PrepareSelected(definition,selectedCells,intervals[0].Start,intervals[^1].End,owners.ToImmutable());
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
    private readonly Dictionary<(CurveKey,DateOnly),ImmutableArray<IntervalPriceResolution>> cache=[];
    private readonly Dictionary<(CurveKey,DateOnly),ShapedCurveBuild> shapes=[];
    public int Resolved { get; private set; }
    public int ReusedMonths { get; private set; }
    public int CalibratedBlocks { get; private set; }
    public int ReusedBlocks { get; private set; }
    public IntervalPriceResolver(CurveBuild scenario,IReadOnlyDictionary<CurveKey,PreparedCurve> curves,IntervalPriceResolver? @base=null)
    {
        ArgumentNullException.ThrowIfNull(scenario);ArgumentNullException.ThrowIfNull(curves);
        this.scenario=scenario;this.curves=curves.ToImmutableDictionary();basis=@base;values=scenario.Values.ToDictionary(v=>v.Request);
        if(curves.Count==0 || curves.Any(p=>p.Key!=p.Value.Key || p.Key.Revision!=scenario.Definitions || p.Value.ValuationAt!=scenario.Snapshot.ValuationAt) ||
            curves.Values.Select(c=>c.KnownAt).Distinct().Count()!=1 || scenario.NumericalPolicy!=CurveModel.NumericalPolicy)
            throw Invalid("Every prepared curve must share the exact scenario, valuation and knowledge context.");
        if(@base is not null && (!ReferenceEquals(scenario.Model,@base.scenario.Model) || !scenario.Snapshot.HasSameInputs(@base.scenario.Snapshot) ||
            @base.scenario.Shock is not null || curves.Count!=@base.curves.Count ||
            curves.Any(p=>!@base.curves.TryGetValue(p.Key,out var old) || !ReferenceEquals(p.Value,old))))
            throw Invalid("Reuse requires the same prepared request, immutable snapshot and unshocked base scenario.");
    }
    public ImmutableArray<IntervalPriceResolution> Prices(CurveKey curve,DateOnly month)
    {
        var key=(curve,month);if(cache.TryGetValue(key,out var cached))return cached;
        if(!curves.TryGetValue(curve,out var prepared))throw new MarketDataException("risk_dependency","The curve was not declared for this request.");
        var period=prepared.Period(month);
        // Observed cells suppress market work, never the validation of their exact source definition.
        foreach(var source in period.Sources)scenario.Model.RequireIntervalPriceSource(source.Request,source.Contract,prepared.Unit);
        foreach(var dependency in period.Dependencies)
        {
            if(!values.ContainsKey(dependency))throw new MarketDataException("risk_dependency","A declared price dependency is absent from the scenario.");
        }
        if(basis is not null && period.Dependencies.All(d=>basis.values.TryGetValue(d,out var prior) && ReferenceEquals(prior,values[d])))
        {
            var reused=basis.Prices(curve,month);cache.Add(key,reused);ReusedMonths++;return reused;
        }
        ImmutableArray<IntervalPriceResolution> result;ShapedCurveBuild? completedShape=null;
        if(prepared.Curve is PeriodContractCurve pc)
        {
            result=[..period.Contracts.Select((contract,i)=>
            {
                var request=new CurveRequest(pc.Key,contract.Key);scenario.Model.RequireIntervalPriceSource(request,contract,pc.Unit);
                var fixing=pc.Fixings.SingleOrDefault(f=>f.From==contract.DeliveryFrom && f.To==contract.DeliveryTo);
                if(fixing is not null && IndexFixingPricing.Resolve(scenario,scenario.Model.PeriodLookup(request,contract,fixing)) is {} fixedPrice)
                    return new IntervalPriceResolution(period.Intervals[i],IntervalPriceState.PublishedFixing,ExactUnitPrice.FromPrice(fixedPrice.Price),[],null,fixing.FixingAt)
                        {Publication="published_fixing",Anchor=new(request,InputUse.FrozenFact,fixedPrice.Lineage),LiveMarketRiskCoverage="observed"};
                var mark=values[request];
                return new IntervalPriceResolution(period.Intervals[i],IntervalPriceState.Projected,ExactUnitPrice.FromPrice(mark.Price),mark.Lineage,null,fixing?.FixingAt??contract.Expiry)
                    {Anchor=new(request,InputUse.Variable,mark.Lineage)};
            })];
        }
        else
        {
            var rule=prepared.Curve is MarkedIntervalCurve m?m.Rule:((ShapedIntervalCurve)prepared.Curve).Rule;
            var forward=new Dictionary<DateTimeOffset,(ExactUnitPrice Price,IntervalAnchor Anchor)>();
            if(period.Shape is {} inputs)
            {
                if(basis is not null)basis.Prices(curve,month);
                var shaped=BlockShaping.Evaluate(inputs,scenario,basis is not null && basis.shapes.TryGetValue(key,out var old)?old:null);
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
                {
                    var knowledge=((ShapedIntervalCurve)prepared.Curve).FrozenKnowledge;
                    var cutoff=interval.End>prepared.ValuationAt && prepared.ValuationAt<prepared.KnownAt?prepared.ValuationAt:prepared.KnownAt;
                    if(cell.Anchor.Lineage.Count==0 || cell.Anchor.Lineage.Any(id=>!knowledge.TryGetValue(id,out var learned) ||
                        learned.Offset!=TimeSpan.Zero || learned>cutoff))
                        throw new MarketDataException("frozen_fact","The exact frozen anchor needs certified knowledge eligible at the request cutoff and, for live intervals, at valuation.");
                }
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
        {shapes.Add(key,completed);CalibratedBlocks+=completed.CalibratedBlocks;ReusedBlocks+=completed.ReusedBlocks;}
        cache.Add(key,result);Resolved++;return result;
    }
    private static MarketDataException Invalid(string message)=>new("interval_price_source",message);
}
