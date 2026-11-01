using System.Collections.Immutable;
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
        var european = BackwardInduction.Solve(lattice, new TestPut("100", false), CancellationToken.None);
        Equal(european.Root.ToString(), "8.91");
        Equal(european.Chosen[1][1][TestState.Alive], "hold");

        var american = BackwardInduction.Solve(lattice, new TestPut("100", true), CancellationToken.None);
        Equal(american.Root.ToString(), "9.81");
        Equal(american.Chosen[1][1][TestState.Alive], "exercise");
        Equal(american.Chosen[1][0][TestState.Alive], "hold");
        Equal(american.Chosen[0][0][TestState.Alive], "hold");  // root: hold 9.81 beats exercising at 0
        Equal(american.Chosen[1][1][TestState.Exercised], "hold");
        Equal(american.Chosen.Length.ToString(), "2");

        var tie = BackwardInduction.Solve(new TestLattice(2, "1"), new TestPut("100", true), CancellationToken.None);
        Equal(tie.Chosen[1][1][TestState.Alive], "hold");
        Equal(tie.Root.ToString(), "11");  // up node 2, down node 20, root (2+20)/2
    }

    private static void InductionContractErrors()
    {
        var put = new TestPut("100", true);
        Reject("induction_contract", () => BackwardInduction.Solve(new TestLattice(0, "0.9"), put, CancellationToken.None));
        Reject("induction_contract", () => BackwardInduction.Solve(new TestLattice(2, "0.9") { UpProbability = LatticeDecimal.Parse("0.6") }, put, CancellationToken.None));
        Reject("induction_contract", () => BackwardInduction.Solve(new TestLattice(2, "0.9") { Overshoot = true }, put, CancellationToken.None));
        Reject("induction_contract", () => BackwardInduction.Solve(new TestLattice(2, "0.9"), new TestPut("100", true) { OmitExercised = true }, CancellationToken.None));
        Reject("induction_contract", () => BackwardInduction.Solve(new TestLattice(2, "0.9"), new TestPut("100", true) { NoDecisions = true }, CancellationToken.None));
        Reject("induction_contract", () => BackwardInduction.Solve(new TestLattice(2, "0.9") { UpProbability = LatticeDecimal.Parse("-0.5"), DownProbability = LatticeDecimal.Parse("1.5") }, put, CancellationToken.None));
        Reject("induction_contract", () => BackwardInduction.Solve(new TestLattice(2, "0.9") { NodeCount = step => step == 0 ? 2 : step + 1 }, put, CancellationToken.None));
        Reject("induction_contract", () => BackwardInduction.Solve(new TestLattice(2, "0.9") { NodeCount = step => step == 2 ? 0 : step + 1 }, put, CancellationToken.None));
        Reject("induction_contract", () => BackwardInduction.Solve(new TestLattice(2, "0.9") { NodeCount = step => step == 1 ? 0 : step + 1 }, put, CancellationToken.None));
        Reject("induction_contract", () => BackwardInduction.Solve(new TestLattice(2, "0.9") { NullNext = true }, put, CancellationToken.None));
        Reject("induction_contract", () => BackwardInduction.Solve(new TestLattice(2, "0.9"), new TestPut("100", true) { DuplicateStates = true }, CancellationToken.None));
        Reject("induction_contract", () => BackwardInduction.Solve(new TestLattice(2, "0.9"), new TestPut("100", true) { NoStates = true }, CancellationToken.None));
        var cancelled = new CancellationTokenSource(); cancelled.Cancel();
        try { BackwardInduction.Solve(new TestLattice(2, "0.9"), put, cancelled.Token); throw new Exception("Expected cancellation."); }
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
