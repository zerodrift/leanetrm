using System.Text.Json.Nodes;
using OpenEtrm.MarketData;
using OpenEtrm.MarketData.Options;

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
