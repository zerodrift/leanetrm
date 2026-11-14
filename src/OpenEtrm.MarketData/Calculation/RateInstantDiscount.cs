using System.Collections.Immutable;
using OpenEtrm.MarketData.Trading;
using OpenEtrm.Shared.Numerics;

namespace OpenEtrm.MarketData;

public sealed record RateDiscountTiming(DateTimeOffset CalculationAt,DateTimeOffset PaymentAt,string Implementation)
{
    public const string Version="linear_utc_day_discount_ratio_v1";
}
public sealed record RateDatedDiscount(DateOnly Date,ExactDecimal Factor,ImmutableArray<int> UsedPillars);
public sealed record RateInstantDiscount(ExactDecimal Factor,ImmutableArray<int> UsedPillars,ImmutableArray<RateDatedDiscount> Nodes);

public static partial class RateCurveMath
{
    public static RateInstantDiscount InstantDiscount(DateOnly reference,ImmutableArray<RatePillarRate> pillars,
        RateDiscountTiming timing,RateConventionSet conventions)
    {
        if(timing is null||timing.Implementation!=RateDiscountTiming.Version)
            throw new MarketDataException("discount_timing","Select the explicit supported exact-instant discount convention.");
        var origin=new DateTimeOffset(reference.ToDateTime(TimeOnly.MinValue),TimeSpan.Zero);
        if(reference.Year is <1901 or >2199||!TradingInstant.IsSupported(timing.CalculationAt)||
            !TradingInstant.IsSupported(timing.PaymentAt)||timing.CalculationAt<origin||timing.PaymentAt<=timing.CalculationAt||
            timing.PaymentAt.Year>2199)
            throw new MarketDataException("discount_context","Supply supported exact UTC instants after the curve origin and strictly before payment.");
        var nodes=new Dictionary<DateOnly,RateDatedDiscount>();
        RateDatedDiscount Node(DateOnly date)
        {
            if(nodes.TryGetValue(date,out var found))return found;
            if(date.Year>2199)throw new MarketDataException("discount_coverage","The exact-instant bracket exceeds supported dated coverage.");
            var value=Discount(reference,pillars,date,conventions);
            if(value.Factor.CompareTo(default)<=0)throw new MarketDataException("discount_curve","Dated discount factors must be positive.");
            var node=new RateDatedDiscount(date,value.Factor,value.UsedPillars);nodes.Add(date,node);return node;
        }
        SignedRatio At(DateTimeOffset instant)
        {
            var date=DateOnly.FromDateTime(instant.UtcDateTime);var left=Node(date);
            var midnight=new DateTimeOffset(date.ToDateTime(TimeOnly.MinValue),TimeSpan.Zero);
            var elapsed=(instant.Ticks-midnight.Ticks)/10;
            if(elapsed==0)return new(left.Factor.ScaledValue,ExactDecimal.WorkingScale);
            const long day=TimeSpan.TicksPerDay/10;
            var right=Node(date.AddDays(1));
            return new(left.Factor.ScaledValue*(day-elapsed)+right.Factor.ScaledValue*elapsed,ExactDecimal.WorkingScale*day);
        }
        var calculation=At(timing.CalculationAt);var payment=At(timing.PaymentAt);
        var ratio=payment*new SignedRatio(calculation.Denominator,calculation.Numerator);
        var factor=ExactDecimal.Parse(HalfEven18.Report(ratio));
        if(factor.CompareTo(default)<=0)throw new MarketDataException("discount_ratio","The conditional discount factor must remain positive under its numerical policy.");
        return new(factor,[..nodes.Values.SelectMany(n=>n.UsedPillars).Distinct().Order()],
            [..nodes.Values.OrderBy(n=>n.Date)]);
    }
}
