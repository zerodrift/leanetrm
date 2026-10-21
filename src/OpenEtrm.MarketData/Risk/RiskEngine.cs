using System.Collections.Immutable;
using OpenEtrm.MarketData.Trading;

namespace OpenEtrm.MarketData.Risk;

public sealed record UnitPricingContract(Guid Id,string ModelVersion,ImmutableArray<CurveRequest> Dependencies,
    ReferenceKey Quantity,ReferenceKey Currency);
// A pricer sees only its declared inputs. No ambient market lookup or services.
public sealed class PricingInputs
{
    private readonly IReadOnlyDictionary<CurveRequest,BuiltValue> values;
    private readonly ImmutableHashSet<CurveRequest> permitted;
    public Guid SnapshotId{get;}
    public DateTimeOffset ValuationAt{get;}
    public DateTimeOffset MarketCutoff{get;}
    public string ScenarioId{get;}
    public string NumericalPolicy=>CurveModel.NumericalPolicy;
    internal PricingInputs(CurveBuild market,UnitPricingContract contract,IReadOnlyDictionary<CurveRequest,BuiltValue> values)
    {SnapshotId=market.SnapshotId;ValuationAt=market.Snapshot.ValuationAt;MarketCutoff=market.Snapshot.MarketCutoff;ScenarioId=market.Scenario.Id;
        this.values=values;permitted=contract.Dependencies.ToImmutableHashSet();}
    public UnitPrice Price(CurveRequest request)=>permitted.Contains(request) && values.TryGetValue(request,out var value)?value.Price:
        throw new MarketDataException("risk_dependency","A pricer requested an undeclared or unavailable market input.");
}
public sealed record UnitMoney(ExactMoney Money,ReferenceKey Quantity)
{
    public UnitMoney(Money money,ReferenceKey quantity):this(ExactMoney.FromMoney(money),quantity){}
}
public sealed record RiskPosition(Guid TradeId,Guid BindingId,Guid BookId,Guid ValuationId,Quantity Quantity,UnitMoney UnitBasis,Money FixedBasis);
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
    public int RepeatedInstrumentReferences=>PositionCount-UniqueValuations;
    public long UnitScenarioCandidates=>(long)UniqueValuations*ScenarioCount;
    public long ReusedUnitScenarioValues=>UnitScenarioCandidates-UnitPricerCalls;
    public int QuantityApplications{get;init;}
    public int PreparationScenarioCount{get;init;}
}
public enum RiskDetail { Holdings=1, Books }
public sealed record RiskRun(Guid SnapshotId,string NumericalPolicy,ImmutableArray<UnitPricingContract> Contracts,
    ImmutableArray<RiskFactorResult> Factors,RiskWork Work,QuoteScenario Scenario)
{
    public RiskDetail Detail{get;init;}=RiskDetail.Holdings;
    public ImmutableArray<BookCurrencyValue> BaseValues{get;init;}=[];
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
}
public sealed record ExactGroupedRiskRun(Guid SnapshotId,ImmutableArray<ExactBookRiskRun> Groups,RiskWork Work);

public static class RiskEngine
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
    public static GroupedRiskRun Calculate(PreparedPortfolioRisk prepared,ImmutableArray<QuoteBump> bumps,
        string numericalPolicy,CancellationToken cancellation=default,RiskDetail detail=RiskDetail.Holdings)
    {
        Validate(numericalPolicy,detail);cancellation.ThrowIfCancellationRequested();
        var (runners,work)=RunGroups(prepared,bumps,detail,false,cancellation);
        return new(prepared.Market.SnapshotId,runners.Select(r=>r.Finish()).ToImmutableArray(),work);
    }
    public static ExactGroupedRiskRun CalculateExact(PreparedPortfolioRisk prepared,ImmutableArray<QuoteBump> bumps,
        string numericalPolicy,CancellationToken cancellation=default,RiskDetail detail=RiskDetail.Books)
    {
        if(numericalPolicy!=ExactNumericalPolicy)throw Error("risk_policy","Select the exact rational portfolio repricing policy explicitly.");
        if(!Enum.IsDefined(detail))throw Error("risk_detail","Choose holding details or book totals explicitly.");
        cancellation.ThrowIfCancellationRequested();
        var (runners,work)=RunGroups(prepared,bumps,detail,true,cancellation);
        return new(prepared.Market.SnapshotId,runners.Select(r=>r.FinishExact()).ToImmutableArray(),work);
    }
    private static (Runner[] Runners,RiskWork Work) RunGroups(PreparedPortfolioRisk prepared,ImmutableArray<QuoteBump> bumps,
        RiskDetail detail,bool exact,CancellationToken cancellation)
    {
        if(bumps.IsDefault)throw Error("risk_bump","Supply an explicit shock list, including an empty list for base-only valuation.");
        var runners=prepared.Portfolios.Select((p,n)=>new Runner(prepared.Market,p,prepared.Contracts[n],detail,cancellation,exact,
            prepared.PriorRebuilt,prepared.PriorReused,prepared.PreparationScenarioCount)).ToArray();
        var rebuilt=prepared.Market.EvaluatedConstituents+prepared.PriorRebuilt;var reused=prepared.Market.ReusedConstituents+prepared.PriorReused;var scenarios=1;
        foreach(var factor in bumps.IsEmpty?Enumerable.Empty<FactorScenarios>():prepared.Market.Model.Scenarios(prepared.Market,bumps,cancellation))
        {
            cancellation.ThrowIfCancellationRequested();
            foreach(var runner in runners)runner.Add(factor,cancellation);
            rebuilt+=factor.Up.EvaluatedConstituents+factor.Down.EvaluatedConstituents;
            reused+=factor.Up.ReusedConstituents+factor.Down.ReusedConstituents;scenarios+=2;
        }
        var work=runners.Select(r=>r.Work).ToArray();
        return (runners,new(work.Sum(g=>g.PositionCount),work.Sum(g=>g.UniqueValuations),
            prepared.Contracts.SelectMany(g=>g).SelectMany(c=>c.Dependencies).Select(r=>r.Contract).Distinct().Count(),scenarios,
            work.Sum(g=>g.UnitPricerCalls),rebuilt,reused){QuantityApplications=work.Sum(g=>g.QuantityApplications),PreparationScenarioCount=prepared.PreparationScenarioCount});
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
        var runner=new Runner(baseMarket,portfolio,contracts,detail,cancellation);
        foreach(var factor in scenarios)runner.Add(factor,cancellation);
        return runner.Finish();
    }
    private static void Validate(string policy,RiskDetail detail)
    {
        if(policy!=NumericalPolicy)throw Error("risk_policy","Select the exact symmetric repricing numerical policy explicitly.");
        if(!Enum.IsDefined(detail))throw Error("risk_detail","Choose holding details or book totals explicitly.");
    }
    private sealed class Runner
    {
        private readonly CurveBuild baseMarket;
        private readonly ValuationPortfolio portfolio;
        private readonly ImmutableArray<UnitPricingContract> contracts;
        private readonly RiskDetail detail;
        private readonly PortfolioValue baseValue;
        private readonly Money[] basis;
        private readonly ImmutableArray<RiskFactorResult>.Builder factors=ImmutableArray.CreateBuilder<RiskFactorResult>();
        private readonly ImmutableArray<ExactBookRiskFactor>.Builder exactFactors=ImmutableArray.CreateBuilder<ExactBookRiskFactor>();
        private readonly bool exact;
        private readonly int preparationScenarioCount;
        private int applications,calls,rebuilt,reused;
        internal Runner(CurveBuild baseMarket,ValuationPortfolio portfolio,ImmutableArray<UnitPricingContract> contracts,RiskDetail detail,CancellationToken ct,bool exact=false,
            int priorRebuilt=0,int priorReused=0,int preparationScenarioCount=0)
        {
            this.baseMarket=baseMarket;this.portfolio=portfolio;this.contracts=contracts;this.detail=detail;
            this.exact=exact;this.preparationScenarioCount=preparationScenarioCount;
            baseValue=portfolio.PV(baseMarket,contracts,ct:ct,detail:detail);
            basis=detail==RiskDetail.Holdings&&!exact?baseValue.Positions.Select(v=>v.ToMoney()).ToArray():[];
            applications=baseValue.QuantityApplications;calls=baseValue.UnitValuations;rebuilt=baseMarket.EvaluatedConstituents+priorRebuilt;reused=baseMarket.ReusedConstituents+priorReused;
        }
        internal void Add(FactorScenarios factor,CancellationToken cancellation)
        {
            cancellation.ThrowIfCancellationRequested();
            var up=portfolio.PV(factor.Up,contracts,baseMarket,baseValue,cancellation,detail);
            var down=portfolio.PV(factor.Down,contracts,baseMarket,baseValue,cancellation,detail);
            var upWork=new ScenarioWork(factor.Up.EvaluatedConstituents,factor.Up.ReusedConstituents,up.UnitValuations,contracts.Length-up.UnitValuations);
            var downWork=new ScenarioWork(factor.Down.EvaluatedConstituents,factor.Down.ReusedConstituents,down.UnitValuations,contracts.Length-down.UnitValuations);
            if(exact)
                exactFactors.Add(new(factor.Bump,baseValue.Totals.Select((b,n)=>new ExactRiskBookTotal(b.BookId,
                    ExactRepricing.Measure(b.Value,up.Totals[n].Value,down.Totals[n].Value,factor.Bump))).ToImmutableArray(),upWork,downWork)
                {
                    Positions=detail==RiskDetail.Books?[]:portfolio.Positions.Select((p,n)=>new ExactRiskPositionResult(
                        p.TradeId,p.BindingId,p.BookId,p.ValuationId,
                        ExactRepricing.Measure(baseValue.Positions[n],up.Positions[n],down.Positions[n],factor.Bump))).ToImmutableArray()
                });
            else
            {
                var rows=ImmutableArray.CreateBuilder<RiskPositionResult>(detail==RiskDetail.Holdings?portfolio.Positions.Length:0);
                var zeros=new Dictionary<ReferenceKey,RiskValues>();
                for(var n=0;detail==RiskDetail.Holdings&&n<portfolio.Positions.Length;n++)
                {
                    cancellation.ThrowIfCancellationRequested();var p=portfolio.Positions[n];var b=basis[n];
                    var unchanged=ReferenceEquals(up.Positions[n],baseValue.Positions[n])&&ReferenceEquals(down.Positions[n],baseValue.Positions[n]);
                    var u=unchanged?b:up.Positions[n].ToMoney();var d=unchanged?b:down.Positions[n].ToMoney();
                    RiskValues measured;
                    if(unchanged)
                    {
                        if(!zeros.TryGetValue(b.Currency,out measured!))
                        {var zero=new Money(default,b.Currency);measured=Measure(zero,zero,zero,factor.Bump);zeros.Add(b.Currency,measured);}
                    }
                    else measured=Measure(b,u,d,factor.Bump);
                    rows.Add(new(p.TradeId,p.BindingId,p.BookId,p.ValuationId,b,u,d,measured.UpPnl,measured.DownPnl,measured.Delta,measured.Gamma));
                }
                var totals=baseValue.Totals.Select((b,n)=>
                {
                    var v=Measure(b.Value.ToMoney(),up.Totals[n].Value.ToMoney(),down.Totals[n].Value.ToMoney(),factor.Bump);
                    return new RiskBookTotal(b.BookId,b.Currency,v.Base,v.Up,v.Down,v.UpPnl,v.DownPnl,v.Delta,v.Gamma);
                }).ToImmutableArray();
                factors.Add(new(factor.Bump,rows.MoveToImmutable(),totals,upWork,downWork));
            }
            applications+=up.QuantityApplications+down.QuantityApplications;
            calls+=up.UnitValuations+down.UnitValuations;rebuilt+=factor.Up.EvaluatedConstituents+factor.Down.EvaluatedConstituents;
            reused+=factor.Up.ReusedConstituents+factor.Down.ReusedConstituents;
        }
        internal RiskWork Work=>new(portfolio.Positions.Length,contracts.Length,
            contracts.SelectMany(c=>c.Dependencies).Select(r=>r.Contract).Distinct().Count(),1+2*(factors.Count+exactFactors.Count),calls,rebuilt,reused){QuantityApplications=applications,PreparationScenarioCount=preparationScenarioCount};
        internal RiskRun Finish()=>new RiskRun(baseMarket.SnapshotId,NumericalPolicy,contracts,factors.ToImmutable(),Work,baseMarket.Scenario){Detail=detail,BaseValues=baseValue.Totals};
        internal ExactBookRiskRun FinishExact()=>new(baseMarket.SnapshotId,ExactNumericalPolicy,contracts,baseValue.Totals,exactFactors.ToImmutable(),Work,baseMarket.Scenario)
        {
            Detail=detail,BasePositions=detail==RiskDetail.Books?[]:portfolio.Positions.Select((p,n)=>
                new ExactRiskPositionValue(p.TradeId,p.BindingId,p.BookId,p.ValuationId,baseValue.Positions[n])).ToImmutableArray()
        };
    }
    private static RiskValues Measure(Money b,Money u,Money d,QuoteBump bump)
    {
        var up=u.Subtract(b);var down=d.Subtract(b);
        var delta=ExactDecimal.CentralDifferenceExact(b.Amount,u.Amount,d.Amount,bump.Size,1);
        var gamma=ExactDecimal.CentralDifferenceExact(b.Amount,u.Amount,d.Amount,bump.Size,2);
        return new(b,u,d,up,down,new(delta,new(b.Currency,bump.Unit,1)),new(gamma,new(b.Currency,bump.Unit,2)));
    }
}
