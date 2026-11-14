using System.Collections.Immutable;
using OpenEtrm.MarketData;
using OpenEtrm.MarketData.Trading;

namespace OpenEtrm.Rates;

// Exact-instant cash chooses this convention explicitly. Date-only schedule
// discounting retains its separate selected contract.
public sealed class InstantCurrencyDiscounting:ISideDiscounting
{
    public MarketSnapshot Snapshot{get;}
    public ImmutableDictionary<SidePaymentKey,SidePaymentFactor> Inputs{get;}
    public InstantCurrencyDiscounting(ImmutableArray<SidePaymentKey> payments,MarketSnapshot snapshot,
        CurrencyDiscountCurve mapping,string timingPolicy)
    {
        if(timingPolicy!=RateDiscountTiming.Version)
            throw new MarketDataException("discount_timing","Select the explicit supported exact-instant discount convention.");
        if(snapshot is null||payments.IsDefault||payments.Distinct().Count()!=payments.Length)
            throw new MarketDataException("discount_context","Supply the exact captured context and distinct payment keys.");
        Snapshot=snapshot;
        foreach(var payment in payments)payment.RequireUndue(snapshot);
        if(mapping is null||mapping.Id==Guid.Empty||mapping.Definition is null||mapping.Definition.DefinitionId==Guid.Empty||
            mapping.Definition.DefinitionVersion<1||mapping.Currency!=mapping.Definition.Currency||
            payments.Any(p=>p.Currency!=mapping.Currency))
            throw new MarketDataException("discount_curve","Resolve the exact selected currency mapping and compatible payment keys.");
        if(payments.IsEmpty){Inputs=ImmutableDictionary<SidePaymentKey,SidePaymentFactor>.Empty;return;}
        var build=new ZeroRateCurve(mapping.Definition).Build(snapshot);
        var rates=build.Values.Select(v=>new RatePillarRate(v.Maturity,v.DecimalRate)).ToImmutableArray();
        Inputs=payments.ToImmutableDictionary(payment=>payment,payment=>
        {
            var timing=new RateDiscountTiming(snapshot.ValuationAt,payment.PaymentAt,timingPolicy);
            var instant=RateCurveMath.InstantDiscount(build.ReferenceDate,rates,timing,build.Conventions);
            var used=instant.UsedPillars.Select(i=>build.Values[i]).ToArray();
            var evidence=new RateDiscountDefinition(mapping.Id,mapping.Definition.Key,mapping.Currency,mapping.Definition.Source,
                build.ReferenceDate,DateOnly.FromDateTime(payment.PaymentAt.UtcDateTime),build.Conventions,build.NumericalPolicy,
                [..used.Select(p=>new RateDiscountPillar(p.Maturity,snapshot.Selections.Single(s=>s.Input==new InputKey(p.Series,InputUse.Variable)&&s.Observation.Key==p.Observation).Observation))])
                {Timing=timing,CurveTerms=new(mapping.Definition.DefinitionId,mapping.Definition.DefinitionVersion,
                    [..mapping.Definition.Tenors.Select(t=>new RateDiscountTenor(t.Series,t.Maturity))])};
            return new SidePaymentFactor(payment,instant.Factor,snapshot,DiscountFactorSource.CurrencyDefault){RateDefinition=evidence};
        });
    }
    public ImmutableArray<SidePaymentFactor> Resolve(ImmutableArray<SidePaymentKey> payments,MarketSnapshot snapshot)
    {
        if(!Snapshot.HasSameInputs(snapshot)||payments.IsDefault||payments.Distinct().Count()!=payments.Length||payments.Any(p=>!Inputs.ContainsKey(p)))
            throw new MarketDataException("discount_context","Request only exact payments prepared under this complete captured context.");
        return [..payments.Select(p=>Inputs[p])];
    }
}
