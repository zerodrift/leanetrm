using System.Collections.Concurrent;
using System.Collections.Immutable;
using OpenEtrm.MarketData;
using OpenEtrm.MarketData.Calculation;
using OpenEtrm.MarketData.Trading;

namespace OpenEtrm.Rates;

// Pure cash created by a qualified event has a deterministic due key but no
// native membership yet. Resolve that key from this fully captured term
// structure; no source reads or tenor rolls occur inside a scenario.
public sealed class LifecycleCurrencyDiscounting:IProjectionCurrencyCurve
{
    public ReferenceKey Currency=>mapping.Currency;
    public MarketSnapshot SourceSnapshot{get;}
    public ProjectionCurrencyCurveKey CurveIdentity{get;}
    public string TimingPolicy{get;}
    private readonly CurrencyDiscountCurve mapping;
    private readonly ZeroRateBuild build;
    private readonly ImmutableArray<RatePillarRate> rates;
    private readonly ConcurrentDictionary<(SidePaymentKey,DateTimeOffset),SidePaymentFactor> factors=new();
    public LifecycleCurrencyDiscounting(CurrencyDiscountCurve mapping,MarketSnapshot source,string timingPolicy,string clockPolicy)
    {
        if(mapping is null||mapping.Id==Guid.Empty||mapping.Definition is null||mapping.Definition.DefinitionId==Guid.Empty||
            mapping.Definition.DefinitionVersion<1||mapping.Currency!=mapping.Definition.Currency||timingPolicy!=RateDiscountTiming.Version||
            clockPolicy!=ClockChange.FrozenInputs)
            throw new MarketDataException("projection_cash_policy","Select a captured native currency curve, exact-instant timing and frozen-input clock policy.");
        this.mapping=mapping;SourceSnapshot=source;TimingPolicy=timingPolicy;
        CurveIdentity=new(mapping.Definition.Key,mapping.Id,mapping.Definition.DefinitionId,mapping.Definition.DefinitionVersion);
        build=new ZeroRateCurve(mapping.Definition).Build(source);
        rates=[..build.Values.Select(v=>new RatePillarRate(v.Maturity,v.DecimalRate))];
    }
    public ImmutableArray<SidePaymentFactor> Resolve(ImmutableArray<SidePaymentKey> payments,MarketSnapshot source)=>
        ResolveForValuation(payments,source,source.ValuationAt,ClockChange.FrozenInputs);
    public ImmutableArray<SidePaymentFactor> ResolveForValuation(ImmutableArray<SidePaymentKey> payments,MarketSnapshot source,
        DateTimeOffset calculationAt,string? clockPolicy)
    {
        if(!SourceSnapshot.HasSameInputs(source)||payments.IsDefault||payments.Distinct().Count()!=payments.Length||
            !TradingInstant.IsSupported(calculationAt)||calculationAt<source.ValuationAt||clockPolicy!=ClockChange.FrozenInputs&&(calculationAt!=source.ValuationAt||clockPolicy is not null))
            throw new MarketDataException("discount_context","Retain this exact captured curve and explicit forward calculation clock.");
        if(payments.Any(p=>p.Currency!=Currency||p.SideId==Guid.Empty||p.Ordinal<0||!TradingInstant.IsSupported(p.PaymentAt)))
            throw new MarketDataException("side_payment_factors","Supply exact distinct due keys in the selected currency.");
        if(payments.Any(p=>p.PaymentAt<=calculationAt))
            throw new MarketDataException("valuation_time_event","A due obligation requires lifecycle treatment rather than a discount factor.");
        return [..payments.Select(payment=>factors.GetOrAdd((payment,calculationAt),key=>
        {
            var timing=new RateDiscountTiming(key.Item2,payment.PaymentAt,TimingPolicy);
            var result=RateCurveMath.InstantDiscount(build.ReferenceDate,rates,timing,build.Conventions);
            var evidence=new RateDiscountDefinition(mapping.Id,mapping.Definition.Key,Currency,mapping.Definition.Source,build.ReferenceDate,
                DateOnly.FromDateTime(payment.PaymentAt.UtcDateTime),build.Conventions,build.NumericalPolicy,
                [..result.UsedPillars.Select(i=>new RateDiscountPillar(build.Values[i].Maturity,SourceSnapshot.Selections.Single(s=>
                    s.Input==new InputKey(build.Values[i].Series,InputUse.Variable)&&s.Observation.Key==build.Values[i].Observation).Observation))])
                {Timing=timing,CurveTerms=new(mapping.Definition.DefinitionId,mapping.Definition.DefinitionVersion,
                    [..mapping.Definition.Tenors.Select(t=>new RateDiscountTenor(t.Series,t.Maturity))])};
            return new SidePaymentFactor(payment,result.Factor,SourceSnapshot,DiscountFactorSource.CurrencyDefault){RateDefinition=evidence};
        }))];
    }
}
