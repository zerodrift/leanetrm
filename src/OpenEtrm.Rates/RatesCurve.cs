using System.Collections.Immutable;
using OpenEtrm.MarketData;
using OpenEtrm.MarketData.Curves;

namespace OpenEtrm.Rates;

public sealed record DiscountFactor(ExactDecimal Value,ReferenceKey Currency,DateOnly ReferenceDate,DateOnly Maturity,
    Guid SnapshotId,string NumericalPolicy,string InterpolationPolicy,ImmutableArray<ZeroRateValue> Pillars);

// One immutable input build with operation-local lookup reuse. The selected
// policy is explicit; this adapter introduces no new convention or rate risk.
public sealed class RatesCurve:CurveBase<DateOnly,DiscountFactor>
{
    private readonly ZeroRateLookup rates;
    private readonly System.Collections.Concurrent.ConcurrentDictionary<DateOnly,DiscountFactor> resolved=new();
    public override string Policy=>DiscountInterpolation.Policy;
    public ZeroRateBuild Build=>rates.Build;
    public RatesCurve(ZeroRateDefinition definition,MarketSnapshot snapshot,string interpolationPolicy):base(definition?.Key??throw new MarketDataException("discount_curve","Supply an exact rate definition."))
    {
        if(interpolationPolicy!=Policy)throw new MarketDataException("discount_curve","Select the supported explicit log-discount policy.");
        rates=new(new ZeroRateCurve(definition).Build(snapshot));
    }
    public override ImmutableArray<CurveDomain> Coverage=>[..Build.Values.Select(p=>new CurveDomain(p.Maturity,p.Maturity,true))
        .Concat(Build.Values.Zip(Build.Values.Skip(1)).Where(p=>p.Second.Maturity.DayNumber-p.First.Maturity.DayNumber<=DiscountInterpolation.MaximumPillarDays)
            .Select(p=>new CurveDomain(p.First.Maturity,p.Second.Maturity,true)))];
    private ImmutableArray<ZeroRateValue> Pillars(DateOnly maturity)
    {
        var index=rates.FindPillar(maturity);
        if(index>=0)return [Build.Values[index]];
        var right=~index;
        if(right==0||right==Build.Values.Length)throw new MarketDataException("discount_coverage","Maturity must be a stored pillar or bracketed by two pillars; extrapolation is unsupported.");
        return [Build.Values[right-1],Build.Values[right]];
    }
    public override void ValidateQuery(DateOnly request)
    {
        var pillars=Pillars(request);
        if(pillars.Length==2&&pillars[1].Maturity.DayNumber-pillars[0].Maturity.DayNumber>DiscountInterpolation.MaximumPillarDays)
            throw new MarketDataException("discount_interpolation","The requested bracket exceeds the supported interpolation span.");
    }
    public override ImmutableArray<CurveInputReference> RequiredInputs(DateOnly request)
    {ValidateQuery(request);return [..Pillars(request).Select(p=>new RatePillarInput(p.Series,p.Maturity,p.Observation))];}
    public override DiscountFactor Evaluate(DateOnly maturity)
    {
        if(resolved.TryGetValue(maturity,out var prior))return prior;
        var pillars=Pillars(maturity);
        var factor=pillars.Length==1?ExactDecimal.Parse(pillars[0].DiscountFactor):DiscountInterpolation.Factor(
            pillars[0].Maturity,ExactDecimal.Parse(pillars[0].DiscountFactor),pillars[1].Maturity,ExactDecimal.Parse(pillars[1].DiscountFactor),maturity,Policy);
        var result=new DiscountFactor(factor,Build.Currency,Build.ReferenceDate,maturity,Build.SnapshotId,Build.NumericalPolicy,Policy,pillars);
        return resolved.GetOrAdd(maturity,result);
    }
}
