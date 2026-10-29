using System.Globalization;
using System.Numerics;
using OpenEtrm.Shared.Numerics;

namespace OpenEtrm.MarketData;

// Original bounded implementation: Actual/365 Fixed, simple compounding.
// D = 1 / (1 + r * actualDays / 365). All intermediates are exact integers.
// Division rounds once, explicitly to nearest/ties-to-even at the requested scale.
public static class RateConventions
{
    public const string Implementation = "openetrm_rates_v1";
    public const string Policy = "simple_actual365fixed_rational_to_even_18_v1";
    // Display names of the one convention this implementation evaluates.
    public const string DayCountName = "Actual/365 Fixed";
    public const string CompoundingName = "Simple compounding";

    public static (string Factor, string DisplayFactor, int Days) SimpleActual365Fixed(
        DateOnly referenceDate, DateOnly maturity, ExactDecimal annualRate)
    {
        if (referenceDate.Year is <1901 or >2199 || maturity.Year is <1901 or >2199)
            throw new MarketDataException("rate_date", "Supported rate dates run from 1901 through 2199.");
        var days = maturity.DayNumber - referenceDate.DayNumber;
        if (days <= 0) throw new MarketDataException("rate_maturity", "Maturity must follow the reference date.");
        if (annualRate.CompareTo(ExactDecimal.Parse("-0.1")) < 0 || annualRate.CompareTo(ExactDecimal.Parse("1")) > 0)
            throw new MarketDataException("rate_domain", "The supported annual decimal rate range is -0.1 to 1.");
        var parts = annualRate.ToString().Split('.');
        var fraction = parts.Length == 2 ? parts[1] : "";
        var scale = BigInteger.Pow(10, fraction.Length);
        var rate = BigInteger.Parse(parts[0].TrimStart('-') + fraction, CultureInfo.InvariantCulture);
        if (parts[0].StartsWith('-')) rate = -rate;
        var numerator = 365 * scale;
        var denominator = numerator + rate * days;
        if (denominator <= 0)
            throw new MarketDataException("rate_domain", "These terms do not produce a positive discount factor.");
        var factor = ExactDecimal.Parse(Rounded(numerator, denominator, 18)).ToString();
        return (factor, Rounded(numerator, denominator, 6), days);
    }

    private static string Rounded(BigInteger numerator, BigInteger denominator, int decimals)
    {
        var scale = BigInteger.Pow(10, decimals);
        var quotient = IntegerRounding.RoundQuotient(numerator * scale, denominator, MidpointRounding.ToEven);
        var whole = BigInteger.DivRem(quotient, scale, out var fraction);
        return whole.ToString(CultureInfo.InvariantCulture) + "." + fraction.ToString("D" + decimals, CultureInfo.InvariantCulture);
    }
}
