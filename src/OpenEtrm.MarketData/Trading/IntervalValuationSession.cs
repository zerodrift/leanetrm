using System.Collections.Immutable;
using OpenEtrm.MarketData.CustomCurves;
using OpenEtrm.MarketData.Power;

namespace OpenEtrm.MarketData.Trading;

// One scenario evaluates immutable schedule graph roots; native rows are materialized only for selected detail.
public sealed class IntervalValuationSession
{
    public IntervalValuationInputs Inputs { get; }
    public IntervalPriceResolver Resolver { get; }
    public CurveBuild Scenario { get; }
    public int UniqueSideCalculations { get; private set; }
    public long RowEvaluations { get; private set; }
    public int UnitValuations { get; private set; }
    public int ReusedUnitValues { get; private set; }
    private sealed record CachedValue(ImmutableArray<SidePaymentFactor> Factors,InstrumentValuation Value,PpaScheduleGraph Graph,ScheduleGraphSession Values);
    private readonly Dictionary<(Guid,DeliveryCoverage),List<CachedValue>> cache=[];
    public long GraphEvaluatedNodes { get; private set; }
    public long GraphReusedReads { get; private set; }
    private readonly IntervalValuationSession? basis;
    internal IntervalValuationSession(IntervalValuationInputs inputs,CurveBuild scenario,IntervalValuationSession? basis)
    {
        if(inputs is null || scenario is null || !scenario.Snapshot.HasSameInputs(inputs.Snapshot) || !ReferenceEquals(scenario.Model,inputs.BaseMarket.Model) ||
            basis is not null && (!ReferenceEquals(basis.Inputs,inputs) || basis.Scenario.Shock is not null))
            throw PpaErrors.Error("ppa_context","scenario","Reuse requires the same prepared request and immutable base inputs.");
        Inputs=inputs;Scenario=scenario;this.basis=basis;Resolver=new(scenario,inputs.Read.Curves,basis?.Resolver);
    }
    public InstrumentValuation Value(PpaInstrument instrument,DeliveryCoverage coverage,ISideDiscounting discounting,CancellationToken cancellation=default)
    {
        cancellation.ThrowIfCancellationRequested();Inputs.ValidateCoverage(instrument,coverage);
        if(discounting is null)throw PpaErrors.Error("ppa_context","discounting","Select explicit discount inputs.");
        var scope=Inputs.Scope(instrument,coverage,cancellation);var work=scope.Rows;var detail=scope.DetailRows;
        RequireWork(work,detail);
        var key=(instrument.Id,coverage);
        if(!cache.ContainsKey(key))RequireWork(RowEvaluations+work,detail);
        var keys=scope.FutureKeys;
        var resolved=keys.IsEmpty?ImmutableArray<SidePaymentFactor>.Empty:discounting.Resolve(keys,Scenario.Snapshot);
        ValidateFactors(instrument,keys,resolved);
        var byKey=resolved.ToImmutableDictionary(f=>f.Payment);
        var factors=keys.Select(k=>byKey[k]).ToImmutableArray();
        if(basis is not null && basis.cache.TryGetValue(key,out var baseValues) && !baseValues.Any(v=>SameFactors(v.Factors,factors)))
            throw PpaErrors.Error("ppa_context","discounting","Commodity scenarios must retain the base calculation's resolved discount factors.");
        if(cache.TryGetValue(key,out var prior))
            foreach(var entry in prior)if(SameFactors(entry.Factors,factors))
            {ReusedUnitValues++;return entry.Value;}
        // Cache admission needs the resolved immutable factor identity. Only a miss consumes rows.
        RequireWork(RowEvaluations+work,detail);
        PpaScheduleGraph graph;ScheduleGraphSession values;
        var retained=basis is not null&&basis.cache.TryGetValue(key,out var baseEntries)?baseEntries.FirstOrDefault(entry=>SameFactors(entry.Factors,factors)):null;
        if(retained is not null){graph=retained.Graph;values=graph.SessionFor(Scenario,retained.Values,cancellation);}
        else{graph=PpaScheduleGraph.Compile(Inputs,instrument,coverage,Scenario,factors,Resolver,cancellation);values=graph.SessionFor(Scenario,cancellation:cancellation);}
        var graphBefore=retained is not null&&ReferenceEquals(values,retained.Values)?values.Work:default;
        var context=new SideValuationContext(Scenario,new ResolvedDiscounting(byKey)){Interval=new(this,instrument,coverage,graph,values,factors,cancellation)};
        // The common SidedInstrument path owns delegation, signs and the exact sum.
        var result=instrument.PV(context);
        if(!cache.TryGetValue(key,out prior))cache[key]=prior=[];
        prior.Add(new(factors,result,graph,values));UnitValuations++;
        GraphEvaluatedNodes+=values.Work.EvaluatedNodes-graphBefore.EvaluatedNodes;GraphReusedReads+=values.Work.ReusedReads-graphBefore.ReusedReads;return result;
    }
    private static bool SameFactors(ImmutableArray<SidePaymentFactor> left,ImmutableArray<SidePaymentFactor> right)=>
        left.Length==right.Length && left.Zip(right).All(p=>p.First.Payment==p.Second.Payment &&
            p.First.DiscountFactor==p.Second.DiscountFactor && p.First.Source==p.Second.Source);
    public static void RequireWork(long rowEvaluations,long returnedRows)
    {
        if(rowEvaluations is <0 or >2_000_000 || returnedRows is <0 or >35_136)
            throw PpaErrors.Error("ppa_size","work","Interactive valuations admit at most two million row evaluations and 35,136 detailed rows; select period summaries for larger detail requests.");
    }
    private void ValidateFactors(PpaInstrument instrument,ImmutableArray<SidePaymentKey> keys,ImmutableArray<SidePaymentFactor> factors)
    {
        var expected=keys.ToHashSet();
        if(factors.IsDefault || factors.Length!=keys.Length || factors.Any(f=>f is null || !expected.Contains(f.Payment) ||
            f.DiscountFactor.CompareTo(default)<=0 || !Enum.IsDefined(f.Source) || (!ReferenceEquals(Scenario.Snapshot,f.Snapshot)&&!Scenario.Snapshot.HasSameInputs(f.Snapshot)) ||
            instrument.Discounting==(f.Source==DiscountFactorSource.Undiscounted) || f.Source==DiscountFactorSource.Undiscounted && f.DiscountFactor!=PpaErrors.One) ||
            factors.Select(f=>f.Payment).Distinct().Count()!=factors.Length)
            throw PpaErrors.Error("side_payment_factors","discounting","Each future value date requires its exact selected factor and context.");
    }
    private sealed class ResolvedDiscounting(ImmutableDictionary<SidePaymentKey,SidePaymentFactor> factors):ISideDiscounting
    {
        public ImmutableArray<SidePaymentFactor> Resolve(ImmutableArray<SidePaymentKey> keys,MarketSnapshot snapshot)=>
            [..keys.Select(k=>factors.TryGetValue(k,out var factor)?factor:throw PpaErrors.Error("side_payment_factors","discounting","A prepared value factor is missing."))];
    }
    internal IntervalSideValuation Evaluate(Side side,PpaInstrument instrument,DeliveryCoverage coverage,SideValuationContext context)
    {
        var slot=instrument.Memberships.SingleOrDefault(m=>ReferenceEquals(m.Side,side))?.Slot
            ??throw PpaErrors.Error("ppa_context","side","This side is not a member of the selected instrument.");
        var scope=context.Interval;
        if(scope is null||!ReferenceEquals(scope.Session,this)||!ReferenceEquals(scope.Instrument,instrument)||scope.Coverage!=coverage||
            !scope.Graph.Sides.TryGetValue(side.Id,out var linked))
            throw PpaErrors.Error("ppa_context","schedule_graph","The side requires this exact linked graph and instrument scope.");
        var before=scope.Values.Work;var total=new MeasureAccumulator(instrument.Currency);
        foreach(var group in linked.Groups)
        {
            scope.Cancellation.ThrowIfCancellationRequested();
            total.AddGroup(new(group.Key,group.Rows,scope.Values.Quantity(group.Quantity,scope.Cancellation),scope.Values.Cash(group.Amount,scope.Cancellation),
                group.PresentValue is null?null:scope.Values.Cash(group.PresentValue,scope.Cancellation)),group.Modeled);
        }
        total.AddDetails(linked.MaterializeRows(scope.Values,Inputs.DetailMonths,scope.Cancellation));
        var after=scope.Values.Work;
        RowEvaluations+=after.EvaluatedWork-before.EvaluatedWork;
        if(after.EvaluatedNodes!=before.EvaluatedNodes)UniqueSideCalculations++;
        var factors=scope.Factors.Where(f=>f.Payment.SideId==side.Id).ToImmutableArray();
        return new(Inputs.Schedule(instrument,side),context,total.Finish(),
            new(Inputs.Receipt,instrument.EconomicIdentity,side.Id,slot,coverage,Scenario.Shock,"schedule_graph_interval_leg_pv_v1","exact_interval_leg_pv_v1",factors)
            {ScheduleGraphManifest=scope.Graph.Plan.ManifestId,ScheduleScenario=scope.Values.Id,RetainedScheduleManifest=side.RetainedSchedule?.Manifest});
    }
}
