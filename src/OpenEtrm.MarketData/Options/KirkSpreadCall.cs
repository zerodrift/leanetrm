namespace OpenEtrm.MarketData.Options;

// One European call on long − short − strike, per unit of the long quantity.
// The short price already carries any conversion factor, such as a heat rate;
// scaling a lognormal price leaves its volatility unchanged.
public sealed record SpreadCallInputs(LatticeDecimal Long, LatticeDecimal Short, LatticeDecimal Strike,
    LatticeDecimal LongVolatility, LatticeDecimal ShortVolatility, LatticeDecimal Correlation,
    LatticeDecimal Years, LatticeDecimal Discount);

// Kirk's approximation: the short price plus the strike is treated as one
// lognormal price, whose volatility is the short volatility weighted by the
// short price's share of that sum. At a zero strike it is the exact exchange
// option; at a certain short price it is Black-76 at the combined strike.
// Both prices are positive, so a negative long price has no value here.
public static class KirkSpreadCall
{
    public const string Implementation = "spread_call_kirk_lognormal_v1";
    private static readonly LatticeDecimal Zero = LatticeDecimal.Parse("0"), One = LatticeDecimal.Parse("1"),
        Two = LatticeDecimal.Parse("2");

    public static LatticeDecimal UnitPrice(SpreadCallInputs inputs)
    {
        if (inputs is null || inputs.Long.CompareTo(Zero) <= 0 || inputs.Short.CompareTo(Zero) <= 0 ||
            (inputs.Short + inputs.Strike).CompareTo(Zero) <= 0 || inputs.LongVolatility.CompareTo(Zero) < 0 ||
            inputs.ShortVolatility.CompareTo(Zero) < 0 || inputs.Correlation.CompareTo(One) > 0 ||
            inputs.Correlation.CompareTo(Zero - One) < 0 || inputs.Years.CompareTo(Zero) <= 0 ||
            inputs.Discount.CompareTo(Zero) <= 0)
            throw new MarketDataException("spread_option_inputs",
                "A spread call needs positive long and short prices, a positive short price plus strike, nonnegative " +
                "volatilities, a correlation from -1 to 1, time remaining and a positive discount factor.");
        var floor = inputs.Short + inputs.Strike;
        var shortShare = inputs.ShortVolatility * inputs.Short / floor;
        // (σ₁ − ρ·s)² + s²·(1 − ρ²) is σ₁² − 2ρσ₁s + s², written so rounding cannot make it negative.
        var offset = inputs.LongVolatility - inputs.Correlation * shortShare;
        var variance = offset * offset + shortShare * shortShare * (One - inputs.Correlation * inputs.Correlation);
        var deviation = (variance * inputs.Years).Sqrt();
        if (deviation == Zero) return inputs.Discount * LatticeDecimal.Max(inputs.Long - floor, Zero);
        var d1 = ((inputs.Long / floor).Ln() + deviation * deviation / Two) / deviation;
        return inputs.Discount * (inputs.Long * NormalDistribution.Cdf(d1) - floor * NormalDistribution.Cdf(d1 - deviation));
    }
}
