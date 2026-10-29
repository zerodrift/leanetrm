using System.Globalization;
using System.Numerics;

namespace OpenEtrm.MarketData;

public static class DiscountInterpolation
{
    public const string Policy = "log_discount_geometric_root_to_even_18_v1";
    public const int MaximumPillarDays = 3660;
    private static readonly BigInteger Scale = BigInteger.Pow(10, 18);

    // exp((1-w)log(a)+w log(b)) is the positive d-th root of
    // a^(d-n)*b^n when w=n/d. Work in integer scaled factors: no floating point
    // logarithm, intermediate rounding or platform math-library dependency.
    public static ExactDecimal Factor(DateOnly left, ExactDecimal leftFactor,
        DateOnly right, ExactDecimal rightFactor, DateOnly payment, string policy)
    {
        var days = right.DayNumber - left.DayNumber;
        if (policy != Policy || days <= 0 || days > MaximumPillarDays || payment < left || payment > right ||
            left.Year is < 1901 or > 2199 || right.Year is < 1901 or > 2199 ||
            leftFactor.CompareTo(default) <= 0 || rightFactor.CompareTo(default) <= 0)
            throw new MarketDataException("discount_interpolation", "Use positive factors, ordered covered dates within the supported pillar span, and the explicit log-discount interpolation policy.");
        if (payment == left) return leftFactor;
        if (payment == right) return rightFactor;
        var n = payment.DayNumber - left.DayNumber;
        var gcd = (int)BigInteger.GreatestCommonDivisor(n, days);
        n /= gcd; days /= gcd;
        var a = Scaled(leftFactor); var b = Scaled(rightFactor);
        if (a == b) return leftFactor;
        var power = BigInteger.Pow(a, days - n) * BigInteger.Pow(b, n);
        var low = BigInteger.Min(a, b); var high = BigInteger.Max(a, b);
        // The root lies between the two positive endpoint factors. The search
        // and exact half-integer comparison also handle genuine rounding ties.
        if (high - low > BigInteger.One)
        {
            // AM-GM bounds the root by this exact weighted arithmetic mean.
            // Its integer floor is at least floor(root), in either endpoint order.
            high = ((days - n) * a + n * b) / days;
            var highPower = BigInteger.Pow(high, days);
            var twicePower = power << 1;
            if (highPower <= power) low = high;
            while (high - low > BigInteger.One)
            {
                // Bisection bounds work for arbitrarily separated endpoints.
                // Once high^d <= 2P, integer Newton converges near the root.
                var newton = highPower <= twicePower;
                var candidate = newton
                    ? ((days - 1) * high + power / (highPower / high)) / days
                    : (low + high) / 2;
                var candidatePower = BigInteger.Pow(candidate, days);
                if (candidatePower <= power)
                {
                    low = candidate;
                    // Newton from an upper bound is at least floor(root), so
                    // a point also below the root is its exact integer floor.
                    if (newton) break;
                }
                else
                {
                    high = candidate;
                    highPower = candidatePower;
                }
            }
        }
        var comparison = (power << days).CompareTo(BigInteger.Pow(2 * low + 1, days));
        if (comparison > 0 || comparison == 0 && !low.IsEven) low++;
        var whole = BigInteger.DivRem(low, Scale, out var fraction);
        return ExactDecimal.Parse(whole.ToString(CultureInfo.InvariantCulture) + "." + fraction.ToString("D18", CultureInfo.InvariantCulture));
    }

    private static BigInteger Scaled(ExactDecimal value)
    {
        var parts = value.ToString().Split('.');
        return BigInteger.Parse(parts[0] + (parts.Length == 2 ? parts[1] : "").PadRight(18, '0'), CultureInfo.InvariantCulture);
    }
}
