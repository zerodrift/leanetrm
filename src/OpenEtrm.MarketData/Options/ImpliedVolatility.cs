using System.Globalization;

namespace OpenEtrm.MarketData.Options;

public abstract record OptionCalibration;

// Residual = price at Volatility - settlement. UpperBound is the effective top of
// the bracket: the policy's VolatilityMax, or lower where the tree's range ends.
public sealed record SolvedVolatility(LatticeDecimal Volatility, LatticeDecimal Residual, int Iterations, LatticeDecimal UpperBound)
    : OptionCalibration;

// Residual = settlement - intrinsic, at most one clearing increment below zero.
// Scenarios value the option as exercised: max(0, intrinsic at the scenario
// futures price + Residual), so a negative residual never makes a negative value.
public sealed record IntrinsicFallback(LatticeDecimal Intrinsic, LatticeDecimal Residual) : OptionCalibration;

// Finds the volatility at which the pinned tree reproduces an option's settlement
// price within half its clearing increment. Each input maps to one outcome, tested
// in this order:
//   1. invalid inputs or policy, before any price         -> option_input / option_numerical_policy / option_expired
//      (settlement and increment, the model's own input checks, then the bracket and iteration limit);
//   2. settlement < intrinsic - increment                 -> option_settlement_below_intrinsic;
//   3. settlement <= price at VolatilityMin               -> IntrinsicFallback when declared,
//                                                            else option_calibration_fallback_undeclared;
//   4. settlement > price at the effective upper bound    -> option_settlement_above_bound;
//   5. bisection within MaxIterations                     -> SolvedVolatility, else option_calibration_nonconvergent.
// Bisection on volatility is deterministic and needs no derivatives: each midpoint
// is (lo + hi) / 2 rounded half-even at 30 digits, and the first one within half an
// increment is returned.
public static class ImpliedVolatility
{
    public const string FallbackMode = "option_settlement_at_intrinsic_v1";

    private static readonly LatticeDecimal Zero = LatticeDecimal.Parse("0");
    private static readonly LatticeDecimal Two = LatticeDecimal.Parse("2");
    // The tree's extreme node stays below 10^23, a decade inside LatticeDecimal's range.
    private static readonly LatticeDecimal LnNodeLimit = LatticeDecimal.Parse("100000000000000000000000").Ln();

    public static OptionCalibration Solve(FuturesOptionTerms terms, LatticeDecimal settlement, LatticeDecimal futures,
        LatticeDecimal discountToExpiry, LatticeDecimal clearingIncrement, DateTimeOffset valuationAt, OptionNumericalPolicy policy,
        bool fallbackDeclared, CancellationToken ct)
    {
        if (settlement.CompareTo(Zero) < 0)
            throw new MarketDataException("option_input", $"The settlement price must not be negative; got {settlement}.");
        if (clearingIncrement.CompareTo(Zero) <= 0)
            throw new MarketDataException("option_input", $"The clearing increment must be positive; got {clearingIncrement}.");
        var years = AmericanFuturesOptionModel.ValidateInputs(terms, futures, discountToExpiry, valuationAt, policy);
        if (policy.MaxIterations < 1 || policy.VolatilityMin.CompareTo(Zero) <= 0 || policy.VolatilityMin.CompareTo(policy.VolatilityMax) >= 0)
            throw new MarketDataException("option_numerical_policy",
                $"Policy '{policy.Id}' needs at least one iteration and 0 < VolatilityMin < VolatilityMax.");

        var upperBound = UpperBound(futures, years, policy);
        if (upperBound.CompareTo(policy.VolatilityMin) <= 0)
            throw new MarketDataException("option_input",
                $"At futures {futures} the tree's range admits no volatility above VolatilityMin {policy.VolatilityMin}.");

        LatticeDecimal Price(LatticeDecimal volatility) =>
            AmericanFuturesOptionModel.UnitPrice(terms, futures, volatility, discountToExpiry, valuationAt, policy, ct);
        var series = $"{terms.Right} strike {terms.Strike} expiring {terms.ExpiryAt:O}";

        var strike = LatticeDecimal.From(terms.Strike);
        var intrinsic = LatticeDecimal.Max(terms.Right == OptionRight.Call ? futures - strike : strike - futures, Zero);
        if (settlement.CompareTo(intrinsic - clearingIncrement) < 0)
            throw new MarketDataException("option_settlement_below_intrinsic",
                $"{series}: settlement {settlement} is more than one increment ({clearingIncrement}) below intrinsic {intrinsic}.");

        var low = policy.VolatilityMin;
        if (settlement.CompareTo(Price(low)) <= 0)
            return fallbackDeclared ? new IntrinsicFallback(intrinsic, settlement - intrinsic)
                : throw new MarketDataException("option_calibration_fallback_undeclared",
                    $"{series}: settlement {settlement} is at or below the price at VolatilityMin {low}, and the valuation policy does not declare {FallbackMode}.");

        var high = upperBound;
        var highPrice = Price(high);
        if (settlement.CompareTo(highPrice) > 0)
            throw new MarketDataException("option_settlement_above_bound",
                $"{series}: settlement {settlement} is above {highPrice}, the price at the upper volatility bound {upperBound}.");

        var tolerance = clearingIncrement / Two;
        for (var iteration = 1; iteration <= policy.MaxIterations; iteration++)
        {
            var middle = (low + high) / Two;
            var residual = Price(middle) - settlement;
            if (LatticeDecimal.Max(residual, Zero - residual).CompareTo(tolerance) <= 0)
                return new SolvedVolatility(middle, residual, iteration, upperBound);
            if (residual.CompareTo(Zero) < 0) low = middle; else high = middle;
        }
        throw new MarketDataException("option_calibration_nonconvergent",
            $"{series}: no volatility within {policy.MaxIterations.ToString(CultureInfo.InvariantCulture)} halvings reprices settlement {settlement} within {tolerance}.");
    }

    // The largest volatility whose tree keeps F*exp(sigma*sqrt(Steps*T)) and
    // exp(sigma*sqrt(Steps*T)) itself below 10^23:
    // sigma = (ln 10^23 - max(ln F, 0)) / sqrt(Steps*T), capped at VolatilityMax.
    private static LatticeDecimal UpperBound(LatticeDecimal futures, LatticeDecimal years, OptionNumericalPolicy policy)
    {
        var steps = LatticeDecimal.Parse(policy.Steps.ToString(CultureInfo.InvariantCulture));
        var rangeLimit = (LnNodeLimit - LatticeDecimal.Max(futures.Ln(), Zero)) / (steps * years).Sqrt();
        return rangeLimit.CompareTo(policy.VolatilityMax) < 0 ? rangeLimit : policy.VolatilityMax;
    }
}
