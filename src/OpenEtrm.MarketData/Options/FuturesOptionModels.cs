namespace OpenEtrm.MarketData.Options;

// Chooses the model a listed futures option is priced by from the exercise style its series states: the tree its
// numerical policy names for American exercise, the Black-76 formula for European. A series is never priced as a
// style it is not: a policy that does not price the series' style is refused, not substituted.
public static class FuturesOptionModels
{
    public static void RequireStyle(FuturesOptionTerms terms, OptionNumericalPolicy policy)
    {
        if (ClosedForm(policy) != European(terms))
            throw new MarketDataException("option_terms", $"Numerical policy '{policy.Id}' does not price {terms.ExerciseStyle} exercise.");
    }

    // Value per unit of quote, before the contract multiplier.
    public static LatticeDecimal UnitPrice(FuturesOptionTerms terms, LatticeDecimal futures, LatticeDecimal volatility,
        LatticeDecimal discountToExpiry, DateTimeOffset valuationAt, OptionNumericalPolicy policy, CancellationToken ct)
    {
        RequireStyle(terms, policy);
        return ClosedForm(policy)
            ? EuropeanFuturesOptionModel.UnitPrice(terms, futures, volatility, discountToExpiry, valuationAt, policy)
            : AmericanFuturesOptionModel.UnitPrice(terms, futures, volatility, discountToExpiry, valuationAt, policy, ct);
    }

    // The move of the futures price across which a tree's value repeats its pattern (AmericanFuturesOptionModel), or
    // null: the formula is smooth in the futures price.
    public static LatticeDecimal? PriceStep(FuturesOptionTerms terms, LatticeDecimal futures, LatticeDecimal volatility,
        LatticeDecimal discountToExpiry, DateTimeOffset valuationAt, OptionNumericalPolicy policy)
    {
        RequireStyle(terms, policy);
        return ClosedForm(policy) ? null
            : AmericanFuturesOptionModel.PriceStep(terms, futures, volatility, discountToExpiry, valuationAt, policy);
    }

    // The model's value as the volatility goes to zero. An American option is exercised at once for intrinsic; a
    // European one is paid at expiry, so its intrinsic is discounted.
    public static LatticeDecimal Floor(FuturesOptionTerms terms, LatticeDecimal futures, LatticeDecimal discountToExpiry) =>
        European(terms) ? EuropeanFuturesOptionModel.ZeroVolatilityValue(terms, futures, discountToExpiry) : terms.Intrinsic(futures);

    // The least settlement a series is calibrated to or falls back from. On a tree it is intrinsic less one clearing
    // increment. A European settlement is struck with the market's discounting, not the selected curve's, so far in
    // the money it can lie below the curve's discount factor times intrinsic without being wrong: the bound takes
    // intrinsic at a discount factor lower by the policy's tolerance, less one increment.
    internal static LatticeDecimal LeastSettlement(FuturesOptionTerms terms, LatticeDecimal futures, LatticeDecimal discountToExpiry,
        LatticeDecimal clearingIncrement, OptionNumericalPolicy policy)
    {
        if (!European(terms)) return terms.Intrinsic(futures) - clearingIncrement;
        if (policy.DiscountTolerance is not { } tolerance || tolerance.CompareTo(default) < 0)
            throw new MarketDataException("option_numerical_policy", $"Policy '{policy.Id}' must state a discount tolerance that is not negative.");
        return (discountToExpiry - tolerance) * terms.Intrinsic(futures) - clearingIncrement;
    }

    // The checks on everything but volatility of the model that prices the series. Returns the year fraction to expiry.
    internal static LatticeDecimal ValidateInputs(FuturesOptionTerms terms, LatticeDecimal futures, LatticeDecimal discountToExpiry,
        DateTimeOffset valuationAt, OptionNumericalPolicy policy)
    {
        RequireStyle(terms, policy);
        return ClosedForm(policy)
            ? EuropeanFuturesOptionModel.ValidateInputs(terms, futures, discountToExpiry, valuationAt, policy)
            : AmericanFuturesOptionModel.ValidateInputs(terms, futures, discountToExpiry, valuationAt, policy);
    }

    private static bool European(FuturesOptionTerms terms) => terms.ExerciseStyle == FuturesOptionTerms.European;
    private static bool ClosedForm(OptionNumericalPolicy policy) => policy.Lattice == OptionLattice.ClosedForm;
}
