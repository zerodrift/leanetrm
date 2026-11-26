using System.Numerics;
using OpenEtrm.Shared.Numerics;

namespace OpenEtrm.MarketData.Options;

internal sealed class SwingPlanArithmetic
{
    internal int Bits { get; }
    private readonly BigInteger maximum;

    internal SwingPlanArithmetic(int bits)
    {
        Bits = Math.Min(4096, bits);
        var highBit = BigInteger.One << (Bits - 1);
        maximum = highBit + (highBit - 1);
    }

    private void Require(BigInteger value)
    {
        if (BigInteger.Abs(value).GetBitLength() > Bits) throw SwingPlanGrid.Scope("exact integer bits", Bits);
    }

    internal BigInteger Product(BigInteger left, BigInteger right)
    {
        Require(left);
        Require(right);
        if (BigInteger.Abs(left).GetBitLength() + BigInteger.Abs(right).GetBitLength() > Bits &&
            !right.IsZero && BigInteger.Abs(left) > maximum / BigInteger.Abs(right))
            throw SwingPlanGrid.Scope("exact integer bits", Bits);
        return left * right;
    }

    internal BigInteger Add(BigInteger left, BigInteger right)
    {
        Require(left);
        Require(right);
        if (left.Sign == right.Sign && Math.Max(BigInteger.Abs(left).GetBitLength(), BigInteger.Abs(right).GetBitLength()) == Bits &&
            BigInteger.Abs(left) > maximum - BigInteger.Abs(right))
            throw SwingPlanGrid.Scope("exact integer bits", Bits);
        return left + right;
    }

    internal BigInteger Lcm(BigInteger left, BigInteger right)
    {
        Require(left);
        Require(right);
        return Product(left / BigInteger.GreatestCommonDivisor(left,right), right);
    }

    internal SignedRatio Reciprocal(SignedRatio value)
    {
        Require(value);
        return new(value.Denominator, value.Numerator);
    }

    internal SignedRatio Multiply(SignedRatio left, SignedRatio right)
    {
        Require(left);
        Require(right);
        var a = BigInteger.GreatestCommonDivisor(left.Numerator, right.Denominator);
        var b = BigInteger.GreatestCommonDivisor(right.Numerator, left.Denominator);
        return new(Product(left.Numerator/a, right.Numerator/b), Product(left.Denominator/b, right.Denominator/a));
    }

    internal SignedRatio Add(SignedRatio left, SignedRatio right)
    {
        Require(left);
        Require(right);
        var gcd = BigInteger.GreatestCommonDivisor(left.Denominator, right.Denominator);
        return new(Add(Product(left.Numerator, right.Denominator/gcd), Product(right.Numerator, left.Denominator/gcd)),
            Product(left.Denominator/gcd, right.Denominator));
    }

    internal int Compare(SignedRatio left, SignedRatio right)
    {
        Require(left);
        Require(right);
        if (left.Numerator.Sign != right.Numerator.Sign) return left.Numerator.Sign.CompareTo(right.Numerator.Sign);
        var common = BigInteger.GreatestCommonDivisor(left.Denominator,right.Denominator);
        return Product(left.Numerator,right.Denominator/common).CompareTo(Product(right.Numerator,left.Denominator/common));
    }

    internal BigInteger ToModelAmount(SignedRatio value)
    {
        Require(value);
        var numerator = Product(value.Numerator,ExactDecimal.WorkingScale);
        var quotient = BigInteger.DivRem(BigInteger.Abs(numerator),value.Denominator,out var remainder);
        int halves = remainder.CompareTo(value.Denominator-remainder);
        if (halves > 0 || halves == 0 && !quotient.IsEven) quotient = Add(quotient,1);
        return numerator.Sign < 0 ? -quotient : quotient;
    }

    internal SignedRatio Subtract(SignedRatio left, SignedRatio right)
    {
        Require(right);
        return Add(left, new SignedRatio(-right.Numerator, right.Denominator));
    }

    private void Require(SignedRatio value)
    {
        Require(value.Numerator);
        Require(value.Denominator);
    }
}
