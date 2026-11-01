using System.Collections.Immutable;
using System.Diagnostics;
using System.Globalization;
using System.Text.Json.Nodes;
using OpenEtrm.MarketData;
using OpenEtrm.MarketData.Options;

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
        Check("american futures options at a unit discount equal Black-76", ZeroRateCallEqualsBlack76);
        Check("american futures options respect no-arbitrage bounds", Bounds);
        Check("american futures options rise with volatility and time and move with the futures price", Monotonic);
        Check("american futures options converge at the pinned step count", Convergence);
        Check("american futures option year fractions use act/365 fixed instants", YearFractions);
        Check("american futures options reject valuation at or after expiry", ExpiredValuation);
        Check("american futures options reject unsupported terms, policies and inputs", RejectsUnsupportedTerms);
        Check("american futures option unit price runtime", PrintRuntime);
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
        if (cases.Length != 12) throw new Exception($"Expected 12 Black-76 cases; found {cases.Length}.");
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

    // At a unit discount a future has no carry, so neither right gains from early
    // exercise and the American value is the European Black-76 value.
    private static void ZeroRateCallEqualsBlack76()
    {
        var tolerance = Policy.PriceTolerance * L("100");
        foreach (var c in Black76Cases())
        {
            Within(Price(c, OptionRight.Call, "1"), L(c.UndiscountedCall), tolerance, $"call F={c.Futures} K={c.Strike}");
            Within(Price(c, OptionRight.Put, "1"), L(c.UndiscountedPut), tolerance, $"put F={c.Futures} K={c.Strike}");
        }
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
            AtLeast(call, L(c.Call) - Policy.PriceTolerance, $"call European {label}");
            AtLeast(put, L(c.Put) - Policy.PriceTolerance, $"put European {label}");
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

    // Six cases across both price levels: at the money, deep out of the money,
    // two years out and high volatility near expiry.
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
        Equal(LatticeDecimal.Parse("55.262").Exp().ToString(), "999957769034655906977865.83186437816185466000672134975");
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

    private static void Check(string label, Action run) { run(); Console.WriteLine($"PASS {label}"); }
    private static void Equal(string actual, string expected)
    { if (actual != expected) throw new Exception($"Expected {expected}; got {actual}."); }
    private static void Reject(string code, Action action)
    { try { action(); } catch (MarketDataException error) when (error.Code == code) { return; } throw new Exception($"Expected {code}."); }
}
