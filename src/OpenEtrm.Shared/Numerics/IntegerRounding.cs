using System.Numerics;

namespace OpenEtrm.Shared.Numerics;

// Rounds the exact signed quotient once. Callers own units, scales and policies.
public static class IntegerRounding
{
    public static BigInteger RoundQuotient(BigInteger numerator, BigInteger denominator, MidpointRounding tieRule)
    {
        if (denominator.Sign <= 0) throw new ArgumentOutOfRangeException(nameof(denominator));
        if (tieRule is not (MidpointRounding.ToEven or MidpointRounding.AwayFromZero))
            throw new ArgumentOutOfRangeException(nameof(tieRule));
        var quotient = BigInteger.DivRem(BigInteger.Abs(numerator), denominator, out var remainder);
        var comparison = (2 * remainder).CompareTo(denominator);
        if (comparison > 0 || comparison == 0 && (tieRule == MidpointRounding.AwayFromZero || !quotient.IsEven))
            quotient++;
        return numerator.Sign * quotient;
    }
}
