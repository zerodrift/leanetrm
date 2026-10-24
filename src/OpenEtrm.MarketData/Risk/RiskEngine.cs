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
{public RiskDetail Detail{get;init;}=RiskDetail.Holdings;}

public static class RiskEngine
{
    public const string NumericalPolicy="exact_18_fractional_book_repricing_v2";
    private static MarketDataException Error(string code,string text)=>new(code,text);
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
        cancellation.ThrowIfCancellationRequested();
        var contracts=portfolio.Declare(basis.Snapshot);
        var available=basis.Values.Select(v=>v.Request).ToHashSet();
        if(contracts.SelectMany(c=>c.Dependencies).Any(d=>!available.Contains(d)))
            throw Error("risk_dependency","The selected scenario must contain every declared portfolio input.");
        return Calculate(basis,basis.Model.Scenarios(basis,bumps,cancellation),portfolio,contracts,numericalPolicy,cancellation,detail);
    }
    internal static RiskRun Calculate(CurveBuild baseMarket,IEnumerable<FactorScenarios> scenarios,ValuationPortfolio portfolio,
        ImmutableArray<UnitPricingContract> contracts,string numericalPolicy,CancellationToken cancellation=default,RiskDetail detail=RiskDetail.Holdings)
    {
        if(numericalPolicy!=NumericalPolicy)throw Error("risk_policy","Select the exact symmetric repricing numerical policy explicitly.");
        if(!Enum.IsDefined(detail))throw Error("risk_detail","Choose holding details or book totals explicitly.");
        var baseValue=portfolio.PV(baseMarket,contracts,ct:cancellation,detail:detail);
        var basis=detail==RiskDetail.Holdings?baseValue.Positions.Select(v=>v.ToMoney()).ToArray():[];
        var factors=ImmutableArray.CreateBuilder<RiskFactorResult>();
        var applications=baseValue.QuantityApplications;
        var calls=baseValue.UnitValuations;var rebuilt=baseMarket.EvaluatedConstituents;var reused=baseMarket.ReusedConstituents;
        foreach(var factor in scenarios)
        {
            cancellation.ThrowIfCancellationRequested();
            var up=portfolio.PV(factor.Up,contracts,baseMarket,baseValue,cancellation,detail);
            var down=portfolio.PV(factor.Down,contracts,baseMarket,baseValue,cancellation,detail);
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
            factors.Add(new(factor.Bump,rows.MoveToImmutable(),totals,
                new(factor.Up.EvaluatedConstituents,factor.Up.ReusedConstituents,up.UnitValuations,contracts.Length-up.UnitValuations),
                new(factor.Down.EvaluatedConstituents,factor.Down.ReusedConstituents,down.UnitValuations,contracts.Length-down.UnitValuations)));
            applications+=up.QuantityApplications+down.QuantityApplications;
            calls+=up.UnitValuations+down.UnitValuations;rebuilt+=factor.Up.EvaluatedConstituents+factor.Down.EvaluatedConstituents;
            reused+=factor.Up.ReusedConstituents+factor.Down.ReusedConstituents;
        }
        return new RiskRun(baseMarket.SnapshotId,NumericalPolicy,contracts,factors.ToImmutable(),
            new(portfolio.Positions.Length,contracts.Length,contracts.SelectMany(c=>c.Dependencies).Select(r=>r.Contract).Distinct().Count(),1+2*factors.Count,calls,rebuilt,reused){QuantityApplications=applications},baseMarket.Scenario){Detail=detail};
    }
    private static RiskValues Measure(Money b,Money u,Money d,QuoteBump bump)
    {
        var up=u.Subtract(b);var down=d.Subtract(b);
        var delta=ExactDecimal.CentralDifferenceExact(b.Amount,u.Amount,d.Amount,bump.Size,1);
        var gamma=ExactDecimal.CentralDifferenceExact(b.Amount,u.Amount,d.Amount,bump.Size,2);
        return new(b,u,d,up,down,new(delta,new(b.Currency,bump.Unit,1)),new(gamma,new(b.Currency,bump.Unit,2)));
    }
}
