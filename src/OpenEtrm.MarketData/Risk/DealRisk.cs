using System.Collections.Immutable;

namespace OpenEtrm.MarketData.Risk;

public sealed record ExactRiskDerivative(ExactMoney Value,DerivativeUnit Unit);
public sealed record ExactDealRiskValues(ExactMoney Base,ExactMoney Up,ExactMoney Down,ExactMoney UpPnl,ExactMoney DownPnl,ExactRiskDerivative Delta,ExactRiskDerivative Gamma);
public sealed record DealFactorRisk(QuoteFactor Factor,QuoteBump Bump,ExactDealRiskValues Values);
public sealed record DealRiskRun(ExactMoney Base,ImmutableArray<DealFactorRisk> Factors,int ScenarioCount);

internal static class ExactRepricing
{
    internal static ExactDealRiskValues Measure(ExactMoney b,ExactMoney u,ExactMoney d,QuoteBump bump)
        =>Measure(b,u,d,bump.Size,bump.Unit);
    internal static ExactDealRiskValues Measure(ExactMoney b,ExactMoney u,ExactMoney d,ExactDecimal size,PriceUnit unit)
    {
        var two=ExactDecimal.Parse("2");
        return new(b,u,d,u.Subtract(b),d.Subtract(b),
            new(u.Subtract(d).DivideBy(two).DivideBy(size),new(b.Currency,unit,1)),
            new(u.Add(d).Subtract(b.Scale(two)).DivideBy(size).DivideBy(size),new(b.Currency,unit,2)));
    }
}

// A single temporary deal is repriced as a black box under immutable shared
// quote scenarios. No trade persistence or inference of upstream exposure.
public static class DealRisk
{
    public const string NumericalPolicy="exact_rational_symmetric_deal_repricing_v1";
    public const string BumpPolicy="quote_tick_symmetric_v1";
    public static DealRiskRun Calculate(CurveModel model,MarketSnapshot snapshot,ImmutableArray<CurveRequest> dependencies,
        Func<CurveBuild,ExactMoney> price,string bumpPolicy,string numericalPolicy,CancellationToken ct=default)
    {
        if(bumpPolicy!=BumpPolicy||numericalPolicy!=NumericalPolicy||dependencies.IsDefault||price is null)
            throw new MarketDataException("risk_policy","Select explicit quote-tick symmetric shocks and the supported exact repricing policy.");
        var market=model.Build(snapshot,dependencies,ct);
        var lineage=market.Evaluations.Where(e=>e.Use==InputUse.Variable).SelectMany(e=>e.Result.Lineage).ToHashSet();
        var factors=model.QuoteFactors(snapshot).Where(f=>lineage.Contains(f.Observation)).ToArray();
        if(factors.Length>128||factors.Length==0&&!dependencies.IsEmpty)
            throw new MarketDataException("risk_factor","A draft with projected dependencies requires one to 128 live declared price factors.");
        var baseValue=price(market);var results=ImmutableArray.CreateBuilder<DealFactorRisk>();
        if(factors.Length>0)
        {
            ct.ThrowIfCancellationRequested();
            var bumps=factors.Select(f=>new QuoteBump(f.Input,f.Quote,f.Unit,f.TradingTick,QuoteBump.Convention)).ToImmutableArray();
            foreach(var (factor,scenario) in factors.Zip(model.Scenarios(market,bumps,ct)))
                results.Add(new(factor,scenario.Bump,ExactRepricing.Measure(baseValue,price(scenario.Up),price(scenario.Down),scenario.Bump)));
        }
        ct.ThrowIfCancellationRequested();
        return new(baseValue,results.ToImmutable(),1+2*factors.Length);
    }
}
