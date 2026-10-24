using System.Numerics;
using OpenEtrm.Shared.Numerics;

internal static class IntegerRoundingChecks
{
    public static int Run()
    {
        foreach (var rule in new[] { MidpointRounding.ToEven, MidpointRounding.AwayFromZero })
        for (var denominator = 1; denominator <= 31; denominator++)
        for (var numerator = -300; numerator <= 300; numerator++)
        {
            var expected = new BigInteger(decimal.Round(numerator / (decimal)denominator, 0, rule));
            if (IntegerRounding.RoundQuotient(numerator, denominator, rule) != expected)
                throw new Exception($"Incorrect signed rational rounding: {numerator}/{denominator}, {rule}.");
        }

        var even = BigInteger.Pow(10, 300);
        foreach (var sign in new[] { -1, 1 })
        {
            Equal(IntegerRounding.RoundQuotient(sign * (2 * even + 1), 2, MidpointRounding.ToEven), sign * even);
            Equal(IntegerRounding.RoundQuotient(sign * (2 * even + 3), 2, MidpointRounding.ToEven), sign * (even + 2));
            Equal(IntegerRounding.RoundQuotient(sign * (2 * even + 1), 2, MidpointRounding.AwayFromZero), sign * (even + 1));
            Equal(IntegerRounding.RoundQuotient(sign * even, 3, MidpointRounding.ToEven), sign * (even / 3));
        }

        Reject(() => IntegerRounding.RoundQuotient(1, 0, MidpointRounding.ToEven));
        Reject(() => IntegerRounding.RoundQuotient(1, -1, MidpointRounding.ToEven));
        Reject(() => IntegerRounding.RoundQuotient(1, 2, MidpointRounding.ToZero));
        Reject(() => IntegerRounding.RoundQuotient(1, 2, (MidpointRounding)99));
        Console.WriteLine("PASS shared integer rounding: signed independent decimal oracle, large ties and explicit policy bounds");
        return 3;
    }

    private static void Equal(BigInteger actual, BigInteger expected)
    {
        if (actual != expected) throw new Exception("Incorrect unbounded rational rounding.");
    }
    private static void Reject(Action action)
    {
        try { action(); }
        catch (ArgumentOutOfRangeException) { return; }
        throw new Exception("Expected an explicit positive denominator and supported tie policy.");
    }
}
