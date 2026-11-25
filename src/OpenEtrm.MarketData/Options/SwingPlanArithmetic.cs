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
        maximum = (BigInteger.One << Bits) - 1;
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

    internal BigInteger Lcm(BigInteger left, BigInteger right) => Product(left / BigInteger.GreatestCommonDivisor(left,right), right);

    internal SignedRatio ReciprocalOfStep(SignedRatio step)
    {
        Require(step);
        return new(step.Denominator, step.Numerator);
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
