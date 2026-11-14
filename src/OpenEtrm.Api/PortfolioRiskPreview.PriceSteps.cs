using System.Collections.Immutable;
using OpenEtrm.MarketData;
using OpenEtrm.MarketData.Calculation;
using OpenEtrm.MarketData.Risk;

namespace OpenEtrm.Api;

public sealed partial class PortfolioRiskPreview
{
    // A price step a held valuation declares for one quoted price: the factor's name, its trading tick and the step.
    internal readonly record struct DeclaredStep(string Factor,ExactDecimal Tick,ExactDecimal Size);

    // The bump report reads each delta and gamma from one up and one down revaluation and tests no agreement. A
    // valuation that declares a price step bends once per step (D394), so that pair states its Greeks only across at
    // least two of them. A smaller shock is refused (D395), naming up to three such targets with the smallest shock, in
    // whole trading ticks, that spans two steps of every price the target moves.
    internal static void RequireTwoPriceSteps(IEnumerable<(string Target,ExactDecimal Size,ImmutableArray<DeclaredStep> Declared)> shocks)
    {
        const int named=3;
        var small=new List<string>();var others=0;
        foreach(var (target,size,declared) in shocks)
        {
            if(declared.IsDefaultOrEmpty)continue;
            var largest=declared.MaxBy(step=>step.Size);
            if(size.CompareTo(largest.Size+largest.Size)>=0)continue;
            if(small.Count==named){others++;continue;}
            small.Add($"{target}: a shock of {size} is smaller than that for {(largest.Factor==target?"it":largest.Factor)}; "+
                $"shock by at least {DeclaredPriceStep.SmallestBump(largest.Size,largest.Tick)}.");
        }
        if(small.Count==0)return;
        if(others>0)small.Add(others==1?"1 more shock is also too small.":$"{others} more shocks are also too small.");
        throw new MarketDataException("risk_bump","One up and one down move state a delta and a gamma only across two price steps of every holding that declares one. "+
            string.Join(" ",small));
    }

    // Each bump with the price steps that held valuations declare for the quoted prices it moves. A step is declared at
    // the unstressed price; where nested stresses move that price the step is taken at the stressed one.
    private static IEnumerable<(string Target,ExactDecimal Size,ImmutableArray<DeclaredStep> Declared)> DeclaredSteps(PortfolioValuation.Prepared portfolio,
        ImmutableArray<PortfolioCurveBump> bumps,ImmutableArray<PortfolioCurveShift> stress,CancellationToken ct)
    {
        var steps=PortfolioValuation.LargestPriceSteps(portfolio.Markets.SelectMany(market=>market.Connected.DeclaredPriceSteps));
        if(steps.Count==0)yield break;
        var factors=portfolio.Factors.Where(factor=>steps.ContainsKey(factor.Input)).ToDictionary(factor=>factor.Input);
        IEnumerable<InputKey> Stepped(PortfolioRiskAxis axis)
        {
            var plan=portfolio.Markets.Single(market=>market.Snapshot.Definitions==axis.Market).Connected.Scenario.Plan;
            return axis.Points.Select(point=>plan.Points[point].Expression).OfType<ConnectedSource>().Select(source=>source.Input).Distinct().Where(steps.ContainsKey);
        }
        var stressed=new Dictionary<InputKey,ExactDecimal>();
        foreach(var shift in stress)
        foreach(var input in Stepped(shift.Axis))
            stressed[input]=stressed.GetValueOrDefault(input,factors[input].BaseValue)+shift.Size;
        foreach(var bump in bumps)
            yield return(bump.Axis.Label,bump.Size,[..Stepped(bump.Axis).Select(input=>
            {
                var factor=factors[input];
                // A stress that takes the price to zero or below fails in the valuation that reads it.
                var step=stressed.TryGetValue(input,out var price)&&price.CompareTo(default)>0?DeclaredPriceStep.AtPrice(steps[input],factor.BaseValue,price):steps[input];
                return new DeclaredStep(CurveRiskAxes.Quote(portfolio,factor,ct).Label,factor.TradingTick,step);
            })]);
    }
}
