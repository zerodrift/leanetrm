using System.Globalization;
using System.Numerics;
using System.Text.RegularExpressions;

namespace OpenEtrm.Shared.Numerics;

// Signed reduced ratio with a positive denominator, read from plain ASCII decimals.
// Arithmetic is exact; rounding happens only when a caller reports at its own scale
// and tie rule. ExactRatio is positive-only.
public readonly partial record struct SignedRatio : IComparable<SignedRatio>
{
    private readonly BigInteger numerator, denominator;
    public BigInteger Numerator=>numerator;
    public BigInteger Denominator=>denominator;

    public SignedRatio(BigInteger numerator, BigInteger denominator)
    {
        RequireDenominator(denominator);
        var gcd = BigInteger.GreatestCommonDivisor(numerator, denominator);
        (this.numerator, this.denominator) = (numerator / gcd, denominator / gcd);
    }

    private static void RequireDenominator(BigInteger denominator)
    {
        if (denominator.Sign <= 0) throw new ArgumentOutOfRangeException(nameof(denominator), "The denominator must be positive.");
    }

    public static SignedRatio Zero { get; } = new(0, 1);

    public static bool TryParse(string text, out SignedRatio value)
    {
        value = Zero;
        if (!PlainDecimal().IsMatch(text)) return false;
        var places = text.Contains('.') ? text.Length - text.IndexOf('.') - 1 : 0;
        value = new(BigInteger.Parse(text.Replace(".", ""), NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture), BigInteger.Pow(10, places));
        return true;
    }

    public static SignedRatio Sum(IEnumerable<SignedRatio> terms) => terms.Aggregate(Zero, (sum, term) => sum + term);
    public static SignedRatio operator +(SignedRatio a, SignedRatio b)
    {
        RequireDenominator(a.denominator); RequireDenominator(b.denominator);
        var shared = BigInteger.GreatestCommonDivisor(a.denominator, b.denominator);
        return new(a.numerator * (b.denominator / shared) + b.numerator * (a.denominator / shared),
            a.denominator * (b.denominator / shared));
    }
    public static SignedRatio operator -(SignedRatio a, SignedRatio b) => a + new SignedRatio(-b.numerator, b.denominator);
    public static SignedRatio operator *(SignedRatio a, SignedRatio b)
    {
        RequireDenominator(a.denominator); RequireDenominator(b.denominator);
        // Cancel before multiplying: large reciprocal factors need not create large intermediates.
        var left = BigInteger.GreatestCommonDivisor(a.numerator, b.denominator);
        var right = BigInteger.GreatestCommonDivisor(b.numerator, a.denominator);
        return new((a.numerator / left) * (b.numerator / right),
            (a.denominator / right) * (b.denominator / left));
    }
    public bool TryScaleToInteger(BigInteger scale, out BigInteger value)
    {
        if (scale.Sign <= 0) throw new ArgumentOutOfRangeException(nameof(scale));
        RequireDenominator(denominator);
        value = BigInteger.DivRem(numerator * scale, denominator, out var remainder);
        return remainder.IsZero;
    }
    public static SignedRatio operator /(SignedRatio a, BigInteger divisor) => new(a.numerator, a.denominator * divisor);
    public int CompareTo(SignedRatio other) => (numerator * other.denominator).CompareTo(other.numerator * denominator);

    // The value rounded once at the scale, as a ratio for exact use after reporting.
    public SignedRatio Round(int scale, MidpointRounding tieRule) =>
        new(IntegerRounding.RoundQuotient(numerator * BigInteger.Pow(10, scale), denominator, tieRule), BigInteger.Pow(10, scale));

    // The value rounded once at the scale, as a plain decimal without trailing zeros.
    public string Format(int scale, MidpointRounding tieRule)
    {
        var scaled = IntegerRounding.RoundQuotient(numerator * BigInteger.Pow(10, scale), denominator, tieRule);
        var digits = BigInteger.Abs(scaled).ToString(CultureInfo.InvariantCulture).PadLeft(scale + 1, '0');
        var whole = digits[..^scale];
        var fraction = digits[^scale..].TrimEnd('0');
        return (scaled.Sign < 0 ? "-" : "") + (fraction.Length == 0 ? whole : $"{whole}.{fraction}");
    }

    [GeneratedRegex(@"^-?[0-9]+(\.[0-9]+)?\z")]
    private static partial Regex PlainDecimal();
}

// exact_rational_half_even_18_v1: exact signed rational arithmetic; each reported
// value is rounded once, half to even, at 18 decimal places.
public static class HalfEven18
{
    public const string Policy = "exact_rational_half_even_18_v1";
    private const int Scale = 18;
    public static string Report(SignedRatio value) => value.Format(Scale, MidpointRounding.ToEven);
    public static SignedRatio Round(SignedRatio value) => value.Round(Scale, MidpointRounding.ToEven);
}
