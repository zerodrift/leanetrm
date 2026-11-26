using System.Collections.Immutable;
using System.Numerics;
using OpenEtrm.MarketData;
using OpenEtrm.MarketData.Gas;
using OpenEtrm.MarketData.Options;
using OpenEtrm.MarketData.Trading;
using OpenEtrm.Shared.Numerics;

internal static partial class GasSwingChecks
{
    private static readonly ReferenceKey Native = new GasInstrumentScheduleChecks.IdentityFixture().Instrument.UnitQuantity.Specification;
    private static readonly DateTimeOffset Before = new(2039, 1, 1, 0, 0, 0, TimeSpan.Zero);
    private static readonly LevelPlanBudget PlanBudget = new(10000, 100000, 10000, 4096);
    private static SignedRatio R(long amount, long basis = 1) => new(amount, basis);
    private static ExactQuantity Units(int amount) => ExactQuantity.FromRatio(R(amount), Native);
    private static SwingQuantityRight Terms(int days = 3, DateOnly? start = null)
    {
        var first = start ?? From;
        return new(0, 0, [0, 1], ExerciseHolder.Us, R(7,10), R(11,10), R(1,10),
            "term", null, R(31,10), "per_period_v1", null, null,
            "swing_elected_quantity_v1", "best_plan_on_forwards_v1", "lead_before_first_gas_day_v1", 6,
            [..Enumerable.Range(0, days).Select(i => new SwingDay(first.AddDays(i),
                new DateTimeOffset(first.AddDays(i).ToDateTime(TimeOnly.MinValue), TimeSpan.Zero).AddHours(-6)))]);
    }

    private static SwingElection Fact(SwingQuantityRight right, int index, int amount) =>
        new(0, right.Days[index].Day, Units(amount), right.Holder, Before, Before, Guid.NewGuid(), "Synthetic day election");

    private static void ExactWorkedPlans()
    {
        var terms = Terms();
        void Case(SwingQuantityRight right, int[] cents, int[] quantities, int objective, int? elected = null, int size = 1000)
        {
            // Independently list the five native quantities of this worked contract.
            // Enumeration order settles ties before inspecting the solver result.
            var choices = new[] { 7,8,9,10,11 }.Select(tenths => tenths * size / 10)
                .OrderBy(q => Math.Abs(q-size)).ThenBy(q => q).ToArray();
            int[]? optimum = null;
            int? bestCents = null;
            foreach (var x in elected.HasValue ? new[]{elected.Value} : choices)
            foreach (var y in choices)
            foreach (var z in choices)
            {
                int[] schedule = [x,y,z];
                var total = R(x+y+z,size);
                if (right.Lower is {} lower && total.CompareTo(lower) < 0 ||
                    right.Upper is {} upper && total.CompareTo(upper) > 0 ||
                    schedule.Count(q => q != size) > right.ChangeCount) continue;
                var value = x*cents[0] + y*cents[1] + z*cents[2];
                if (bestCents == null || (right.Holder == ExerciseHolder.Us ? value > bestCents : value < bestCents))
                { optimum = schedule; bestCents = value; }
            }
            Require(optimum!.SequenceEqual(quantities) && bestCents == objective*100, "Independent enumeration of the worked example.");
            var facts = elected is {} amount ? ImmutableArray.Create(Fact(right, 0, amount)) : [];
            var budget = SwingFeasibility.Resolve(right, Units(size), facts, Before).Single();
            var grid = SwingPlanGrid.Build(budget, PlanBudget);
            var plan = SwingForwardPlan.Search(grid, [..grid.Days.Select(d => R(cents[d.Day.DayNumber - right.Days[0].Day.DayNumber] * size, 100))]);
            var path = budget.Days.Select(d => d.QuantityRatio ?? plan.QuantityRatios[grid.Days.IndexOf(d.Day)]).ToArray();
            Require(path.SequenceEqual(quantities.Select(q => R(q, size))), "Wrong chronological worked-example plan.");
            var fixedValue = elected is {} e ? R(e * cents[0], 100) : R(0);
            Require(plan.ValueToUs + fixedValue == R(objective), $"Expected worked-example value {objective}.");
        }
        int[] margins = [20,5,10];
        Case(terms with { Upper = null, TotalBasis = null }, margins, [1100,1100,1100], 385);
        Case(terms, margins, [1100,900,1100], 375);
        Case(terms with { ChangeCount = 1, CountRule = "differs_from_base_v1" }, margins, [1100,1000,1000], 370);
        Case(terms with { ChangeCount = 0, CountRule = "differs_from_base_v1" }, margins, [1000,1000,1000], 350);
        var seller = terms with { Holder = ExerciseHolder.Counterparty };
        Case(seller, margins, [700,700,700], 245);
        Case(seller, [-20,-5,-10], [1100,900,1100], -375);
        Case(terms, margins, [1100,900,1100], 375, 1100);
        Case(terms, margins, [700,1100,1100], 305, 700);
        Case(terms, margins, [1050,900,1100], 365, 1050);
        Case(terms with { Upper = R(3150,1000) }, margins, [1100,900,1100], 375);
        Case(terms, [10,10,10], [1000,1000,1100], 310);
        Case(terms, [0,0,0], [1000,1000,1000], 0);
        Case(terms with { Lower = R(25,10) }, [-20,-5,-10], [700,1100,700], -265);
        Case(terms, [21,6,11], [1100,900,1100], 406);
        Case(terms, [20,-5,10], [1100,700,1100], 295);
        Case(terms, margins, [11000,9000,11000], 3750, size: 10000);
        Case(terms, margins, [10500,9000,11000], 3650, 10500, 10000);

        // With strictly ordered positive margins, fill the best-priced free day first.
        // Both continuous optima have integer quantities; the unit mesh includes each vertex.
        foreach (var (cap, elected, expected, value, coarse) in new[] {
            (3100, (int?)1050, new[]{1050,950,1100}, 36750, 36500),
            (3150, (int?)null, new[]{1100,950,1100}, 37750, 37500) })
        {
            var best = int.MinValue;
            int[]? path = null;
            for (var x = elected ?? 700; x <= (elected ?? 1100); x++)
                for (var y = 700; y <= 1100; y++)
                {
                    var z = Math.Min(1100, cap - x - y);
                    if (z < 700) continue;
                    var score = 20*x + 5*y + 10*z;
                    if (score > best) { best = score; path = [x,y,z]; }
                }
            Require(best == value && value > coarse && path!.SequenceEqual(expected), "Continuous comparison optimum and strict grid shortfall.");
        }
    }

    private static void ExactPeriodBudgets()
    {
        var terms = Terms(31, new(2040,1,1)) with { Minimum = R(9,10), Upper = R(1005,1000),
            TotalBasis = "per_day_v1", Period = "delivery_month", ChangeCount = 2, CountRule = "differs_from_base_v1" };
        Require(SwingFeasibility.Resolve(terms, Units(1000), [], Before).Single().AppliedUpper == R(31155,1000),
            "January has a 31,155 cap at booking.");
        var budget = SwingFeasibility.Resolve(terms, Units(1000), [Fact(terms, 30, 1050)], Before).Single();
        Require(budget.AppliedUpper == R(31155,1000) && budget.Elected == R(1050,1000) &&
            budget.AppliedUpper - budget.Elected == R(30105,1000) && budget.RemainingMaximum == R(30100,1000) && budget.RemainingMinimum == R(29900,1000) &&
            budget.ChangedDays == 1 && budget.AvailableChanges == 1 && budget.Days[^1].Election != null,
            "Reserve the future election using the complete January rate and exact remaining allowance.");
        var grid = SwingPlanGrid.Build(budget, PlanBudget);
        Require(grid.TerminalMaximum == 31 && grid.Days.Length == 30, "Use inward index 31 after the future January reservation.");
        var months = Terms(32, new(2040,1,30)) with { Period = "delivery_month", Upper = R(1005,1000),
            TotalBasis = "per_day_v1", ChangeCount = 1, CountRule = "differs_from_base_v1" };
        var split = SwingFeasibility.Resolve(months, Units(1000), [Fact(months, 1, 950), Fact(months, 2, 975)], Before);
        Require(split.Select(p => p.AppliedUpper).SequenceEqual([R(2010,1000),R(29145,1000),R(1005,1000)]) &&
            split.Select(p => p.AvailableChanges).SequenceEqual([0,0,1]), "Full leap February, partial January/March and monthly reset.");
        foreach (var basis in new[]{"per_day_v1","per_period_v1"})
        {
            var pair = terms with { Days = terms.Days.RemoveAt(30), Lower = R(1), Upper = R(105,100), TotalBasis = basis };
            Refuse("swing_right", () => SwingFeasibility.Resolve(pair, Units(1000), [], Before));
        }
        var february = Terms(5, new(2040,2,27)) with { Period = "delivery_month", Upper = R(11,10), TotalBasis = "per_day_v1" };
        var partial = SwingFeasibility.Resolve(february, Units(1000), [], Before);
        Require(partial.Select(p => p.AppliedUpper).SequenceEqual([R(33,10),R(22,10)]), "Use the three retained leap-February days.");
        var absolute = february with { TotalBasis = "per_period_v1", Upper = R(31,10) };
        Require(SwingFeasibility.Resolve(absolute, Units(1000), [], Before).All(p => p.AppliedUpper == R(31,10)),
            "A per-period bound applies as stated to each partial month.");
        Refuse("swing_feasibility", () => SwingFeasibility.Resolve(absolute with { Lower = R(25,10) }, Units(1000), [], Before));
        foreach (var (basis, lower) in new[] { ("per_day_v1", R(95,100)), ("per_period_v1", R(31)) })
        {
            var pair = terms with { Lower = lower, Upper = lower + R(1,10), TotalBasis = basis };
            Require(SwingFeasibility.Resolve(pair, Units(1000), [], Before).Length == 1, "Paired bounds exactly one stated step apart.");
            Refuse("swing_right", () => SwingFeasibility.Resolve(pair with { Upper = pair.Upper - R(1,1000000000) }, Units(1000), [], Before));
        }
        var right = Terms() with { Minimum = R(850,1000), Maximum = R(1150,1000), Step = R(75,1000), Upper = R(3050,1000) };
        var offGrid = SwingFeasibility.Resolve(right, Units(1000), [Fact(right, 2, 1025)], Before).Single();
        Require(offGrid.Elected == R(41,40) && offGrid.RemainingMaximum == R(2025,1000), "Non-reciprocal relative step and off-grid elected quantity.");
        var picks = SwingForwardPlan.Search(SwingPlanGrid.Build(offGrid, PlanBudget), [R(100),R(-100)]);
        Require(picks.QuantityRatios.SequenceEqual([R(1150,1000),R(850,1000)]) && picks.ValueToUs == R(30),
            "A 75-unit grid is shifted by its 850 minimum; the 1,025 elected third day remains fixed.");
        var futureFact = Fact(right, 2, 1025) with { EffectiveAt = Before.AddHours(1) };
        Require(SwingFeasibility.Resolve(right, Units(1000), [futureFact], Before).Single().Elected == R(0),
            "A known future-effective revision reserves nothing at the earlier assessment.");
        var facts = Enumerable.Range(0,3).Select(i => Fact(right,i,1100)).ToImmutableArray();
        Refuse("swing_feasibility", () => SwingFeasibility.Resolve(right, Units(1000), facts, Before));
        Refuse("swing_election", () => SwingFeasibility.Resolve(right, Units(1000), [Fact(right,0,849)], Before));
        foreach (var bad in new[] { right with { Period = "delivery_month,term" }, right with { CountRule = "differs_from_base_v1" },
            right with { ChangeCount = 1, CountRule = "previous_day" }, right with { Upper = null }, right with { TotalBasis = null },
            right with { Minimum = R(851,1000) }, right with { PlanRule = "" }, right with { QuantityPolicy = "scheduled_only_v1" } })
            Refuse("swing_right", () => SwingFeasibility.Resolve(bad, Units(1000), [], Before));
        var huge = BigInteger.Pow(10, 600);
        var largeReference = ExactQuantity.FromRatio(new(huge,7), Native);
        var largeFact = Fact(right, 1, 1) with { Quantity = ExactQuantity.FromRatio(new(huge * 41, 280), Native) };
        Require(SwingFeasibility.Resolve(right, largeReference, [largeFact], Before).Single().Elected == R(41,40),
            "Commercial arithmetic reduces very large native ratios exactly.");
        var optional = Terms(1) with { Minimum = R(0), Lower = null, Upper = null, TotalBasis = null };
        var zero = SwingFeasibility.Resolve(optional, Units(1000), [Fact(optional,0,0)], Before).Single();
        var fixedPlan = SwingForwardPlan.Search(SwingPlanGrid.Build(zero, PlanBudget), []);
        Require(zero.Days[0].QuantityRatio == R(0) && zero.ChangedDays == 1 && fixedPlan.QuantityRatios.IsEmpty && fixedPlan.ValueToUs == R(0),
            "An explicit zero is an election and a fully elected period has zero remaining reward.");
        var late = SwingFeasibility.Resolve(right, Units(1000), [], right.Days[0].ExerciseAt).Single();
        Require(late.Days.All(d => d.Election is null), "A missed deadline is an open day for commercial recording.");
        Refuse("swing_determination", () => SwingPlanGrid.Build(late, PlanBudget));
    }
}
