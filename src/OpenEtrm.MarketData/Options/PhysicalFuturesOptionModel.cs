using System.Collections.Immutable;
using OpenEtrm.MarketData.Trading;
using OpenEtrm.Shared.Numerics;

namespace OpenEtrm.MarketData.Options;

// This is a selected holding strategy, not an optimal composite exercise model.
// The quoted vanilla calibrates the A process; B follows the declared frozen basis.
public sealed record PhysicalOptionPolicy(string Implementation,string BasisLaw,string Stopping,string Calibration,string IntrinsicStopping)
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

    // Every possible exercise time on the selected numerical grid has one
    // explicit next due. The provider resolves these exact saved due inputs.
    public static ImmutableArray<FuturesMarginOccurrence> RequiredMarginOccurrences(FuturesOptionTerms terms,FuturesMarginPolicy margin,
        DateTimeOffset at,OptionNumericalPolicy numerical)=>
        [..NodeMargins(terms,margin,at,numerical).DistinctBy(o=>o.Id).OrderBy(o=>o.At).ThenBy(o=>o.Phase)];
    private static ImmutableArray<FuturesMarginOccurrence> NodeMargins(FuturesOptionTerms terms,FuturesMarginPolicy margin,
        DateTimeOffset at,OptionNumericalPolicy numerical)
    {
        if(terms is null||numerical is null||numerical.Steps<1||!TradingInstant.IsSupported(at)||
            !TradingInstant.IsSupported(terms.ExpiryAt)||at>=terms.ExpiryAt)throw Error();
        var ticks=terms.ExpiryAt.UtcTicks-at.UtcTicks;
        return [..Enumerable.Range(0,numerical.Steps+1).Select(step=>
            FuturesMarginSchedule.NextAfterExecution(margin,at,new SignedRatio((System.Numerics.BigInteger)ticks*step,numerical.Steps)))];
    }

    public static PhysicalOptionValue Price(FuturesOptionTerms terms,LatticeDecimal futures,LatticeDecimal asset,
        OptionCalibration calibration,LatticeDecimal referenceExpiryDiscount,DateTimeOffset at,OptionNumericalPolicy numerical,
        PhysicalOptionPolicy policy,FuturesOptionAutomaticRule rule,PhysicalOptionTerminal terminal,
        FuturesMarginPolicy margin,PhysicalOptionCarrying carrying,CancellationToken ct)
    {
        if(terms is null||numerical is null||policy is null||policy.Implementation!=PhysicalOptionPolicy.Version||policy.BasisLaw!=PhysicalOptionPolicy.FrozenBasis||
            policy.Stopping!=PhysicalOptionPolicy.ReferenceStopping||policy.Calibration!=PhysicalOptionPolicy.ReferenceCalibration||
            policy.IntrinsicStopping!=PhysicalOptionPolicy.IntrinsicStrategy||calibration is null||rule is null||!Enum.IsDefined(terminal)||
            carrying is null||!TradingInstant.IsSupported(carrying.DeliveryAt)||!TradingInstant.IsSupported(carrying.PrincipalDueAt)||
            carrying.PrincipalDueAt<carrying.DeliveryAt||carrying.AssetFactor.CompareTo(Zero)<=0||carrying.PrincipalFactor.CompareTo(Zero)<=0||
            margin?.Implementation!=FuturesMarginPolicy.DeliveryVersion||margin.Delivery?.Terms is null||margin.Delivery.Terms.From!=carrying.DeliveryAt)
            throw Error();
        ct.ThrowIfCancellationRequested();
        _=AmericanFuturesOptionModel.ValidateInputs(terms,futures,referenceExpiryDiscount,at,numerical);
        _=rule.Decide(terms.Right,terms.Strike,terms.Strike);
        var nodeMargins=NodeMargins(terms,margin,at,numerical);var required=nodeMargins.DistinctBy(o=>o.Id).ToArray();
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
        bool AtExpiry(LatticeDecimal underlying)=>terminal==PhysicalOptionTerminal.Exercise||terminal==PhysicalOptionTerminal.Automatic&&
            rule.Decide(terms.Right,strike,underlying)=="exercised";
        if(calibration is IntrinsicFallback fallback)
        {
            ct.ThrowIfCancellationRequested();
            var intrinsic=LatticeDecimal.Max(sign*(futures-strike),Zero);
            var vanilla=LatticeDecimal.Max(intrinsic+fallback.Residual,Zero);
            var payoff=intrinsic.CompareTo(Zero)>0?Exercise(0,futures):AtExpiry(futures)?Exercise(numerical.Steps,futures):(Zero,Zero);
            return new(vanilla,payoff.Item1,payoff.Item2,payoff.Item1+payoff.Item2,PhysicalOptionPolicy.IntrinsicStrategy);
        }
        if(calibration is not SolvedVolatility solved)throw Error();
        var lattice=AmericanFuturesOptionModel.CreateLattice(terms,futures,solved.Volatility,referenceExpiryDiscount,at,numerical);
        var reference=AmericanFuturesOptionModel.SolveReference(terms,lattice,true,ct);
        var values=new (LatticeDecimal Reference,LatticeDecimal Delivery)[lattice.Nodes(lattice.Steps)];
        for(var node=0;node<values.Length;node++)
        {
            var underlying=lattice.Underlying(lattice.Steps,node);
            if(AtExpiry(underlying))values[node]=Exercise(lattice.Steps,underlying);
        }
        for(var step=lattice.Steps-1;step>=0;step--)
        {
            ct.ThrowIfCancellationRequested();
            var current=new (LatticeDecimal Reference,LatticeDecimal Delivery)[lattice.Nodes(step)];
            for(var node=0;node<current.Length;node++)
            {
                if(reference.Chosen!.Code(step,node,FuturesOptionState.Alive)=="exercise")
                    current[node]=Exercise(step,lattice.Underlying(step,node));
                else foreach(var (successor,probability) in lattice.Next(step,node))
                {
                    current[node].Reference+=probability*values[successor].Reference;
                    current[node].Delivery+=probability*values[successor].Delivery;
                }
            }
            values=current;
        }
        // Each stopping payoff is already expressed at the root using its exact
        // cash/physical due factors. Applying lattice discount again would double carry.
        var result=values.Single();
        return new(reference.Root,result.Reference,result.Delivery,result.Reference+result.Delivery,policy.Stopping);
    }
    private static MarketDataException Error()=>new("physical_option_policy",
        "Select the supported physical option law, reference calibration/stopping strategy, exact delivery carrying and every saved possible margin due.");
}
