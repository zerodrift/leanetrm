using OpenEtrm.MarketData.Options;
using OpenEtrm.MarketData.Trading;
using OpenEtrm.Shared.Numerics;

internal static partial class GasSwingChecks
{
    private static SignedRatio R(int value) => new(value, 1);
    private static LatticeDecimal D(string value) => LatticeDecimal.Parse(value);

    private static SwingChoiceInputs Inputs(int[] amounts, int low, int high, int minimum, int maximum) =>
        new([.. amounts.Select((amount, i) => new SwingPayoffDay(new(From.AddDays(i), At.AddDays(i + 1)),
            R(amount), R(0), D("0")))], low, high, minimum, maximum, 0, At, SwingPricingBinding.SupportedNumericalPolicy);

    private static void ChoiceNumerics()
    {
        void Exact(SwingChoiceInputs inputs, SignedRatio expected, params int[] lots)
        {
            var result = SwingChoice.Value(inputs);
            Require(result.Value == expected && result.IntrinsicValue == expected && result.IntrinsicLots.SequenceEqual(lots),
                $"Unexpected swing amount {result.Value} or lots {string.Join(',', result.IntrinsicLots)}.");
        }

        Exact(Inputs([20, -20, 40], 7, 11, 24, 27), R(480), 9, 7, 11);
        Exact(Inputs([-10, -20, -30], 7, 11, 24, 27), R(-450), 10, 7, 7);
        var shared = Inputs([100, -50, 200], 0, 10, 0, 10);
        Exact(shared, R(2000), 0, 0, 10);
        Exact(shared with { CommittedLots = 9 }, R(200), 0, 0, 1);
        Exact(Inputs([1, 1], 0, 1, 1, 1), R(1), 0, 1);
        Exact(Inputs([-10, -20], 0, 1, 0, 1), R(0), 0, 0);
        var forced = Inputs([1, 2], 0, 1, 2, 2);
        var small = new SignedRatio(1, 3 * System.Numerics.BigInteger.Pow(10, 25));
        Exact(forced with { Days = [..forced.Days.Select(d => d with { Uncertain = small, Volatility = null })] },
            small + small, 1, 1);
        var extreme = Inputs([1], int.MaxValue - 1, int.MaxValue, int.MaxValue, int.MaxValue);
        Exact(extreme, R(int.MaxValue), int.MaxValue);

        var halfAtom = new SignedRatio(1, 2 * System.Numerics.BigInteger.Pow(10, 18));
        var fixedOnly = Inputs([0], 0, 3, 0, 3);
        var fixedDay = fixedOnly.Days[0] with { Certain = halfAtom, Volatility = D("0.5") };
        Refuse("exercise_marks", () => SwingChoice.Value(fixedOnly with { Days = [fixedDay with { Volatility = null }] }));
        Exact(fixedOnly with { Days = [fixedDay] }, R(3) * halfAtom, 3);
        var nextMonth = new SwingDay(fixedDay.Day.Day.AddDays(2), fixedDay.Day.ExerciseAt.AddDays(2));
        var zeroVolDay = new SwingPayoffDay(nextMonth, R(2) * halfAtom, R(0), D("0"));
        Exact(fixedOnly with { Days = [fixedDay, zeroVolDay], MaximumLots = 4 }, R(7) * halfAtom, 1, 3);

        var optional = Inputs([5], 0, 1, 0, 1);
        var day = optional.Days[0] with { Volatility = D("0.5") };
        var active = optional with { Days = [day] };
        Refuse("exercise_marks", () => SwingChoice.Value(optional with { Days = [day with { Volatility = null }] }));
        Refuse("exercise_marks", () => SwingChoice.Value(active with { Days = [day with { Volatility = D("-0.1") }] }));
        Refuse("swing_feasibility", () => SwingChoice.Value(active with { MinimumLots = 2, MaximumLots = 2 }));
        Refuse("swing_determination", () => SwingChoice.Value(active with { CalculationAt = day.Day.ExerciseAt }));
        Refuse("swing_numerical_scope", () => SwingChoice.Value(active with { NumericalPolicy = "unspecified" }));
        Refuse("swing_numerical_scope", () => SwingChoice.Value(Inputs(new int[32], 0, 1, 0, 1)));
        Refuse("swing_numerical_scope", () => SwingChoice.Value(active with { DailyMaximumLots = 33 }));
        Refuse("swing_numerical_scope", () => SwingChoice.Value(active with { CalculationAt = At.AddDays(-401) }));
        Refuse("swing_numerical_scope", () => SwingChoice.Value(active with { Days = [day with { Volatility = D("9999") }] }));
        // Exactly 1,600 regular ticks fit. An off-grid second deadline would add a 1,601st layer.
        var lastAt = At.AddDays(400);
        var offGrid = day with { Day = new(From.AddDays(1), At.AddDays(399).AddHours(1)) };
        var lastGrid = day with { Day = new(From.AddDays(2), lastAt) };
        var fullGrid = active with { Days = [day with { Day = new(From, At.AddHours(6)) }, lastGrid] };
        Require(SwingPriceLattice.Grid(fullGrid).Transitions.Length == 1600, "The maximum regular grid must fit exactly.");
        Refuse("swing_numerical_scope", () => SwingChoice.Value(fullGrid with { Days = [fullGrid.Days[0], offGrid, lastGrid] }));
        var wide = Inputs([.. Enumerable.Repeat(1, 31)], 0, 32, 0, 31 * 32);
        Refuse("swing_numerical_scope", () => SwingChoice.Value(wide with { CalculationAt = At.AddDays(-320),
            Days = [.. wide.Days.Select(d => d with { Volatility = D("0.5") })] }));
        SwingVolumeGrid.RequireStateCount(2049);
        Refuse("swing_numerical_scope", () => SwingVolumeGrid.RequireStateCount(2050));
        try { SwingChoice.Value(active, new CancellationToken(true)); throw new Exception("Swing ignored cancellation."); }
        catch (OperationCanceledException) { }

        // Cancel after the solver starts, on a stochastic workload admitted by the production caps.
        using var cancel = new CancellationTokenSource();
        using var started = new ManualResetEventSlim();
        var running = Task.Run(() =>
        {
            started.Set();
            return SwingChoice.Value(active with { CalculationAt = At.AddDays(-200) }, cancel.Token);
        });
        if (!started.Wait(TimeSpan.FromSeconds(5))) throw new Exception("Swing task did not start.");
        cancel.CancelAfter(TimeSpan.FromMilliseconds(10));
        try { running.WaitAsync(TimeSpan.FromSeconds(5)).GetAwaiter().GetResult(); throw new Exception("Swing calculation completed despite cancellation."); }
        catch (OperationCanceledException e) when (e.CancellationToken == cancel.Token) { }
        Console.WriteLine("PASS swing cancellation before and during pricing; days, daily choices, volume states, time and inserted deadlines, actions and magnitude bounds");

        Exact(Inputs([], 0, 1, 4, 5) with { CommittedLots = 4 }, R(0));
        Refuse("swing_feasibility", () => SwingChoice.Value(Inputs([], 0, 1, 4, 5)));
        Refuse("swing_model_scope", () => SwingChoice.Value(active with { Days = [day with { Certain = default }] }));
        var sameMonth = day with { Day = day.Day with { Day = From.AddDays(1), ExerciseAt = day.Day.ExerciseAt.AddDays(1) } };
        Refuse("exercise_marks", () => SwingChoice.Value(active with { Days = [day, sameMonth with { Volatility = D("0.4") }] }));
        Refuse("swing_numerical_scope", () => SwingChoice.Value(active with
            { Days = [day with { Uncertain = new SignedRatio(System.Numerics.BigInteger.Pow(10, 24), 1) }] }));
        var oneStep = active with { CalculationAt = day.Day.ExerciseAt.AddHours(-1) };
        // Two intrinsic choices + one root continuation + two choices at each of three end nodes.
        Require(SwingChoice.Value(oneStep).Work == new SwingWorkCounts(1, 3, 2, 9),
            "Count reachable work, including the exact intrinsic plan.");
        var tinyUnit = new SignedRatio(1, System.Numerics.BigInteger.Pow(10, 18));
        foreach (var scale in new[] { 1, 2 })
        {
            var quantity = R(scale) * tinyUnit;
            var scaled = day with { Uncertain = quantity, Certain = quantity * new SignedRatio(-2, 5), Volatility = D("0.1") };
            Require(SwingChoice.Value(oneStep with { Days = [scaled] }).Value == tinyUnit,
                "A scaled contractual size requires fresh half-even model inputs.");
        }

        TinyTree();
        Martingale();
        Console.WriteLine("PASS swing exact values, adapted policies, final decisions, tie breaking, fractional time, martingale and bounded work");
    }

    private static void TinyTree()
    {
        var half = D("0.5");
        LatticeDecimal[][] probabilities = [[half, half], [half, half]];
        // Node prices 2/4 and 4/3, 8/3, 16/3. Compute signed cash per thousand before the root rounding.
        LatticeDecimal[][] payoffs = [[], [D("-1000"), D("1000")],
            [D("-5000") / D("3"), D("-1000") / D("3"), D("7000") / D("3")]];
        SignedRatio[][] exact = [[R(-1000), R(1000)], [new(-5000, 3), new(-1000, 3), new(7000, 3)]];
        var one = Inputs([1, 1], 0, 1, 0, 1);
        SignedRatio Run(SwingChoiceInputs inputs) => SwingChoice.Induct(inputs, probabilities, payoffs);
        var (adapted, hindsight, strip) = EnumerateBinaryPolicies(exact, 0, 1);
        Require(adapted == new SignedRatio(7000, 12) && hindsight == new SignedRatio(5000, 6) &&
            strip == new SignedRatio(13000, 12) && Run(one) == HalfEven18.Round(adapted),
            "Joint decisions must use only observable history.");
        foreach (var (minimum, maximum) in new[] { (0, 2), (1, 1), (2, 2), (0, 0) })
        {
            var expected = EnumerateBinaryPolicies(exact, minimum, maximum).Adapted;
            Require(Run(one with { MinimumLots = minimum, MaximumLots = maximum }) == HalfEven18.Round(expected),
                $"Adapted policy enumeration disagrees for [{minimum},{maximum}].");
        }
    }

    // One action per observed history. Test a complete policy across all four paths before maximizing.
    private static (SignedRatio Adapted, SignedRatio Hindsight, SignedRatio Strip) EnumerateBinaryPolicies(
        SignedRatio[][] amounts, int minimum, int maximum)
    {
        SignedRatio? best = null;
        var hindsight = SignedRatio.Zero;
        var strip = SignedRatio.Zero;
        for (var first = 0; first < 2; first++)
        for (var second = 0; second < 2; second++)
        {
            var a = amounts[0][first];
            var b = amounts[1][first + second];
            hindsight += new[] { SignedRatio.Zero, a, b }.Max() / 4;
            strip += (new[] { SignedRatio.Zero, a }.Max() + new[] { SignedRatio.Zero, b }.Max()) / 4;
        }
        for (var mask = 0; mask < 64; mask++)
        {
            var sum = SignedRatio.Zero;
            var feasible = true;
            for (var path = 0; path < 4; path++)
            {
                var first = path / 2;
                var early = (mask >> first) & 1;
                var last = (mask >> (2 + path)) & 1;
                feasible &= early + last >= minimum && early + last <= maximum;
                sum += amounts[0][first] * R(early) + amounts[1][first + path % 2] * R(last);
            }
            if (feasible && (best is null || sum.CompareTo(best.Value) > 0)) best = sum;
        }
        return (best!.Value / 4, hindsight, strip);
    }

    private static void Martingale()
    {
        var inputs = Inputs([1, 1], 0, 1, 0, 1);
        var start = inputs.CalculationAt;
        var first = new SwingDay(new(2041, 1, 31), start.AddHours(2));
        var last = new SwingDay(new(2041, 2, 1), start.AddHours(25));
        inputs = inputs with { Days = [new(first, R(1), R(0), D("0.2")), new(last, R(1), R(0), D("0.7"))] };
        var grid = SwingPriceLattice.Grid(inputs);
        var pays = grid.Payoffs(inputs, default);
        var third = D("1") / D("3");
        Require(grid.Transitions.Length == 5 && grid.Transitions[0][0] == third / D("6") &&
            grid.Transitions[^1][0] == D("5") / D("36"), "Retain fractional first and last intervals.");
        LatticeDecimal[] mass = [D("1")];
        for (var t = 0; t < grid.Transitions.Length; t++)
        {
            var p = grid.Transitions[t];
            Require(p.Length == 3 && p[0] == p[2] && p.Aggregate(default(LatticeDecimal), (a, b) => a + b) == D("1")
                && p.All(x => x.CompareTo(default) >= 0), "Each transition must be a probability distribution.");
            var next = new LatticeDecimal[mass.Length + 2];
            for (var node = 0; node < mass.Length; node++)
            for (var branch = 0; branch < 3; branch++) next[node + branch] += mass[node] * p[branch];
            mass = next;
            if (pays[t + 1].Length == 0) continue;
            var mean = mass.Zip(pays[t + 1], (a, b) => a * b).Aggregate(default(LatticeDecimal), (a, b) => a + b);
            Require((mean - D("1")).CompareTo(D("0.000000000000000000000001")) <= 0 &&
                (D("1") - mean).CompareTo(D("0.000000000000000000000001")) <= 0, "Forward martingale mean drifted.");
        }
    }
}
