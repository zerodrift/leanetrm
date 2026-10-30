using System.Collections.Immutable;
using OpenEtrm.MarketData.Trading;
using OpenEtrm.MarketData.Power;
using OpenEtrm.MarketData.CustomCurves;

namespace OpenEtrm.MarketData.Risk;

public sealed record UnitPricingContract(Guid Id,string ModelVersion,ImmutableArray<CurveRequest> Dependencies,
    ReferenceKey Quantity,ReferenceKey Currency)
{
    public string? ScheduleIdentity { get; init; }
    public ImmutableArray<SettlementProjection> SettlementProjections { get; init; }=[];
}
// A pricer sees only its declared inputs. No ambient market lookup or services.
public sealed class PricingInputs
{
    private readonly PreparedValuationMarket market;
    private readonly CancellationToken cancellation;
    private readonly ImmutableHashSet<CurveRequest> permitted;
    public Guid SnapshotId{get;}
    public DateTimeOffset ValuationAt{get;}
    public DateTimeOffset MarketCutoff{get;}
    public string ScenarioId{get;}
    public string NumericalPolicy=>market.NumericalPolicy;
    internal PricingInputs(PreparedValuationMarket market,UnitPricingContract contract,CancellationToken cancellation)
    {SnapshotId=market.Snapshot.Id;ValuationAt=market.Snapshot.ValuationAt;MarketCutoff=market.Snapshot.MarketCutoff;ScenarioId=market.ScenarioId;
        this.market=market;this.cancellation=cancellation;permitted=contract.Dependencies.ToImmutableHashSet();}
    public ExactUnitPrice ExactPrice(CurveRequest request)=>permitted.Contains(request)?market.Price(request,cancellation):
        throw new MarketDataException("risk_dependency","A pricer requested an undeclared or unavailable market input.");
    public UnitPrice Price(CurveRequest request)=>permitted.Contains(request)?market.WorkingPrice(request,cancellation):
        throw new MarketDataException("risk_dependency","A pricer requested an undeclared or unavailable market input.");
}
public record UnitMoney(ExactMoney Money,ReferenceKey Quantity)
{
    public UnitMoney(Money money,ReferenceKey quantity):this(ExactMoney.FromMoney(money),quantity){}
}
public sealed record UnitValuation(UnitValuationContext Context,ExactMoney Money,ReferenceKey Quantity):UnitMoney(Money,Quantity);
public sealed record RiskPosition(Guid TradeId,Guid BindingId,Guid BookId,Guid ValuationId,Quantity Quantity,UnitMoney UnitBasis,Money FixedBasis)
{
    public DeliveryCoverage? Coverage{get;init;}
}
public sealed record DerivativeUnit(ReferenceKey Currency,PriceUnit Factor,int Order);
public sealed record RiskDerivative(ExactDecimal Value,DerivativeUnit Unit);
public sealed record RiskValues(Money Base,Money Up,Money Down,Money UpPnl,Money DownPnl,RiskDerivative Delta,RiskDerivative Gamma);
public sealed record RiskPositionResult(Guid TradeId,Guid BindingId,Guid BookId,Guid ValuationId,
    Money Base,Money Up,Money Down,Money UpPnl,Money DownPnl,RiskDerivative Delta,RiskDerivative Gamma);
public sealed record RiskBookTotal(Guid BookId,ReferenceKey Currency,Money Base,Money Up,Money Down,Money UpPnl,Money DownPnl,RiskDerivative Delta,RiskDerivative Gamma);
public sealed record ScenarioWork(int RebuiltCurveNodes,int ReusedCurveNodes,int UnitPricerCalls,int ReusedUnitValues);
public sealed record RiskFactorResult(QuoteBump Bump,ImmutableArray<RiskPositionResult> Positions,
    ImmutableArray<RiskBookTotal> Totals,ScenarioWork UpWork,ScenarioWork DownWork);
public sealed record RiskWork(int PositionCount,int UniqueValuations,int UniqueMarketContracts,int ScenarioCount,int UnitPricerCalls,int RebuiltCurveNodes,int ReusedCurveNodes)
{
    public int UniqueInstruments{get;init;}=UniqueValuations;
    public int RepeatedInstrumentReferences=>PositionCount-UniqueInstruments;
    public int RepeatedScopeReferences=>PositionCount-UniqueValuations;
    public long UnitScenarioCandidates=>(long)UniqueValuations*ScenarioCount;
    public long ReusedUnitScenarioValues=>UnitScenarioCandidates-UnitPricerCalls;
    public int QuantityApplications{get;init;}
    public int PreparationScenarioCount{get;init;}
    public long CurveGraphEvaluatedNodes{get;init;}
    public long CurveGraphReusedReads{get;init;}
    public int VolumeComputations{get;init;}
    public int UniqueSideCalculations{get;init;}
    public long RowEvaluations{get;init;}
    public long ScheduleGraphEvaluatedNodes{get;init;}
    public long ScheduleGraphReusedReads{get;init;}
    public int ResolvedCurveMonths{get;init;}
    public int ReusedCurveMonths{get;init;}
    public int CalibratedBlocks{get;init;}
    public int ReusedBlocks{get;init;}
}
public enum RiskDetail { Holdings=1, Books }
public sealed record RiskRun(Guid SnapshotId,string NumericalPolicy,ImmutableArray<UnitPricingContract> Contracts,
    ImmutableArray<RiskFactorResult> Factors,RiskWork Work,QuoteScenario Scenario)
{
    public RiskDetail Detail{get;init;}=RiskDetail.Holdings;
    public ImmutableArray<BookCurrencyValue> BaseValues{get;init;}=[];
    public ImmutableArray<BookValueMeasures> BaseMeasures{get;init;}=[];
}

// A single immutable base and declared factor set can serve several explicitly
// labelled valuation measures. Pricing remains the responsibility of each unit.
public sealed class PreparedPortfolioRisk
{
    internal CurveBuild Market{get;}
    internal ImmutableArray<ValuationPortfolio> Portfolios{get;}
    internal ImmutableArray<ImmutableArray<UnitPricingContract>> Contracts{get;}
    public ImmutableArray<QuoteFactor> Factors{get;}
    public QuoteScenario Scenario=>Market.Scenario;
    internal int PriorRebuilt{get;}
    internal int PriorReused{get;}
    internal int PreparationScenarioCount{get;}
    internal PreparedPortfolioRisk(CurveBuild market,ImmutableArray<ValuationPortfolio> portfolios,
        ImmutableArray<ImmutableArray<UnitPricingContract>> contracts,ImmutableArray<QuoteFactor> factors,
        int priorRebuilt=0,int priorReused=0,int preparationScenarioCount=0)
    {Market=market;Portfolios=portfolios;Contracts=contracts;Factors=factors;PriorRebuilt=priorRebuilt;PriorReused=priorReused;PreparationScenarioCount=preparationScenarioCount;}
}
public sealed record GroupedRiskRun(Guid SnapshotId,ImmutableArray<RiskRun> Groups,RiskWork Work);
public sealed record ExactRiskBookTotal(Guid BookId,ExactDealRiskValues Values);
public sealed record ExactRiskPositionValue(Guid TradeId,Guid BindingId,Guid BookId,Guid ValuationId,ExactMoney Value);
public sealed record ExactRiskPositionResult(Guid TradeId,Guid BindingId,Guid BookId,Guid ValuationId,ExactDealRiskValues Values);
public sealed record ExactBookRiskFactor(QuoteBump Bump,ImmutableArray<ExactRiskBookTotal> Totals,ScenarioWork UpWork,ScenarioWork DownWork)
{
    public ImmutableArray<ExactRiskPositionResult> Positions{get;init;}=[];
}
public sealed record ExactBookRiskRun(Guid SnapshotId,string NumericalPolicy,ImmutableArray<UnitPricingContract> Contracts,
    ImmutableArray<BookCurrencyValue> BaseValues,ImmutableArray<ExactBookRiskFactor> Factors,RiskWork Work,QuoteScenario Scenario)
{
    public RiskDetail Detail{get;init;}=RiskDetail.Books;
    public ImmutableArray<ExactRiskPositionValue> BasePositions{get;init;}=[];
    public ImmutableArray<BookValueMeasures> BaseMeasures{get;init;}=[];
}
public sealed record ExactGroupedRiskRun(Guid SnapshotId,ImmutableArray<ExactBookRiskRun> Groups,RiskWork Work);

public static partial class RiskEngine
{
    public const string NumericalPolicy="exact_18_fractional_book_repricing_v2";
    public const string ExactNumericalPolicy="exact_rational_portfolio_repricing_v1";
    private static MarketDataException Error(string code,string text)=>new(code,text);
    public static PreparedPortfolioRisk Prepare(CurveModel model,MarketSnapshot snapshot,
        ImmutableArray<ValuationPortfolio> portfolios,CancellationToken cancellation=default)
    {
        cancellation.ThrowIfCancellationRequested();
        var contracts=DeclarePortfolios(portfolios,snapshot);
        var market=model.Build(snapshot,contracts.SelectMany(g=>g).SelectMany(c=>c.Dependencies).Distinct().ToImmutableArray(),cancellation);
        return Prepare(market,portfolios,contracts,cancellation);
    }
    public static PreparedPortfolioRisk Prepare(CurveBuild basis,ImmutableArray<ValuationPortfolio> portfolios,
        CancellationToken cancellation=default)
    {
        cancellation.ThrowIfCancellationRequested();
        if(basis is null)throw Error("snapshot_pin","Select an explicit immutable scenario build.");
        var contracts=DeclarePortfolios(portfolios,basis.Snapshot);
        RequireDependencies(basis,contracts.SelectMany(g=>g));
        return Prepare(basis,portfolios,contracts,cancellation);
    }
    private static ImmutableArray<ImmutableArray<UnitPricingContract>> DeclarePortfolios(
        ImmutableArray<ValuationPortfolio> portfolios,MarketSnapshot snapshot)
    {
        if(portfolios.IsDefaultOrEmpty||portfolios.Length>16||portfolios.Any(p=>p is null))
            throw Error("risk_contract","Select one to sixteen explicit valuation groups.");
        var instruments=portfolios.SelectMany(p=>p.Instruments).ToArray();
        var positions=portfolios.SelectMany(p=>p.Positions).ToArray();
        if(instruments.Select(i=>i.Id).Distinct().Count()!=instruments.Length||
            positions.Select(p=>(p.TradeId,p.BindingId)).Distinct().Count()!=positions.Length)
            throw Error("risk_contract","A shared operation must assign each unit instrument and trade binding to exactly one valuation group.");
        return portfolios.Select(p=>p.Declare(snapshot)).ToImmutableArray();
    }
    private static PreparedPortfolioRisk Prepare(CurveBuild market,ImmutableArray<ValuationPortfolio> portfolios,
        ImmutableArray<ImmutableArray<UnitPricingContract>> contracts,CancellationToken cancellation)
    {
        var selected=portfolios.SelectMany((p,n)=>p.RiskFactors(market,contracts[n],cancellation))
            .SelectMany(b=>b.Factors).Select(f=>f.Input).ToHashSet();
        return new(market,portfolios,contracts,market.Model.QuoteFactors(market.Snapshot).Where(f=>selected.Contains(f.Input))
            .Select(f=>f with{BaseValue=f.BaseValue+market.Scenario.Offset(f.Input)}).ToImmutableArray());
    }
    public static PreparedPortfolioRisk WithShifts(PreparedPortfolioRisk parent,ImmutableArray<QuoteShift> shifts,
        CancellationToken cancellation=default)
    {
        cancellation.ThrowIfCancellationRequested();
        if(parent is null)throw Error("snapshot_pin","Select an explicit prepared parent scenario.");
        var market=parent.Market.Model.WithShifts(parent.Market,shifts,cancellation);
        var child=Prepare(market,parent.Portfolios,parent.Contracts,cancellation);
        return new(market,child.Portfolios,child.Contracts,child.Factors,
            parent.PriorRebuilt+parent.Market.EvaluatedConstituents,parent.PriorReused+parent.Market.ReusedConstituents,parent.PreparationScenarioCount+1);
    }
    // Count only scenarios reaching a unit's declared dependency closure. This
    // performs no unit valuation and is independent of held quantity/exposure.
    public static (long Rows,long RowScenarios) IntervalWork(PreparedPortfolioRisk prepared)=>
        IntervalWork(prepared,prepared.Factors.Select(f=>f.Input).ToImmutableHashSet());
    public static (long Rows,long RowScenarios) IntervalWork(PreparedPortfolioRisk prepared,ImmutableArray<QuoteBump> bumps)
    {
        if(bumps.IsDefault||bumps.Any(b=>b is null)||bumps.Select(b=>b.Input).Distinct().Count()!=bumps.Length||
            bumps.Any(b=>!prepared.Factors.Any(f=>f.Input==b.Input)))
            throw Error("risk_bump","Admission requires an explicit distinct set of the prepared native factor scenarios.");
        return IntervalWork(prepared,bumps.Select(b=>b.Input).ToImmutableHashSet());
    }
    private static (long Rows,long RowScenarios) IntervalWork(PreparedPortfolioRisk prepared,ImmutableHashSet<InputKey> selected)=>
        IntervalWork(prepared.Market.Pricing,prepared.Portfolios,prepared.Contracts,selected);
    public static (long Rows,long RowScenarios) IntervalWork(PreparedConnectedPortfolioRisk prepared)=>
        IntervalWork(prepared,prepared.Factors.Select(f=>f.Input).ToImmutableArray());
    public static (long Rows,long RowScenarios) IntervalWork(PreparedConnectedPortfolioRisk prepared,ImmutableArray<InputKey> selectedNativeFactors)
    {
        if(prepared is null||selectedNativeFactors.IsDefault||selectedNativeFactors.Distinct().Count()!=selectedNativeFactors.Length||
            selectedNativeFactors.Any(input=>!prepared.Factors.Any(factor=>factor.Input==input)))
            throw Error("risk_bump","Admission requires an explicit distinct set of the prepared native factor scenarios.");
        return IntervalWork(prepared.Market,prepared.Portfolios,prepared.Contracts,selectedNativeFactors.ToImmutableHashSet());
    }
    private static (long Rows,long RowScenarios) IntervalWork(PreparedValuationMarket market,ImmutableArray<ValuationPortfolio> portfolios,
        ImmutableArray<ImmutableArray<UnitPricingContract>> contracts,ImmutableHashSet<InputKey> selected)
    {
        long rows=0,work=0;
        for(var n=0;n<portfolios.Length;n++)
        {
            var portfolio=portfolios[n];
            for(var index=0;index<portfolio.Scopes.Length;index++)
            {
                var scope=portfolio.Scopes[index];
                var context=new UnitValuationContext(market.Snapshot,portfolio.Setup,market,contracts[n][index],coverage:scope.Coverage);
                var count=scope.Instrument.WorkRows(context);
                if(count<0)throw Error("risk_contract","An instrument's declared work must be nonnegative.");
                if(count==0)continue;
                // Provenance may be shared by frozen and live samples. Count
                // variable source inputs reached by this scope, not matching IDs.
                var reached=market.Factors(contracts[n][index].Dependencies,default).Select(f=>f.Input).Distinct().Count(selected.Contains);
                rows=checked(rows+count);work=checked(work+count*(1L+2L*reached));
            }
        }
        return(rows,work);
    }
    public static GroupedRiskRun Calculate(PreparedPortfolioRisk prepared,ImmutableArray<QuoteBump> bumps,
        string numericalPolicy,CancellationToken cancellation=default,RiskDetail detail=RiskDetail.Holdings)
    {
        Validate(numericalPolicy,detail);cancellation.ThrowIfCancellationRequested();
        var result=RunGroups(prepared,bumps,detail,true,cancellation);
        return new(result.SnapshotId,result.Groups.Select(WorkingResult).ToImmutableArray(),result.Work);
    }
    public static ExactGroupedRiskRun CalculateExact(PreparedPortfolioRisk prepared,ImmutableArray<QuoteBump> bumps,
        string numericalPolicy,CancellationToken cancellation=default,RiskDetail detail=RiskDetail.Books)
    {
        if(numericalPolicy!=ExactNumericalPolicy)throw Error("risk_policy","Select the exact rational portfolio repricing policy explicitly.");
        if(!Enum.IsDefined(detail))throw Error("risk_detail","Choose holding details or book totals explicitly.");
        cancellation.ThrowIfCancellationRequested();
        return RunGroups(prepared,bumps,detail,false,cancellation);
    }
    private static ExactGroupedRiskRun RunGroups(PreparedPortfolioRisk prepared,ImmutableArray<QuoteBump> bumps,
        RiskDetail detail,bool workingDecimal,CancellationToken cancellation)
    {
        if(prepared is null)throw Error("snapshot_pin","Select an explicit prepared portfolio scenario.");
        if(bumps.IsDefault)throw Error("risk_bump","Supply an explicit shock list, including an empty list for base-only valuation.");
        return RunQuoteGroups(prepared,bumps.IsEmpty?Enumerable.Empty<FactorScenarios>():prepared.Market.Model.Scenarios(prepared.Market,bumps,cancellation),
            detail,workingDecimal,cancellation);
    }
    private static ExactGroupedRiskRun RunQuoteGroups(PreparedPortfolioRisk prepared,IEnumerable<FactorScenarios> scenarios,
        RiskDetail detail,bool workingDecimal,CancellationToken cancellation)
    {
        var calculation=new PortfolioRiskCalculation(prepared.Market.Pricing,prepared.Portfolios,prepared.Contracts,detail,cancellation);
        // A working-decimal holding report explicitly rejects fractional cash,
        // including base-only reports. Exact book netting happens before projection.
        if(workingDecimal&&detail==RiskDetail.Holdings)
            foreach(var basis in calculation.Bases)foreach(var value in basis.Positions)_=value.ToMoney();
        var factors=prepared.Portfolios.Select(_=>ImmutableArray.CreateBuilder<ExactBookRiskFactor>()).ToArray();
        var rebuilt=prepared.Market.EvaluatedConstituents+prepared.PriorRebuilt;
        var reused=prepared.Market.ReusedConstituents+prepared.PriorReused;var count=1;
        foreach(var factor in scenarios)
        {
            cancellation.ThrowIfCancellationRequested();
            var measured=calculation.Measure(factor.Up.Pricing,factor.Down.Pricing,factor.Bump.Size,factor.Bump.Unit,cancellation);
            for(var group=0;group<measured.Length;group++)
            {
                var value=measured[group];var units=prepared.Contracts[group].Length;
                factors[group].Add(new(factor.Bump,value.Totals,
                    new(factor.Up.EvaluatedConstituents,factor.Up.ReusedConstituents,value.UpPricerCalls,units-value.UpPricerCalls),
                    new(factor.Down.EvaluatedConstituents,factor.Down.ReusedConstituents,value.DownPricerCalls,units-value.DownPricerCalls)){Positions=value.Positions});
            }
            rebuilt+=factor.Up.EvaluatedConstituents+factor.Down.EvaluatedConstituents;
            reused+=factor.Up.ReusedConstituents+factor.Down.ReusedConstituents;count+=2;
        }
        RiskWork Work(IEnumerable<int> indices)
        {
            var selected=indices.ToArray();var contracts=selected.SelectMany(n=>prepared.Contracts[n]).ToImmutableArray();
            var intervals=selected.Select(n=>calculation.IntervalWork[n]).ToArray();
            return new(selected.Sum(n=>prepared.Portfolios[n].Positions.Length),contracts.Length,
                contracts.SelectMany(c=>c.Dependencies).Select(r=>r.Contract).Distinct().Count(),count,selected.Sum(n=>calculation.Calls[n]),rebuilt,reused)
            {
                QuantityApplications=selected.Sum(n=>calculation.Applications[n]),PreparationScenarioCount=prepared.PreparationScenarioCount,
                UniqueInstruments=selected.Sum(n=>prepared.Portfolios[n].Instruments.Length),
                VolumeComputations=selected.Select(n=>prepared.Portfolios[n].Setup.Interval).OfType<IntervalValuationInputs>().Distinct(ReferenceEqualityComparer.Instance).Cast<IntervalValuationInputs>().Sum(i=>i.VolumeComputations),
                UniqueSideCalculations=intervals.Sum(w=>w.Sides),RowEvaluations=intervals.Sum(w=>w.Rows),
                ScheduleGraphEvaluatedNodes=intervals.Sum(w=>w.GraphNodes),ScheduleGraphReusedReads=intervals.Sum(w=>w.GraphReused),
                ResolvedCurveMonths=intervals.Sum(w=>w.Resolved),ReusedCurveMonths=intervals.Sum(w=>w.Reused),
                CalibratedBlocks=intervals.Sum(w=>w.Calibrated),ReusedBlocks=intervals.Sum(w=>w.ReusedBlocks)
            };
        }
        var groups=prepared.Portfolios.Select((portfolio,n)=>new ExactBookRiskRun(prepared.Market.SnapshotId,ExactNumericalPolicy,prepared.Contracts[n],
            calculation.Bases[n].Totals,factors[n].ToImmutable(),Work([n]),prepared.Scenario)
            {BaseMeasures=calculation.Bases[n].BaseMeasures,Detail=detail,BasePositions=workingDecimal?[]:BasePositions(portfolio,calculation.Bases[n],detail)}).ToImmutableArray();
        cancellation.ThrowIfCancellationRequested();
        return new(prepared.Market.SnapshotId,groups,Work(Enumerable.Range(0,prepared.Portfolios.Length)));
    }
    public static RiskRun Calculate(CurveModel model,MarketSnapshot snapshot,ValuationPortfolio portfolio,
        ImmutableArray<QuoteBump> bumps,string numericalPolicy,CancellationToken cancellation=default,RiskDetail detail=RiskDetail.Holdings)
    {
        if(numericalPolicy!=NumericalPolicy)throw Error("risk_policy","Select the exact symmetric repricing numerical policy explicitly.");
        cancellation.ThrowIfCancellationRequested();
        var contracts=portfolio.Declare(snapshot);
        var requests=contracts.SelectMany(c=>c.Dependencies).Distinct().ToImmutableArray();
        var basis=model.Build(snapshot,requests,cancellation);
        return Calculate(basis,model.Scenarios(basis,bumps,cancellation),portfolio,contracts,numericalPolicy,cancellation,detail);
    }
    public static RiskRun Calculate(CurveBuild basis,ValuationPortfolio portfolio,ImmutableArray<QuoteBump> bumps,
        string numericalPolicy,CancellationToken cancellation=default,RiskDetail detail=RiskDetail.Holdings)
    {
        Validate(numericalPolicy,detail);cancellation.ThrowIfCancellationRequested();
        if(basis is null)throw Error("snapshot_pin","Select an explicit immutable scenario build.");
        var contracts=portfolio.Declare(basis.Snapshot);
        RequireDependencies(basis,contracts);
        return Calculate(basis,basis.Model.Scenarios(basis,bumps,cancellation),portfolio,contracts,numericalPolicy,cancellation,detail);
    }
    private static void RequireDependencies(CurveBuild basis,IEnumerable<UnitPricingContract> contracts)
    {
        var available=basis.Values.Select(v=>v.Request).ToHashSet();
        if(contracts.SelectMany(c=>c.Dependencies).Any(d=>!available.Contains(d)))
            throw Error("risk_dependency","The selected scenario must contain every declared portfolio input.");
    }
    internal static RiskRun Calculate(CurveBuild baseMarket,IEnumerable<FactorScenarios> scenarios,ValuationPortfolio portfolio,
        ImmutableArray<UnitPricingContract> contracts,string numericalPolicy,CancellationToken cancellation=default,RiskDetail detail=RiskDetail.Holdings)
    {
        Validate(numericalPolicy,detail);cancellation.ThrowIfCancellationRequested();
        var prepared=new PreparedPortfolioRisk(baseMarket,[portfolio],[contracts],[]);
        return WorkingResult(RunQuoteGroups(prepared,scenarios,detail,true,cancellation).Groups.Single());
    }
    private static void Validate(string policy,RiskDetail detail)
    {
        if(policy!=NumericalPolicy)throw Error("risk_policy","Select the exact symmetric repricing numerical policy explicitly.");
        if(!Enum.IsDefined(detail))throw Error("risk_detail","Choose holding details or book totals explicitly.");
    }
    // Quote-only and general curve-coordinate reports share exact repricing.
    // This projection is the explicit working-decimal output contract.
    private static RiskRun WorkingResult(ExactBookRiskRun run)
    {
        var factors=run.Factors.Select(factor=>new RiskFactorResult(factor.Bump,
            factor.Positions.Select(position=>
            {
                var value=WorkingValues(position.Values);
                return new RiskPositionResult(position.TradeId,position.BindingId,position.BookId,position.ValuationId,
                    value.Base,value.Up,value.Down,value.UpPnl,value.DownPnl,value.Delta,value.Gamma);
            }).ToImmutableArray(),factor.Totals.Select(total=>
            {
                var value=WorkingValues(total.Values);
                return new RiskBookTotal(total.BookId,value.Base.Currency,value.Base,value.Up,value.Down,value.UpPnl,value.DownPnl,value.Delta,value.Gamma);
            }).ToImmutableArray(),factor.UpWork,factor.DownWork)).ToImmutableArray();
        return new(run.SnapshotId,NumericalPolicy,run.Contracts,factors,run.Work,run.Scenario){Detail=run.Detail,BaseValues=run.BaseValues,BaseMeasures=run.BaseMeasures};
    }
    private static RiskValues WorkingValues(ExactDealRiskValues value)=>new(value.Base.ToMoney(),value.Up.ToMoney(),value.Down.ToMoney(),
        value.UpPnl.ToMoney(),value.DownPnl.ToMoney(),new(value.Delta.Value.ToMoney().Amount,value.Delta.Unit),new(value.Gamma.Value.ToMoney().Amount,value.Gamma.Unit));
}
