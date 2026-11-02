using System.Globalization;
using System.Numerics;
using System.Text.RegularExpressions;
using OpenEtrm.Shared.Numerics;

namespace OpenEtrm.MarketData.Options;

// Deterministic fixed-point arithmetic for option lattices: 30 fractional digits,
// magnitude < 10^24, integer (BigInteger) operations only, so results are
// identical on every host. * and / round half-even once per operation. Exp, Ln
// and Sqrt evaluate with 30 guard digits and round once, so they are within one
// unit of the 30th digit of the true value.
public readonly partial record struct LatticeDecimal : IComparable<LatticeDecimal>
{
    public const int FractionDigits = 30;
    private const int GuardDigits = 30;
    private static readonly BigInteger Scale = BigInteger.Pow(10, FractionDigits);
    private static readonly BigInteger Limit = BigInteger.Pow(10, 24 + FractionDigits);
    private static readonly BigInteger GuardFactor = BigInteger.Pow(10, GuardDigits);
    private static readonly BigInteger Wide = Scale * GuardFactor;
    // ln 2 at 10^-70, from 2*atanh(1/3); private to the guard-digit computations.
    private static readonly BigInteger Ln2Fine = 2 * AtanhSeries(BigInteger.Pow(10, 70) / 3, BigInteger.Pow(10, 70));
    private static readonly BigInteger Ln2Wide = Ln2Fine / BigInteger.Pow(10, 10);
    // exp(x) reaches 10^24 above ExpMaximum; exp(x) rounds to zero below about -69.77,
    // so -70 is a safe cutoff.
    private static readonly LatticeDecimal ExpMaximum = Parse("55.262");
    private static readonly LatticeDecimal ExpMinimum = Parse("-70");
    private readonly BigInteger scaled;

    private LatticeDecimal(BigInteger value)
    {
        if (BigInteger.Abs(value) >= Limit)
            throw new MarketDataException("numeric_range", "Magnitude must be below 10^24.");
        scaled = value;
    }

    [GeneratedRegex(@"\A-?(0|[1-9][0-9]*)(\.[0-9]{1,30})?\z", RegexOptions.CultureInvariant)]
    private static partial Regex DecimalSyntax();

    public static LatticeDecimal Parse(string text)
    {
        if (text.Length > 60 || !DecimalSyntax().IsMatch(text))
            throw new MarketDataException("numeric_format", "Supply a plain decimal with at most 30 fractional digits.");
        var parts = text.TrimStart('-').Split('.');
        var value = BigInteger.Parse(parts[0], CultureInfo.InvariantCulture) * Scale
            + BigInteger.Parse((parts.Length == 2 ? parts[1] : "").PadRight(FractionDigits, '0'), CultureInfo.InvariantCulture);
        return new(text.StartsWith('-') ? -value : value);
    }

    public static LatticeDecimal From(ExactDecimal value) => new(value.ScaledValue * (Scale / ExactDecimal.WorkingScale));

    public ExactDecimal ToExact() =>
        ExactDecimal.FromScaled(Round(scaled, Scale / ExactDecimal.WorkingScale));

    public static LatticeDecimal operator +(LatticeDecimal a, LatticeDecimal b) => new(a.scaled + b.scaled);
    public static LatticeDecimal operator -(LatticeDecimal a, LatticeDecimal b) => new(a.scaled - b.scaled);
    public static LatticeDecimal operator *(LatticeDecimal a, LatticeDecimal b) => new(Round(a.scaled * b.scaled, Scale));
    public static LatticeDecimal operator /(LatticeDecimal a, LatticeDecimal b)
    {
        if (b.scaled.IsZero) throw new MarketDataException("numeric_division", "A divisor cannot be zero.");
        // RoundQuotient needs a positive divisor, so carry the divisor's sign on the numerator.
        return new(Round(a.scaled * Scale * b.scaled.Sign, BigInteger.Abs(b.scaled)));
    }

    public int CompareTo(LatticeDecimal other) => scaled.CompareTo(other.scaled);
    public static LatticeDecimal Max(LatticeDecimal a, LatticeDecimal b) => a.CompareTo(b) >= 0 ? a : b;

    public LatticeDecimal Pow(int exponent)
    {
        if (exponent < 0)
            throw new MarketDataException("lattice_domain", "The exponent must be a nonnegative integer.");
        var result = new LatticeDecimal(Scale);
        var factor = this;
        for (; exponent > 0; exponent >>= 1)
        {
            if ((exponent & 1) != 0) result *= factor;
            if (exponent > 1) factor *= factor;
        }
        return result;
    }

    // Reduce x = k*ln2 + r with |r| <= ln2/2, then exp(x) = 2^k * exp(r).
    // |r| <= 0.35, so the Taylor term r^n/n! falls below 10^-60 by n = 45; the loop
    // stops at the first zero term and is bounded at 60 terms. Relative error is
    // below 10^-57 for |x| < 56, far inside the 30-digit result.
    public LatticeDecimal Exp()
    {
        if (CompareTo(ExpMaximum) > 0)
            throw new MarketDataException("numeric_range", "exp(x) must stay below 10^24.");
        if (CompareTo(ExpMinimum) < 0) return new(BigInteger.Zero);
        var wide = scaled * GuardFactor;
        var k = Round(wide, Ln2Wide);
        var r = wide - k * Ln2Wide;
        var sum = Wide;
        var term = Wide;
        for (var n = 1; n <= 60 && !term.IsZero; n++)
        {
            term = Round(term * r, Wide) / n;
            sum += term;
        }
        var shift = (int)k;
        var result = shift >= 0 ? sum << shift : Round(sum, BigInteger.One << -shift);
        return new(Round(result, GuardFactor));
    }

    // ln x = j*ln2 + ln m with m = x/2^j in [0.75, 1.5), and ln m = 2*atanh((m-1)/(m+1))
    // with |z| <= 0.2. Each series term shrinks by z^2 <= 0.04, so 45 terms reach 10^-60;
    // AtanhSeries is bounded at 119 terms. Each normalization loop below runs at most
    // twice, because the starting j (from bit lengths) is within one of the final j.
    // Absolute error is below 10^-57.
    public LatticeDecimal Ln()
    {
        if (scaled.Sign <= 0)
            throw new MarketDataException("lattice_domain", "The logarithm needs a positive argument.");
        var wide = scaled * GuardFactor;
        var j = (int)(wide.GetBitLength() - Wide.GetBitLength());
        BigInteger Numerator(int e) => e >= 0 ? wide : wide << -e;
        BigInteger Denominator(int e) => e >= 0 ? Wide << e : Wide;
        while (2 * Numerator(j) >= 3 * Denominator(j)) j++;
        while (4 * Numerator(j) < 3 * Denominator(j)) j--;
        var z = Round((Numerator(j) - Denominator(j)) * Wide, Numerator(j) + Denominator(j));
        return new(Round(j * Ln2Wide + 2 * AtanhSeries(z, Wide), GuardFactor));
    }

    // Integer square root of x*10^30, rounded to nearest (an exact tie cannot occur).
    // Newton's iteration from a power-of-two upper bound decreases monotonically and
    // doubles its correct bits each step; it ends within log2(bits)+2 steps.
    public LatticeDecimal Sqrt()
    {
        if (scaled.Sign < 0)
            throw new MarketDataException("lattice_domain", "The square root needs a nonnegative argument.");
        var n = scaled * Scale;
        if (n.IsZero) return this;
        var root = BigInteger.One << (int)((n.GetBitLength() + 1) / 2);
        for (var i = 0; i < 64; i++)
        {
            var next = (root + n / root) >> 1;
            if (next >= root) break;
            root = next;
        }
        if (n - root * root > root) root += 1;
        return new(root);
    }

    // atanh(z) = z + z^3/3 + z^5/5 + ..., z and the result at the given scale.
    private static BigInteger AtanhSeries(BigInteger z, BigInteger scale)
    {
        var square = Round(z * z, scale);
        var power = z;
        var sum = z;
        for (var n = 3; n < 240 && !power.IsZero; n += 2)
        {
            power = Round(power * square, scale);
            sum += power / n;
        }
        return sum;
    }

    private static BigInteger Round(BigInteger numerator, BigInteger denominator) =>
        IntegerRounding.RoundQuotient(numerator, denominator, MidpointRounding.ToEven);

    public override string ToString()
    {
        var whole = BigInteger.DivRem(BigInteger.Abs(scaled), Scale, out var fraction);
        var suffix = fraction.IsZero ? "" : "." + fraction.ToString("D30", CultureInfo.InvariantCulture).TrimEnd('0');
        return (scaled.Sign < 0 ? "-" : "") + whole.ToString(CultureInfo.InvariantCulture) + suffix;
    }
}
