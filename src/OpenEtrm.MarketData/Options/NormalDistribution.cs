using System.Numerics;

namespace OpenEtrm.MarketData.Options;

// Project-owned standard normal distribution. The integral of exp(-t²/2) is
// evaluated by its Taylor series with 90 fractional digits before rounding to
// LatticeDecimal's 30. At |x| < 12 cancellation consumes fewer than 33 digits;
// the omitted tails at 12 are below 2e-33. Inversion bisects the wide integral,
// not the rounded CDF, so probabilities down to 1e-30 retain meaningful roots.
public static class NormalDistribution
{
    public const string ImplementationId = "normal_decimal_taylor_bisection_v1";
    public static LatticeDecimal Cdf(LatticeDecimal x) => LatticeDecimal.NormalCdf(x);
    public static LatticeDecimal Inverse(LatticeDecimal probability) => LatticeDecimal.NormalInverse(probability);
}

public readonly partial record struct LatticeDecimal
{
    private static readonly BigInteger NormalScale = BigInteger.Pow(10, 90);
    private static readonly BigInteger NormalGuard = BigInteger.Pow(10, 60);
    // 1/sqrt(2*pi), rounded at 90 fractional digits.
    private static readonly BigInteger NormalDensity = BigInteger.Parse(
        "398942280401432677939946059934381868475858631164934657665925829670657925899301838501252334");

    private static BigInteger NormalWide(LatticeDecimal x)
    {
        var negative = x.scaled.Sign < 0;
        var value = BigInteger.Abs(x.scaled);
        if (value >= 12 * Scale) return negative ? BigInteger.Zero : NormalScale;
        var wide = value * NormalGuard;
        var square = Round(wide * wide, 2 * NormalScale);
        var term = wide;
        var sum = wide;
        // Integrate each exponential term: (-1)^n*x^(2n+1)/(2^n*n!*(2n+1)).
        for (var n = 1; n <= 600; n++)
        {
            term = -Round(term * square, NormalScale * n);
            if (term.IsZero) break;
            sum += Round(term, 2 * n + 1);
        }
        var cdf = NormalScale / 2 + Round(sum * NormalDensity, NormalScale);
        return negative ? NormalScale - cdf : cdf;
    }

    internal static LatticeDecimal NormalCdf(LatticeDecimal x) => new(Round(NormalWide(x), NormalGuard));

    internal static LatticeDecimal NormalInverse(LatticeDecimal p)
    {
        if (p.scaled <= 0 || p.scaled >= Scale)
            throw new MarketDataException("normal_probability", "A normal probability must be strictly between zero and one.");
        if (p.scaled * 2 == Scale) return default;
        // Solve the lower tail and reflect, preserving symmetry also in the tails.
        var reflect = p.scaled * 2 > Scale;
        var target = (reflect ? Scale - p.scaled : p.scaled) * NormalGuard;
        var low = new LatticeDecimal(-12 * Scale);
        var high = new LatticeDecimal(BigInteger.Zero);
        while (high.scaled - low.scaled > BigInteger.One)
        {
            var middle = new LatticeDecimal((low.scaled + high.scaled) / 2);
            if (NormalWide(middle) < target) low = middle; else high = middle;
        }
        var root = new LatticeDecimal(Round(low.scaled + high.scaled, 2));
        return reflect ? new(-root.scaled) : root;
    }
}
