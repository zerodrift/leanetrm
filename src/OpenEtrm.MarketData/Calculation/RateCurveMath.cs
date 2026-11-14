using System.Collections.Immutable;
using OpenEtrm.MarketData.Options;
using OpenEtrm.Shared.Numerics;

namespace OpenEtrm.MarketData;

public sealed record RateChoice(string Field, string Code, string Label, bool Offered);

// A rate curve's explicit conventions. Every term is required; an unknown or coming-soon code is refused.
public sealed record RateConventionSet(string DayCount, string Compounding, string Interpolation, string Extrapolation, string MarkUnit)
{
    public static RateConventionSet Parse(string? dayCount, string? compounding, string? interpolation, string? extrapolation, string? markUnit) =>
        new(Offered("dayCount", dayCount), Offered("compounding", compounding), Offered("interpolation", interpolation),
            Offered("extrapolation", extrapolation), Offered("markUnit", markUnit));

    private static string Offered(string field, string? code) =>
        RateCurveMath.Choices.Any(c => c.Field == field && c.Code == code && c.Offered) ? code! :
            throw new MarketDataException("unsupported_rate_convention", $"Choose a supported {field}; '{code}' is not offered.");
}

public sealed record RatePillarRate(DateOnly Maturity, ExactDecimal DecimalRate);
public sealed record RateDiscount(ExactDecimal Factor, ImmutableArray<int> UsedPillars);

// A discount node: days after t_0, its decimal annual zero rate and its discount factor.
public sealed record RateNode(int Days, ExactDecimal DecimalRate, ExactDecimal Factor)
{
    // t_0 discounts at exactly 1. Its zero rate is the first pillar's, so linear-zero holds that rate flat before the first pillar.
    public static RateNode Anchor(RateNode first) => new(0, first.DecimalRate, ExactDecimal.Parse("1"));
}

// Project-owned rate conventions. Exponentials and logarithms use LatticeDecimal (30 digits with
// guard digits), and every factor rounds once to 18 places, half-even.
public static partial class RateCurveMath
{
    public const string Policy = "rate_curve_lattice30_to_even_18_v1";

    public const string DiscountRatioPolicy=HalfEven18.Policy;
    public static ExactDecimal DiscountRatio(ExactDecimal numerator,ExactDecimal denominator,string policy)
    {
        if(policy!=DiscountRatioPolicy||numerator.CompareTo(default)<=0||denominator.CompareTo(default)<=0)
            throw new MarketDataException("discount_ratio","Select positive discount operands and the exact rational half-even 18 policy.");
        var ratio=new SignedRatio(numerator.ScaledValue,denominator.ScaledValue);
        return ExactDecimal.Parse(HalfEven18.Report(ratio));
    }

    public static readonly ImmutableArray<RateChoice> Choices =
    [
        new("dayCount", "actual_365_fixed", "ACT/365 Fixed", true), new("dayCount", "actual_360", "ACT/360", true),
        new("dayCount", "thirty_360", "30/360", false), new("dayCount", "actual_actual_isda", "ACT/ACT ISDA", false),
        new("compounding", "simple", "Simple", true), new("compounding", "continuous", "Continuous", true), new("compounding", "annual", "Annual", true),
        new("compounding", "semi_annual", "Semi-annual", false), new("compounding", "quarterly", "Quarterly", false),
        new("interpolation", "log_linear_discount", "Log-linear discount factor", true), new("interpolation", "linear_zero", "Linear zero rate", true),
        new("interpolation", "cubic_spline", "Cubic spline", false), new("interpolation", "monotone_convex", "Monotone convex", false),
        new("extrapolation", "none", "None", true), new("extrapolation", "flat_zero", "Flat zero rate", true), new("extrapolation", "flat_forward", "Flat forward", true),
        new("markUnit", "percent_per_year", "Percent per year", true), new("markUnit", "decimal_per_year", "Decimal per year", true),
        new("markUnit", "basis_points", "Basis points", false),
    ];

    private static readonly LatticeDecimal One = LatticeDecimal.Parse("1");

    public static string MarkUnitLabel(string markUnit) => markUnit switch
    {
        "percent_per_year" => "% per year",
        "decimal_per_year" => "per year",
        _ => throw Unsupported(markUnit),
    };

    // Each mark unit's explicit range; a tenor series' value specification must state exactly this range.
    public static (ExactDecimal Minimum, ExactDecimal Maximum) MarkRange(string markUnit) => markUnit switch
    {
        "percent_per_year" => (ExactDecimal.Parse("-10"), ExactDecimal.Parse("100")),
        "decimal_per_year" => (ExactDecimal.Parse("-0.1"), ExactDecimal.Parse("1")),
        _ => throw Unsupported(markUnit),
    };

    // A mark in its declared unit becomes a decimal annual rate.
    public static ExactDecimal DecimalRate(ExactDecimal mark, string markUnit)
    {
        var (low, high) = MarkRange(markUnit);
        if (mark.CompareTo(low) < 0 || mark.CompareTo(high) > 0)
            throw new MarketDataException("rate_domain", $"A {MarkUnitLabel(markUnit)} mark must lie from {low} to {high}.");
        return mark * ExactDecimal.Parse(markUnit == "percent_per_year" ? "0.01" : "1");
    }

    public static ExactDecimal Factor(ExactDecimal decimalRate, int days, RateConventionSet c) =>
        Factor(LatticeDecimal.From(decimalRate), days, c).ToExact();

    // Between two nodes, or between the t_0 anchor and the first pillar.
    public static ExactDecimal Interpolate(RateNode left, RateNode right, int days, RateConventionSet c)
    {
        if (right.Days <= left.Days || right.Days - left.Days > DiscountInterpolation.MaximumPillarDays || days < left.Days || days > right.Days)
            throw new MarketDataException("discount_interpolation", "The requested bracket exceeds the supported interpolation span.");
        if (days == left.Days) return left.Factor;
        if (days == right.Days) return right.Factor;
        return c.Interpolation switch
        {
            // DiscountInterpolation needs dates; only day differences matter, so nodes sit on a fixed epoch.
            "log_linear_discount" => DiscountInterpolation.Factor(Epoch.AddDays(left.Days), left.Factor, Epoch.AddDays(right.Days), right.Factor,
                Epoch.AddDays(days), DiscountInterpolation.Policy),
            "linear_zero" => Factor(LatticeDecimal.From(left.DecimalRate) + (LatticeDecimal.From(right.DecimalRate) - LatticeDecimal.From(left.DecimalRate)) *
                Ratio(days - left.Days, right.Days - left.Days), days, c).ToExact(),
            _ => throw Unsupported(c.Interpolation),
        };
    }

    // After the last pillar. beforeLast is the previous pillar, or the t_0 anchor for a one-pillar curve.
    public static ExactDecimal Extrapolate(RateNode beforeLast, RateNode last, int days, RateConventionSet c)
    {
        if (days == last.Days) return last.Factor;
        if (days < last.Days || last.Days <= beforeLast.Days)
            throw new MarketDataException("discount_coverage", "Extrapolation applies only after the last pillar.");
        return c.Extrapolation switch
        {
            "none" => throw new MarketDataException("discount_coverage", "This curve does not extrapolate after its last pillar."),
            "flat_zero" => Factor(last.DecimalRate, days, c),
            // The last interval's log-discount slope continues: a constant continuously compounded forward rate.
            "flat_forward" => (LatticeDecimal.From(last.Factor).Ln() + (LatticeDecimal.From(last.Factor).Ln() - LatticeDecimal.From(beforeLast.Factor).Ln()) *
                Ratio(days - last.Days, last.Days - beforeLast.Days)).Exp().ToExact(),
            _ => throw Unsupported(c.Extrapolation),
        };
    }

    // A payment date's discount factor from a curve's pillars (each a maturity and its decimal annual zero rate) at referenceDate,
    // and the pillars it used. t_0 discounts at exactly 1; before the first pillar the t_0 anchor is the left bracket.
    public static RateDiscount Discount(DateOnly referenceDate, ImmutableArray<RatePillarRate> pillars, DateOnly payment, RateConventionSet c)
    {
        if (pillars.IsDefaultOrEmpty || payment < referenceDate)
            throw new MarketDataException("discount_context", "Discount a payment on or after the reference date from at least one pillar.");
        var nodes = pillars.Select((p, i) =>
        {
            var days = p.Maturity.DayNumber - referenceDate.DayNumber;
            if (days <= 0 || i > 0 && p.Maturity <= pillars[i - 1].Maturity)
                throw new MarketDataException("rate_maturity", "Pillars must follow the reference date in strictly increasing order.");
            return new RateNode(days, p.DecimalRate, Factor(p.DecimalRate, days, c));
        }).ToImmutableArray();
        var used=RequiredPillars(referenceDate,[..pillars.Select(p=>p.Maturity)],payment,c);
        var target = payment.DayNumber - referenceDate.DayNumber;
        if (target == 0) return new(ExactDecimal.Parse("1"),used);
        var right=used[^1];
        if(nodes[right].Days==target)return new(nodes[right].Factor,used);
        if(target<nodes[^1].Days)return new(Interpolate(used.Length==1?RateNode.Anchor(nodes[0]):nodes[used[0]],nodes[right],target,c),used);
        var last = nodes.Length - 1;
        var before = last == 0 ? RateNode.Anchor(nodes[0]) : nodes[last - 1];
        var factor = Extrapolate(before, nodes[last], target, c);
        return new(factor,used);
    }

    // Dependency geometry is independent of mark levels. Equal rates cannot
    // erase a pillar that another scenario needs.
    public static ImmutableArray<int> RequiredPillars(DateOnly reference,ImmutableArray<DateOnly> maturities,DateOnly payment,RateConventionSet conventions)
    {
        if(maturities.IsDefaultOrEmpty||payment<reference||conventions is null)
            throw new MarketDataException("discount_context","Select a nonempty exact tenor geometry and a date on or after its origin.");
        if(maturities.Select((date,i)=>date<=reference||i>0&&date<=maturities[i-1]).Any(invalid=>invalid))
            throw new MarketDataException("rate_maturity","Tenor maturities must follow the origin in strictly increasing order.");
        if(payment==reference)return [];
        var right=maturities.TakeWhile(date=>date<payment).Count();
        if(right<maturities.Length)
        {
            if(maturities[right]==payment)return [right];
            var left=right==0?reference:maturities[right-1];
            if(maturities[right].DayNumber-left.DayNumber>DiscountInterpolation.MaximumPillarDays)
                throw new MarketDataException("discount_interpolation","The requested bracket exceeds the supported interpolation span.");
            return right==0?[0]:[right-1,right];
        }
        var last=maturities.Length-1;
        return conventions.Extrapolation switch{
            "none"=>throw new MarketDataException("discount_coverage","This curve does not extrapolate after its last pillar."),
            "flat_zero"=>[last],"flat_forward"=>last==0?[last]:[last-1,last],
            _=>throw Unsupported(conventions.Extrapolation)};
    }

    private static readonly DateOnly Epoch = new(2000, 1, 1);

    private static LatticeDecimal Factor(LatticeDecimal rate, int days, RateConventionSet c)
    {
        if (days <= 0) throw new MarketDataException("rate_maturity", "Maturity must follow the reference date.");
        var years = Ratio(days, c.DayCount switch
        {
            "actual_365_fixed" => 365,
            "actual_360" => 360,
            _ => throw Unsupported(c.DayCount),
        });
        switch (c.Compounding)
        {
            case "simple":
                var accrual = One + rate * years;
                if (accrual.CompareTo(default) <= 0) throw new MarketDataException("rate_domain", "These terms do not produce a positive discount factor.");
                return One / accrual;
            case "continuous":
                return (LatticeDecimal.Parse("0") - rate * years).Exp();
            case "annual":
                return (LatticeDecimal.Parse("0") - years * (One + rate).Ln()).Exp();
            default:
                throw Unsupported(c.Compounding);
        }
    }

    private static LatticeDecimal Ratio(int numerator, int denominator) =>
        LatticeDecimal.Parse(numerator.ToString(System.Globalization.CultureInfo.InvariantCulture)) /
        LatticeDecimal.Parse(denominator.ToString(System.Globalization.CultureInfo.InvariantCulture));

    private static MarketDataException Unsupported(string code) =>
        new("unsupported_rate_convention", $"The rate convention '{code}' is not supported.");
}
