using System.Numerics;
using OpenEtrm.Shared.Numerics;

internal static class SignedRatioChecks
{
    public static int Run()
    {
        for (var a = -7; a <= 7; a++)
        for (var b = 1; b <= 9; b++)
        for (var c = -7; c <= 7; c++)
        for (var d = 1; d <= 9; d++)
        {
            var left = new SignedRatio(a, b); var right = new SignedRatio(c, d);
            Equal(left + right, a * d + c * b, b * d);
            Equal(left - right, a * d - c * b, b * d);
            Equal(left * right, a * c, b * d);
        }
        var large = BigInteger.Pow(10, 1400) + 1;
        Equal(new SignedRatio(1, large) + new SignedRatio(large - 1, large), 1, 1);
        Equal(new SignedRatio(large, 3) * new SignedRatio(3, large), 1, 1);
        Equal(new SignedRatio(-large, 7) - new SignedRatio(-large, 7), 0, 1);
        if (!new SignedRatio(-3, 8).TryScaleToInteger(1000, out var exact) || exact != -375)
            throw new Exception("Terminating signed fractions must project exactly.");
        if (new SignedRatio(1, 3).TryScaleToInteger(BigInteger.Pow(10, 18), out _))
            throw new Exception("A nonterminating value must not be silently rounded.");
        RejectInvalid(() => _ = default(SignedRatio) + default(SignedRatio));
        RejectInvalid(() => _ = default(SignedRatio) * SignedRatio.Zero);
        RejectInvalid(() => _ = SignedRatio.Zero * default(SignedRatio));
        RejectInvalid(() => default(SignedRatio).TryScaleToInteger(100, out _));
        Console.WriteLine("PASS signed rational arithmetic: independent exhaustive fractions, large cancellation and exact projection");
        return 1;
    }

    private static void RejectInvalid(Action action)
    {
        try { action(); } catch (ArgumentOutOfRangeException) { return; }
        throw new Exception("An uninitialized ratio must fail with an invalid denominator.");
    }

    private static void Equal(SignedRatio value, BigInteger numerator, BigInteger denominator)
    {
        if (value.Denominator <= 0 || value.Numerator * denominator != numerator * value.Denominator ||
            BigInteger.GreatestCommonDivisor(value.Numerator, value.Denominator) != 1)
            throw new Exception("Signed ratio differs from the independent cross-product oracle or is not reduced.");
    }
}
