using System.Collections.Immutable;
using OpenEtrm.MarketData.Trading;

namespace OpenEtrm.MarketData.Options;

// This is a selected holding strategy, not an optimal composite exercise model.
// The quoted vanilla calibrates the A process; B follows the declared frozen basis.
[System.Text.Json.Serialization.JsonUnmappedMemberHandling(System.Text.Json.Serialization.JsonUnmappedMemberHandling.Disallow)]
public sealed record PhysicalOptionPolicy(
    [property:System.Text.Json.Serialization.JsonRequired]string Implementation,
    [property:System.Text.Json.Serialization.JsonRequired]string BasisLaw,
    [property:System.Text.Json.Serialization.JsonRequired]string Stopping,
    [property:System.Text.Json.Serialization.JsonRequired]string Calibration,
    [property:System.Text.Json.Serialization.JsonRequired]string IntrinsicStopping)
{
    public const string Version="physical_futures_option_reference_strategy_v1";
    public const string FrozenBasis="frozen_quoted_asset_minus_futures_basis_v1";
    public const string ReferenceStopping="reference_american_stopping_then_hold_delivery_v1";
    public const string ReferenceCalibration="quoted_vanilla_calibrates_reference_futures_v1";
    public const string IntrinsicStrategy="intrinsic_now_else_flat_reference_to_expiry_v1";
}
public enum PhysicalOptionTerminal { Automatic=1,Exercise,Abandon }
public sealed record PhysicalOptionMarginDiscount(Guid OccurrenceId,DateTimeOffset DueAt,LatticeDecimal Factor);
public sealed record PhysicalOptionCarrying(DateTimeOffset DeliveryAt,DateTimeOffset PrincipalDueAt,
    LatticeDecimal AssetFactor,LatticeDecimal PrincipalFactor,ImmutableArray<PhysicalOptionMarginDiscount> MarginDiscounts);
public sealed record PhysicalOptionValue(LatticeDecimal VanillaReference,LatticeDecimal ContractReference,
    LatticeDecimal DeliveryContinuation,LatticeDecimal Total,string Mode);

public static class PhysicalFuturesOptionModel
{
    private static readonly LatticeDecimal Zero=LatticeDecimal.Parse("0"),One=LatticeDecimal.Parse("1");
    public static void RequirePolicy(PhysicalOptionPolicy policy)
    {
        if(policy is null||policy.Implementation!=PhysicalOptionPolicy.Version||policy.BasisLaw!=PhysicalOptionPolicy.FrozenBasis||
            policy.Stopping!=PhysicalOptionPolicy.ReferenceStopping||policy.Calibration!=PhysicalOptionPolicy.ReferenceCalibration||
            policy.IntrinsicStopping!=PhysicalOptionPolicy.IntrinsicStrategy)throw Error();
    }

    // Every possible exercise time on the selected numerical grid has one
    // explicit next due. The provider resolves these exact saved due inputs.
    public static ImmutableArray<FuturesMarginOccurrence> RequiredMarginOccurrences(FuturesOptionTerms terms,FuturesMarginPolicy margin,
        DateTimeOffset at,OptionNumericalPolicy numerical)=>
        [..ReferenceFuturesOptionStrategy.NodeMargins(terms,margin,at,numerical).DistinctBy(o=>o.Id).OrderBy(o=>o.At).ThenBy(o=>o.Phase)];

    public static PhysicalOptionValue Price(FuturesOptionTerms terms,LatticeDecimal futures,LatticeDecimal asset,
        OptionCalibration calibration,LatticeDecimal referenceExpiryDiscount,DateTimeOffset at,OptionNumericalPolicy numerical,
        PhysicalOptionPolicy policy,FuturesOptionAutomaticRule rule,PhysicalOptionTerminal terminal,
        FuturesMarginPolicy margin,PhysicalOptionCarrying carrying,CancellationToken ct)
    {
        RequirePolicy(policy);
        if(terms is null||numerical is null||rule is null||!Enum.IsDefined(terminal)||
            calibration is not(SolvedVolatility or IntrinsicFallback)||carrying is null||!TradingInstant.IsSupported(carrying.DeliveryAt)||!TradingInstant.IsSupported(carrying.PrincipalDueAt)||
            carrying.PrincipalDueAt<carrying.DeliveryAt||carrying.AssetFactor.CompareTo(Zero)<=0||carrying.PrincipalFactor.CompareTo(Zero)<=0||
            margin?.Implementation!=FuturesMarginPolicy.DeliveryVersion||margin.Delivery?.Terms is null||margin.Delivery.Terms.From!=carrying.DeliveryAt)
            throw Error();
        ct.ThrowIfCancellationRequested();
        _=AmericanFuturesOptionModel.ValidateInputs(terms,futures,referenceExpiryDiscount,at,numerical);
        _=rule.Decide(terms.Right,terms.Strike,terms.Strike);
        var nodeMargins=ReferenceFuturesOptionStrategy.NodeMargins(terms,margin,at,numerical);var required=nodeMargins.DistinctBy(o=>o.Id).ToArray();
        if(margin.Occurrences.Single(o=>o.Phase==FuturesMarginPhase.Final).At>carrying.DeliveryAt||
            carrying.DeliveryAt<terms.ExpiryAt||carrying.MarginDiscounts.IsDefault||carrying.MarginDiscounts.Any(d=>d is null)||carrying.MarginDiscounts.Length!=required.Length||
            carrying.MarginDiscounts.Select(d=>d.OccurrenceId).Distinct().Count()!=required.Length||required.Any(o=>
                carrying.MarginDiscounts.Count(d=>d.OccurrenceId==o.Id&&d.DueAt==o.DueAt&&d.Factor.CompareTo(Zero)>0)!=1))throw Error();
        var discounts=carrying.MarginDiscounts.ToImmutableDictionary(d=>d.OccurrenceId,d=>d.Factor);
        var strike=LatticeDecimal.From(terms.Strike);var sign=terms.Right==OptionRight.Call?One:Zero-One;
        var basis=asset-futures;
        (LatticeDecimal Reference,LatticeDecimal Delivery) Exercise(int step,LatticeDecimal underlying)
        {
            var occurrence=nodeMargins[step];
            return (sign*(underlying-strike)*discounts[occurrence.Id],
                sign*((underlying+basis)*carrying.AssetFactor-underlying*carrying.PrincipalFactor));
        }
        var value=ReferenceFuturesOptionStrategy.Price(terms,futures,calibration,referenceExpiryDiscount,at,numerical,rule,terminal,
            policy.Stopping,policy.IntrinsicStopping,Exercise,ct);
        return new(value.Vanilla,value.Contract,value.Continuation,value.Contract+value.Continuation,value.Mode);
    }

    private static MarketDataException Error()=>new("physical_option_policy",
        "Select the supported physical option law, reference calibration/stopping strategy, exact delivery carrying and every saved possible margin due.");
}
