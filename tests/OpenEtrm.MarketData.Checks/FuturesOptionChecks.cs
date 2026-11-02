using System.Collections.Immutable;
using System.Diagnostics;
using System.Globalization;
using System.Text.Json.Nodes;
using OpenEtrm.MarketData;
using OpenEtrm.MarketData.Options;
using OpenEtrm.MarketData.Risk;
using OpenEtrm.MarketData.Trading;

internal static class FuturesOptionChecks
{
    public static void Run()
    {
        Check("lattice functions match the independent oracle to one unit in the 30th digit", LatticeFunctionsMatchOracle);
        Check("lattice domain and range errors are structured", LatticeErrors);
        Check("lattice converts to exact decimals with half-even rounding", LatticeExactConversion);
        Check("lattice arithmetic rounds half-even once per operation", LatticeArithmetic);
        Check("backward induction solves the two-step hand tree", TwoStepHandTree);
        Check("backward induction rejects invalid contracts with structured errors", InductionContractErrors);
        Check("american futures options at a unit discount equal Black-76 within the relative discretisation bound", ZeroRateCallEqualsBlack76);
        Check("american futures option two-step hand tree exercises at an interior node", TwoStepAmericanPut);
        Check("american futures options respect no-arbitrage bounds", Bounds);
        Check("american futures options rise with volatility and time and move with the futures price", Monotonic);
        Check("american futures options converge at the pinned step count", Convergence);
        Check("american futures option year fractions use act/365 fixed instants", YearFractions);
        Check("american futures options reject valuation at or after expiry", ExpiredValuation);
        Check("american futures options reject unsupported terms, policies and inputs", RejectsUnsupportedTerms);
        Check("american futures option unit price runtime", PrintRuntime);
        Check("implied volatility reprices the settlement within half a clearing increment", ImpliedVolatilityRoundTrip);
        Check("implied volatility caps its bracket inside the tree's range", ImpliedVolatilityRangeCap);
        Check("implied volatility falls back to intrinsic only when declared", ImpliedVolatilityFallbackAtIntrinsic);
        Check("implied volatility treats an out-of-the-money zero settlement as intrinsic", ImpliedVolatilityOutOfMoneyZero);
        Check("implied volatility rejects settlements below intrinsic or above the bound", ImpliedVolatilityOutOfBounds);
        Check("implied volatility rejects invalid inputs and non-convergence", ImpliedVolatilityErrors);
        Check("futures option base unit PV equals settlement times multiplier", BasePvEqualsSettlement);
        Check("futures option delta reprices at the held volatility under futures bumps", HeldVolatilityDelta);
        Check("short futures option holdings flip delta", ShortPositionsFlipSign);
        Check("futures option fallback delta is one contract in the money and zero out of it", FallbackDelta);
        Check("futures option declares only its underlying futures input", DeclaredDependencies);
        Check("futures option risk replays identically", ReplayIdentical);
        Check("futures option risk digest is host independent", HostIndependentDigest);
        Check("futures option binding rejects unsupported roles, terms and missing inputs", OptionBindingErrors);
        Check("futures option fallback scenario value is floored at zero", FallbackFloor);
        Check("futures option binding is refused by another market definition with the same snapshot", ForeignMarketBinding);
        Check("futures option receipts record every calibration input", ReceiptInputs);
        Check("an expired futures option settlement is never frozen as a forward block's final settlement", NoFrozenOptionSettlement);
    }

    private static readonly DateTimeOffset ValuationAt = new(2026, 1, 5, 19, 30, 0, TimeSpan.Zero);
    private static readonly OptionNumericalPolicy Policy = OptionNumericalPolicies.Require(AmericanFuturesOptionModel.Id);

    private sealed record Black76Case(string Futures, string Strike, string Volatility, string Years, string Discount,
        string Call, string Put, string UndiscountedCall, string UndiscountedPut);

    private static Black76Case[] Black76Cases()
    {
        var oracle = JsonNode.Parse(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "fixtures", "futures-option-oracle.json")))!;
        string Field(JsonNode row, string name) => row[name]!.GetValue<string>();
        var cases = oracle["black76"]!.AsArray().Select(row => new Black76Case(
            Field(row!, "futures"), Field(row!, "strike"), Field(row!, "volatility"), Field(row!, "years"), Field(row!, "discount"),
            Field(row!, "call"), Field(row!, "put"), Field(row!, "undiscounted_call"), Field(row!, "undiscounted_put"))).ToArray();
        if (cases.Length != 17) throw new Exception($"Expected 17 Black-76 cases; found {cases.Length}.");
        return cases;
    }

    // Years are whole seconds under act/365 fixed in every oracle case.
    private static DateTimeOffset ExpiryAfter(string years) =>
        ValuationAt.AddSeconds((double)(decimal.Parse(years, CultureInfo.InvariantCulture) * 365 * 86400));

    private static LatticeDecimal Price(OptionRight right, string futures, string strike, string volatility, string years,
        string discount, OptionNumericalPolicy? policy = null) =>
        AmericanFuturesOptionModel.UnitPrice(
            new FuturesOptionTerms(right, ExactDecimal.Parse(strike), ExpiryAfter(years), "american", "exercise_into_future"),
            L(futures), L(volatility), L(discount), ValuationAt, policy ?? Policy, CancellationToken.None);

    private static LatticeDecimal Price(Black76Case c, OptionRight right, string discount, OptionNumericalPolicy? policy = null) =>
        Price(right, c.Futures, c.Strike, c.Volatility, c.Years, discount, policy);

    private static LatticeDecimal L(string value) => LatticeDecimal.Parse(value);
    private static LatticeDecimal Abs(LatticeDecimal value) => LatticeDecimal.Max(value, L("0") - value);

    private static void Within(LatticeDecimal actual, LatticeDecimal expected, LatticeDecimal tolerance, string label)
    {
        if (Abs(actual - expected).CompareTo(tolerance) > 0)
            throw new Exception($"{label}: expected {expected} within {tolerance}; got {actual}.");
    }

    private static void AtLeast(LatticeDecimal actual, LatticeDecimal bound, string label)
    { if (actual.CompareTo(bound) < 0) throw new Exception($"{label}: {actual} is below {bound}."); }

    private static void AtMost(LatticeDecimal actual, LatticeDecimal bound, string label)
    { if (actual.CompareTo(bound) > 0) throw new Exception($"{label}: {actual} is above {bound}."); }

    // The model's stated accuracy: |tree - Black-76| <= C * F * sigma * sqrt(T) / Steps
    // at a unit discount. The largest observed ratio over the oracle cases is 0.0996
    // (at the money, matching CRR's leading error term 1/(4*sqrt(2*pi)) = 0.0997);
    // C = 0.125 leaves 25% headroom.
    private static readonly LatticeDecimal DiscretisationConstant = L("0.125");

    private static LatticeDecimal DiscretisationBound(Black76Case c, int steps) =>
        DiscretisationConstant * L(c.Futures) * L(c.Volatility) * L(c.Years).Sqrt() / L(steps.ToString(CultureInfo.InvariantCulture));

    // At a unit discount a future has no carry, so neither right gains from early
    // exercise and the American value is the European Black-76 value.
    private static void ZeroRateCallEqualsBlack76()
    {
        foreach (var c in Black76Cases())
        {
            var bound = DiscretisationBound(c, Policy.Steps);
            Within(Price(c, OptionRight.Call, "1"), L(c.UndiscountedCall), bound, $"call F={c.Futures} K={c.Strike} sigma={c.Volatility} T={c.Years}");
            Within(Price(c, OptionRight.Put, "1"), L(c.UndiscountedPut), bound, $"put F={c.Futures} K={c.Strike} sigma={c.Volatility} T={c.Years}");
        }
    }

    // F=100, K=100 put, T=0.5 in two 0.25-year steps. sigma = 2 ln 1.25 (to 30 digits),
    // so u = exp(sigma*0.5) = 1.25 and d = 0.8 (each to 2e-31), p = (1-0.8)/(1.25-0.8) = 4/9;
    // DF = 0.81, so each step discounts by 0.9.
    //   Step 2 futures 156.25, 100, 64; put payoffs 0, 0, 36.
    //   Step 1 up (125): hold 0.9*(4/9*0 + 5/9*0) = 0; intrinsic 0; hold.
    //   Step 1 down (80): hold 0.9*(5/9*36) = 18 < exercise 20; exercise, value 20.
    //   Root (100): hold 0.9*(5/9*20) = 10 > intrinsic 0; value 10.
    // A European tree gives 0.9*(5/9*18) = 9, so max(European, intrinsic) at the root is 9, not 10.
    private static void TwoStepAmericanPut()
    {
        var terms = new FuturesOptionTerms(OptionRight.Put, ExactDecimal.Parse("100"), ExpiryAfter("0.5"), "american", "exercise_into_future");
        var result = AmericanFuturesOptionModel.Solve(terms, L("100"), L("0.446287102628419511532590180620"), L("0.81"),
            ValuationAt, Policy with { Steps = 2 }, recordDecisions: true, CancellationToken.None);
        Within(result.Root, L("10"), L("0.0000000000000000000000001"), "two-step American put");
        Equal(result.Chosen!.Code(1, 1, FuturesOptionState.Alive), "exercise");
        Equal(result.Chosen.Code(1, 0, FuturesOptionState.Alive), "hold");
        Equal(result.Chosen.Code(0, 0, FuturesOptionState.Alive), "hold");
        Equal(AmericanFuturesOptionModel.UnitPrice(terms, L("100"), L("0.446287102628419511532590180620"), L("0.81"),
            ValuationAt, Policy with { Steps = 2 }, CancellationToken.None).ToString(), result.Root.ToString());
    }

    private static void Bounds()
    {
        foreach (var c in Black76Cases())
        {
            var (futures, strike) = (L(c.Futures), L(c.Strike));
            var call = Price(c, OptionRight.Call, c.Discount);
            var put = Price(c, OptionRight.Put, c.Discount);
            var label = $"F={c.Futures} K={c.Strike} sigma={c.Volatility} T={c.Years}";
            AtLeast(call, LatticeDecimal.Max(futures - strike, L("0")), $"call intrinsic {label}");
            AtLeast(put, LatticeDecimal.Max(strike - futures, L("0")), $"put intrinsic {label}");
            AtLeast(call, L(c.Call) - DiscretisationBound(c, Policy.Steps), $"call European {label}");
            AtLeast(put, L(c.Put) - DiscretisationBound(c, Policy.Steps), $"put European {label}");
            AtMost(call, futures, $"call futures bound {label}");
            AtMost(put, strike, $"put strike bound {label}");
        }
    }

    private static void Monotonic()
    {
        void Rising(string label, params LatticeDecimal[] values)
        {
            for (var i = 1; i < values.Length; i++)
                if (values[i].CompareTo(values[i - 1]) <= 0) throw new Exception($"{label} must rise: {string.Join(", ", values)}.");
        }
        foreach (var right in new[] { OptionRight.Call, OptionRight.Put })
        {
            Rising($"{right} in volatility", [.. new[] { "0.2", "0.5", "0.9" }.Select(v => Price(right, "3", "3.25", v, "0.25", "0.98"))]);
            Rising($"{right} in time", [.. new[] { "0.05", "0.25", "1" }.Select(t => Price(right, "3", "3.25", "0.5", t, "0.98"))]);
        }
        var futures = new[] { "2.5", "3", "3.5" };
        Rising("call in futures", [.. futures.Select(f => Price(OptionRight.Call, f, "3.25", "0.5", "0.25", "0.98"))]);
        Rising("put as futures fall", [.. futures.Reverse().Select(f => Price(OptionRight.Put, f, "3.25", "0.5", "0.25", "0.98"))]);
    }

    // A stability (self-consistency) check, not accuracy evidence: the pinned step
    // count agrees with four times as many steps within PriceTolerance on six cases
    // at gas and crude price levels (at the money, deep out of the money, two years
    // out, high volatility near expiry). Accuracy is checked against Black-76 above.
    private static void Convergence()
    {
        var fine = Policy with { Steps = Policy.Steps * 4 };
        int[] selected = [0, 2, 5, 6, 9, 11];
        foreach (var c in selected.Select(index => Black76Cases()[index]))
            foreach (var right in new[] { OptionRight.Call, OptionRight.Put })
                Within(Price(c, right, c.Discount), Price(c, right, c.Discount, fine), Policy.PriceTolerance,
                    $"{right} F={c.Futures} K={c.Strike} at {Policy.Steps} vs {fine.Steps} steps");
    }

    private static void YearFractions()
    {
        Equal(Policy.DayCount, "act_365_fixed_instant_v1");
        Equal(AmericanFuturesOptionModel.YearFraction(ValuationAt, ValuationAt.AddDays(365), Policy).ToString(), "1");
        Equal(AmericanFuturesOptionModel.YearFraction(ValuationAt, ValuationAt.AddDays(1), Policy).ToString(), "0.00273972602739726027397260274");
        Equal(AmericanFuturesOptionModel.YearFraction(ValuationAt, ValuationAt.AddSeconds(1), Policy).ToString(), "0.000000031709791983764586504313");
        // Offsets do not change the instant.
        Equal(AmericanFuturesOptionModel.YearFraction(ValuationAt, new DateTimeOffset(2026, 1, 6, 14, 30, 0, TimeSpan.FromHours(-5)), Policy).ToString(),
            "0.00273972602739726027397260274");
    }

    private static void ExpiredValuation()
    {
        Reject("option_expired", () => AmericanFuturesOptionModel.YearFraction(ValuationAt, ValuationAt, Policy));
        Reject("option_expired", () => AmericanFuturesOptionModel.YearFraction(ValuationAt.AddTicks(1), ValuationAt, Policy));
        var terms = new FuturesOptionTerms(OptionRight.Put, ExactDecimal.Parse("3"), ValuationAt, "american", "exercise_into_future");
        Reject("option_expired", () => AmericanFuturesOptionModel.UnitPrice(terms, L("2"), L("0.5"), L("0.99"), ValuationAt, Policy, CancellationToken.None));
    }

    private static void RejectsUnsupportedTerms()
    {
        var expiry = ExpiryAfter("0.25");
        var strike = ExactDecimal.Parse("3");
        Reject("option_terms", () => _ = new FuturesOptionTerms(OptionRight.Call, strike, expiry, "european", "exercise_into_future"));
        Reject("option_terms", () => _ = new FuturesOptionTerms(OptionRight.Call, strike, expiry, "american", "cash"));
        Reject("option_terms", () => _ = new FuturesOptionTerms((OptionRight)0, strike, expiry, "american", "exercise_into_future"));
        Reject("option_numerical_policy", () => OptionNumericalPolicies.Require("american_futures_option_crr_v2"));
        Reject("option_numerical_policy", () => AmericanFuturesOptionModel.YearFraction(ValuationAt, expiry, Policy with { DayCount = "act_360" }));
        Reject("option_numerical_policy", () => Price(OptionRight.Call, "3", "3", "0.5", "0.25", "0.99", Policy with { Steps = 0 }));
        Reject("option_input", () => Price(OptionRight.Call, "0", "3", "0.5", "0.25", "0.99"));
        Reject("option_input", () => Price(OptionRight.Call, "3", "0", "0.5", "0.25", "0.99"));
        Reject("option_input", () => Price(OptionRight.Call, "3", "-1", "0.5", "0.25", "0.99"));
        Reject("option_input", () => Price(OptionRight.Call, "3", "3", "0", "0.25", "0.99"));
        Reject("option_input", () => Price(OptionRight.Call, "3", "3", "0.5", "0.25", "0"));
        Reject("option_input", () => Price(OptionRight.Call, "3", "3", "0.5", "0.25", "1.01"));
        // ln F + Steps*sigma*sqrt(T/Steps) beyond the Exp range is an error, not a clipped tree.
        Reject("numeric_range", () => Price(OptionRight.Call, "80", "80", "3", "2", "0.9"));
    }

    private static void PrintRuntime()
    {
        _ = Price(OptionRight.Put, "80", "82.5", "0.4", "0.5", "0.975");
        const int runs = 5;
        var watch = Stopwatch.StartNew();
        for (var i = 0; i < runs; i++) _ = Price(OptionRight.Put, "80", "82.5", "0.4", "0.5", "0.975");
        Console.WriteLine($"INFO one UnitPrice at {Policy.Steps} steps: {watch.Elapsed.TotalMilliseconds / runs:F1} ms (mean of {runs})");
    }

    private const string TwoDays = "0.005479452054794520547945205479";

    private static OptionCalibration Calibrate(OptionRight right, string futures, string strike, string years, string discount,
        string settlement, string increment, bool fallbackDeclared, OptionNumericalPolicy? policy = null) =>
        ImpliedVolatility.Solve(
            new FuturesOptionTerms(right, ExactDecimal.Parse(strike), ExpiryAfter(years), "american", "exercise_into_future"),
            L(settlement), L(futures), L(discount), L(increment), ValuationAt, policy ?? Policy, fallbackDeclared, CancellationToken.None);

    // An exchange publishes settlements on the clearing increment.
    private static string RoundToIncrement(LatticeDecimal price, string increment) =>
        decimal.Round(decimal.Parse(price.ToString(), CultureInfo.InvariantCulture), increment.Length - 2, MidpointRounding.ToEven)
            .ToString(CultureInfo.InvariantCulture);

    // Settlements come from pricing at a known oracle volatility and rounding to the
    // clearing increment. The check is that the solver inverts the model: the solved
    // volatility reprices within half an increment of the settlement.
    private static void ImpliedVolatilityRoundTrip()
    {
        (OptionRight Right, int Case, string Increment)[] series =
        [
            (OptionRight.Call, 0, "0.001"), (OptionRight.Put, 1, "0.001"), (OptionRight.Call, 2, "0.001"), (OptionRight.Put, 5, "0.001"),
            (OptionRight.Put, 6, "0.01"), (OptionRight.Call, 7, "0.01"), (OptionRight.Call, 9, "0.01"), (OptionRight.Put, 11, "0.01"),
        ];
        var cases = Black76Cases();
        foreach (var (right, index, increment) in series)
        {
            var c = cases[index];
            var settlement = RoundToIncrement(Price(c, right, c.Discount), increment);
            var label = $"{right} F={c.Futures} K={c.Strike} T={c.Years} sigma={c.Volatility} settlement {settlement}";
            var watch = Stopwatch.StartNew();
            var solved = Calibrate(right, c.Futures, c.Strike, c.Years, c.Discount, settlement, increment, fallbackDeclared: false) as SolvedVolatility
                ?? throw new Exception($"{label}: expected a solved volatility.");
            var elapsed = watch.Elapsed.TotalMilliseconds;
            var repriced = Price(right, c.Futures, c.Strike, solved.Volatility.ToString(), c.Years, c.Discount);
            Within(repriced, L(settlement), L(increment) / L("2"), $"{label} repriced");
            Equal(solved.Residual.ToString(), (repriced - L(settlement)).ToString());
            AtLeast(solved.Volatility, Policy.VolatilityMin, $"{label} volatility");
            AtMost(solved.Volatility, solved.UpperBound, $"{label} volatility");
            AtMost(solved.UpperBound, Policy.VolatilityMax, $"{label} upper bound");
            if (solved.Iterations < 1 || solved.Iterations > Policy.MaxIterations)
                throw new Exception($"{label}: {solved.Iterations} iterations is outside 1..{Policy.MaxIterations}.");
            Console.WriteLine($"INFO {label}: sigma {solved.Volatility.ToExact()} in {solved.Iterations} iterations, {elapsed:F0} ms");
        }
    }

    // F=80 over two years at 400 steps: the extreme node F*exp(sigma*sqrt(Steps*T))
    // reaches 10^23 below the policy's VolatilityMax of 3, so the tree's range sets
    // the bound. A settlement above the price there is above_bound, never
    // numeric_range, and a settlement below it still solves.
    private static void ImpliedVolatilityRangeCap()
    {
        Reject("option_settlement_above_bound", () => Calibrate(OptionRight.Call, "80", "80", "2", "0.9", "79", "0.01", fallbackDeclared: true));
        var settlement = RoundToIncrement(Price(OptionRight.Call, "80", "80", "1.5", "2", "0.9"), "0.01");
        var solved = (SolvedVolatility)Calibrate(OptionRight.Call, "80", "80", "2", "0.9", settlement, "0.01", fallbackDeclared: false);
        var extremeNode = L("80") * (solved.UpperBound * L("800").Sqrt()).Exp();
        Within(extremeNode, L("100000000000000000000000"), L("10000000000000"), "extreme node at the bound");
        AtMost(solved.UpperBound, L("1.72"), "range-capped bound");
        Within(Price(OptionRight.Call, "80", "80", solved.Volatility.ToString(), "2", "0.9"), L(settlement), L("0.005"), "range-capped round trip");
        // Below a futures price of 1 the Exp argument itself is the limit: exp(sigma*sqrt(Steps*T)) reaches 10^23.
        var low = RoundToIncrement(Price(OptionRight.Call, "0.5", "0.5", "0.6", "1", "0.97"), "0.001");
        var subunit = (SolvedVolatility)Calibrate(OptionRight.Call, "0.5", "0.5", "1", "0.97", low, "0.001", fallbackDeclared: false);
        Within((subunit.UpperBound * L("400").Sqrt()).Exp(), L("100000000000000000000000"), L("10000000000000"), "sub-unit exp at the bound");
        Within(Price(OptionRight.Call, "0.5", "0.5", subunit.Volatility.ToString(), "1", "0.97"), L(low), L("0.0005"), "sub-unit round trip");
        // Short-dated gas: the policy's VolatilityMax is the bound.
        var gas = (SolvedVolatility)Calibrate(OptionRight.Call, "3", "3", "0.25", "0.99", "0.295", "0.001", fallbackDeclared: false);
        Equal(gas.UpperBound.ToString(), Policy.VolatilityMax.ToString());
    }

    // A deep in-the-money put two days out: at VolatilityMin the tree is worth
    // exactly intrinsic (immediate exercise), so a settlement at intrinsic, or up to
    // one increment below it, is the declared fallback with residual
    // settlement - intrinsic.
    private static void ImpliedVolatilityFallbackAtIntrinsic()
    {
        Equal(ImpliedVolatility.FallbackMode, "option_settlement_at_intrinsic_v1");
        foreach (var (settlement, residual) in new[] { ("1", "0"), ("0.9995", "-0.0005"), ("0.999", "-0.001") })
        {
            var fallback = Calibrate(OptionRight.Put, "2", "3", TwoDays, "0.9998", settlement, "0.001", fallbackDeclared: true) as IntrinsicFallback
                ?? throw new Exception($"Expected the intrinsic fallback at settlement {settlement}.");
            Equal(fallback.Intrinsic.ToString(), "1");
            Equal(fallback.Residual.ToString(), residual);
            Reject("option_calibration_fallback_undeclared",
                () => Calibrate(OptionRight.Put, "2", "3", TwoDays, "0.9998", settlement, "0.001", fallbackDeclared: false));
        }
    }

    private static void ImpliedVolatilityOutOfMoneyZero()
    {
        var fallback = Calibrate(OptionRight.Call, "3", "4", "0.1", "0.995", "0", "0.001", fallbackDeclared: true) as IntrinsicFallback
            ?? throw new Exception("Expected the intrinsic fallback for an out-of-the-money zero settlement.");
        Equal(fallback.Intrinsic.ToString(), "0");
        Equal(fallback.Residual.ToString(), "0");
        Reject("option_calibration_fallback_undeclared", () => Calibrate(OptionRight.Call, "3", "4", "0.1", "0.995", "0", "0.001", fallbackDeclared: false));
    }

    private static void ImpliedVolatilityOutOfBounds()
    {
        Reject("option_settlement_below_intrinsic", () => Calibrate(OptionRight.Put, "2", "3", TwoDays, "0.9998", "0.998", "0.001", fallbackDeclared: true));
        Reject("option_settlement_below_intrinsic", () => Calibrate(OptionRight.Put, "2", "3", TwoDays, "0.9998", "0.998999", "0.001", fallbackDeclared: true));
        // An American call on a future is worth less than the future, a put less than the strike.
        Reject("option_settlement_above_bound", () => Calibrate(OptionRight.Call, "3", "3", "0.25", "0.99", "3", "0.001", fallbackDeclared: true));
        Reject("option_settlement_above_bound", () => Calibrate(OptionRight.Put, "3", "3", "0.25", "0.99", "2.9", "0.001", fallbackDeclared: true));
    }

    private static void ImpliedVolatilityErrors()
    {
        Reject("option_input", () => Calibrate(OptionRight.Call, "3", "3", "0.25", "0.99", "-0.001", "0.001", fallbackDeclared: true));
        Reject("option_input", () => Calibrate(OptionRight.Call, "3", "3", "0.25", "0.99", "0.3", "0", fallbackDeclared: true));
        Reject("option_input", () => Calibrate(OptionRight.Call, "3", "3", "0.25", "0.99", "0.3", "-0.001", fallbackDeclared: true));
        Reject("option_input", () => Calibrate(OptionRight.Call, "0", "3", "0.25", "0.99", "0.3", "0.001", fallbackDeclared: true));
        Reject("option_expired", () => Calibrate(OptionRight.Call, "3", "3", "0", "0.99", "0.3", "0.001", fallbackDeclared: true));
        Reject("option_numerical_policy", () => Calibrate(OptionRight.Call, "3", "3", "0.25", "0.99", "0.3", "0.001", true, Policy with { Steps = 0 }));
        Reject("option_numerical_policy", () => Calibrate(OptionRight.Call, "3", "3", "0.25", "0.99", "0.3", "0.001", true, Policy with { MaxIterations = 0 }));
        Reject("option_numerical_policy", () => Calibrate(OptionRight.Call, "3", "3", "0.25", "0.99", "0.3", "0.001", true, Policy with { VolatilityMin = L("3") }));
        Reject("option_numerical_policy", () => Calibrate(OptionRight.Call, "3", "3", "0.25", "0.99", "0.3", "0.001", true, Policy with { VolatilityMin = L("0") }));
        Reject("option_numerical_policy", () => Calibrate(OptionRight.Call, "3", "3", "0.25", "0.99", "0.3", "0.001", true, Policy with { VolatilityMin = L("-0.01") }));
        // Inputs are validated before any classification, so a settlement far below
        // intrinsic does not mask an invalid strike or discount factor.
        Reject("option_input", () => Calibrate(OptionRight.Call, "3", "0", "0.25", "0.99", "1", "0.001", true));
        Reject("option_input", () => Calibrate(OptionRight.Call, "3", "-1", "0.25", "0.99", "1", "0.001", true));
        Reject("option_input", () => Calibrate(OptionRight.Put, "2", "3", "0.25", "1.01", "0.5", "0.001", true));
        Reject("option_input", () => Calibrate(OptionRight.Put, "2", "3", "0.25", "0", "0.5", "0.001", true));
        // A futures price at the tree's range leaves no volatility in the bracket.
        Reject("option_input", () => Calibrate(OptionRight.Call, "100000000000000000000000", "3", "0.25", "0.99", "0.3", "0.001", true));
        // One halving cannot pin a gas-level settlement to half a tenth of a cent.
        Reject("option_calibration_nonconvergent",
            () => Calibrate(OptionRight.Call, "3", "3", "0.25", "0.99", "0.295", "0.001", true, Policy with { MaxIterations = 1 }));
    }

    private static void LatticeFunctionsMatchOracle()
    {
        var oracle = JsonNode.Parse(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "fixtures", "futures-option-oracle.json")))!;
        var count = 0;
        foreach (var (name, function) in new (string, Func<LatticeDecimal, LatticeDecimal>)[]
                 { ("exp", v => v.Exp()), ("ln", v => v.Ln()), ("sqrt", v => v.Sqrt()) })
            foreach (var row in oracle[name]!.AsArray())
            {
                var x = row!["x"]!.GetValue<string>();
                var expected = LatticeDecimal.Parse(row["y"]!.GetValue<string>());
                var actual = function(LatticeDecimal.Parse(x));
                var difference = (actual - expected).ToString();
                if (difference is not ("0" or "0.000000000000000000000000000001" or "-0.000000000000000000000000000001"))
                    throw new Exception($"{name}({x}) expected {expected}; got {actual}.");
                count++;
            }
        if (count < 40) throw new Exception($"Oracle must hold at least 40 rows; found {count}.");
    }

    private static void LatticeErrors()
    {
        Reject("lattice_domain", () => _ = LatticeDecimal.Parse("0").Ln());
        Reject("lattice_domain", () => _ = LatticeDecimal.Parse("-1").Sqrt());
        Reject("numeric_division", () => _ = LatticeDecimal.Parse("1") / LatticeDecimal.Parse("0"));
        Reject("lattice_domain", () => _ = LatticeDecimal.Parse("2").Pow(-1));
        Reject("numeric_range", () => _ = LatticeDecimal.Parse("100").Exp());
        Reject("numeric_range", () => _ = LatticeDecimal.Parse("1000000000000").Pow(2));
        Reject("numeric_format", () => _ = LatticeDecimal.Parse("1e5"));
        Equal(LatticeDecimal.Parse("-100").Exp().ToString(), "0");
        Equal(LatticeDecimal.Parse("-69.8").Exp().ToString(), "0");
        Equal(LatticeDecimal.Parse("-69.7").Exp().ToString(), "0.000000000000000000000000000001");
        Equal(LatticeDecimal.Parse("0").Sqrt().ToString(), "0");
    }

    private static void LatticeExactConversion()
    {
        var value = ExactDecimal.Parse("1.25");
        if (LatticeDecimal.From(value).ToExact() != value) throw new Exception("1.25 must round-trip.");
        var third = LatticeDecimal.Parse("1") / LatticeDecimal.Parse("3");
        Equal(third.ToString(), "0.333333333333333333333333333333");
        Equal(third.ToExact().ToString(), "0.333333333333333333");
        Equal(LatticeDecimal.Parse("0.000000000000000000500000000000").ToExact().ToString(), "0");
        Equal(LatticeDecimal.Parse("0.000000000000000001500000000000").ToExact().ToString(), "0.000000000000000002");
        Equal(LatticeDecimal.Parse("-0.000000000000000002500000000000").ToExact().ToString(), "-0.000000000000000002");
    }

    private static void LatticeArithmetic()
    {
        var tiny = LatticeDecimal.Parse("0.000000000000000000000000000001");
        var half = LatticeDecimal.Parse("0.5");
        Equal((tiny * half).ToString(), "0");                         // 0.5 ulp ties to even (0)
        Equal((tiny * LatticeDecimal.Parse("1.5")).ToString(), "0.000000000000000000000000000002"); // 1.5 ulp ties to even (2)
        Equal((LatticeDecimal.Parse("2") / LatticeDecimal.Parse("3")).ToString(), "0.666666666666666666666666666667");
        Equal(LatticeDecimal.Parse("1.1").Pow(3).ToString(), "1.331");
        Equal(LatticeDecimal.Parse("7").Pow(0).ToString(), "1");
        Equal(LatticeDecimal.Max(LatticeDecimal.Parse("-1"), LatticeDecimal.Parse("0.5")).ToString(), "0.5");
        Equal((LatticeDecimal.Parse("1") - LatticeDecimal.Parse("1.5")).ToString(), "-0.5");
        Equal((LatticeDecimal.Parse("1") / LatticeDecimal.Parse("-2")).ToString(), "-0.5");
        Equal((LatticeDecimal.Parse("-1") / LatticeDecimal.Parse("-3")).ToString(), "0.333333333333333333333333333333");
        Equal((LatticeDecimal.Parse("1") / LatticeDecimal.Parse("-3")).ToString(), "-0.333333333333333333333333333333");
        // 0.000...001 / -2 = -0.5 ulp ties to even (0); 0.000...003 / -2 = -1.5 ulp ties to even (-2).
        Equal((tiny / LatticeDecimal.Parse("-2")).ToString(), "0");
        Equal((LatticeDecimal.Parse("0.000000000000000000000000000003") / LatticeDecimal.Parse("-2")).ToString(), "-0.000000000000000000000000000002");
        Equal((LatticeDecimal.Parse("0") / LatticeDecimal.Parse("-2")).ToString(), "0");
    }

    // Hand tree: S0=100, up 1.2, down 0.8, p=0.5; terminal S = 144, 96, 64, so put (K=100)
    // payoffs are 0, 4, 36. Discount(step) is the factor applied rolling back from step+1 to step.
    //   European, discount 0.9: step-1 up node 0.9*(0.5*0+0.5*4)=1.8, down node 0.9*(0.5*4+0.5*36)=18,
    //   root 0.9*(0.5*1.8+0.5*18)=8.91.
    //   American, discount 0.9: down node max(exercise 100-80=20, hold 18)=20, so root 0.9*(1.8+20)/2=9.81.
    //   American, discount 1: down node hold 0.5*(4+36)=20 ties exercise 20; the first declared decision (hold) wins.
    private static void TwoStepHandTree()
    {
        var lattice = new TestLattice(2, "0.9");
        var european = BackwardInduction.Solve(lattice, new TestPut("100", false), CancellationToken.None, recordDecisions: true);
        Equal(european.Root.ToString(), "8.91");
        Equal(european.Chosen!.Code(1, 1, TestState.Alive), "hold");

        var american = BackwardInduction.Solve(lattice, new TestPut("100", true), CancellationToken.None, recordDecisions: true);
        Equal(american.Root.ToString(), "9.81");
        Equal(american.Chosen!.Code(1, 1, TestState.Alive), "exercise");
        Equal(american.Chosen!.Code(1, 0, TestState.Alive), "hold");
        Equal(american.Chosen!.Code(0, 0, TestState.Alive), "hold");  // root: hold 9.81 beats exercising at 0
        Equal(american.Chosen!.Code(1, 1, TestState.Exercised), "hold");
        Equal(american.Chosen!.Steps.ToString(), "2");

        var tie = BackwardInduction.Solve(new TestLattice(2, "1"), new TestPut("100", true), CancellationToken.None, recordDecisions: true);
        Equal(tie.Chosen!.Code(1, 1, TestState.Alive), "hold");
        Equal(tie.Root.ToString(), "11");  // up node 2, down node 20, root (2+20)/2

        var unrecorded = BackwardInduction.Solve(lattice, new TestPut("100", true), CancellationToken.None, recordDecisions: false);
        Equal(unrecorded.Root.ToString(), "9.81");
        if (unrecorded.Chosen is not null) throw new Exception("Unrecorded decisions must not be kept.");
    }

    private static void InductionContractErrors()
    {
        var put = new TestPut("100", true);
        Reject("induction_contract", () => BackwardInduction.Solve(new TestLattice(0, "0.9"), put, CancellationToken.None, recordDecisions: true));
        Reject("induction_contract", () => BackwardInduction.Solve(new TestLattice(2, "0.9") { UpProbability = LatticeDecimal.Parse("0.6") }, put, CancellationToken.None, recordDecisions: true));
        Reject("induction_contract", () => BackwardInduction.Solve(new TestLattice(2, "0.9") { Overshoot = true }, put, CancellationToken.None, recordDecisions: true));
        Reject("induction_contract", () => BackwardInduction.Solve(new TestLattice(2, "0.9"), new TestPut("100", true) { OmitExercised = true }, CancellationToken.None, recordDecisions: true));
        Reject("induction_contract", () => BackwardInduction.Solve(new TestLattice(2, "0.9"), new TestPut("100", true) { NoDecisions = true }, CancellationToken.None, recordDecisions: true));
        Reject("induction_contract", () => BackwardInduction.Solve(new TestLattice(2, "0.9") { UpProbability = LatticeDecimal.Parse("-0.5"), DownProbability = LatticeDecimal.Parse("1.5") }, put, CancellationToken.None, recordDecisions: true));
        Reject("induction_contract", () => BackwardInduction.Solve(new TestLattice(2, "0.9") { NodeCount = step => step == 0 ? 2 : step + 1 }, put, CancellationToken.None, recordDecisions: true));
        Reject("induction_contract", () => BackwardInduction.Solve(new TestLattice(2, "0.9") { NodeCount = step => step == 2 ? 0 : step + 1 }, put, CancellationToken.None, recordDecisions: true));
        Reject("induction_contract", () => BackwardInduction.Solve(new TestLattice(2, "0.9") { NodeCount = step => step == 1 ? 0 : step + 1 }, put, CancellationToken.None, recordDecisions: true));
        Reject("induction_contract", () => BackwardInduction.Solve(new TestLattice(2, "0.9") { NullNext = true }, put, CancellationToken.None, recordDecisions: true));
        Reject("induction_contract", () => BackwardInduction.Solve(new TestLattice(2, "0.9"), new TestPut("100", true) { DuplicateStates = true }, CancellationToken.None, recordDecisions: true));
        Reject("induction_contract", () => BackwardInduction.Solve(new TestLattice(2, "0.9"), new TestPut("100", true) { NoStates = true }, CancellationToken.None, recordDecisions: true));
        var cancelled = new CancellationTokenSource(); cancelled.Cancel();
        try { BackwardInduction.Solve(new TestLattice(2, "0.9"), put, cancelled.Token, recordDecisions: true); throw new Exception("Expected cancellation."); }
        catch (OperationCanceledException) { }
    }

    // A synthetic two-curve market with fixed identities, so serialized results
    // can be compared across runs and hosts: one NG-style future and a direct
    // daily-settlement curve over four option series on it.
    private sealed class OptionMarket
    {
        public static Guid G(int n) => Guid.Parse($"00000000-0000-0000-0000-{n:000000000000}");
        public readonly DefinitionRevision Revision = new(G(901), G(902));
        public readonly ReferenceRevision Reference = new(G(903), G(904));
        public ReferenceKey R(string code) => new(Reference, code);
        public ContractKey K(int n) => new(Revision, G(n));
        public SeriesKey S(int n) => new(Revision, G(500 + n));
        public CurveKey C(string code) => new(Revision, code);
        public static readonly DateTimeOffset At = new(2040, 12, 18, 0, 0, 0, TimeSpan.Zero);
        public static readonly DateTimeOffset Expiry = new(2040, 12, 24, 19, 30, 0, TimeSpan.Zero);
        public static readonly ExactDecimal Multiplier = ExactDecimal.Parse("10000");
        public static readonly ExactDecimal Discount = ExactDecimal.Parse("0.999");
        public const string Futures = "3";
        public const string Tick = "0.001";
        // Contract number, right, strike, synthetic settlement.
        public static readonly (int Contract, OptionRight Right, string Strike, string Settlement)[] Options =
        [
            (11, OptionRight.Call, "3", "0.08"),    // solved, at the money
            (12, OptionRight.Put, "3", "0.08"),     // solved, at the money
            (13, OptionRight.Call, "2", "1"),       // fallback, in the money at intrinsic
            (14, OptionRight.Call, "5", "0"),       // fallback, out of the money at zero
            (15, OptionRight.Call, "2", "0.999"),   // fallback, one increment below intrinsic
        ];
        public QuoteSpecification FuturesQuote => Quote("ng_quote");
        public QuoteSpecification OptionQuote => Quote("on_quote");
        private QuoteSpecification Quote(string code, string increment = Tick) => new(R(code), QuoteKind.Outright, R("usd"), R("mmbtu"),
            ExactDecimal.Parse("1"), ExactDecimal.Parse(Tick), ExactDecimal.Parse(increment), ExactDecimal.Parse("0"), ExactDecimal.Parse("1000"));
        public FuturesSeriesSpecification Underlying => new(R("ng_jan41"), R("mmbtu"), R("usd"), ExactDecimal.Parse("1"),
            ExactDecimal.Parse(Tick), R("hub"), R("uniform"), new(2041, 1, 1), new(2041, 2, 1),
            new DateTimeOffset(2040, 12, 28, 19, 30, 0, TimeSpan.Zero), R("mmbtu"), Multiplier, LotBasis.FixedTotal,
            new DateTimeOffset(2040, 1, 2, 0, 0, 0, TimeSpan.Zero), "physical");
        public CurveRequest UnderlyingRequest => new(C("ng"), K(1));
        public CurveRequest SettlementRequest(int contract) => new(C("on_settle"), K(contract));
        public static string SeriesCode(int contract) => $"on_jan41_{contract}";
        public FuturesOptionSeriesSpecification Series(int contract, string premiumStyle = "premium_paid_up_front")
        {
            var option = Options.Single(o => o.Contract == contract);
            return new(R(SeriesCode(contract)), R("on"), R("ng_jan41"),
                new(option.Right, ExactDecimal.Parse(option.Strike), Expiry, "american", "exercise_into_future"),
                Multiplier, R("usd"), R("on_contract"), ExactDecimal.Parse(Tick), "synthetic_auto_exercise_v1", true,
                Expiry.AddHours(-2), premiumStyle);
        }
        public FuturesOptionInstrument Option(int contract) => new(Series(contract));
        public CurveModel Model(ObservationRole optionRole = ObservationRole.DailySettlement,
            ObservationRole futuresRole = ObservationRole.DailySettlement, string optionIncrement = Tick,
            ConstituentExpiryRule optionExpiry = ConstituentExpiryRule.RequireLive)
        {
            var options = Options.Select(o => new MarketContract(K(o.Contract), ContractKind.FutureOption, new(2041, 1, 1), new(2041, 2, 1),
                Expiry, R(SeriesCode(o.Contract)))).ToArray();
            var futures = new MarketContract(K(1), ContractKind.Future, new(2041, 1, 1), new(2041, 2, 1), Underlying.LastTradingAt, R("ng_jan41"));
            Curve Direct(string code, ReferenceKey quote, ImmutableArray<ContractKey> members, ObservationRole role) =>
                new(C(code), quote, R("hub"), R("uniform"), new(SettlementKind.FutureSeries, null, null), CurveRule.Direct,
                    members, [], role, R("exchange"), null);
            return new(Revision, Reference,
                [new(R("mmbtu"), R("gas"), R("energy_basis"), R("mmbtu_unit"), R("energy")),
                 new(R("on_contract"), R("gas"), R("contract_basis"), R("contract_unit"), R("count"))],
                [FuturesQuote, Quote("on_quote", optionIncrement)], [futures, .. options],
                [Direct("ng", R("ng_quote"), [K(1)], futuresRole),
                 Direct("on_settle", R("on_quote"), [.. options.Select(o => o.Key)], optionRole) with { ConstituentExpiry = optionExpiry }],
                [Underlying], [],
                [new(S(1), K(1), R("ng_quote"), null), .. Options.Select(o => new MarketSeries(S(o.Contract), K(o.Contract), R("on_quote"), null))]);
        }
        public MarketSnapshot Snapshot(ObservationRole futuresRole = ObservationRole.DailySettlement)
        {
            SelectedObservation Observation(int series, ObservationRole role, string value) => new(new(S(series), InputUse.Variable),
                new(new(Revision.CatalogId, G(700 + series)), S(series), R("exchange"), role, PublicationState.Final, At, ExactDecimal.Parse(value)));
            return new(G(800), Revision, At, At, InputSelectionPolicy.ExactPinnedFinalObservationsNoAgeFiltering,
                [Observation(1, futuresRole, Futures), .. Options.Select(o => Observation(o.Contract, ObservationRole.DailySettlement, o.Settlement))]);
        }
        public QuoteBump FuturesBump => Bump(1, FuturesQuote);
        public QuoteBump FuturesBumpOf(string size) => FuturesBump with { Size = ExactDecimal.Parse(size) };
        public QuoteBump SettlementBump(int contract) => Bump(contract, OptionQuote);
        private QuoteBump Bump(int series, QuoteSpecification quote) =>
            new(new(S(series), InputUse.Variable), quote.Key, PriceUnit.FromQuote(quote), ExactDecimal.Parse(Tick), QuoteBump.Convention);
        public static Trade Holding(int trade, int book, FuturesOptionInstrument option, string lots) => new(G(1000 + trade), G(2000 + book),
            [new TradeInstrument(G(3000 + trade), option, new Quantity(ExactDecimal.Parse(lots), option.UnitQuantity.Specification))]);
        public RiskRun Calculate(ImmutableArray<Trade> trades, ImmutableArray<QuoteBump> bumps, bool fallbackDeclared = true,
            ISideDiscounting? discounting = null, string role = "daily_settlement", CurveModel? model = null, MarketSnapshot? snapshot = null) =>
            FuturesOptionPricing.Calculate(trades, snapshot ?? Snapshot(), model ?? Model(), bumps, FuturesOptionPricing.ScenarioPolicy,
                RiskEngine.NumericalPolicy, Policy, fallbackDeclared, discounting ?? new ExplicitDiscounting(Discount, DiscountFactorSource.Explicit), role);
    }

    // The fixed four-option case: long and short holdings across two books, with a
    // one-tick futures bump and a bump on the call's own settlement price.
    private static readonly Lazy<(OptionMarket Market, RiskRun Run)> Portfolio = new(() =>
    {
        var market = new OptionMarket();
        ImmutableArray<Trade> trades =
        [
            OptionMarket.Holding(1, 1, market.Option(11), "2"),
            OptionMarket.Holding(2, 1, market.Option(12), "1"),
            OptionMarket.Holding(3, 1, market.Option(13), "3"),
            OptionMarket.Holding(4, 1, market.Option(14), "1"),
            OptionMarket.Holding(5, 2, market.Option(11), "-1"),
            OptionMarket.Holding(6, 2, market.Option(12), "-1"),
        ];
        return (market, market.Calculate(trades, [market.FuturesBump, market.SettlementBump(11)]));
    });

    private static RiskPositionResult Position(RiskFactorResult factor, int trade) =>
        factor.Positions.Single(p => p.TradeId == OptionMarket.G(1000 + trade));

    private static ExactDecimal D(string value) => ExactDecimal.Parse(value);
    private static void Same(ExactDecimal actual, ExactDecimal expected, string label)
    { if (actual != expected) throw new Exception($"{label}: expected {expected}; got {actual}."); }

    private static void BasePvEqualsSettlement()
    {
        var (_, run) = Portfolio.Value;
        var factor = run.Factors[0];
        // Calibration reprices settlement within half an increment; per lot that is
        // multiplier * increment / 2 = 5 USD.
        foreach (var (trade, settlement, lots) in new[] { (1, "0.08", "2"), (2, "0.08", "1"), (3, "1", "3"), (4, "0", "1") })
        {
            var expected = D(settlement) * OptionMarket.Multiplier * D(lots);
            var bound = OptionMarket.Multiplier * D("0.0005") * D(lots);
            var difference = Position(factor, trade).Base.Amount - expected;
            if (difference.CompareTo(bound) > 0 || (default(ExactDecimal) - difference).CompareTo(bound) > 0)
                throw new Exception($"Trade {trade}: base {Position(factor, trade).Base.Amount} is not settlement value {expected} within {bound}.");
        }
        // The fallback mode reproduces settlement exactly.
        Same(Position(factor, 3).Base.Amount, D("30000"), "fallback base");
        Same(Position(factor, 4).Base.Amount, D("0"), "out-of-the-money base");
    }

    private static void HeldVolatilityDelta()
    {
        var (market, run) = Portfolio.Value;
        var factor = run.Factors[0];
        if (factor.Bump.Input.Series != market.S(1)) throw new Exception("The first factor must be the futures bump.");
        foreach (var (trade, contract, lots) in new[] { (1, 11, "2"), (2, 12, "1") })
        {
            var receipt = run.OptionCalibrations.Single(r => r.Series.Code == OptionMarket.SeriesCode(contract));
            Equal(receipt.Mode, "solved");
            var volatility = L(receipt.Volatility!);
            ExactDecimal Expected(string futures)
            {
                var unit = AmericanFuturesOptionModel.UnitPrice(market.Series(contract).Terms, L(futures), volatility,
                    LatticeDecimal.From(OptionMarket.Discount), OptionMarket.At, Policy, CancellationToken.None);
                // The tree price carries 30 digits; the PV rounds it once, half-even, to 18.
                if (unit.ToString().Split('.')[1].Length <= 18) throw new Exception("The case must exercise unit-price rounding.");
                return unit.ToExact() * OptionMarket.Multiplier * D(lots);
            }
            var position = Position(factor, trade);
            Same(position.Up.Amount, Expected("3.001"), $"trade {trade} up");
            Same(position.Down.Amount, Expected("2.999"), $"trade {trade} down");
            var sign = position.Delta.Value.CompareTo(default);
            if (sign != (contract == 11 ? 1 : -1)) throw new Exception($"Trade {trade}: delta {position.Delta.Value} has the wrong sign.");
        }
    }

    private static void ShortPositionsFlipSign()
    {
        var (_, run) = Portfolio.Value;
        var factor = run.Factors[0];
        // Trade 1 holds 2 long calls, trade 5 one short; trade 2 one long put, trade 6 one short.
        Same(Position(factor, 5).Delta.Value * D("-2"), Position(factor, 1).Delta.Value, "short call delta");
        Same(Position(factor, 6).Delta.Value, default(ExactDecimal) - Position(factor, 2).Delta.Value, "short put delta");
        Same(Position(factor, 6).Base.Amount, default(ExactDecimal) - Position(factor, 2).Base.Amount, "short put base");
    }

    private static void FallbackDelta()
    {
        var (_, run) = Portfolio.Value;
        var factor = run.Factors[0];
        foreach (var contract in new[] { 13, 14 })
            Equal(run.OptionCalibrations.Single(r => r.Series.Code == OptionMarket.SeriesCode(contract)).Mode, ImpliedVolatility.FallbackMode);
        // Three in-the-money lots move one underlying contract each: 3 * 10000 USD per USD/MMBtu.
        Same(Position(factor, 3).Delta.Value, D("30000"), "in-the-money fallback delta");
        Same(Position(factor, 3).Up.Amount, D("30030"), "in-the-money fallback up");
        Same(Position(factor, 3).Gamma.Value, D("0"), "in-the-money fallback gamma");
        Same(Position(factor, 4).Delta.Value, D("0"), "out-of-the-money fallback delta");
        Same(Position(factor, 4).Up.Amount, D("0"), "out-of-the-money fallback up");
    }

    private static void DeclaredDependencies()
    {
        var (market, run) = Portfolio.Value;
        foreach (var contract in run.Contracts)
        {
            Equal(contract.ModelVersion, AmericanFuturesOptionModel.Id);
            if (contract.Dependencies.Length != 1 || contract.Dependencies[0] != market.UnderlyingRequest)
                throw new Exception("An option must declare exactly its underlying futures request.");
        }
        // Bumping the call's own settlement price is not a risk factor of any option:
        // nothing reprices and every value stays at base.
        var settlement = run.Factors[1];
        if (settlement.Bump.Input.Series != market.S(11)) throw new Exception("The second factor must be the settlement bump.");
        Equal(settlement.UpWork.UnitPricerCalls.ToString(CultureInfo.InvariantCulture), "0");
        Equal(settlement.DownWork.UnitPricerCalls.ToString(CultureInfo.InvariantCulture), "0");
        foreach (var position in settlement.Positions)
        {
            Same(position.Up.Amount, position.Base.Amount, "settlement-bumped up");
            Same(position.Delta.Value, D("0"), "settlement-bumped delta");
        }
    }

    // The result fields a replay must reproduce: every holding's values under every
    // factor and every option's calibration receipt.
    private static string Serialize(RiskRun run) => System.Text.Json.JsonSerializer.Serialize(new
    {
        factors = run.Factors.Select(f => new
        {
            series = f.Bump.Input.Series.Id, size = f.Bump.Size.ToString(),
            positions = f.Positions.OrderBy(p => p.TradeId).Select(p => new
            {
                trade = p.TradeId, book = p.BookId, valuation = p.ValuationId,
                values = new[] { p.Base.Amount, p.Up.Amount, p.Down.Amount, p.Delta.Value, p.Gamma.Value }.Select(v => v.ToString()),
            }),
        }),
        totals = run.Factors.SelectMany(f => f.Totals).Select(t => new[] { t.Base.Amount, t.Up.Amount, t.Down.Amount, t.Delta.Value }.Select(v => v.ToString())),
        calibrations = run.OptionCalibrations,
    });

    private static void ReplayIdentical()
    {
        var (_, run) = Portfolio.Value;
        var market = new OptionMarket();
        ImmutableArray<Trade> replay =
        [
            OptionMarket.Holding(1, 1, market.Option(11), "2"),
            OptionMarket.Holding(2, 1, market.Option(12), "1"),
            OptionMarket.Holding(3, 1, market.Option(13), "3"),
            OptionMarket.Holding(4, 1, market.Option(14), "1"),
            OptionMarket.Holding(5, 2, market.Option(11), "-1"),
            OptionMarket.Holding(6, 2, market.Option(12), "-1"),
        ];
        var second = market.Calculate(replay, [market.FuturesBump, market.SettlementBump(11)]);
        Equal(Serialize(second), Serialize(run));
    }

    // Pinned from the first macOS run; CI repeats the check on Linux.
    private const string PinnedDigest = "e13e99c71fa2c947cdda13d8b6d15c7d791b29df53b2b8a025adfa6a59ec0931";

    private static void HostIndependentDigest()
    {
        var digest = Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(
            System.Text.Encoding.UTF8.GetBytes(Serialize(Portfolio.Value.Run))));
        Equal(digest, PinnedDigest);
    }

    private static void FallbackFloor()
    {
        var market = new OptionMarket();
        // Settlement 0.999 against intrinsic 1 leaves residual -0.001. A down shock of
        // 1.25 takes F to 1.75, below the strike: intrinsic + residual is negative
        // and the value is floored at zero; the up shock keeps the residual.
        var run = market.Calculate([OptionMarket.Holding(1, 1, market.Option(15), "1")], [market.FuturesBumpOf("1.25")]);
        Equal(run.OptionCalibrations.Single().Residual, "-0.001");
        var position = Position(run.Factors[0], 1);
        Same(position.Base.Amount, D("9990"), "base");
        Same(position.Down.Amount, D("0"), "floored down");
        Same(position.Up.Amount, D("22490"), "up");
    }

    private static void ForeignMarketBinding()
    {
        var market = new OptionMarket();
        var snapshot = market.Snapshot();
        var option = market.Option(13);
        var basis = market.Model().Build(snapshot, [market.UnderlyingRequest, market.SettlementRequest(13)]);
        var binding = FuturesOptionPricing.Prepare(option, basis, snapshot, new ExplicitDiscounting(OptionMarket.Discount, DiscountFactorSource.Explicit),
            FuturesOptionPricing.DailySettlement, Policy, true, CancellationToken.None);
        var portfolio = ValuationPortfolio.FromTrades([OptionMarket.Holding(1, 1, option, "1")],
            new(ValuationMeasure.PresentValue, ImmutableDictionary<Guid, FuturesMarketBinding>.Empty, null)
            { OptionBindings = ImmutableDictionary<Guid, FuturesOptionMarketBinding>.Empty.Add(option.Id, binding) });
        // Same snapshot, same instruments; only the option quote's clearing increment differs.
        _ = RiskEngine.Calculate(market.Model(), snapshot, portfolio, [market.FuturesBump], RiskEngine.NumericalPolicy);
        Reject("option_definition", () => RiskEngine.Calculate(market.Model(optionIncrement: "0.0005"), snapshot, portfolio,
            [market.FuturesBump], RiskEngine.NumericalPolicy));
    }

    private static void ReceiptInputs()
    {
        var (_, run) = Portfolio.Value;
        Equal(run.ScenarioPolicy!, FuturesOptionPricing.ScenarioPolicy);
        var receipt = run.OptionCalibrations.Single(r => r.Series.Code == OptionMarket.SeriesCode(11));
        Equal(receipt.ScenarioPolicy, FuturesOptionPricing.ScenarioPolicy);
        Equal(receipt.DiscountToExpiry, "0.999");
        Equal(receipt.BaseFutures, "3");
        Equal(receipt.Settlement, "0.08");
    }

    private sealed class NoDiscounting : ISideDiscounting
    {
        public ImmutableArray<SidePaymentFactor> Resolve(ImmutableArray<SidePaymentKey> payments, MarketSnapshot snapshot) => [];
    }

    private static void OptionBindingErrors()
    {
        var market = new OptionMarket();
        ImmutableArray<Trade> fallback = [OptionMarket.Holding(1, 1, market.Option(13), "1")];
        ImmutableArray<QuoteBump> bumps = [market.FuturesBump];
        Reject("option_observation_role", () => market.Calculate(fallback, bumps, role: "trader_mark"));
        Reject("option_observation_role", () => market.Calculate(fallback, bumps,
            model: market.Model(futuresRole: ObservationRole.TraderMark), snapshot: market.Snapshot(ObservationRole.TraderMark)));
        Reject("futures_option_specification", () => market.Model(optionRole: ObservationRole.TraderMark));
        Reject("option_discount", () => market.Calculate(fallback, bumps, discounting: new NoDiscounting()));
        try { market.Calculate(fallback, bumps, fallbackDeclared: false); throw new Exception("Expected an undeclared fallback error."); }
        catch (MarketDataException error) when (error.Code == "option_calibration_fallback_undeclared")
        {
            if (!error.Message.Contains(OptionMarket.SeriesCode(13), StringComparison.Ordinal)) throw new Exception("The error must name the option series.");
            if (error.InnerException is not MarketDataException { Code: "option_calibration_fallback_undeclared" })
                throw new Exception("The labelled error must keep the solver's error.");
        }
        Reject("unsupported", () => new FuturesOptionInstrument(market.Series(13, "futures_style_margin")));
        Reject("option_definition", () => new FuturesOptionInstrument(market.Series(13) with { Multiplier = D("0") }));
        // A product that accepts no contrary instructions has no instruction cutoff; a stated cutoff still precedes expiry.
        _ = new FuturesOptionInstrument(market.Series(13) with { ContraryInstructionsAllowed = false, InstructionCutoffAt = null });
        Reject("option_definition", () => new FuturesOptionInstrument(market.Series(13) with { InstructionCutoffAt = OptionMarket.Expiry.AddTicks(10) }));
        Reject("option_definition", () => new FuturesOptionInstrument(market.Series(13) with { ContraryInstructionsAllowed = true, InstructionCutoffAt = null }));
        Reject("option_lots", () => OptionMarket.Holding(1, 1, market.Option(13), "1.5"));
        Reject("option_lots", () => OptionMarket.Holding(1, 1, market.Option(13), "0"));
        Reject("option_expired", () => market.Calculate(fallback, bumps, snapshot: market.Snapshot() with
            { ValuationAt = OptionMarket.Expiry, MarketCutoff = OptionMarket.Expiry }));
    }

    private enum TestState { Alive, Exercised }

    private sealed class TestLattice(int steps, string discount) : ILattice
    {
        public LatticeDecimal UpProbability { get; init; } = LatticeDecimal.Parse("0.5");
        public LatticeDecimal DownProbability { get; init; } = LatticeDecimal.Parse("0.5");
        public Func<int, int>? NodeCount { get; init; }
        public bool NullNext { get; init; }
        public bool Overshoot { get; init; }
        public int Steps => steps;
        public int Nodes(int step) => NodeCount?.Invoke(step) ?? step + 1;
        public LatticeDecimal Underlying(int step, int node) =>
            LatticeDecimal.Parse("100") * LatticeDecimal.Parse("1.2").Pow(step - node) * LatticeDecimal.Parse("0.8").Pow(node);
        public (int Node, LatticeDecimal Probability)[] Next(int step, int node) =>
            NullNext ? null! : [(node, UpProbability), (Overshoot ? step + 2 : node + 1, DownProbability)];
        public LatticeDecimal Discount(int step) => LatticeDecimal.Parse(discount);
    }

    private sealed class TestPut(string strike, bool american) : IInductionContract<TestState>
    {
        public bool OmitExercised { get; init; }
        public bool NoDecisions { get; init; }
        public bool DuplicateStates { get; init; }
        public bool NoStates { get; init; }
        public ImmutableArray<TestState> States => NoStates ? [] : DuplicateStates ? [TestState.Alive, TestState.Alive] : american ? [TestState.Alive, TestState.Exercised] : [TestState.Alive];
        private LatticeDecimal Intrinsic(LatticeDecimal underlying) =>
            LatticeDecimal.Max(LatticeDecimal.Parse(strike) - underlying, LatticeDecimal.Parse("0"));
        public LatticeDecimal Terminal(LatticeDecimal underlying, TestState state) =>
            state == TestState.Alive ? Intrinsic(underlying) : LatticeDecimal.Parse("0");
        public ImmutableArray<InductionDecision<TestState>> Decisions(int step, LatticeDecimal underlying, TestState state)
        {
            var zero = LatticeDecimal.Parse("0");
            if (NoDecisions) return [];
            if (state == TestState.Exercised) return [new("hold", zero, TestState.Exercised)];
            var hold = new InductionDecision<TestState>("hold", zero, TestState.Alive);
            if (!american) return [hold];
            return [hold, new("exercise", Intrinsic(underlying), OmitExercised ? (TestState)7 : TestState.Exercised)];
        }
    }

    // Exercise replaces an option's final settlement: a frozen-settlement curve over options is refused, not read as a forward.
    private static void NoFrozenOptionSettlement()
    {
        var market = new OptionMarket();
        var model = market.Model(optionExpiry: ConstituentExpiryRule.FreezeFinalSettlement);
        var after = OptionMarket.Expiry.AddHours(1);
        var final = new MarketObservation(new(market.Revision.CatalogId, OptionMarket.G(799)), market.S(11), market.R("exchange"),
            ObservationRole.FinalContractSettlement, PublicationState.Final, OptionMarket.Expiry, D("0.08"));
        var snapshot = new MarketSnapshot(OptionMarket.G(798), market.Revision, after, after, InputSelectionPolicy.ExactPinnedFinalObservationsNoAgeFiltering,
            [new(new(market.S(11), InputUse.FrozenFact), final)]);
        Reject("frozen_fact", () => model.Build(snapshot, [market.SettlementRequest(11)]));
    }

    private static void Check(string label, Action run) { run(); Console.WriteLine($"PASS {label}"); }
    private static void Equal(string actual, string expected)
    { if (actual != expected) throw new Exception($"Expected {expected}; got {actual}."); }
    private static void Reject(string code, Action action)
    { try { action(); } catch (MarketDataException error) when (error.Code == code) { return; } throw new Exception($"Expected {code}."); }
}
