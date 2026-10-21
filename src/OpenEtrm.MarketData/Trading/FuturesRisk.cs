using System.Collections.Immutable;
using OpenEtrm.MarketData.Risk;

namespace OpenEtrm.MarketData.Trading;

// Open mark sensitivity and execution-P&L scenarios only; never settlement PV.
public static class FuturesRisk
{
    public const string Implementation="unprocessed_futures_execution_pnl_risk_v2";
    public static RiskRun Calculate(ImmutableArray<FuturesTrade> trades,MarketSnapshot snapshot,CurveModel model,
        ImmutableArray<QuoteBump> bumps,string implementation,string numericalPolicy,CancellationToken cancellation=default,RiskDetail detail=RiskDetail.Holdings,
        ImmutableArray<QuoteShift> stress=default)
    {
        var (basis,portfolio)=Prepare(trades,snapshot,model,implementation,cancellation);
        var preparedNodes=0;var reusedNodes=0;
        if(!stress.IsDefault)foreach(var shift in stress)
        {
            preparedNodes+=basis.EvaluatedConstituents;reusedNodes+=basis.ReusedConstituents;
            basis=model.WithShifts(basis,[shift],cancellation);
        }
        var result=RiskEngine.Calculate(basis,model.Scenarios(basis,bumps,cancellation),portfolio,portfolio.Declare(snapshot),numericalPolicy,cancellation,detail);
        return result with{Work=result.Work with{RebuiltCurveNodes=result.Work.RebuiltCurveNodes+preparedNodes,
            ReusedCurveNodes=result.Work.ReusedCurveNodes+reusedNodes,PreparationScenarioCount=stress.IsDefault?0:stress.Length}};
    }
    public static ImmutableArray<BookRiskFactors> Factors(ImmutableArray<FuturesTrade> trades,MarketSnapshot snapshot,CurveModel model,
        string implementation,CancellationToken cancellation=default)
    {
        var (basis,portfolio)=Prepare(trades,snapshot,model,implementation,cancellation);
        return portfolio.RiskFactors(basis,portfolio.Declare(snapshot),cancellation);
    }
    // Stored mixed-book operations reuse the same lifecycle, lot and pricing
    // binding validation as the dedicated futures report.
    public static ValuationPortfolio CreatePortfolio(ImmutableArray<FuturesTrade> trades,MarketSnapshot snapshot,CurveModel model,
        string implementation,CancellationToken cancellation=default)=>Prepare(trades,snapshot,model,implementation,cancellation).Item2;
    private static (CurveBuild,ValuationPortfolio) Prepare(ImmutableArray<FuturesTrade> trades,MarketSnapshot snapshot,CurveModel model,
        string implementation,CancellationToken cancellation)
    {
        cancellation.ThrowIfCancellationRequested();
        if(implementation!=Implementation)throw new MarketDataException("risk_convention","Select the supported unprocessed-futures execution-P&L risk convention.");
        if(trades.IsDefaultOrEmpty || trades.Any(t=>t is null))throw new MarketDataException("risk_position","Select explicit live holdings before running risk.");
        var sides=trades.SelectMany(t=>t.Instruments).Select(p=>p.MarketBinding).ToArray();
        var requests=sides.Select(s=>s.Market).Distinct().ToImmutableArray();
        if(requests.IsEmpty)throw new MarketDataException("risk_position","Select at least one live holding for risk.");
        var basis=model.Build(snapshot,requests,cancellation);
        // Reuse the complete immutable-definition, lifecycle and manifest validation.
        var holdings=FuturesHoldings.Resolve(trades,snapshot,basis,FuturesMarketBinding.GeneralImplementation,cancellation);
        var bindings=sides.GroupBy(s=>s.Instrument.Id).ToImmutableDictionary(g=>g.Key,g=>
        {
            var first=g.First();
            if(g.Any(s=>s.Market!=first.Market||s.Series!=first.Series||s.Quote!=first.Quote||s.Convention!=first.Convention||s.EligibleFrom!=first.EligibleFrom))
                throw new MarketDataException("valuation_binding","One instrument must resolve to one exact pricing binding in the shared context.");
            return first;
        });
        var instruments=bindings.Values.Select(s=>(Instrument)s.Instrument).ToImmutableArray();
        var positions=holdings.Select(h=>new RiskPosition(h.TradeId,h.Position.Id,h.BookId,
            h.Position.Instrument.Id,h.Quantity,
            new UnitMoney(h.Position.ExecutionPrice.ExactTimes(new Quantity(ExactDecimal.Parse("1"),h.Quantity.Specification)),h.Quantity.Specification),new Money(default,h.Position.Instrument.Currency))).ToImmutableArray();
        var portfolio=new ValuationPortfolio(instruments,positions,new(ValuationMeasure.ExecutionPnl,bindings,null));
        return (basis,portfolio);
    }
}
