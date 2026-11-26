using System.Numerics;
using OpenEtrm.MarketData.Trading;
using OpenEtrm.Shared.Numerics;

namespace OpenEtrm.MarketData.Options;

// Inward integer endpoints for a period's unelected on-grid decisions.
internal readonly record struct SwingIndexBounds(BigInteger Last, BigInteger Rest, BigInteger Lower, BigInteger Upper)
{
    internal static SwingIndexBounds From(SwingPeriodBudget period, int days, SwingPlanArithmetic arithmetic)
    {
        var right = period.Right;
        var inverse = arithmetic.Reciprocal(right.Step);
        SignedRatio Index(SignedRatio x, SignedRatio y) => arithmetic.Multiply(arithmetic.Subtract(x, y), inverse);
        var last = Index(right.Maximum, right.Minimum).Numerator;
        var rest = Index(SwingQuantityRight.Base, right.Minimum).Numerator;
        var offset = right.HasTotal ? arithmetic.Add(period.Elected, arithmetic.Multiply(new(days, 1), right.Minimum)) : SignedRatio.Zero;
        return new(last, rest, period.AppliedLower is { } low ? -Floor(Index(offset, low)) : 0,
            period.AppliedUpper is { } high ? Floor(Index(high, offset)) : right.HasTotal ? arithmetic.Product(days, last) : 0);
    }

    private static BigInteger Floor(SignedRatio value)
    {
        var quotient = BigInteger.DivRem(value.Numerator, value.Denominator, out var remainder);
        return remainder.Sign < 0 ? quotient - 1 : quotient;
    }
}
