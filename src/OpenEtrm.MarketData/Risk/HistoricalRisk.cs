using System.Collections.Immutable;
using System.Numerics;
using OpenEtrm.Shared.Numerics;

namespace OpenEtrm.MarketData.Risk;

[System.Text.Json.Serialization.JsonUnmappedMemberHandling(System.Text.Json.Serialization.JsonUnmappedMemberHandling.Disallow)]
public sealed record HistoricalRiskPolicy(string Convention,string NumericalPolicy,string Horizon,string SamplePolicy,string QuantilePolicy,
    string TailPolicy,string ValuationPolicy,string Confidence)
{
    public const string Version1="dated_factor_full_pv_historical_v1",Numerics="exact_empirical_probability_mass_v1",
        Session=ParametricRiskPolicy.Session,Sampling="equal_weight_raw_joint_changes_v1",Quantile="empirical_inverse_cdf_v1",
        Tail="signed_upper_loss_fractional_mass_uniform_ties_v1",Valuation="current_instrument_pv_frozen_nonfactor_inputs_v1";
}
public sealed record HistoricalExposure(string Id,Guid BookId,Guid TradeId,Guid BindingId,string Measure,ReferenceKey Currency,string BaseValue);
public sealed record HistoricalScenarioValue(string ExposureId,string Value);
public sealed record HistoricalObservation(string Id,ImmutableArray<HistoricalScenarioValue> Values);
public sealed record HistoricalContribution(Guid Id,string BaseValue,string ValueAtRisk,string ExpectedShortfall);
public sealed record HistoricalLoss(string Id,string Value,string Loss,string VarWeight,string EsWeight);
public sealed record HistoricalHistogramBin(string Lower,string Upper,int Count,string Probability);
public sealed record HistoricalPartition(string Measure,ReferenceKey Currency,string BaseValue,string Mean,string ValueAtRisk,string ExpectedShortfall,
    string TailSampleMass,int QuantileTies,ImmutableArray<HistoricalContribution> Books,ImmutableArray<HistoricalContribution> Trades,
    ImmutableArray<HistoricalLoss> Samples,ImmutableArray<HistoricalHistogramBin> Histogram);
public sealed record HistoricalRiskReport(HistoricalRiskPolicy Policy,string HistoryId,int Samples,ImmutableArray<HistoricalPartition> Partitions,
    bool Available,ImmutableArray<string> UnavailableReasons);

/// <summary>Reduces complete full-PV observations. Every row keeps its equal probability mass, including duplicates and zeros.</summary>
public static class HistoricalRisk
{
    public const int MaximumSamples=2048,MaximumFactors=128,MaximumExposures=4096,MaximumValues=250000;
    public static void Admit(int factors,int samples)
    {
        if(factors is <1 or >MaximumFactors||samples is <0 or >MaximumSamples)
            throw Error("risk_historical_capacity","The complete historical matrix admits at most 128 factors and 2048 common changes.");
    }
    public static void ValidatePolicy(HistoricalRiskPolicy policy)
    {
        if(policy is null||policy.Convention!=HistoricalRiskPolicy.Version1||policy.NumericalPolicy!=HistoricalRiskPolicy.Numerics||
            policy.Horizon!=HistoricalRiskPolicy.Session||policy.SamplePolicy!=HistoricalRiskPolicy.Sampling||policy.QuantilePolicy!=HistoricalRiskPolicy.Quantile||
            policy.TailPolicy!=HistoricalRiskPolicy.Tail||policy.ValuationPolicy!=HistoricalRiskPolicy.Valuation)
            throw Error("risk_historical_policy","Choose the explicit one-session, frozen-input, equal-weight full-PV empirical risk contract.");
        SignedRatio confidence;
        try{confidence=Number(ExactDecimal.Parse(policy.Confidence??"").ToString());}
        catch(MarketDataException error){throw new MarketDataException("risk_historical_policy","Confidence must be a plain decimal with at most 18 fractional digits.",error);}
        if(confidence.CompareTo(new(1,2))<=0||confidence.CompareTo(new(9999,10000))>0)
            throw Error("risk_historical_policy","Choose confidence greater than 0.5 and at most 0.9999.");
    }
    public static HistoricalRiskReport Calculate(string historyId,ImmutableArray<string> expectedSamples,HistoricalRiskPolicy policy,
        ImmutableArray<HistoricalExposure> exposures,ImmutableArray<HistoricalObservation> observations,CancellationToken cancellation=default)
    {
        cancellation.ThrowIfCancellationRequested();ValidatePolicy(policy);
        if(!expectedSamples.IsDefault&&expectedSamples.Length>MaximumSamples)
            throw Error("risk_historical_samples","Historical simulation admits at most 2048 captured sample rows.");
        var result=EmpiricalRisk.Calculate(historyId,expectedSamples,policy.Confidence,exposures,observations,cancellation);
        return new(policy,result.HistoryId,result.Samples,result.Partitions,true,[]);
    }
    private static SignedRatio Number(string value){var(n,d)=ExactNumberText.Parse(value);return new(n,d);}
    private static MarketDataException Error(string code,string message)=>new(code,message);
}
