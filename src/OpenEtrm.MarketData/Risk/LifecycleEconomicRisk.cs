using System.Collections.Immutable;
using OpenEtrm.MarketData.Trading;

namespace OpenEtrm.MarketData.Risk;

public sealed record LifecycleEconomicRiskPolicy(string Implementation,string Confidence,string QuantilePolicy,string TailPolicy)
{
    public const string Historical="economic_lifecycle_historical_full_revaluation_v1",MonteCarlo="economic_lifecycle_monte_carlo_full_revaluation_v1",
        Measure="economic_pnl_v1",MarketOnly="market_only_horizon_diagnostic_v1";
}
public sealed record LifecycleEconomicRiskReport(LifecycleEconomicRiskPolicy Policy,string HistoryId,DateTimeOffset Horizon,
    ImmutableArray<LifecycleProjectionValue> NoShockTimeCarry,ImmutableArray<HistoricalPartition> Economic,
    ImmutableArray<HistoricalPartition> MarketOnlyDiagnostic,int Samples)
{
    public ImmutableArray<MonteCarloDiagnostic> MonteCarloDiagnostics{get;init;}=[];
}

// Sampling supplies terminal native primitive moves. Every sample runs the same
// pure event path and ordinary residual pricers. A failed path aborts reduction;
// no catch/drop/resample path exists here.
public static class LifecycleEconomicRisk
{
    // Called by the source adapter before history reads, as well as by replay.
    public static ImmutableArray<QuoteFactor> Factors(ImmutableArray<PreparedLifecycleProjection> prepared)
    {
        if(prepared.IsDefaultOrEmpty||prepared.Any(p=>p is null))throw Error("risk_economic_scope","Supply the complete prepared market partitions.");
        var groups=prepared.SelectMany(p=>p.Factors).GroupBy(f=>f.Input).ToArray();
        if(groups.Any(g=>g.Any(f=>!SameFactor(g.First(),f))))
            throw Error("risk_economic_history","One native factor input cannot name inconsistent source observations, units, roles or base levels across partitions.");
        return [..groups.Select(g=>g.First() with{Curves=[..g.SelectMany(f=>f.Curves).Distinct().OrderBy(c=>c.Code,StringComparer.Ordinal)]})
            .OrderBy(f=>f.Input.Series.Revision.CatalogId).ThenBy(f=>f.Input.Series.Revision.RevisionId).ThenBy(f=>f.Input.Series.Id)];
    }
    public static LifecycleEconomicRiskReport Historical(ImmutableArray<PreparedLifecycleProjection> prepared,AlignedFactorHistory history,
        LifecycleEconomicRiskPolicy policy,CancellationToken cancellation=default)
    {
        history=FactorHistory.Replay(history,cancellation);
        Require(prepared,history,policy,LifecycleEconomicRiskPolicy.Historical);
        var samples=history.Samples.Select(s=>(s.Id,s.Changes)).ToImmutableArray();
        return Calculate(prepared,history,policy,samples,null,cancellation);
    }
    public static LifecycleEconomicRiskReport MonteCarlo(ImmutableArray<PreparedLifecycleProjection> prepared,AlignedFactorHistory history,
        LifecycleEconomicRiskPolicy policy,MonteCarloRiskPolicy simulation,CancellationToken cancellation=default)
    {
        history=FactorHistory.Replay(history,cancellation);
        Require(prepared,history,policy,LifecycleEconomicRiskPolicy.MonteCarlo);
        if(simulation.Convention!=MonteCarloRiskPolicy.Economic||simulation.ValuationPolicy!=MonteCarloRiskPolicy.EconomicValuation||simulation.Confidence!=policy.Confidence)
            throw Error("risk_economic_policy","Choose the explicit economic full-revaluation simulation policy and matching confidence.");
        var generation=MonteCarloRisk.Generate(history,simulation,cancellation:cancellation);
        if(!generation.Available)throw Error("risk_economic_history","The complete covariance sample is unavailable; simulation cannot drop factors.");
        return Calculate(prepared,history,policy,[..generation.Samples.Select(s=>(s.Id,s.Changes))],generation,cancellation);
    }
    public static void RequireParametricAvailable()=>throw Error("risk_economic_parametric_unavailable",
        "Event-aware parametric economic risk requires separate qualification. Select historical or Monte Carlo full revaluation.");
    private static LifecycleEconomicRiskReport Calculate(ImmutableArray<PreparedLifecycleProjection> prepared,AlignedFactorHistory history,
        LifecycleEconomicRiskPolicy policy,ImmutableArray<(string Id,ImmutableArray<ExactDecimal> Changes)> samples,
        MonteCarloGeneration? generation,CancellationToken cancellation)
    {
        if((long)(1+samples.Length)*prepared.Sum(p=>p.Components.Length)>HistoricalRisk.MaximumValues)
            throw Error("risk_economic_capacity","The complete economic base/path matrix exceeds the retained value capacity.");
        var baseline=prepared.Select(p=>p.Evaluate("no_shock",[],cancellation)).SelectMany(p=>p.Values).ToImmutableArray();
        var exposure=baseline.Select((v,i)=>new HistoricalExposure(Id(i),v.Owner.BookId,v.Owner.Owner,v.Owner.BindingId,
            LifecycleEconomicRiskPolicy.Measure,v.Currency,v.OpeningValue.ToExactString())).ToImmutableArray();
        var marketExposure=exposure.Select((e,i)=>e with{Measure=LifecycleEconomicRiskPolicy.MarketOnly,
            BaseValue=baseline[i].ResidualValue.Add(baseline[i].PeriodCash).ToExactString()}).ToImmutableArray();
        var observations=ImmutableArray.CreateBuilder<HistoricalObservation>(samples.Length);
        foreach(var sample in samples)
        {
            cancellation.ThrowIfCancellationRequested();
            if(sample.Changes.Length!=history.Columns.Length)throw Error("risk_economic_samples","Each path must retain every prepared factor exactly once.");
            var shifts=history.Columns.Select((column,i)=>new QuoteShift(column.Mapping.Factor.Input,column.Mapping.Factor.Quote,
                column.Mapping.Factor.Unit,sample.Changes[i],QuoteShift.Convention)).ToImmutableArray();
            var values=prepared.SelectMany(p=>p.Evaluate(sample.Id,[..shifts.Where(s=>p.Factors.Any(f=>f.Input==s.Input))],cancellation).Values).ToArray();
            if(values.Length!=baseline.Length||values.Where((v,i)=>v.Owner!=baseline[i].Owner||v.Currency!=baseline[i].Currency).Any())
                throw Error("risk_economic_samples","A path changed the prepared economic owner or currency scope.");
            observations.Add(new(sample.Id,[..values.Select((v,i)=>new HistoricalScenarioValue(Id(i),v.ResidualValue.Add(v.PeriodCash).ToExactString()))]));
        }
        var expected=samples.Select(s=>s.Id).ToImmutableArray();var complete=observations.ToImmutable();
        var economic=EmpiricalRisk.Calculate(history.Id,expected,policy.Confidence,exposure,complete,cancellation);
        var diagnostic=EmpiricalRisk.Calculate(history.Id,expected,policy.Confidence,marketExposure,complete,cancellation);
        return new(policy,history.Id,prepared[0].Horizon,baseline,economic.Partitions,diagnostic.Partitions,samples.Length){
            MonteCarloDiagnostics=generation is null?[]:MonteCarloRisk.Calculate(generation,exposure,complete,cancellation).Diagnostics};
    }
    private static void Require(ImmutableArray<PreparedLifecycleProjection> prepared,AlignedFactorHistory history,LifecycleEconomicRiskPolicy policy,string implementation)
    {
        if(policy is null||policy.Implementation!=implementation||policy.QuantilePolicy!=HistoricalRiskPolicy.Quantile||policy.TailPolicy!=HistoricalRiskPolicy.Tail)
            throw Error("risk_economic_policy","Select the explicit economic full-revaluation method and empirical tail conventions.");
        HistoricalRisk.ValidatePolicy(new(HistoricalRiskPolicy.Version1,HistoricalRiskPolicy.Numerics,HistoricalRiskPolicy.Session,HistoricalRiskPolicy.Sampling,
            policy.QuantilePolicy,policy.TailPolicy,HistoricalRiskPolicy.Valuation,policy.Confidence));
        if(prepared.IsDefaultOrEmpty||prepared.Any(p=>p is null)||prepared.Select(p=>p.Horizon).Distinct().Count()!=1||
            prepared.Select(p=>p.SourceSnapshot.ValuationAt).Distinct().Count()!=1||prepared.Select(p=>p.SourceSnapshot.Knowledge).Distinct().Count()!=1||
            prepared.Select(p=>p.Policy).Distinct().Count()!=1||
            prepared.SelectMany(p=>p.Components).Select(c=>c.Owner).Distinct().Count()!=prepared.Sum(p=>p.Components.Length))
            throw Error("risk_economic_scope","Every market partition must retain one common opening, certified cutoff, next close and complete distinct owners.");
        if(prepared[0].Policy.CashAccumulation==LifecycleProjectionPolicy.Accumulated&&prepared.SelectMany(p=>p.CashCurveIdentities)
            .GroupBy(p=>p.Key).Any(g=>g.Select(p=>p.Value).Distinct().Count()!=1))
            throw Error("risk_economic_scope","Accumulated cash must use one explicitly selected retained curve version per currency across the portfolio.");
        var source=prepared[0].SourceSnapshot;
        if(history is null||!history.Available||history.Samples.Length<2||history.Forecast.ContextId!=source.Knowledge!.ContextId||
            history.Forecast.KnownAt!=source.Knowledge.KnownAt||history.Forecast.ValuationAt!=source.ValuationAt||
            prepared.Any(p=>!history.Forecast.Markets.Contains(p.Market))||
            history.Columns.IsDefault||history.Columns.Select(c=>c.Mapping.Factor.Input).Distinct().Count()!=history.Columns.Length)
            throw Error("risk_economic_history","Supply complete aligned native factor history from the exact opening forecast.");
        var factors=Factors(prepared);
        if(factors.Length!=history.Columns.Length||history.Columns.Any(c=>!factors.Any(f=>SameFactor(f,c.Mapping.Factor))))
            throw Error("risk_economic_history","Prepare the full transition and every residual branch factor union before loading history or generating paths.");
    }
    private static bool SameFactor(QuoteFactor a,QuoteFactor b)=>a.Input==b.Input&&a.Quote==b.Quote&&a.Unit==b.Unit&&a.Contract==b.Contract&&
        a.Observation==b.Observation&&a.Source==b.Source&&a.Role==b.Role&&a.BaseValue==b.BaseValue&&a.TradingTick==b.TradingTick;
    private static string Id(int n)=>"economic_"+n.ToString("D5",System.Globalization.CultureInfo.InvariantCulture);
    private static MarketDataException Error(string code,string message)=>new(code,message);
}
