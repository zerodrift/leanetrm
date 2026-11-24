using System.Collections.Immutable;
using System.Text.Json.Nodes;
using OpenEtrm.MarketData;
using OpenEtrm.MarketData.Options;
using OpenEtrm.MarketData.Risk;
using OpenEtrm.MarketData.Trading;
using Market = FuturesOptionChecks.OptionMarket;

// A listed European option on a future (D446): the Black-76 formula against the independent 60-digit oracle and
// hand arithmetic. Every expected figure comes from the oracle fixture or from a value worked in the comment beside it.
internal static class EuropeanFuturesOptionChecks
{
    private static readonly DateTimeOffset ValuationAt = FuturesOptionChecks.ValuationAt;
    private static readonly OptionNumericalPolicy Policy = OptionNumericalPolicies.Require(OptionNumericalPolicies.Black76);
    private static readonly OptionNumericalPolicy Tree = OptionNumericalPolicies.Require(OptionNumericalPolicies.CoxRossRubinstein);
    private static readonly OptionRight[] Rights = [OptionRight.Call, OptionRight.Put];
    // The design asks for 20 decimal places. Observed: at most 1.5 units of the 28th place on the 17 cases, and 1.2 of
    // the 27th on the wide cases, whose strikes reach 3,276. The arithmetic is integer, so every host gives the same.
    private static readonly LatticeDecimal OracleBound = L("0.000000000000000000000000001");
    private static readonly LatticeDecimal WideBound = L("0.00000000000000000000000001");

    public static void Run()
    {
        Check("option terms admit American and European exercise and no other style", TermsAdmitTwoStyles);
        Check("the formula's numerical policy states its bracket, iteration limit and discount tolerance", FormulaPolicy);
        Check("the Black-76 formula equals the independent oracle to 27 decimal places", FormulaEqualsOracle);
        Check("put-call parity holds: call minus put is the discount times futures minus strike", PutCallParity);
        Check("the formula rises with volatility and never falls below the discounted intrinsic value", RisesWithVolatility);
        Check("far out of the money the formula is never negative", NeverNegative);
        Check("sixty seconds from expiry the formula returns discounted intrinsic in the money and zero out of it", SixtySecondsFromExpiry);
        Check("the formula refuses an American series, nonpositive inputs, a discount above one and an expired valuation", FormulaRefusals);
        Check("the tree refuses a European series and the formula's policy", TreeRefusesEuropean);
        Check("the wide oracle cases price without a range error, to 26 decimal places", WideCases);
        Check("each exercise style is priced by its own model and by no other", ModelFollowsStyle);
        Check("European implied volatility reprices each oracle settlement within half an increment", ImpliedVolatilityRepricesOracleSettlements);
        Check("a European bracket's top is the policy's, where the tree's range would lower it", BracketIsNeverLowered);
        Check("a settlement at discounted intrinsic falls back only when declared", FallbackAtDiscountedIntrinsic);
        Check("a deep in-the-money settlement struck at a slightly higher rate falls back with a negative residual", FallbackBelowDiscountedIntrinsic);
        Check("a settlement beyond the discount tolerance is refused", BeyondDiscountTolerance);
        Check("the discount tolerance admits a settlement struck half a point of rate above the curve for a year and refuses a full point", DiscountToleranceInRates);
        Check("an out-of-the-money zero settlement falls back to zero, and is refused when no fallback is declared", OutOfTheMoneyZero);
        Check("a settlement above the price at volatility three is refused", AboveBound);
        Check("a European calibration refuses invalid inputs and a policy without a discount tolerance", CalibrationRefusals);
        Check("the American solver's outcomes are unchanged", AmericanSolverUnchanged);
        Check("a European option's base unit PV equals its settlement times the multiplier", BasePvEqualsSettlement);
        Check("a European option declares its underlying, one discount input at expiry, its held volatility and no price step", DeclaredInputs);
        Check("a European option's time dependency runs from the calculation instant to expiry", TimeDependencyEndsAtExpiry);
        Check("one-tick revaluations give the oracle's delta within 0.000001 and gamma within 0.01 percent", OneTickSensitivities);
        Check("a European risk run's one-tick delta and gamma are the oracle's within its calibration tolerance", RiskRunSensitivities);
        Check("a European fallback values each scenario at the discount times scenario intrinsic plus the base residual, floored at zero", FallbackScenarios);
        Check("an out-of-the-money European series settling at zero is worth nothing in every scenario and needs the fallback declared", ZeroSettlementScenarios);
        Check("a European binding counts no tree price", NoTreePrice);
        Check("preparing a series with the other style's policy is refused", PreparedUnderOtherPolicy);
    }

    private static void TermsAdmitTwoStyles()
    {
        Equal(FuturesOptionTerms.American, "american");
        Equal(FuturesOptionTerms.European, "european");
        var expiry = FuturesOptionChecks.ExpiryAfter("0.25");
        Equal(Terms(OptionRight.Call, "3", expiry, "american").ExerciseStyle, "american");
        Equal(Terms(OptionRight.Call, "3", expiry, "european").ExerciseStyle, "european");
        Reject("option_terms", () => _ = Terms(OptionRight.Call, "3", expiry, "bermudan"));
    }

    private static void FormulaPolicy()
    {
        Equal(OptionNumericalPolicies.Black76, "european_futures_option_black76_v1");
        if (Policy is not { Id: OptionNumericalPolicies.Black76, Lattice: OptionLattice.ClosedForm, Steps: 0, MaxIterations: 30,
                DayCount: OptionNumericalPolicies.ActualOver365FixedInstant })
            throw new Exception($"The formula's policy changed: {Policy}.");
        Equal(Policy.VolatilityMin.ToString(), "0.01");
        Equal(Policy.VolatilityMax.ToString(), "3");
        Equal(Policy.PriceTolerance.ToString(), "0");
        Equal(Policy.DiscountTolerance?.ToString() ?? "none", "0.005");
        foreach (var tree in new[] { OptionNumericalPolicies.CoxRossRubinstein, OptionNumericalPolicies.LeisenReimer })
            if (OptionNumericalPolicies.Require(tree).DiscountTolerance is not null) throw new Exception($"{tree} states no discount tolerance.");
    }

    private static void FormulaEqualsOracle()
    {
        foreach (var c in FuturesOptionChecks.Black76Cases())
        {
            var label = $"F={c.Futures} K={c.Strike} sigma={c.Volatility} T={c.Years}";
            Within(Price(c, OptionRight.Call, c.Discount), L(c.Call), OracleBound, $"call {label}");
            Within(Price(c, OptionRight.Put, c.Discount), L(c.Put), OracleBound, $"put {label}");
            Within(Price(c, OptionRight.Call, "1"), L(c.UndiscountedCall), OracleBound, $"undiscounted call {label}");
            Within(Price(c, OptionRight.Put, "1"), L(c.UndiscountedPut), OracleBound, $"undiscounted put {label}");
        }
    }

    private static void PutCallParity()
    {
        foreach (var c in FuturesOptionChecks.Black76Cases())
            Within(Price(c, OptionRight.Call, c.Discount) - Price(c, OptionRight.Put, c.Discount),
                L(c.Discount) * (L(c.Futures) - L(c.Strike)), OracleBound, $"parity F={c.Futures} K={c.Strike} sigma={c.Volatility} T={c.Years}");
    }

    // Out of, at and in the money for each right, a quarter of a year out at a discount of 0.98. At a volatility of 0.01
    // the two away from the money are worth exactly their zero-volatility value.
    private static void RisesWithVolatility()
    {
        var expiry = FuturesOptionChecks.ExpiryAfter("0.25");
        foreach (var right in Rights)
            foreach (var (futures, strike) in new[] { ("3", "3.25"), ("3", "3"), ("3.25", "3") })
            {
                var floor = EuropeanFuturesOptionModel.ZeroVolatilityValue(Terms(right, strike, expiry), L(futures), L("0.98"));
                var values = new[] { "0.01", "0.2", "0.5", "0.9", "3" }.Select(v => Price(right, futures, strike, v, "0.25", "0.98")).ToArray();
                for (var i = 0; i < values.Length; i++)
                    if (values[i].CompareTo(floor) < 0 || i > 0 && values[i].CompareTo(values[i - 1]) <= 0)
                        throw new Exception($"{right} F={futures} K={strike} must rise from {floor}: {string.Join(", ", values)}.");
            }
        // Discount times intrinsic: 0.98 × (3.25 − 3) for the put and nothing for the call.
        Equal(EuropeanFuturesOptionModel.ZeroVolatilityValue(Terms(OptionRight.Put, "3.25", expiry), L("3"), L("0.98")).ToString(), "0.245");
        Equal(EuropeanFuturesOptionModel.ZeroVolatilityValue(Terms(OptionRight.Call, "3.25", expiry), L("3"), L("0.98")).ToString(), "0");
    }

    // Eleven and a half deviations out, both normal probabilities are about one unit of the 30th digit. Their rounded
    // difference must not make a call struck above twice the futures price worth less than nothing.
    private static void NeverNegative()
    {
        var zero = L("0");
        for (var step = 0; step <= 400; step++)
        {
            var volatility = L("0.06") + L("0.00005") * L(step.ToString(System.Globalization.CultureInfo.InvariantCulture));
            var value = Price(OptionRight.Call, "3", "6.7", volatility.ToString(), "1", "1");
            if (value.CompareTo(zero) < 0) throw new Exception($"A call at volatility {volatility} is worth {value}.");
        }
    }

    // F = 5, K = 3, D = 0.99, volatility 0.5, one minute out: volatility × √T is 0.0007 and ln(F ÷ K) ÷ that is over 700
    // deviations, so the call is worth D × (F − K) = 1.98 and the put nothing.
    private static void SixtySecondsFromExpiry()
    {
        var expiry = ValuationAt.AddSeconds(60);
        LatticeDecimal At(OptionRight right, string futures, string strike) => EuropeanFuturesOptionModel.UnitPrice(
            Terms(right, strike, expiry), L(futures), L("0.5"), L("0.99"), ValuationAt, Policy);
        var bound = L("0.000000000001");
        Within(At(OptionRight.Call, "5", "3"), L("1.98"), bound, "call in the money");
        Within(At(OptionRight.Put, "5", "3"), L("0"), bound, "put out of the money");
        Within(At(OptionRight.Put, "3", "5"), L("1.98"), bound, "put in the money");
        Within(At(OptionRight.Call, "3", "5"), L("0"), bound, "call out of the money");
        // One tick of the clock from expiry, at the bracket's lowest volatility and a futures price far from the strike.
        var tick = EuropeanFuturesOptionModel.UnitPrice(Terms(OptionRight.Call, "0.01", ValuationAt.AddTicks(1)),
            L("100000"), L("0.01"), L("1"), ValuationAt, Policy);
        Equal(tick.ToString(), "99999.99");
    }

    private static void FormulaRefusals()
    {
        var expiry = FuturesOptionChecks.ExpiryAfter("0.25");
        var european = Terms(OptionRight.Call, "3", expiry);
        LatticeDecimal At(FuturesOptionTerms terms, string futures, string volatility, string discount, OptionNumericalPolicy policy) =>
            EuropeanFuturesOptionModel.UnitPrice(terms, L(futures), L(volatility), L(discount), ValuationAt, policy);
        Reject("option_terms", () => At(Terms(OptionRight.Call, "3", expiry, FuturesOptionTerms.American), "3", "0.5", "0.99", Policy));
        Reject("option_input", () => At(european, "0", "0.5", "0.99", Policy));
        Reject("option_input", () => At(european, "-1", "0.5", "0.99", Policy));
        Reject("option_input", () => At(Terms(OptionRight.Call, "0", expiry), "3", "0.5", "0.99", Policy));
        Reject("option_input", () => At(european, "3", "0", "0.99", Policy));
        Reject("option_input", () => At(european, "3", "-0.5", "0.99", Policy));
        Reject("option_input", () => At(european, "3", "0.5", "0", Policy));
        Reject("option_input", () => At(european, "3", "0.5", "1.01", Policy));
        Reject("option_expired", () => At(Terms(OptionRight.Call, "3", ValuationAt), "3", "0.5", "0.99", Policy));
        Reject("option_expired", () => At(Terms(OptionRight.Call, "3", ValuationAt.AddTicks(-1)), "3", "0.5", "0.99", Policy));
        Reject("option_numerical_policy", () => At(european, "3", "0.5", "0.99", Tree));
        Reject("option_numerical_policy", () => At(european, "3", "0.5", "0.99", Policy with { DayCount = "act_360" }));
        Reject("option_input", () => EuropeanFuturesOptionModel.ZeroVolatilityValue(european, L("0"), L("0.99")));
        Reject("option_input", () => EuropeanFuturesOptionModel.ZeroVolatilityValue(european, L("3"), L("1.01")));
        Reject("option_terms", () => EuropeanFuturesOptionModel.ZeroVolatilityValue(Terms(OptionRight.Call, "3", expiry, FuturesOptionTerms.American), L("3"), L("0.99")));
    }

    private static void TreeRefusesEuropean()
    {
        var expiry = FuturesOptionChecks.ExpiryAfter("0.25");
        Reject("option_terms", () => AmericanFuturesOptionModel.UnitPrice(Terms(OptionRight.Call, "3", expiry), L("3"), L("0.5"), L("1"),
            ValuationAt, Tree, CancellationToken.None));
        Reject("option_terms", () => AmericanFuturesOptionModel.PriceStep(Terms(OptionRight.Call, "3", expiry), L("3"), L("0.5"), L("1"), ValuationAt, Tree));
        var american = Terms(OptionRight.Call, "3", expiry, FuturesOptionTerms.American);
        Reject("option_numerical_policy", () => AmericanFuturesOptionModel.UnitPrice(american, L("3"), L("0.5"), L("1"), ValuationAt, Policy, CancellationToken.None));
        Reject("option_numerical_policy", () => AmericanFuturesOptionModel.UnitPrice(american, L("3"), L("0.5"), L("1"), ValuationAt,
            Policy with { Steps = 400 }, CancellationToken.None));
    }

    // Strikes 7 and 55 times the futures price at volatilities of 1 and 2: the trees' range ends before these, the
    // formula has none.
    private static void WideCases()
    {
        var rows = Oracle["black76_wide"]!.AsArray();
        if (rows.Count != 2) throw new Exception($"Expected 2 wide cases; found {rows.Count}.");
        foreach (var row in rows)
        {
            string Field(string name) => row![name]!.GetValue<string>();
            Within(Price(OptionRight.Call, Field("futures"), Field("strike"), Field("volatility"), Field("years"), "1"),
                L(Field("undiscounted_call")), WideBound, $"wide call K={Field("strike")}");
        }
    }

    private static void ModelFollowsStyle()
    {
        var expiry = FuturesOptionChecks.ExpiryAfter("0.25");
        var european = Terms(OptionRight.Call, "3", expiry);
        var american = Terms(OptionRight.Call, "3", expiry, FuturesOptionTerms.American);
        LatticeDecimal At(FuturesOptionTerms terms, OptionNumericalPolicy policy) =>
            FuturesOptionModels.UnitPrice(terms, L("5"), L("0.5"), L("0.99"), ValuationAt, policy, CancellationToken.None);
        Equal(At(european, Policy).ToString(), EuropeanFuturesOptionModel.UnitPrice(european, L("5"), L("0.5"), L("0.99"), ValuationAt, Policy).ToString());
        Equal(At(american, Tree).ToString(),
            AmericanFuturesOptionModel.UnitPrice(american, L("5"), L("0.5"), L("0.99"), ValuationAt, Tree, CancellationToken.None).ToString());
        // An American call can be exercised at once for F − K = 2, so it is worth at least that. The European one is
        // paid at expiry and is worth less.
        if (At(american, Tree).CompareTo(L("2")) < 0 || At(european, Policy).CompareTo(L("2")) >= 0)
            throw new Exception($"Expected the American value {At(american, Tree)} at or above 2 and the European value {At(european, Policy)} below it.");
        foreach (var (terms, policy) in new[] { (european, Tree), (american, Policy) })
        {
            Reject("option_terms", () => FuturesOptionModels.RequireStyle(terms, policy));
            Reject("option_terms", () => At(terms, policy));
            Reject("option_terms", () => FuturesOptionModels.PriceStep(terms, L("5"), L("0.5"), L("0.99"), ValuationAt, policy));
            Reject("option_terms", () => ImpliedVolatility.Solve(terms, L("2.1"), L("5"), L("0.99"), L("0.001"), ValuationAt, policy, true, CancellationToken.None));
        }
        FuturesOptionModels.RequireStyle(european, Policy);
        FuturesOptionModels.RequireStyle(american, Tree);
        if (FuturesOptionModels.PriceStep(european, L("5"), L("0.5"), L("0.99"), ValuationAt, Policy) is not null)
            throw new Exception("The formula has no price step.");
        Equal(FuturesOptionModels.PriceStep(american, L("5"), L("0.5"), L("0.99"), ValuationAt, Tree)?.ToString() ?? "none",
            AmericanFuturesOptionModel.PriceStep(american, L("5"), L("0.5"), L("0.99"), ValuationAt, Tree)!.Value.ToString());
        // The value at zero volatility: intrinsic 5 − 3 on the tree, 0.99 times it in the formula.
        Equal(FuturesOptionModels.Floor(american, L("5"), L("0.99")).ToString(), "2");
        Equal(FuturesOptionModels.Floor(european, L("5"), L("0.99")).ToString(), "1.98");
        Equal(ImpliedVolatility.FallbackModeFor(american), "option_settlement_at_intrinsic_v1");
        Equal(ImpliedVolatility.FallbackModeFor(european), "option_settlement_at_discounted_intrinsic_v1");
        Equal(ImpliedVolatility.DiscountedFallbackMode, "option_settlement_at_discounted_intrinsic_v1");
    }

    private const string Increment = "0.001";

    private static OptionCalibration Calibrate(OptionRight right, string futures, string strike, string years, string discount,
        string settlement, bool fallbackDeclared, OptionNumericalPolicy? policy = null) =>
        ImpliedVolatility.Solve(Terms(right, strike, FuturesOptionChecks.ExpiryAfter(years)), L(settlement), L(futures), L(discount),
            L(Increment), ValuationAt, policy ?? Policy, fallbackDeclared, CancellationToken.None);

    // Each settlement is the oracle's own Black-76 value, rounded to the clearing increment as an exchange publishes
    // it. The solver must find the oracle's volatility again: the rounding moves it by at most half an increment over
    // the option's vega, which is under 0.005 on these eight series.
    private static void ImpliedVolatilityRepricesOracleSettlements()
    {
        (OptionRight Right, int Case)[] series =
        [
            (OptionRight.Call, 0), (OptionRight.Put, 1), (OptionRight.Call, 2), (OptionRight.Put, 5),
            (OptionRight.Put, 6), (OptionRight.Call, 7), (OptionRight.Call, 9), (OptionRight.Put, 11),
        ];
        var cases = FuturesOptionChecks.Black76Cases();
        foreach (var (right, index) in series)
        {
            var c = cases[index];
            var settlement = FuturesOptionChecks.RoundToIncrement(L(right == OptionRight.Call ? c.Call : c.Put), Increment);
            var label = $"{right} F={c.Futures} K={c.Strike} T={c.Years} sigma={c.Volatility} settlement {settlement}";
            var solved = Calibrate(right, c.Futures, c.Strike, c.Years, c.Discount, settlement, fallbackDeclared: false) as SolvedVolatility
                ?? throw new Exception($"{label}: expected a solved volatility.");
            Within(solved.Residual, L("0"), L("0.0005"), $"{label} residual");
            Equal(solved.Residual.ToString(), (Price(right, c.Futures, c.Strike, solved.Volatility.ToString(), c.Years, c.Discount) - L(settlement)).ToString());
            Within(solved.Volatility, L(c.Volatility), L("0.005"), $"{label} volatility");
            Equal(solved.UpperBound.ToString(), "3");
            if (solved.Iterations < 1 || solved.Iterations > Policy.MaxIterations)
                throw new Exception($"{label}: {solved.Iterations} iterations is outside 1..{Policy.MaxIterations}.");
        }
    }

    // F = 80 over two years: the rebuilt tree's range ends at a volatility of about 1.72 (FuturesOptionChecks). The
    // formula has no range, so a settlement priced at 2.5 solves under a bracket whose top is still 3.
    private static void BracketIsNeverLowered()
    {
        var settlement = FuturesOptionChecks.RoundToIncrement(Price(OptionRight.Call, "80", "80", "2.5", "2", "0.9"), Increment);
        var solved = (SolvedVolatility)Calibrate(OptionRight.Call, "80", "80", "2", "0.9", settlement, fallbackDeclared: false);
        Equal(solved.UpperBound.ToString(), "3");
        Within(solved.Volatility, L("2.5"), L("0.001"), "volatility above the tree's range");
    }

    // The hand case of the next three checks: a call with F = 5, K = 3 and D = 0.99, a quarter of a year out. At the
    // bracket's lowest volatility it is worth its discounted intrinsic value, 0.99 × 2 = 1.98.
    private static OptionCalibration DeepCall(string settlement, bool fallbackDeclared) =>
        Calibrate(OptionRight.Call, "5", "3", "0.25", "0.99", settlement, fallbackDeclared);

    private static void FallbackAtDiscountedIntrinsic()
    {
        var fallback = DeepCall("1.98", true) as IntrinsicFallback ?? throw new Exception("Expected the fallback at discounted intrinsic.");
        Equal(fallback.Intrinsic.ToString(), "1.98");
        Equal(fallback.Residual.ToString(), "0");
        Reject("option_calibration_fallback_undeclared", () => DeepCall("1.98", false));
    }

    // A market discount factor of 0.9875 against the curve's 0.99 strikes the settlement at 0.9875 × 2 = 1.975: it is
    // 0.005 below the curve's discounted intrinsic and inside the tolerance.
    private static void FallbackBelowDiscountedIntrinsic()
    {
        var fallback = DeepCall("1.975", true) as IntrinsicFallback ?? throw new Exception("Expected the fallback below discounted intrinsic.");
        Equal(fallback.Intrinsic.ToString(), "1.98");
        Equal(fallback.Residual.ToString(), "-0.005");
        Reject("option_calibration_fallback_undeclared", () => DeepCall("1.975", false));
    }

    // The least settlement admitted is (0.99 − 0.005) × 2 less one increment: 1.97 − 0.001 = 1.969.
    private static void BeyondDiscountTolerance()
    {
        Equal(((IntrinsicFallback)DeepCall("1.969", true)).Residual.ToString(), "-0.011");
        Reject("option_settlement_below_intrinsic", () => DeepCall("1.968999", true));
        Reject("option_settlement_below_intrinsic", () => DeepCall("1.96", true));
        Reject("option_settlement_below_intrinsic", () => DeepCall("1.96", false));
    }

    // The evidence for the tolerance of 0.005, as rates. A call with F = 150 and K = 75 is worth its intrinsic 75
    // discounted. Each settlement is 75 × exp(−market rate × T) rounded to the increment, against a curve at 4%:
    //   one year, curve factor exp(−0.04) = 0.960789: least admitted (0.960789 − 0.005) × 75 − 0.001 = 71.683175;
    //     a market rate of 4.5% settles at 75 × 0.955997 = 71.700 and falls back, 5% at 71.342 and is refused;
    //   a quarter, curve factor exp(−0.01) = 0.990050: least admitted 0.985050 × 75 − 0.001 = 73.877750;
    //     a market rate of 6% settles at 75 × 0.985112 = 73.883 and falls back, 6.5% at 73.791 and is refused.
    private static void DiscountToleranceInRates()
    {
        OptionCalibration Deep(string years, string discount, string settlement) =>
            Calibrate(OptionRight.Call, "150", "75", years, discount, settlement, fallbackDeclared: true);
        // 71.700 − 0.960789 × 75 and 73.883 − 0.990050 × 75.
        Equal(((IntrinsicFallback)Deep("1", "0.960789", "71.7")).Residual.ToString(), "-0.359175");
        Reject("option_settlement_below_intrinsic", () => Deep("1", "0.960789", "71.342"));
        Equal(((IntrinsicFallback)Deep("0.25", "0.99005", "73.883")).Residual.ToString(), "-0.37075");
        Reject("option_settlement_below_intrinsic", () => Deep("0.25", "0.99005", "73.791"));
    }

    private static void OutOfTheMoneyZero()
    {
        var fallback = Calibrate(OptionRight.Call, "3", "4", "0.1", "0.995", "0", fallbackDeclared: true) as IntrinsicFallback
            ?? throw new Exception("Expected the fallback for an out-of-the-money zero settlement.");
        Equal(fallback.Intrinsic.ToString(), "0");
        Equal(fallback.Residual.ToString(), "0");
        Reject("option_calibration_fallback_undeclared", () => Calibrate(OptionRight.Call, "3", "4", "0.1", "0.995", "0", fallbackDeclared: false));
    }

    // A European call is worth less than the discounted futures price, a put less than the discounted strike; at a
    // volatility of 3 the at-the-money pair is worth about 1.62.
    private static void AboveBound()
    {
        Reject("option_settlement_above_bound", () => Calibrate(OptionRight.Call, "3", "3", "0.25", "0.99", "2.9", fallbackDeclared: true));
        Reject("option_settlement_above_bound", () => Calibrate(OptionRight.Put, "3", "3", "0.25", "0.99", "2.9", fallbackDeclared: true));
        _ = (SolvedVolatility)Calibrate(OptionRight.Call, "3", "3", "0.25", "0.99", "1.6", fallbackDeclared: false);
    }

    private static void CalibrationRefusals()
    {
        Reject("option_input", () => Calibrate(OptionRight.Call, "3", "3", "0.25", "0.99", "-0.001", true));
        Reject("option_input", () => Calibrate(OptionRight.Call, "0", "3", "0.25", "0.99", "0.3", true));
        Reject("option_input", () => Calibrate(OptionRight.Call, "3", "3", "0.25", "1.01", "0.3", true));
        Reject("option_expired", () => Calibrate(OptionRight.Call, "3", "3", "0", "0.99", "0.3", true));
        Reject("option_numerical_policy", () => Calibrate(OptionRight.Call, "3", "3", "0.25", "0.99", "0.3", true, Policy with { MaxIterations = 0 }));
        Reject("option_numerical_policy", () => Calibrate(OptionRight.Call, "3", "3", "0.25", "0.99", "0.3", true, Policy with { DiscountTolerance = null }));
        Reject("option_numerical_policy", () => Calibrate(OptionRight.Call, "3", "3", "0.25", "0.99", "0.3", true, Policy with { DiscountTolerance = L("-0.001") }));
        Reject("option_calibration_nonconvergent", () => Calibrate(OptionRight.Call, "3", "3", "0.25", "0.99", "0.295", true, Policy with { MaxIterations = 1 }));
    }

    // Outcomes FuturesOptionChecks pins for the tree, read again through the shared solver: the solved at-the-money gas
    // call, the fallback and the refusal against undiscounted intrinsic, and the bracket lowered to the tree's range.
    private static void AmericanSolverUnchanged()
    {
        OptionCalibration American(OptionRight right, string futures, string strike, string years, string discount, string settlement) =>
            ImpliedVolatility.Solve(Terms(right, strike, FuturesOptionChecks.ExpiryAfter(years), FuturesOptionTerms.American), L(settlement), L(futures),
                L(discount), L(Increment), ValuationAt, Tree, true, CancellationToken.None);
        var solved = (SolvedVolatility)American(OptionRight.Call, "3", "3", "0.25", "0.99", "0.295");
        Equal(solved.Volatility.ToString(), "0.4990869140625");
        Equal(solved.Residual.ToString(), "0.00022824699970030074018428749");
        Equal(solved.UpperBound.ToString(), "3");
        Equal(solved.Iterations.ToString(System.Globalization.CultureInfo.InvariantCulture), "11");
        const string twoDays = "0.005479452054794520547945205479";
        var fallback = (IntrinsicFallback)American(OptionRight.Put, "2", "3", twoDays, "0.9998", "0.9995");
        Equal(fallback.Intrinsic.ToString(), "1");
        Equal(fallback.Residual.ToString(), "-0.0005");
        Reject("option_settlement_below_intrinsic", () => American(OptionRight.Put, "2", "3", twoDays, "0.9998", "0.998999"));
        Reject("option_settlement_above_bound", () => American(OptionRight.Call, "80", "80", "2", "0.9", "79"));
    }

    private static readonly ExplicitDiscounting Undiscounted = new(ExactDecimal.Parse("1"), DiscountFactorSource.Undiscounted);
    private static ExactDecimal D(string value) => ExactDecimal.Parse(value);
    private static RiskPositionResult Position(RiskRun run, int trade) => run.Factors[0].Positions.Single(p => p.TradeId == Market.G(1000 + trade));
    private static FuturesOptionCalibrationReceipt Receipt(RiskRun run, int contract) =>
        run.OptionCalibrations.Single(r => r.Series.Code == Market.SeriesCode(contract));
    private static void Same(ExactDecimal actual, ExactDecimal expected, string label)
    { if (actual != expected) throw new Exception($"{label}: expected {expected}; got {actual}."); }

    // The fixture at its discount of 0.999 and a futures price of 3. The call struck at 2 settling at 1 lies above its
    // discounted intrinsic of 0.999, so it has a volatility; settling at 0.999 it has none.
    private static void BasePvEqualsSettlement()
    {
        var market = new Market();
        var run = market.Calculate(
            [Market.Holding(1, 1, market.EuropeanOption(11), "2"), Market.Holding(2, 1, market.EuropeanOption(12), "1"),
             Market.Holding(3, 1, market.EuropeanOption(13), "3"), Market.Holding(4, 1, market.EuropeanOption(14), "1"),
             Market.Holding(5, 2, market.EuropeanOption(15), "-2")], [market.FuturesBump], policy: Policy);
        foreach (var (trade, contract, settlement, lots, mode) in new[]
                 {
                     (1, 11, "0.08", "2", FuturesOptionPricing.SolvedMode), (2, 12, "0.08", "1", FuturesOptionPricing.SolvedMode),
                     (3, 13, "1", "3", FuturesOptionPricing.SolvedMode), (4, 14, "0", "1", ImpliedVolatility.DiscountedFallbackMode),
                     (5, 15, "0.999", "-2", ImpliedVolatility.DiscountedFallbackMode),
                 })
        {
            var receipt = Receipt(run, contract);
            Equal(receipt.Mode, mode);
            Equal(receipt.NumericalPolicy, OptionNumericalPolicies.Black76);
            // Calibration reprices the settlement within half an increment: 10,000 × 0.0005 = 5 a lot. A fallback is exact.
            var expected = D(settlement) * Market.Multiplier * D(lots);
            var bound = mode == FuturesOptionPricing.SolvedMode ? D("5") * D(lots) : default;
            var difference = Position(run, trade).Base.Amount - expected;
            if (difference.CompareTo(bound) > 0 || (default(ExactDecimal) - difference).CompareTo(bound) > 0)
                throw new Exception($"Trade {trade}: base {Position(run, trade).Base.Amount} is not settlement value {expected} within {bound}.");
        }
        // The zero-volatility value recorded for the fallback at 0.999 is 0.999 × (3 − 2), not the undiscounted 1.
        Equal(Receipt(run, 15).Intrinsic ?? "none", "0.999");
        Equal(Receipt(run, 15).Residual, "0");
    }

    private static void DeclaredInputs()
    {
        var market = new Market();
        var run = market.Calculate([Market.Holding(1, 1, market.EuropeanOption(11), "1"), Market.Holding(2, 1, market.EuropeanOption(14), "1")],
            [market.FuturesBump], policy: Policy);
        if (run.Contracts.Length != 2) throw new Exception($"Expected two option contracts; found {run.Contracts.Length}.");
        foreach (var contract in run.Contracts)
        {
            var receipt = run.OptionCalibrations.Single(r => r.InstrumentId == contract.Id);
            Equal(contract.ModelVersion, OptionNumericalPolicies.Black76);
            if (contract.Dependencies is not [var dependency] || dependency != market.UnderlyingRequest)
                throw new Exception("A European option declares exactly its underlying futures request.");
            if (contract.DiscountInputs is not [{ Payment: { PaymentAt: var paid, Timing: ValueTimingKind.Instant } }] || paid != Market.Expiry)
                throw new Exception("A European option declares one discount input at its expiry instant.");
            if (!contract.PriceSteps.IsEmpty) throw new Exception("The formula declares no price step.");
            var solved = receipt.Mode == FuturesOptionPricing.SolvedMode;
            if (solved ? contract.Volatility is not [{ Convention: VolatilityInput.Annualized } held] || held.Value != receipt.Volatility
                    : !contract.Volatility.IsEmpty)
                throw new Exception("A solved option declares its held volatility; a fallback declares none.");
        }
    }

    // An American series' value can change at its instruction cutoff, so its interval ends there. A European series
    // is exercised only at expiry: a stated cutoff does not end its interval.
    private static void TimeDependencyEndsAtExpiry()
    {
        var market = new Market();
        var cutoff = Market.Expiry.AddHours(-2);
        ValuationTimeDependency Time(FuturesOptionInstrument option, OptionNumericalPolicy policy) =>
            market.Calculate([Market.Holding(1, 1, option, "1")], [market.FuturesBump], policy: policy).Contracts.Single().Time;
        foreach (var stated in new DateTimeOffset?[] { null, cutoff })
        {
            var time = Time(market.EuropeanOption(11, stated), Policy);
            if (time.Use != ValuationTimeDependency.TimeUse.Interval || time.From != Market.At || time.To != Market.Expiry)
                throw new Exception($"A European option's interval must run from {Market.At:O} to {Market.Expiry:O}; got {time.From:O} to {time.To:O}.");
        }
        if (Time(market.Option(11), Tree).To != cutoff) throw new Exception("An American option's interval still ends at its instruction cutoff.");
        // A fallback has no model time: it stays pinned to its source instant.
        if (Time(market.EuropeanOption(14), Policy).Use != ValuationTimeDependency.TimeUse.Pinned)
            throw new Exception("A European fallback is pinned to its source instant.");
    }

    private sealed record Greeks(LatticeDecimal Volatility, LatticeDecimal Delta, LatticeDecimal Gamma);

    // The oracle's sensitivity case is the fixture's at-the-money call at a unit discount.
    private static Greeks OracleGreeks()
    {
        var row = Oracle["black76_greeks"]!.AsArray().Single()!;
        LatticeDecimal Field(string name) => L(row[name]!.GetValue<string>());
        if (row["futures"]!.GetValue<string>() != Market.Futures || row["strike"]!.GetValue<string>() != "3" || row["call"]!.GetValue<string>() != "0.08" ||
            row["seconds"]!.GetValue<long>() != (long)(Market.Expiry - Market.At).TotalSeconds)
            throw new Exception("The oracle's sensitivity case is not the fixture's at-the-money call.");
        return new(Field("volatility"), Field("delta_call"), Field("gamma"));
    }

    private static FuturesOptionMarketBinding Prepare(Market market, FuturesOptionInstrument option, ISideDiscounting discounting,
        OptionNumericalPolicy policy, bool fallbackDeclared = true)
    {
        var source = market.Snapshot(); var model = market.Model();
        var requests = FuturesOptionPricing.Requests(model, option.Series, FuturesOptionPricing.DailySettlement);
        return FuturesOptionPricing.Prepare(option, model.Build(source, [requests.Underlying, requests.Settlement]), source, discounting,
            FuturesOptionPricing.DailySettlement, policy, fallbackDeclared, CancellationToken.None);
    }

    // The binding's value at the oracle's own volatility, one tick of 0.001 either side of the futures price.
    private static void OneTickSensitivities()
    {
        var oracle = OracleGreeks(); var market = new Market();
        var binding = Prepare(market, market.EuropeanOption(11), Undiscounted, Policy);
        LatticeDecimal At(string futures) => binding.UnitPrice(L(futures), Market.At, CancellationToken.None, L("1"), oracle.Volatility);
        var (down, centre, up, tick) = (At("2.999"), At("3"), At("3.001"), L("0.001"));
        Within(centre, L("0.08"), L("0.00000000000000000001"), "value at the oracle's volatility");
        Within((up - down) / (tick + tick), oracle.Delta, L("0.000001"), "delta");
        Within((up + down - centre - centre) / (tick * tick), oracle.Gamma, oracle.Gamma * L("0.0001"), "gamma");
    }

    // The same through the shared base, up and down scenarios, at the volatility the calibration holds. That one
    // reprices 0.08 within half an increment of 0.001, so it lies within 0.0005 ÷ vega (0.163) = 0.0031 of the oracle's:
    // a delta within 0.001 and a gamma within 1%. One lot is 10,000 units.
    private static void RiskRunSensitivities()
    {
        var oracle = OracleGreeks(); var market = new Market();
        var run = market.Calculate([Market.Holding(1, 1, market.EuropeanOption(11), "1")], [market.FuturesBump], discounting: Undiscounted, policy: Policy);
        Within(L(Receipt(run, 11).Volatility!), oracle.Volatility, L("0.0031"), "held volatility");
        var position = Position(run, 1);
        Within(LatticeDecimal.From(position.Delta.Value) / L("10000"), oracle.Delta, L("0.001"), "delta");
        Within(LatticeDecimal.From(position.Gamma.Value) / L("10000"), oracle.Gamma, oracle.Gamma * L("0.01"), "gamma");
    }

    // The call struck at 2 settles at 0.999 with F = 3. At a discount of 0.9995 its zero-volatility value is 0.9995, so
    // the residual is −0.0005. A shock of 1.25 either way, one lot of 10,000:
    //   base  0.9995 × 1 − 0.0005 = 0.999        ->  9,990
    //   up    0.9995 × 2.25 − 0.0005 = 2.248375  -> 22,483.75
    //   down  0.9995 × 0 − 0.0005 < 0            ->      0
    private static void FallbackScenarios()
    {
        var market = new Market();
        var run = market.Calculate([Market.Holding(1, 1, market.EuropeanOption(15), "1")], [market.FuturesBumpOf("1.25")],
            discounting: new ExplicitDiscounting(D("0.9995"), DiscountFactorSource.Explicit), policy: Policy);
        var receipt = run.OptionCalibrations.Single();
        Equal(receipt.Mode, ImpliedVolatility.DiscountedFallbackMode);
        Equal(receipt.Intrinsic ?? "none", "0.9995");
        Equal(receipt.Residual, "-0.0005");
        Same(Position(run, 1).Base.Amount, D("9990"), "base");
        Same(Position(run, 1).Up.Amount, D("22483.75"), "up");
        Same(Position(run, 1).Down.Amount, D("0"), "floored down");
    }

    private static void ZeroSettlementScenarios()
    {
        var market = new Market();
        ImmutableArray<Trade> trades = [Market.Holding(1, 1, market.EuropeanOption(14), "3")];
        var run = market.Calculate(trades, [market.FuturesBumpOf("1.25")], policy: Policy);
        Equal(run.OptionCalibrations.Single().Mode, ImpliedVolatility.DiscountedFallbackMode);
        foreach (var value in new[] { Position(run, 1).Base.Amount, Position(run, 1).Up.Amount, Position(run, 1).Down.Amount })
            Same(value, D("0"), "a zero settlement out of the money");
        try { market.Calculate(trades, [market.FuturesBump], fallbackDeclared: false, policy: Policy); }
        catch (MarketDataException error) when (error.Code == "option_calibration_fallback_undeclared")
        {
            if (!error.Message.Contains(Market.SeriesCode(14), StringComparison.Ordinal) || !error.Message.Contains(ImpliedVolatility.DiscountedFallbackMode, StringComparison.Ordinal))
                throw new Exception("The refusal must name the series and the fallback it needs declared.");
            return;
        }
        throw new Exception("Expected option_calibration_fallback_undeclared.");
    }

    private static void NoTreePrice()
    {
        var market = new Market();
        var european = Prepare(market, market.EuropeanOption(11), Undiscounted, Policy);
        var american = Prepare(market, market.Option(11), Undiscounted, Tree);
        foreach (var binding in new[] { european, american })
            _ = binding.UnitPrice(L("3.001"), Market.At, CancellationToken.None, L("1"), ((SolvedVolatility)binding.Calibration).Volatility);
        Equal(european.TreePrices.ToString(System.Globalization.CultureInfo.InvariantCulture), "0");
        Equal(american.TreePrices.ToString(System.Globalization.CultureInfo.InvariantCulture), "1");
    }

    private static void PreparedUnderOtherPolicy()
    {
        var market = new Market();
        foreach (var (option, policy) in new[] { (market.EuropeanOption(11), Tree), (market.Option(11), Policy) })
            try { Prepare(market, option, Undiscounted, policy); throw new Exception("Expected option_terms."); }
            catch (MarketDataException error) when (error.Code == "option_terms")
            {
                if (!error.Message.Contains(Market.SeriesCode(11), StringComparison.Ordinal)) throw new Exception("The refusal must name the series.");
            }
    }

    private static JsonNode Oracle => JsonNode.Parse(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "fixtures", "futures-option-oracle.json")))!;

    private static FuturesOptionTerms Terms(OptionRight right, string strike, DateTimeOffset expiry, string style = FuturesOptionTerms.European) =>
        new(right, ExactDecimal.Parse(strike), expiry, style, "exercise_into_future");

    private static LatticeDecimal Price(OptionRight right, string futures, string strike, string volatility, string years, string discount) =>
        EuropeanFuturesOptionModel.UnitPrice(Terms(right, strike, FuturesOptionChecks.ExpiryAfter(years)), L(futures), L(volatility), L(discount), ValuationAt, Policy);

    private static LatticeDecimal Price(FuturesOptionChecks.Black76Case c, OptionRight right, string discount) =>
        Price(right, c.Futures, c.Strike, c.Volatility, c.Years, discount);

    private static LatticeDecimal L(string value) => LatticeDecimal.Parse(value);

    private static void Within(LatticeDecimal actual, LatticeDecimal expected, LatticeDecimal tolerance, string label)
    {
        var difference = actual - expected;
        if (LatticeDecimal.Max(difference, L("0") - difference).CompareTo(tolerance) > 0)
            throw new Exception($"{label}: expected {expected} within {tolerance}; got {actual}.");
    }

    private static void Check(string label, Action run) { run(); Console.WriteLine($"PASS {label}"); }
    private static void Equal(string actual, string expected)
    { if (actual != expected) throw new Exception($"Expected {expected}; got {actual}."); }
    private static void Reject(string code, Action action)
    { try { action(); } catch (MarketDataException error) when (error.Code == code) { return; } throw new Exception($"Expected {code}."); }
}
