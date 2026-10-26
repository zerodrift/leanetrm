using OpenEtrm.Shared.Calculation;
using System.Collections.Immutable;
using OpenEtrm.MarketData.Calculation;
using OpenEtrm.MarketData.Trading;

namespace OpenEtrm.MarketData.Risk;

public sealed record ConnectedRiskBump(CurveShockCoordinate Coordinate,CurveShockTarget Target,PriceUnit Unit,ExactDecimal Size,string ConventionId)
{
    public const string Convention="absolute_curve_coordinate_symmetric_v1";
}
public sealed record ConnectedRiskWork(int PositionCount,int UniqueValuations,int UniqueMarketContracts,int ScenarioCount,int UnitPricerCalls,
    int QuantityApplications,int PreparationScenarioCount)
{
    public long UnitScenarioCandidates=>(long)UniqueValuations*ScenarioCount;
    public long ReusedUnitScenarioValues=>UnitScenarioCandidates-UnitPricerCalls;
}
public sealed record ConnectedScenarioWork(GraphWork Graph,int UnitPricerCalls,int ReusedUnitValues);
// Completed factors retain replay evidence, not executable sessions or their
// per-scenario caches. The prepared parent owns the shared calculation graph.
public sealed record ConnectedScenarioReceipt(string Id,string? ParentId,int Depth,string ManifestId,
    ImmutableArray<ResolvedCurveShift> Shifts);
public sealed record ConnectedBookRiskFactor(ConnectedRiskBump Bump,ImmutableArray<ExactRiskBookTotal> Totals,
    ConnectedScenarioReceipt UpScenario,ConnectedScenarioReceipt DownScenario,ConnectedScenarioWork UpWork,ConnectedScenarioWork DownWork)
{
    public ImmutableArray<ExactRiskPositionResult> Positions{get;init;}=[];
}
public sealed record ConnectedBookRiskRun(Guid SnapshotId,string NumericalPolicy,ImmutableArray<UnitPricingContract> Contracts,
    ImmutableArray<BookCurrencyValue> BaseValues,ImmutableArray<ConnectedBookRiskFactor> Factors,ConnectedRiskWork Work,ConnectedScenario Scenario)
{
    public ImmutableArray<BookValueMeasures> BaseMeasures{get;init;}=[];
    public RiskDetail Detail{get;init;}=RiskDetail.Books;
    public ImmutableArray<ExactRiskPositionValue> BasePositions{get;init;}=[];
}
public sealed record ConnectedGroupedRiskRun(Guid SnapshotId,ImmutableArray<ConnectedBookRiskRun> Groups,ConnectedRiskWork Work,GraphWork GraphWork);
public sealed class PreparedConnectedPortfolioRisk
{
    public ConnectedValuation Valuation{get;}
    public ConnectedScenario Scenario=>Valuation.Scenario;
    private readonly ImmutableArray<QuoteFactor> baseFactors;
    private readonly Lazy<ImmutableArray<QuoteFactor>> factors;
    public ImmutableArray<QuoteFactor> Factors=>factors.Value;
    internal PreparedValuationMarket Market{get;}
    internal CurveBuild? IntervalBasis{get;}
    internal ImmutableArray<ValuationPortfolio> Portfolios{get;}
    internal ImmutableArray<ImmutableArray<UnitPricingContract>> Contracts{get;}
    internal PreparedConnectedPortfolioRisk? Parent{get;}
    internal ImmutableDictionary<CurveRequest,CurvePointKey> Bindings{get;}
    internal PreparedConnectedPortfolioRisk(ConnectedValuation valuation,PreparedValuationMarket market,ImmutableArray<ValuationPortfolio> portfolios,
        ImmutableArray<ImmutableArray<UnitPricingContract>> contracts,ImmutableArray<QuoteFactor> factors,ImmutableDictionary<CurveRequest,CurvePointKey> bindings,
        PreparedConnectedPortfolioRisk? parent=null,CurveBuild? intervalBasis=null)
    {
        Valuation=valuation;Market=market;IntervalBasis=intervalBasis;Portfolios=portfolios;Contracts=contracts;Parent=parent;Bindings=bindings;
        baseFactors=parent?.baseFactors??factors;
        this.factors=new(parent is null?()=>baseFactors:ResolveFactors);
    }
    /// <summary>
    /// The graph points the held instruments are priced from: the binding of each dependency they declare, each point once, in the
    /// order of their declarations.
    /// </summary>
    public ImmutableArray<CurvePointKey> DeclaredPoints=>Contracts.SelectMany(group=>group).SelectMany(contract=>contract.Dependencies)
        .Select(dependency=>Bindings.TryGetValue(dependency,out var point)?point:
            throw new MarketDataException("risk_dependency","Every declared pricing dependency requires its exact connected binding."))
        .Distinct().ToImmutableArray();
    private ImmutableArray<QuoteFactor> ResolveFactors()
    {
        // Declarations belong to the immutable plan. Only quote-coordinate
        // offsets change their displayed base value; output interventions do not.
        var offsets=new Dictionary<InputKey,ExactDecimal>();
        for(var current=this;current.Parent is not null;current=current.Parent)
            foreach(var shift in current.Scenario.Shifts)
                if(shift.Coordinate==CurveShockCoordinate.MarketQuote&&shift.Input is{} input)
                    offsets[input]=offsets.GetValueOrDefault(input)+shift.Size;
        if(offsets.Count==0)return baseFactors;
        return baseFactors.Select(factor=>offsets.TryGetValue(factor.Input,out var offset)?
            factor with{BaseValue=factor.BaseValue+offset}:factor).ToImmutableArray();
    }
}

public static partial class RiskEngine
{
    public static PreparedConnectedPortfolioRisk PrepareConnected(ConnectedValuation valuation,ImmutableDictionary<CurveRequest,CurvePointKey> bindings,
        ImmutableArray<ValuationPortfolio> portfolios,CurveBuild? authority=null,CancellationToken cancellation=default)
    {
        cancellation.ThrowIfCancellationRequested();
        if(valuation is null)throw Error("instrument_context","Supply the prepared connected valuation and exact pricing bindings.");
        var contracts=DeclarePortfolios(portfolios,valuation.Snapshot);
        var intervalBases=portfolios.Where(p=>p.Setup.Interval is not null).Select(p=>p.Setup.Interval!.BaseMarket).Distinct().ToArray();
        if(intervalBases.Length>1)throw Error("ppa_context","One market partition requires one shared prepared interval market.");
        var intervalBasis=intervalBases.SingleOrDefault();
        var market=valuation.Market(bindings,authority,cancellation,intervalBasis?.BindConnected(valuation,bindings,cancellation));
        var factors=portfolios.SelectMany((portfolio,n)=>portfolio.RiskFactors(market,contracts[n],cancellation))
            .SelectMany(book=>book.Factors).DistinctBy(factor=>factor.Input).OrderBy(factor=>factor.Input.Series.Id).ToImmutableArray();
        return new(valuation,market,portfolios,contracts,factors,bindings,intervalBasis:intervalBasis);
    }
    public static PreparedConnectedPortfolioRisk WithShifts(PreparedConnectedPortfolioRisk prepared,ImmutableArray<CurveShift> shifts,CancellationToken cancellation=default)
    {
        cancellation.ThrowIfCancellationRequested();
        if(prepared is null)throw Error("instrument_context","Supply the exact prepared connected parent.");
        var valuation=prepared.Valuation.WithScenario(prepared.Scenario.WithShifts(shifts,cancellation));
        return new(valuation,ConnectedValuation.WithScenario(prepared.Market,valuation,prepared.IntervalBasis?.BindConnected(valuation,prepared.Bindings,cancellation)),prepared.Portfolios,prepared.Contracts,[],prepared.Bindings,prepared,prepared.IntervalBasis);
    }
    public static ConnectedGroupedRiskRun CalculateExact(PreparedConnectedPortfolioRisk prepared,ImmutableArray<ConnectedRiskBump> bumps,
        string numericalPolicy,CancellationToken cancellation=default,RiskDetail detail=RiskDetail.Holdings)
    {
        cancellation.ThrowIfCancellationRequested();
        if(!Enum.IsDefined(detail))throw Error("risk_detail","Choose holding details or book totals explicitly.");
        if(numericalPolicy!=ExactNumericalPolicy)throw Error("risk_policy","Select the exact rational symmetric repricing policy explicitly.");
        if(prepared is null)throw Error("instrument_context","Supply the exact prepared connected portfolio.");
        if(bumps.IsDefault||bumps.Any(bump=>bump is null||bump.Target is null||bump.Unit is null||bump.Size.CompareTo(default)<=0||bump.ConventionId!=ConnectedRiskBump.Convention))
            throw Error("risk_bump","Supply explicit positive symmetric curve-coordinate bumps, or an explicit empty list for base valuation.");
        var portfolios=prepared.Portfolios;var contracts=prepared.Contracts;
        var calculation=new PortfolioRiskCalculation(prepared.Market,portfolios,contracts,detail,cancellation);
        var bases=calculation.Bases;var calls=calculation.Calls;var applications=calculation.Applications;
        var factors=portfolios.Select(_=>ImmutableArray.CreateBuilder<ConnectedBookRiskFactor>()).ToArray();
        var graph=default(GraphWork);
        foreach(var bump in bumps)
        {
            cancellation.ThrowIfCancellationRequested();
            var shift=new CurveShift(bump.Coordinate,bump.Target,bump.Unit,bump.Size,CurveShift.ParentAdditive);
            var upScenario=prepared.Scenario.WithShifts([shift],cancellation);
            var downScenario=prepared.Scenario.WithShifts([shift with{Size=default(ExactDecimal)-bump.Size}],cancellation);
            var upMarket=ConnectedValuation.WithScenario(prepared.Market,prepared.Valuation.WithScenario(upScenario),prepared.IntervalBasis?.BindConnected(prepared.Valuation.WithScenario(upScenario),prepared.Bindings,cancellation));
            var downMarket=ConnectedValuation.WithScenario(prepared.Market,prepared.Valuation.WithScenario(downScenario),prepared.IntervalBasis?.BindConnected(prepared.Valuation.WithScenario(downScenario),prepared.Bindings,cancellation));
            var measurements=calculation.Measure(upMarket,downMarket,bump.Size,bump.Unit,cancellation);
            var upGraph=upScenario.Work;var downGraph=downScenario.Work;
            graph=AddGraphWork(graph,AddGraphWork(upGraph,downGraph));
            var upReceipt=Receipt(upScenario);var downReceipt=Receipt(downScenario);
            for(var group=0;group<portfolios.Length;group++)
            {
                cancellation.ThrowIfCancellationRequested();var measured=measurements[group];
                factors[group].Add(new(bump,measured.Totals,upReceipt,downReceipt,
                    new(upGraph,measured.UpPricerCalls,contracts[group].Length-measured.UpPricerCalls),
                    new(downGraph,measured.DownPricerCalls,contracts[group].Length-measured.DownPricerCalls)){Positions=measured.Positions});
            }
        }
        var preparation=0;
        for(var current=prepared;current is not null;current=current.Parent)
        {graph=AddGraphWork(graph,current.Scenario.Work);if(current.Parent is not null)preparation++;}
        var groups=portfolios.Select((portfolio,n)=>new ConnectedBookRiskRun(prepared.Valuation.Snapshot.Id,numericalPolicy,contracts[n],bases[n].Totals,
            factors[n].ToImmutable(),new(portfolio.Positions.Length,contracts[n].Length,contracts[n].SelectMany(c=>c.Dependencies).Select(r=>r.Contract).Distinct().Count(),
                1+2*bumps.Length,calls[n],applications[n],preparation),prepared.Scenario)
            {Detail=detail,BasePositions=BasePositions(portfolio,bases[n],detail),BaseMeasures=bases[n].BaseMeasures}).ToImmutableArray();
        cancellation.ThrowIfCancellationRequested();
        return new(prepared.Valuation.Snapshot.Id,groups,new(portfolios.Sum(p=>p.Positions.Length),contracts.Sum(c=>c.Length),
            contracts.SelectMany(c=>c).SelectMany(c=>c.Dependencies).Select(r=>r.Contract).Distinct().Count(),1+2*bumps.Length,calls.Sum(),applications.Sum(),preparation),graph);
    }
    private static ConnectedScenarioReceipt Receipt(ConnectedScenario scenario)=>
        new(scenario.Id,scenario.ParentId,scenario.Depth,scenario.Plan.ManifestId,scenario.Shifts);
    private static GraphWork AddGraphWork(GraphWork left,GraphWork right)=>new(left.EvaluatedNodes+right.EvaluatedNodes,left.ReusedReads+right.ReusedReads,
        checked(left.AffectedNodes+right.AffectedNodes),left.TraversedAffectedEdges+right.TraversedAffectedEdges);
}
