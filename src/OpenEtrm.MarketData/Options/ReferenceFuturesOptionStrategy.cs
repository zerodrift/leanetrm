using System.Collections.Immutable;
using OpenEtrm.MarketData.Trading;
using OpenEtrm.Shared.Numerics;

namespace OpenEtrm.MarketData.Options;

internal sealed record ReferenceOptionStrategyValue(LatticeDecimal Vanilla,LatticeDecimal Contract,LatticeDecimal Continuation,string Mode);

// Both selected strategies use the same calibrated reference stopping tree.
// Components are root-present stopping payoffs, so rollback takes expectations
// without adding a second discount factor.
internal static class ReferenceFuturesOptionStrategy
{
    private static readonly LatticeDecimal Zero=LatticeDecimal.Parse("0"),One=LatticeDecimal.Parse("1");
    // The lifecycle projection (D401) and its cash and physical reference strategies are qualified for American
    // exercise. A series of another style is refused by name until it is qualified on its own (D446).
    internal static void RequireQualifiedStyle(FuturesOptionTerms terms)
    {
        if(terms.ExerciseStyle!=FuturesOptionTerms.American)
            throw new MarketDataException("projection_option_style",
                $"The lifecycle projection and its reference strategies are qualified for American exercise only; the series states '{terms.ExerciseStyle}'.");
    }
    internal static ImmutableArray<FuturesMarginOccurrence> NodeMargins(FuturesOptionTerms terms,FuturesMarginPolicy margin,
        DateTimeOffset at,OptionNumericalPolicy numerical)
    {
        if(terms is not null)RequireQualifiedStyle(terms);
        if(terms is null||numerical is null||numerical.Steps<1||!TradingInstant.IsSupported(at)||
            !TradingInstant.IsSupported(terms.ExpiryAt)||at>=terms.ExpiryAt)throw new MarketDataException("option_reference_strategy","Retain exact option clocks, numerical grid and saved margin schedule.");
        var ticks=terms.ExpiryAt.UtcTicks-at.UtcTicks;
        return [..Enumerable.Range(0,numerical.Steps+1).Select(step=>
            FuturesMarginSchedule.NextAfterExecution(margin,at,new SignedRatio((System.Numerics.BigInteger)ticks*step,numerical.Steps)))];
    }

    internal static ReferenceOptionStrategyValue Price(FuturesOptionTerms terms,LatticeDecimal futures,OptionCalibration calibration,
        LatticeDecimal referenceExpiryDiscount,DateTimeOffset at,OptionNumericalPolicy numerical,FuturesOptionAutomaticRule rule,
        PhysicalOptionTerminal terminal,string mode,string intrinsicMode,
        Func<int,LatticeDecimal,(LatticeDecimal Reference,LatticeDecimal Delivery)> Exercise,CancellationToken ct)
    {
        var strike=LatticeDecimal.From(terms.Strike);var sign=terms.Right==OptionRight.Call?One:Zero-One;
        bool AtExpiry(LatticeDecimal underlying)=>terminal==PhysicalOptionTerminal.Exercise||terminal==PhysicalOptionTerminal.Automatic&&
            rule.Decide(terms.Right,strike,underlying)=="exercised";
        if(calibration is IntrinsicFallback fallback)
        {
            ct.ThrowIfCancellationRequested();
            var intrinsic=LatticeDecimal.Max(sign*(futures-strike),Zero);
            var vanilla=LatticeDecimal.Max(intrinsic+fallback.Residual,Zero);
            var payoff=intrinsic.CompareTo(Zero)>0?Exercise(0,futures):AtExpiry(futures)?Exercise(numerical.Steps,futures):(Zero,Zero);
            return new(vanilla,payoff.Item1,payoff.Item2,intrinsicMode);
        }
        if(calibration is not SolvedVolatility solved)throw new MarketDataException("option_reference_strategy","Retain the supported calibrated reference model.");
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
        return new(reference.Root,result.Reference,result.Delivery,mode);
    }
}
