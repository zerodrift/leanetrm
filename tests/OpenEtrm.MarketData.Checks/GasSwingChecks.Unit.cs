using OpenEtrm.MarketData;
using System.Collections.Immutable;
using System.Text.Json;
using OpenEtrm.MarketData.Options;
using OpenEtrm.MarketData.Trading;
using OpenEtrm.Shared.Numerics;

internal static partial class GasSwingChecks
{
    private static void CheckSwingNodeArithmetic()
    {
        var side = LatticeDecimal.Parse("0.166666666666666666666666666667");
        var middle = LatticeDecimal.Parse("0.666666666666666666666666666666");
        // In particular, adding a cancelling third term must never conceal an
        // overflow in the first sum or in the physical product of an immediate reward.
        var large=SwingLatticeAmount.From(D("900000000000000000000000"));
        var minusLarge=SwingLatticeAmount.From(D("-900000000000000000000000"));
        Refuse("numeric_range",()=>SwingLatticeAmount.Sum(large,large,minusLarge));
        Refuse("numeric_range",()=>new SwingLatticePayoff(D("2"),minusLarge.ToLattice()).Value(large));
        var random = new Random(617);
        var values = new List<LatticeDecimal>();
        foreach (var integer in new[] { "0", "1", "19", "99999999999999", "100000000000001", "900000000000000000000000" })
        foreach (var fraction in new[] { "000000000000000000000000000001", "249999999999999999999999999999",
            "250000000000000000000000000000", "500000000000000000000000000000", "750000000000000000000000000001",
            "999999999999999999999999999999" })
        foreach (var sign in new[] { "", "-" }) values.Add(LatticeDecimal.Parse(sign+integer+"."+fraction));
        for (int i=0;i<4000;i++)
            values.Add(LatticeDecimal.Parse($"{random.NextInt64(-50000000,50000000)}.{random.NextInt64(1000000000000000):D15}{random.NextInt64(1000000000000000):D15}"));
        foreach (var denominator in new[] { "0.999999999999999999999999999999", "1","1.000000000000000000000000000001",
            "1.000427871059220013035917764523","2","7.500000000000000000000000000001","1000000000000000000000" })
        {
            var n=LatticeDecimal.Parse(denominator);
            var normalizer=new SwingLatticeNormalizer(n);
            foreach (var value in values.Where(v=>v.CompareTo(default)>0))
            {
                var expected=value/n;
                if (expected==default)
                    Refuse("swing_numerical_scope",()=>normalizer.Multiplier(new SwingGrowthFactor(value)));
                else
                {
                    var actual=normalizer.Multiplier(new SwingGrowthFactor(value));
                    Require(actual.CompareTo(SwingLatticeAmount.From(expected))==0, $"Incorrect normalized quotient for {value}/{n}.");
                }
            }
        }
        foreach (var value in values)
        {
            var node = SwingLatticeAmount.From(value);
            Require(node.ToLattice()==value, "The rolling representation must preserve every lattice digit.");
            var opposite = SwingLatticeAmount.From(default(LatticeDecimal)-value);
            Require((node+opposite).ToLattice()==default && Math.Sign(node.CompareTo(opposite))==Math.Sign(value.CompareTo(opposite.ToLattice())),
                "Rolling addition and comparison preserve the exact lattice amount.");
            node.RegularWeights(out var weightedSide,out var weightedMiddle);
            Require(weightedSide.ToLattice()==side*value && weightedMiddle.ToLattice()==middle*value &&
                weightedMiddle.CompareTo(SwingLatticeAmount.From(middle*value))==0,
                "Each branch rounds independently before summation.");
            if (value.CompareTo(LatticeDecimal.Parse("100000000000000"))<0 && value.CompareTo(LatticeDecimal.Parse("-100000000000000"))>0)
                foreach (var coefficient in new[] { "-3.245","0","0.001","2.655","7","7.001","3.000000000000000001" })
                {
                    var slope=LatticeDecimal.Parse(coefficient);
                    var fixedAmount=LatticeDecimal.Parse("-3.000000000000000001");
                    var payoff=new SwingLatticePayoff(slope,fixedAmount);
                    Require(payoff.Value(node).ToLattice()==slope*value+fixedAmount,
                        $"Payoff rounding differs for {coefficient} times {value}.");
                }
            opposite.RegularWeights(out _,out var center);
            var actual = SwingLatticeAmount.Sum(weightedSide,center,weightedSide).ToLattice();
            var expected = side*value+middle*opposite.ToLattice()+side*value;
            Require(actual==expected, $"Regular branches differ at {value}: {actual} vs {expected}.");
        }
    }

    private static void UnitWorkLimits()
    {
        var terms = Terms(1200) with { Upper = null, TotalBasis = null };
        var period = SwingFeasibility.Resolve(terms, Units(1000), [], Before).Single();
        long Refusal()
        {
            var start = GC.GetAllocatedBytesForCurrentThread();
            Refuse("swing_numerical_scope", () => SwingPlanGrid.Build(period, PlanBudget with { MaximumStates = 1 }));
            return GC.GetAllocatedBytesForCurrentThread() - start;
        }
        Refusal();
        UnitResourceFixtures();
        Require(Refusal() < 8000, "Refuse before allocating the open-day array.");
        foreach (var (length, steps) in new[] { (90, 357), (365, 1457) })
        {
            var right = Terms(length, new(2041, 1, 1)) with
            {
                Minimum = R(9, 10),
                Upper = length == 90 ? R(901, 10) : R(1005, 1000),
                TotalBasis = length == 90 ? SwingQuantityRight.PerPeriod : SwingQuantityRight.PerDay,
                Period = length == 90 ? SwingQuantityRight.Term : SwingQuantityRight.Month,
                CountRule = SwingQuantityRight.DiffersFromBase,
                ChangeCount = 2
            };
            var at = right.Days[0].ExerciseAt.AddHours(-6);
            var estimate = SwingUnitResources.Booking(right, Units(1000), at);
            Require(estimate.Steps == steps && estimate.MaximumPriceNodes == 2 * steps + 1 && estimate.MaximumStates <= 9,
                "Count the full common clock while resetting monthly quantity states.");
            Refuse("swing_numerical_scope", () => SwingUnitResources.Booking(right, Units(1000), at.AddYears(-30)));
        }
    }

    private sealed record ResourceRow(string Name, int FirstDeadlineTicks, int DailyMaximum, long Operations, long Bytes, string? Refusal);
    private sealed record ResourceCases(string Policy, DateTimeOffset Origin, ResourceRow[] Cases);

    private static void UnitResourceFixtures()
    {
        var path = Path.Combine(AppContext.BaseDirectory, "fixtures", "gas-swing-unit-resource-v2.json");
        var resource = JsonSerializer.Deserialize<ResourceCases>(File.ReadAllText(path))!;
        Require(resource.Policy == SwingUnitResources.Policy, "Fixtures pin the numerical version.");
        foreach (var row in resource.Cases)
        {
            var right = Terms(1) with
            {
                Minimum = R(0),
                Maximum = R(row.DailyMaximum),
                Step = R(1),
                Upper = null,
                TotalBasis = null,
                Days = [new(From, resource.Origin.AddHours(6L * row.FirstDeadlineTicks))]
            };
            var allocated = GC.GetAllocatedBytesForCurrentThread();
            try
            {
                var estimate = SwingUnitResources.Booking(right, Units(1), resource.Origin);
                Require(row.Refusal == null && estimate.Operations == row.Operations && estimate.PeakBytes == row.Bytes,
                    $"Wrong structural counts for {row.Name}: {estimate}.");
            }
            catch (MarketDataException e) when (e.Code == "swing_numerical_scope" && row.Refusal != null &&
                e.Message.Contains(row.Refusal, StringComparison.Ordinal))
            { }
            Require(GC.GetAllocatedBytesForCurrentThread() - allocated < 200000,
                $"Structural admission allocated a large price or move array for {row.Name}.");
        }
        foreach (var (limit, name) in new[] { (SwingUnitResources.MaximumOperations, "operations"), (SwingUnitResources.MaximumBytes, "modeled live bytes") })
        {
            SwingUnitResources.Require(limit - 1, limit, name);
            SwingUnitResources.Require(limit, limit, name);
            Refuse("swing_numerical_scope", () => SwingUnitResources.Require(limit + 1, limit, name));
        }

        var huge = System.Numerics.BigInteger.One << 100;
        var sparse = Terms(3) with
        {
            Minimum = R(0),
            Maximum = R(1),
            Step = new(1, huge),
            Lower = R(3),
            Upper = null,
            TotalBasis = SwingQuantityRight.PerPeriod
        };
        var at = sparse.Days[0].ExerciseAt.AddHours(-6);
        var feasible = SwingUnitResources.Booking(sparse, Units(1), at);
        Require(feasible.StateSlots == 4 && feasible.Moves == 3 && feasible.Steps == 0,
            "A forced sparse commercial term has no price lattice and three exact surviving edges.");
        var wideRest = sparse with { Lower = null, TotalBasis = null, ChangeCount = 0, CountRule = SwingQuantityRight.DiffersFromBase,
            Step = new(1,System.Numerics.BigInteger.One << 4095) };
        var restOnly = SwingUnitResources.Booking(wideRest,Units(1),at);
        Require(restOnly.StateSlots == 4 && restOnly.Moves == 3 && restOnly.Steps == 0,
            "No-total graphs have no cumulative exact level, even when the base index fills the bit budget.");
        var flat = Terms(1200) with { Minimum = R(1), Maximum = R(1), Upper = null, TotalBasis = null,
            ChangeCount = 1200, CountRule = SwingQuantityRight.DiffersFromBase };
        var flatWork = SwingUnitResources.Booking(flat,Units(1),flat.Days[0].ExerciseAt.AddHours(-1));
        Require(flatWork.StateSlots == 1201 && flatWork.Moves == 1200 && flatWork.MaximumStates == 1,
            "A flat daily range cannot consume any changed-day count.");
        var dense = sparse with { Lower = null, Upper = R(2), ChangeCount = 2, CountRule = SwingQuantityRight.DiffersFromBase };
        Refuse("swing_numerical_scope", () => SwingUnitResources.Booking(dense, Units(1), at));

        var late = Terms(3) with { Upper = R(3), ChangeCount = 1, CountRule = SwingQuantityRight.DiffersFromBase };
        var lateAt = late.Days[0].ExerciseAt;
        var structural = SwingUnitResources.Booking(late, Units(1000), lateAt);
        var restored = SwingFeasibility.Resolve(late, Units(1000), [Fact(late, 0, 700)], lateAt);
        var repaired = SwingUnitResources.Estimate(late, restored, lateAt, restored.ToHashSet(), default);
        Require(structural.Steps > 0 && structural.Operations >= repaired.Operations,
            "Late structural admission bounds future work after a timely missed-day election becomes known.");
    }

    private static void UnitScalingAndClock()
    {
        var atom = new SignedRatio(1, System.Numerics.BigInteger.Pow(10, 18));
        var tiny = atom / 3;
        var right = Terms(4, new(2041, 1, 30)) with
        {
            Minimum = R(9, 10),
            Upper = null,
            TotalBasis = null,
            Period = SwingQuantityRight.Month,
            ChangeCount = 1,
            CountRule = SwingQuantityRight.DiffersFromBase
        };
        var at = right.Days[0].ExerciseAt.AddHours(-2);
        // The clock anchors at the elected first day. A retained deadline
        // shifted by an hour inserts an extra transition before the stochastic days.
        right = right with { Days = [.. right.Days.Select((d, i) => i == 1 ? d with { ExerciseAt = d.ExerciseAt.AddHours(-1) } : d)] };
        var normalizedBuys = new Dictionary<bool, SwingUnitResult>();
        foreach (bool forced in new[] { true, false })
            foreach (var holder in new[] { ExerciseHolder.Us, ExerciseHolder.Counterparty })
            {
                var terms = right with { Holder = holder };
                var sideSign = holder == ExerciseHolder.Us ? R(1) : R(-1);
                SwingUnitChoiceInputs Inputs(SignedRatio held, bool includeStrip)
                {
                    var reference = ExactQuantity.FromRatio(held, Native);
                    ImmutableArray<SwingElection> facts = forced
                        ? [new(0, terms.Days[0].Day, ExactQuantity.FromRatio(held * R(21, 20), Native),
                            holder, Before, Before, Guid.NewGuid(), "Synthetic reservation")] : [];
                    var periods = SwingFeasibility.Resolve(terms, reference, facts, at);
                    var amounts = periods.SelectMany(p => p.Days).Where(d => d.Election == null).Select(d =>
                    {
                        bool january = d.Day.Day.Month == 1;
                        var physical = january ? R(10, 3) : R(7, 3);
                        var net = includeStrip && d.Day == terms.Days[1] ? forced ? tiny : tiny * R(10, 11) : R(0);
                        LatticeDecimal? volatility = january ? forced ? null : D("0") : D("0.25");
                        return SwingUnitChoiceInputs.FromHeld(d.Day, held * sideSign * physical,
                            held * sideSign * (net - physical), held, volatility);
                    });
                    return new(terms, periods, [..amounts], held, at, Pricing);
                }
                var original = Inputs(R(1), true);
                Refuse("swing_model_scope", () => SwingChoice.Value(original with { HeldReference = R(2) }));
                var one = SwingChoice.Value(original);
                if (holder == ExerciseHolder.Us) normalizedBuys.Add(forced, one);
                else Require(one.CountedToUs == normalizedBuys[forced].CountedToUs * R(-1) &&
                    one.Plan.SequenceEqual(normalizedBuys[forced].Plan), "Reversing holder and signed Side amounts reverses only value.");
                Require(one.Estimate.Steps == 14 && one.CountedToUs.Numerator.Sign == sideSign.Numerator.Sign,
                    "Propagate from the retained fixed first deadline through the DST split.");
                foreach (var size in new[] { atom, atom * R(2), R(1), R(10), R(1, 3) })
                {
                    var mixed = SwingChoice.Value(Inputs(size, true));
                    var stochasticOnly = SwingChoice.Value(Inputs(size, false));
                    Require(mixed.CountedToUs - stochasticOnly.CountedToUs == size * tiny * sideSign &&
                        mixed.CountedToUs == one.CountedToUs * size && mixed.IntrinsicToUs == one.IntrinsicToUs * size &&
                        mixed.Plan.SequenceEqual(one.Plan), "Add the exact independent strip after the rounded root and scale once.");
                }
            }

        var coarse = Terms(1) with { Upper = R(3, 4) };
        var single = new SwingUnitChoiceInputs(coarse, SwingFeasibility.Resolve(coarse, Units(1), [], at),
            [new(coarse.Days[0], R(1), R(-3), null)], R(1), at, Pricing);
        Refuse("exercise_marks", () => SwingChoice.Value(single));
        var negative = SwingChoice.Value(single with { Days = [single.Days[0] with { Volatility = D("0") }] });
        Require(negative.CountedToUs == R(-14, 10) && negative.Plan.Single().QuantityRatio == R(7, 10),
            "A single grid action on a flexible interval requires a mark and preserves negative value.");

        var projected = Terms(2, new(2041, 1, 31)) with
        {
            Upper = R(3, 4),
            Period = SwingQuantityRight.Month,
            Days = [new(new(2041, 1, 31), At.AddHours(2)), new(new(2041, 2, 1), At.AddHours(25))]
        };
        for (int target = 0; target < 2; target++)
        {
            var days = projected.Days.Select((d, i) => new SwingUnitPayoffDay(d, i == target ? R(1) : R(0), R(0), D(i == 0 ? "0.2" : "0.7")));
            var unit = new SwingUnitChoiceInputs(projected, SwingFeasibility.Resolve(projected, Units(1), [], At),
                [.. days], R(1), At, Pricing);
            var mean = SwingChoice.Value(unit);
            Require(mean.CountedToUs == R(7, 10) && mean.Work.FactorSteps == 5 && mean.Work.Branches > 0,
                "Each month's normalized forward has unit expectation under the fractional common clock.");
        }

        var seasonal = LongInputs(90, 37);
        var selectedPlan = SwingChoice.Value(seasonal);
        Require(selectedPlan.Plan.Length == 89 && selectedPlan.Plan.All(q => q.Day != seasonal.Right.Days[37]) &&
            selectedPlan.Plan.Count(q => q.QuantityRatio != R(1)) <= 1 && selectedPlan.Work.Branches > 0,
            "Reserve the 1,050 future election before valuing all other seasonal decisions.");
        var miss = right.Days[0].ExerciseAt;
        var late = new SwingUnitChoiceInputs(right, SwingFeasibility.Resolve(right, Units(1), [], miss),
            [.. right.Days.Skip(2).Select(d => new SwingUnitPayoffDay(d, R(3), R(-3), D("0.25")))], R(1), miss,
            Pricing);
        var laterMonth = SwingChoice.Value(late);
        Require(laterMonth.Plan.Length == 2 && laterMonth.Work.FactorSteps == 14 && laterMonth.Work.Branches > 0 &&
            laterMonth.Plan.All(q => q.Day.Day.Month == 2), "A missed January decision still permits the independent February problem.");
        Refuse("exercise_marks", () => SwingChoice.Value(late with { Days = [.. late.Days.Select(d => d with { Volatility = null })] }));

        var optional = coarse with { Minimum = R(0), Maximum = R(1), Step = R(1), Upper = null, TotalBasis = null };
        var asOf = optional.Days[0].ExerciseAt.AddDays(-30);
        var marked = new SwingUnitChoiceInputs(optional, SwingFeasibility.Resolve(optional, Units(1), [], asOf),
            [new(optional.Days[0], R(3), R(-3), D("0.25"))], R(1), asOf, Pricing);
        var baseValue = SwingChoice.Value(marked).CountedToUs;
        var priceUp = SwingChoice.Value(marked with { Days = [marked.Days[0] with { PhysicalToUs = R(301, 100) }] }).CountedToUs;
        var volUp = SwingChoice.Value(marked with { Days = [marked.Days[0] with { Volatility = D("0.26") }] }).CountedToUs;
        Require(baseValue.Numerator.Sign > 0 && priceUp.CompareTo(baseValue) > 0 && volUp.CompareTo(baseValue) > 0,
            "Marked optionality is sensitive to both forward and volatility repricing.");

        long observed = 0;
        using var inside = new ManualResetEventSlim();
        using var resume = new ManualResetEventSlim();
        using var source = new CancellationTokenSource();
        var annual = LongInputs(365);
        var running = Task.Run(() => SwingChoice.Value(annual, source.Token, count =>
        {
            if (count < 8192 || inside.IsSet) return;
            observed = count;
            inside.Set();
            if (!resume.Wait(TimeSpan.FromSeconds(10))) throw new TimeoutException("Cancellation observer did not release.");
        }));
        Require(inside.Wait(TimeSpan.FromSeconds(10)) && !running.IsCompleted, "Observe interior priced continuation on a running kernel.");
        var latency = System.Diagnostics.Stopwatch.StartNew();
        source.Cancel();
        resume.Set();
        try { running.GetAwaiter().GetResult(); throw new Exception("The running swing ignored cancellation."); }
        catch (OperationCanceledException e) when (e.CancellationToken == source.Token) { }
        Require(latency.Elapsed < TimeSpan.FromSeconds(1), "Interior swing cancellation exceeded one second.");
        try { SwingChoice.Value(annual, source.Token); throw new Exception("The swing ignored entry cancellation."); }
        catch (OperationCanceledException e) when (e.CancellationToken == source.Token) { }
        Console.WriteLine($"PASS unit-reference exact strips, normalized positions and factor clock. Cancelled after {observed} branches in {latency.Elapsed.TotalMilliseconds:F2} ms.");
    }

    private static SwingUnitChoiceInputs LongInputs(int length, int election = -1)
    {
        var right = Terms(length, new(2041, 1, 1)) with
        {
            Minimum = R(9, 10),
            Upper = length == 90 ? R(901, 10) : R(1005, 1000),
            TotalBasis = length == 90 ? SwingQuantityRight.PerPeriod : SwingQuantityRight.PerDay,
            Period = length == 90 ? SwingQuantityRight.Term : SwingQuantityRight.Month,
            CountRule = SwingQuantityRight.DiffersFromBase,
            ChangeCount = 2
        };
        var at = right.Days[0].ExerciseAt.AddHours(-6);
        var periods = SwingFeasibility.Resolve(right, Units(1000), election < 0 ? [] : [Fact(right, election, 1050)], at);
        return new(right, periods, [..periods.SelectMany(p => p.Days).Where(d => d.Election == null).Select(d =>
            new SwingUnitPayoffDay(d.Day,R(d.Day.Day.Month%2 == 1 ? 295 : 305,100),R(-3),D(d.Day.Day.Month%2 == 1 ? "0.20" : "0.25")))],
            R(1000), at, Pricing);
    }

    private sealed record UnitDay(int Seconds, string Physical, string Fixed, string Sigma, string? Election);
    private sealed record UnitCase(UnitDay[] Days, int[] Choices, int Count, int Upper, string Held, int? FirstDay, bool Reset);

    internal static void UnitOracles(string input, string output)
    {
        if (new FileInfo(input).Length > 65_536) throw new InvalidDataException("Oversized unit-reference fixture.");

        static SignedRatio Read(string text) => SignedRatio.TryParse(text, out var amount)
            ? amount : throw new InvalidDataException("Expected an exact decimal amount.");
        var results = new List<string>();
        var fixtures = JsonSerializer.Deserialize<UnitCase[]>(File.ReadAllText(input));
        if (fixtures is not { Length: > 0 and <= 32 }) throw new InvalidDataException("Expected 1–32 bounded fixtures.");
        foreach (var fixture in fixtures)
        {
            var start = new DateOnly(2041, 1, fixture.FirstDay ?? 31);
            var terms = Terms(fixture.Days.Length, start) with
            {
                Minimum = R(fixture.Choices[0]),
                Maximum = R(fixture.Choices[^1]),
                Step = R(1),
                Period = fixture.Reset ? SwingQuantityRight.Month : SwingQuantityRight.Term,
                Upper = R(fixture.Upper),
                ChangeCount = fixture.Count,
                CountRule = SwingQuantityRight.DiffersFromBase,
                Days = [.. fixture.Days.Select((day, i) => new SwingDay(start.AddDays(i), At.AddSeconds(day.Seconds)))]
            };
            var rows = fixture.Days.Select((d, i) => (Row: d, Day: terms.Days[i])).ToArray();
            var heldReference = Read(fixture.Held);
            var periods = SwingFeasibility.Resolve(terms, ExactQuantity.FromRatio(heldReference, Native), [..rows.Where(pair => pair.Row.Election != null)
                .Select(pair => new SwingElection(0,pair.Day.Day,ExactQuantity.FromRatio(heldReference*Read(pair.Row.Election!),Native),
                    terms.Holder,Before,Before,Guid.NewGuid(),"Synthetic proportional reservation"))], At);
            var inputs = new SwingUnitChoiceInputs(terms, periods, [..rows.Where(pair => pair.Row.Election == null)
                .Select(pair => new SwingUnitPayoffDay(pair.Day, Read(pair.Row.Physical), Read(pair.Row.Fixed), D(pair.Row.Sigma)))],
                heldReference, At, Pricing);
            results.Add(HalfEven18.Report(SwingChoice.Value(inputs).CountedToUs));
        }
        File.WriteAllText(output, JsonSerializer.Serialize(results));
    }
}
