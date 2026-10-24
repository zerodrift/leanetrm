using System.Collections.Immutable;
using System.Collections.Concurrent;
using OpenEtrm.MarketData.CustomCurves;
using OpenEtrm.MarketData.Risk;
using OpenEtrm.MarketData.Trading;

namespace OpenEtrm.MarketData.Calculation;

// The caller supplies an already-authorized, pinned graph and lifecycle snapshot.
// Historical graph samples retain their own exact observation evidence in the
// plan; the current snapshot remains the instrument's fixing/lifecycle authority.
public sealed class ConnectedValuation
{
    public ConnectedScenario Scenario{get;}
    public MarketSnapshot Snapshot{get;}
    private ConnectedValuation(ConnectedScenario scenario,MarketSnapshot snapshot)
    {Scenario=scenario;Snapshot=snapshot;}

    // Validate the shared source closure once, outside the per-instrument loop.
    public static ConnectedValuation Prepare(ConnectedScenario scenario,MarketSnapshot snapshot,CancellationToken cancellation=default)
        =>Prepare(scenario,snapshot,[],cancellation);
    public static ConnectedValuation Prepare(ConnectedScenario scenario,MarketSnapshot snapshot,ImmutableArray<MarketSnapshot> sourceSnapshots,CancellationToken cancellation=default)
    {
        cancellation.ThrowIfCancellationRequested();
        if(scenario is null||snapshot is null||snapshot.Id==Guid.Empty||snapshot.Definitions.CatalogId==Guid.Empty||
            snapshot.Definitions.RevisionId==Guid.Empty||!TradingInstant.IsSupported(snapshot.ValuationAt)||!TradingInstant.IsSupported(snapshot.MarketCutoff)||
            !snapshot.HasCoherentSelections()||snapshot.SelectionPolicy!=InputSelectionPolicy.ExactPinnedFinalObservationsNoAgeFiltering||
            snapshot.ValuationAt!=scenario.Plan.Context.ValuationAt||snapshot.MarketCutoff!=scenario.Plan.Context.MarketAsOf)
            throw new MarketDataException("instrument_context","Supply one pinned graph scenario, compatible snapshot and exact instrument pricing bindings.");
        if(sourceSnapshots.IsDefault)throw new MarketDataException("instrument_context","Supply the exact admitted source snapshot list, including an explicit empty list when unused.");
        var admitted=new Dictionary<(Guid,DefinitionRevision),(MarketSnapshot Metadata,Dictionary<InputKey,MarketObservation> Inputs)>();
        var selectedInputs=new HashSet<(InputKey,MarketObservation)>();
        var currentInputs=snapshot.Selections.ToDictionary(selected=>selected.Input,selected=>selected.Observation);
        var observed=new Dictionary<ObservationKey,MarketObservation>();
        var visited=new HashSet<MarketSnapshot>(ReferenceEqualityComparer.Instance);
        foreach(var retained in sourceSnapshots.Prepend(snapshot))
        {
            cancellation.ThrowIfCancellationRequested();
            if(retained is not null&&!visited.Add(retained))continue;
            if(retained is null||retained.Id==Guid.Empty||retained.Definitions.CatalogId==Guid.Empty||retained.Definitions.RevisionId==Guid.Empty||
                !retained.HasCoherentSelections()||!TradingInstant.IsSupported(retained.ValuationAt)||!TradingInstant.IsSupported(retained.MarketCutoff)||
                retained.SelectionPolicy!=snapshot.SelectionPolicy||retained.MarketCutoff>retained.ValuationAt||retained.ValuationAt>snapshot.ValuationAt||retained.MarketCutoff>snapshot.MarketCutoff)
                throw new MarketDataException("instrument_context","Every admitted source snapshot requires coherent exact inputs and dates within the retained calculation context.");
            var key=(retained.Id,retained.Definitions);
            if(admitted.TryGetValue(key,out var existing))
            {
                if(existing.Metadata.ValuationAt!=retained.ValuationAt||existing.Metadata.MarketCutoff!=retained.MarketCutoff||existing.Metadata.SelectionPolicy!=retained.SelectionPolicy)
                    throw new MarketDataException("instrument_context","Partial selections of one exact frame must retain identical temporal coordinates.");
            }
            else admitted.Add(key,existing=(retained,new()));
            foreach(var selected in retained.Selections)
            {
                cancellation.ThrowIfCancellationRequested();
                if(existing.Inputs.TryGetValue(selected.Input,out var prior)&&prior!=selected.Observation||
                    observed.TryGetValue(selected.Observation.Key,out var priorObservation)&&priorObservation!=selected.Observation)
                    throw new MarketDataException("instrument_context","Partial selections of one exact frame cannot contain conflicting input or observation evidence.");
                existing.Inputs[selected.Input]=selected.Observation;observed[selected.Observation.Key]=selected.Observation;
                selectedInputs.Add((selected.Input,selected.Observation));
            }
        }
        void Require(InputKey input,MarketObservation observation,ConnectedSourceSample? sample)
        {
            cancellation.ThrowIfCancellationRequested();
            if(sample is not null)
            {
                if(!admitted.TryGetValue((sample.SnapshotId,input.Series.Revision),out var retained)||retained.Metadata.ValuationAt!=sample.ValuationAt||
                    retained.Metadata.MarketCutoff!=sample.MarketCutoff||retained.Metadata.SelectionPolicy!=sample.SelectionPolicy)
                    throw new MarketDataException("instrument_context","A graph sample requires its independently admitted exact snapshot and coordinates.");
                if(!retained.Inputs.TryGetValue(input,out var selected)||selected!=observation)
                    throw new MarketDataException("instrument_context","Every graph sample requires its exact admitted observation and original use.");
            }
            else if(input.Use==InputUse.Variable?(!currentInputs.TryGetValue(input,out var current)||current!=observation):!selectedInputs.Contains((input,observation)))
                throw new MarketDataException("instrument_context","Every graph source requires its independently admitted exact observation and original use.");
        }
        foreach(var point in scenario.Plan.Points.Values)
        {
            cancellation.ThrowIfCancellationRequested();
            if(point.Expression is ConnectedSource source)
                Require(source.Input with{Use=source.Sample?.OriginalUse??source.Input.Use},source.Observation,source.Sample);
            else if(point.Expression is ConnectedBuiltCurve {MultiplierInput:{} numeric} built)
                Require(numeric.Input,numeric.Observation,built.Sample);
        }
        return new(scenario,snapshot);
    }

    public ConnectedValuation WithScenario(ConnectedScenario scenario)
    {
        if(scenario is null||!ReferenceEquals(scenario.Plan,Scenario.Plan))
            throw new MarketDataException("instrument_context","Scenario reuse requires the exact prepared plan and retained context.");
        return new(scenario,Snapshot);
    }

    internal PreparedValuationMarket Market(ImmutableDictionary<CurveRequest,CurvePointKey> bindings,CurveBuild? authority,CancellationToken cancellation,CurveBuild? nativeScenario=null)
        =>new ConnectedMarket(this,bindings,authority,cancellation,nativeScenario);

    internal static PreparedValuationMarket WithScenario(PreparedValuationMarket market,ConnectedValuation valuation,CurveBuild? nativeScenario=null)
        =>market is ConnectedMarket connected?connected.WithScenario(valuation,nativeScenario):
            throw new MarketDataException("instrument_context","A connected child requires its prepared pricing bindings.");

    private sealed class ConnectedMarket : PreparedValuationMarket
    {
        private readonly ConnectedValuation valuation;
        private readonly ImmutableDictionary<CurveRequest,CurvePointKey> bindings;
        private readonly CurveBuild? authority;
        private readonly CurveBuild? nativeScenario;
        private readonly ImmutableDictionary<int,CurvePointKey> primitivePoints;
        private readonly ConnectedInputEvidence evidence;
        private readonly ConcurrentDictionary<int,QuoteFactor> factors=new();
        internal ConnectedMarket(ConnectedValuation valuation,ImmutableDictionary<CurveRequest,CurvePointKey> bindings,CurveBuild? authority,CancellationToken cancellation,CurveBuild? nativeScenario)
        {
            if(bindings is null||authority is not null&&!authority.Snapshot.HasSameInputs(valuation.Snapshot))
                throw new MarketDataException("instrument_context","Connected pricing and lifecycle authority require the same exact pinned snapshot.");
            if(nativeScenario is not null && !ReferenceEquals(nativeScenario.Graph,valuation.Scenario))
                throw new MarketDataException("ppa_context","Native interval inputs must name this exact scenario.");
            this.nativeScenario=nativeScenario;
            this.valuation=valuation;this.bindings=bindings;this.authority=authority;
            evidence=new(valuation.Scenario.Plan);
            var primitives=valuation.Scenario.Plan.Primitives.Select(p=>p.Node).ToHashSet();
            primitivePoints=valuation.Scenario.Plan.Points.Keys.Select(point=>(Node:valuation.Scenario.Plan.IndexOf(point),Point:point))
                .Where(p=>primitives.Contains(p.Node)).DistinctBy(p=>p.Node).ToImmutableDictionary(p=>p.Node,p=>p.Point);
            foreach(var point in bindings.Values){cancellation.ThrowIfCancellationRequested();valuation.Scenario.Plan.GetPoint(point);}
        }
        private ConnectedMarket(ConnectedMarket parent,ConnectedValuation valuation,CurveBuild? nativeScenario)
        {this.nativeScenario=nativeScenario;this.valuation=valuation;bindings=parent.bindings;authority=parent.authority;primitivePoints=parent.primitivePoints;evidence=parent.evidence;}
        internal ConnectedMarket WithScenario(ConnectedValuation child,CurveBuild? nativeScenario)
        {
            if(!ReferenceEquals(child.Scenario.Plan,valuation.Scenario.Plan)||!child.Snapshot.HasSameInputs(Snapshot))
                throw new MarketDataException("instrument_context","Connected reuse requires one exact plan and lifecycle context.");
            if(nativeScenario is not null && !ReferenceEquals(nativeScenario.Graph,child.Scenario))
                throw new MarketDataException("ppa_context","Native interval inputs must name this exact child scenario.");
            return new(this,child,nativeScenario);
        }
        public override MarketSnapshot Snapshot=>valuation.Snapshot;
        public override string ScenarioId=>valuation.Scenario.Id;
        public override string NumericalPolicy=>ConnectedCurves.NumericalPolicy;
        internal override object Identity=>this;
        internal override CurveBuild? Authority=>authority;
        internal override CurveBuild? NativeIntervalScenario=>nativeScenario;
        private CurvePointKey Point(CurveRequest request)=>bindings.TryGetValue(request,out var point)?point:
            throw new MarketDataException("risk_dependency","The exact connected pricing binding is unavailable.");
        internal override ExactUnitPrice Price(CurveRequest request,CancellationToken cancellation)=>valuation.Scenario.Price(Point(request),cancellation);
        private ConnectedInputClosure Sources(ImmutableArray<CurveRequest> requests,CancellationToken cancellation)
            =>evidence.Resolve(requests.Select(Point).ToImmutableArray(),cancellation);
        internal override ImmutableHashSet<ObservationKey> Lineage(CurveRequest request,CancellationToken cancellation)
            =>Sources([request],cancellation).Lineage;
        internal override ImmutableArray<QuoteFactor> Factors(ImmutableArray<CurveRequest> requests,CancellationToken cancellation)=>
            Sources(requests,cancellation).Primitives.Where(p=>p.Source.Input.Use==InputUse.Variable).Select(p=>factors.GetOrAdd(p.Node,_=>
            {
                var source=p.Source;var price=valuation.Scenario.Price(primitivePoints[p.Node],cancellation).ToUnitPriceExact();
                return new(source.Input,source.Quote.Key,p.Unit,source.Request.Contract,source.Observation.Key,source.Observation.Source,
                    source.Observation.Role,price.Value,[source.Request.Curve],source.Quote.TradingTick);
            })).OrderBy(f=>f.Input.Series.Id).ToImmutableArray();
        internal override bool Compatible(PreparedValuationMarket prior)=>prior is ConnectedMarket connected&&
            ReferenceEquals(bindings,connected.bindings)&&ReferenceEquals(valuation.Scenario.Plan,connected.valuation.Scenario.Plan)&&
            ReferenceEquals(authority,connected.authority)&&Snapshot.HasSameInputs(connected.Snapshot);
        internal override bool Unchanged(PreparedValuationMarket prior,ImmutableArray<CurveRequest> requests,CancellationToken cancellation)
        {
            if(prior is not ConnectedMarket connected)return false;
            foreach(var request in requests)if(!ReferenceEquals(Price(request,cancellation),connected.Price(request,cancellation)))return false;
            return true;
        }
        internal override SideValuationContext Context(ISideDiscounting? discounting,ImmutableArray<CurveRequest> dependencies,CancellationToken cancellation)=>
            authority is null?new(Snapshot,discounting,dependencies,request=>Price(request,cancellation),NumericalPolicy,ScenarioId):
                new(authority,discounting,dependencies,request=>Price(request,cancellation),request=>Lineage(request,cancellation),NumericalPolicy,ScenarioId);
    }

    public SideValuationContext PaymentContext(ImmutableDictionary<CurveRequest,CurvePointKey> bindings,CurveBuild authority,
        ISideDiscounting discounting,CancellationToken cancellation=default) =>
        Market(bindings,authority,cancellation).Context(discounting,bindings.Keys.ToImmutableArray(),cancellation);

    public InstrumentValuation PV(Instrument instrument,ImmutableDictionary<CurveRequest,CurvePointKey> bindings,
        ISideDiscounting? discounting=null,CancellationToken cancellation=default)
        =>PV(instrument,bindings,null,discounting,cancellation);
    public InstrumentValuation PV(Instrument instrument,ImmutableDictionary<CurveRequest,CurvePointKey> bindings,
        CurveBuild? authority,ISideDiscounting? discounting,CancellationToken cancellation=default)
    {
        cancellation.ThrowIfCancellationRequested();
        if(instrument is null||bindings is null)
            throw new MarketDataException("instrument_context","Supply an exact instrument and its declared connected pricing bindings.");
        var scenario=Scenario;var snapshot=Snapshot;
        var setup=new ValuationSetup(ValuationMeasure.PresentValue,ImmutableDictionary<Guid,FuturesMarketBinding>.Empty,discounting);
        var declaration=instrument.Declare(new UnitValuationContext(snapshot,setup));
        if(declaration is null||declaration.Id!=instrument.Id||string.IsNullOrWhiteSpace(declaration.ModelVersion)||
            declaration.Currency!=instrument.Currency||declaration.Quantity!=instrument.UnitQuantity.Specification||
            instrument.UnitQuantity.Amount!=ExactDecimal.Parse("1")||declaration.Dependencies.IsDefault||
            declaration.Dependencies.Distinct().Count()!=declaration.Dependencies.Length)
            throw new MarketDataException("risk_contract","The instrument must declare its exact inputs, model, currency and normalized unit basis.");
        if(!declaration.Dependencies.ToHashSet().SetEquals(bindings.Keys))
            throw new MarketDataException("risk_dependency","Connected pricing bindings must match the instrument's complete declared input set.");
        foreach(var point in bindings.Values)
        {cancellation.ThrowIfCancellationRequested();scenario.Plan.GetPoint(point);}
        if(discounting is not null)SideValuationContext.Validate(snapshot,discounting);
        var context=Market(bindings,authority,cancellation).Context(discounting,declaration.Dependencies,cancellation);
        var value=instrument.PV(context);
        cancellation.ThrowIfCancellationRequested();
        if(value is null||value.InstrumentId!=instrument.Id||value.Currency!=instrument.Currency||
            value.UnitQuantity!=instrument.UnitQuantity||!ReferenceEquals(value.Context,context))
            throw new MarketDataException("instrument_context","The returned PV must belong to this instrument and its active connected scenario.");
        return value;
    }
}
