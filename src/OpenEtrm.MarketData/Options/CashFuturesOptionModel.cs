using System.Collections.Immutable;
using OpenEtrm.MarketData.Trading;

namespace OpenEtrm.MarketData.Options;

[System.Text.Json.Serialization.JsonUnmappedMemberHandling(System.Text.Json.Serialization.JsonUnmappedMemberHandling.Disallow)]
public sealed record CashFuturesOptionPolicy(
    [property:System.Text.Json.Serialization.JsonRequired]string Implementation,
    [property:System.Text.Json.Serialization.JsonRequired]string Stopping,
    [property:System.Text.Json.Serialization.JsonRequired]string Calibration,
    [property:System.Text.Json.Serialization.JsonRequired]string IntrinsicStopping,
    [property:System.Text.Json.Serialization.JsonRequired]string Funding)
{
    public const string Version="cash_futures_option_reference_strategy_v1";
    public const string ReferenceStopping="reference_american_stopping_then_hold_cash_future_v1";
    public const string ReferenceCalibration=PhysicalOptionPolicy.ReferenceCalibration;
    public const string IntrinsicStrategy=PhysicalOptionPolicy.IntrinsicStrategy;
}
public sealed record CashFuturesOptionValue(LatticeDecimal VanillaReference,LatticeDecimal Total,string Mode);

public static class CashFuturesOptionModel
{
    private static readonly LatticeDecimal Zero=LatticeDecimal.Parse("0"),One=LatticeDecimal.Parse("1");
    public static void RequirePolicy(CashFuturesOptionPolicy policy)
    {
        if(policy is null||policy.Implementation!=CashFuturesOptionPolicy.Version||policy.Stopping!=CashFuturesOptionPolicy.ReferenceStopping||
            policy.Calibration!=CashFuturesOptionPolicy.ReferenceCalibration||policy.IntrinsicStopping!=CashFuturesOptionPolicy.IntrinsicStrategy||
            policy.Funding!=FuturesMarginValueBinding.FlatReference)throw Error();
    }
    public static ImmutableArray<FuturesMarginOccurrence> RequiredMarginOccurrences(FuturesOptionTerms terms,FuturesMarginPolicy margin,
        DateTimeOffset at,OptionNumericalPolicy numerical)=>
        [..ReferenceFuturesOptionStrategy.NodeMargins(terms,margin,at,numerical).DistinctBy(o=>o.Id).OrderBy(o=>o.At).ThenBy(o=>o.Phase)];
    public static CashFuturesOptionValue Price(FuturesOptionTerms terms,LatticeDecimal futures,OptionCalibration calibration,
        LatticeDecimal referenceExpiryDiscount,DateTimeOffset at,OptionNumericalPolicy numerical,CashFuturesOptionPolicy policy,
        FuturesOptionAutomaticRule rule,PhysicalOptionTerminal terminal,FuturesMarginPolicy margin,
        ImmutableArray<PhysicalOptionMarginDiscount> marginDiscounts,CancellationToken ct)
    {
        RequirePolicy(policy);ct.ThrowIfCancellationRequested();
        if(terms is null||numerical is null||calibration is not(SolvedVolatility or IntrinsicFallback)||rule is null||!Enum.IsDefined(terminal)||
            margin?.Implementation is not(FuturesMarginPolicy.Version or FuturesMarginPolicy.DeliveryVersion)||margin.Delivery is not null)throw Error();
        ReferenceFuturesOptionStrategy.RequireQualifiedStyle(terms);
        _=AmericanFuturesOptionModel.ValidateInputs(terms,futures,referenceExpiryDiscount,at,numerical);
        _=rule.Decide(terms.Right,terms.Strike,terms.Strike);
        var nodes=ReferenceFuturesOptionStrategy.NodeMargins(terms,margin,at,numerical);var required=nodes.DistinctBy(o=>o.Id).ToArray();
        if(marginDiscounts.IsDefault||marginDiscounts.Length!=required.Length||marginDiscounts.Any(d=>d is null)||
            marginDiscounts.Select(d=>d.OccurrenceId).Distinct().Count()!=required.Length||required.Any(o=>
                marginDiscounts.Count(d=>d.OccurrenceId==o.Id&&d.DueAt==o.DueAt&&d.Factor.CompareTo(Zero)>0)!=1))throw Error();
        var factors=marginDiscounts.ToImmutableDictionary(d=>d.OccurrenceId,d=>d.Factor);
        var strike=LatticeDecimal.From(terms.Strike);var sign=terms.Right==OptionRight.Call?One:Zero-One;
        var value=ReferenceFuturesOptionStrategy.Price(terms,futures,calibration,referenceExpiryDiscount,at,numerical,rule,terminal,
            policy.Stopping,policy.IntrinsicStopping,(step,price)=>(sign*(price-strike)*factors[nodes[step].Id],Zero),ct);
        return new(value.Vanilla,value.Contract,value.Mode);
    }
    private static MarketDataException Error()=>new("cash_option_policy",
        "Select the supported cash option reference strategy, calibration, exact native cash-future schedule and every saved possible margin due.");
}
