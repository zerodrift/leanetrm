namespace OpenEtrm.MarketData.Options;

// The Black-76 formula for a European option on a future, per unit of quote and before the contract multiplier:
//   T  = years from the valuation instant to the expiry instant
//   s  = volatility * sqrt(T)
//   d1 = (ln(F / K) + s^2 / 2) / s        d2 = d1 - s
//   call = D * (F * N(d1) - K * N(d2))
//   put  = D * (K * N(-d2) - F * N(-d1))
// in LatticeDecimal, with the project's Ln, Sqrt and normal distribution. The value is smooth in the futures price, so
// the model has no price step. LatticeDecimal has no negation: N(-x) is formed as 1 - N(x).
public static class EuropeanFuturesOptionModel
{
    private static readonly LatticeDecimal Zero = LatticeDecimal.Parse("0");
    private static readonly LatticeDecimal One = LatticeDecimal.Parse("1");
    private static readonly LatticeDecimal Two = LatticeDecimal.Parse("2");
    // NormalDistribution.Cdf is exactly 0 or 1 from twelve deviations out.
    private static readonly LatticeDecimal Saturation = LatticeDecimal.Parse("12");

    public static LatticeDecimal UnitPrice(FuturesOptionTerms terms, LatticeDecimal futures, LatticeDecimal volatility,
        LatticeDecimal discountToExpiry, DateTimeOffset valuationAt, OptionNumericalPolicy policy)
    {
        var years = ValidateInputs(terms, futures, discountToExpiry, valuationAt, policy);
        AmericanFuturesOptionModel.RequirePositive(volatility, "volatility");
        var strike = LatticeDecimal.From(terms.Strike);
        var floor = DiscountedIntrinsic(terms.Right, futures, strike, discountToExpiry);
        var width = volatility * years.Sqrt();
        var moneyness = futures.Ln() - strike.Ln();
        // With d1 and d2 both twelve deviations out on one side, each probability is exactly 0 or 1 and the formula is
        // the discounted intrinsic value. Close to expiry the quotient below has no bound, so it is not formed there.
        if (LatticeDecimal.Max(moneyness, Zero - moneyness).CompareTo((Saturation + width / Two) * width) >= 0) return floor;
        var d1 = moneyness / width + width / Two;
        var (first, second) = (NormalDistribution.Cdf(d1), NormalDistribution.Cdf(d1 - width));
        var value = discountToExpiry * (terms.Right == OptionRight.Call
            ? futures * first - strike * second
            : strike * (One - second) - futures * (One - first));
        // The value is never below the discounted intrinsic value. Rounding the two probabilities at the 30th digit
        // can leave the difference a few units of that digit short of it.
        return LatticeDecimal.Max(value, floor);
    }

    // The formula's limit as the volatility goes to zero: the discount factor times intrinsic.
    public static LatticeDecimal ZeroVolatilityValue(FuturesOptionTerms terms, LatticeDecimal futures, LatticeDecimal discountToExpiry)
    {
        RequireEuropean(terms);
        AmericanFuturesOptionModel.RequireMarketInputs(terms, futures, discountToExpiry);
        return DiscountedIntrinsic(terms.Right, futures, LatticeDecimal.From(terms.Strike), discountToExpiry);
    }

    // The checks on everything but volatility, shared with the implied-volatility solver so that both reject the same
    // inputs with the same codes. Returns the year fraction to expiry.
    internal static LatticeDecimal ValidateInputs(FuturesOptionTerms terms, LatticeDecimal futures, LatticeDecimal discountToExpiry,
        DateTimeOffset valuationAt, OptionNumericalPolicy policy)
    {
        RequireEuropean(terms);
        if (policy.Lattice != OptionLattice.ClosedForm)
            throw new MarketDataException("option_numerical_policy", $"Policy '{policy.Id}' names a tree; the Black-76 formula has none.");
        AmericanFuturesOptionModel.RequireMarketInputs(terms, futures, discountToExpiry);
        return AmericanFuturesOptionModel.YearFraction(valuationAt, terms.ExpiryAt, policy);
    }

    private static void RequireEuropean(FuturesOptionTerms terms)
    {
        if (terms.ExerciseStyle != FuturesOptionTerms.European)
            throw new MarketDataException("option_terms", $"The Black-76 formula prices European exercise only; the series states '{terms.ExerciseStyle}'.");
    }

    private static LatticeDecimal DiscountedIntrinsic(OptionRight right, LatticeDecimal futures, LatticeDecimal strike, LatticeDecimal discountToExpiry) =>
        discountToExpiry * LatticeDecimal.Max(right == OptionRight.Call ? futures - strike : strike - futures, Zero);
}
