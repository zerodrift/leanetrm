using System.Collections.Immutable;
using OpenEtrm.MarketData;
using OpenEtrm.MarketData.Curves;

namespace OpenEtrm.Rates;

public sealed record DiscountFactor(ExactDecimal Value,ReferenceKey Currency,DateOnly ReferenceDate,DateOnly Maturity,
    Guid SnapshotId,string NumericalPolicy,RateConventionSet Conventions,ImmutableArray<ZeroRateValue> Pillars);

// One immutable pillar build with operation-local lookup reuse. The curve's own conventions select interpolation between
// pillars, the exact t_0 anchor before the first, and extrapolation after the last; this adapter adds none of its own.
public sealed record RatesCurvePolicy(RateConventionSet Conventions)
    :CurveEvaluationPolicy($"{Conventions.Interpolation}/{Conventions.Extrapolation}",RateCurveMath.Policy);
public sealed record RatesCurveSelection(DateOnly Maturity,ImmutableArray<ZeroRateValue> Pillars,ExactDecimal Factor);

public sealed class RatesCurve:CurveBase<DateOnly,DiscountFactor,RatesCurveSelection>
{
    private static readonly DateOnly LastSupported=new(2199,12,31);
    private readonly System.Collections.Concurrent.ConcurrentDictionary<DateOnly,DiscountFactor> resolved=new();
    public ZeroRateBuild Build{get;}
    public override CurveEvaluationPolicy EvaluationPolicy{get;}
    public RatesCurve(RateCurveDefinition definition,MarketSnapshot snapshot):base(definition?.Key??throw new MarketDataException("discount_curve","Supply an exact rate definition."))
    {
        Build=new ZeroRateCurve(definition).Build(snapshot);
        EvaluationPolicy=new RatesCurvePolicy(definition.Conventions);
    }
    // From t_0 through each pillar while adjacent brackets stay within the supported span, and after the last pillar when the
    // curve extrapolates.
    public override ImmutableArray<CurveDomain> Coverage
    {
        get
        {
            var dates=Build.Values.Select(p=>p.Maturity).Prepend(Build.ReferenceDate).ToArray();
            var domains=dates.Zip(dates.Skip(1)).Where(p=>p.Second.DayNumber-p.First.DayNumber<=DiscountInterpolation.MaximumPillarDays)
                .Select(p=>new CurveDomain(p.First,p.Second,true)).ToImmutableArray();
            return Build.Conventions.Extrapolation=="none"?domains:domains.Add(new(dates[^1],LastSupported,true));
        }
    }
    protected override RatesCurveSelection ResolveQuery(DateOnly maturity)
    {
        var discount=RateCurveMath.Discount(Build.ReferenceDate,[..Build.Values.Select(p=>new RatePillarRate(p.Maturity,p.DecimalRate))],maturity,Build.Conventions);
        return new(maturity,[..discount.UsedPillars.Select(i=>Build.Values[i])],discount.Factor);
    }
    protected override ImmutableArray<CurveInputReference> RequiredInputs(RatesCurveSelection selection)=>
        [..selection.Pillars.Select(p=>new RatePillarInput(p.Series,p.Maturity,p.Observation))];
    protected override DiscountFactor EvaluateResolved(RatesCurveSelection selection)=>
        resolved.GetOrAdd(selection.Maturity,_=>new DiscountFactor(selection.Factor,Build.Currency,Build.ReferenceDate,selection.Maturity,
            Build.SnapshotId,Build.NumericalPolicy,Build.Conventions,selection.Pillars));
}
