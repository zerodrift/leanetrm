using System.Collections.Immutable;
using System.Numerics;
using OpenEtrm.MarketData;
using OpenEtrm.MarketData.Gas;
using OpenEtrm.MarketData.Options;
using OpenEtrm.MarketData.Trading;
using OpenEtrm.Shared.Numerics;

internal static partial class GasSwingChecks
{
    private static void SmallPlanOracle()
    {
        // Each mask fixes every non-selected open day at B. Its other days can
        // independently take any quantity in their closed interval.
        const int b = 4;
        foreach (var (low, high) in new[] { (2,6), (4,6), (2,4), (4,4) })
        foreach (int? count in new int?[] { null,0,1,2 })
        foreach (var (lower, upper) in new (int?,int?)[] { (null,null),(null,11),(null,16),(13,null),(11,13),(13,15),(15,18),(2,4) })
        foreach (var elected in new int?[] { null, low, b, low == high ? b : high - 1 })
        foreach (var holder in new[] { ExerciseHolder.Us, ExerciseHolder.Counterparty })
        {
            var right = Terms() with { Minimum = R(low,b), Maximum = R(high,b), Step = R(2,b),
                Lower = lower is {} l ? R(l,b) : null, Upper = upper is {} u ? R(u,b) : null,
                TotalBasis = lower.HasValue || upper.HasValue ? "per_period_v1" : null,
                ChangeCount = count, CountRule = count.HasValue ? "differs_from_base_v1" : null, Holder = holder };
            var open = elected.HasValue ? 2 : 3;
            var feasible = false;
            for (int mask = 0; mask < (1 << open); mask++)
            {
                var lo = elected ?? 0;
                var hi = lo;
                var changes = elected.HasValue && elected != b ? 1 : 0;
                for (int day = 0; day < open; day++)
                {
                    if ((mask & (1 << day)) == 0) { lo += b; hi += b; }
                    else { lo += low; hi += high; changes++; }
                }
                if (!(changes > count) && !(lo > upper) && !(hi < lower)) feasible = true;
            }
            var facts = elected is {} fixedAmount ? ImmutableArray.Create(Fact(right, 1, fixedAmount)) : [];
            SwingPeriodBudget budget;
            try { budget = SwingFeasibility.Resolve(right, Units(b), facts, Before).Single(); }
            catch (MarketDataException e) when (e.Code == "swing_feasibility" && !feasible) { continue; }
            Require(feasible, "Continuous feasibility differs from the independent changed-day interval listing.");

            int[] margins = [2,-1,2];
            var grid = SwingPlanGrid.Build(budget, PlanBudget);
            // Decode each state's rank order and compare the native indices independently.
            for (var d = 0; d < grid.Days.Length; d++)
                foreach (var state in grid.Coordinates[d])
                {
                    var choices = grid.Moves(d,state);
                    var indices = Enumerable.Range(0,choices.Count).Select(choices.IndexAt).ToArray();
                    var expected = indices.Distinct().OrderBy(i => BigInteger.Abs(low + 2*i - b)).ThenBy(i => i);
                    Require(indices.SequenceEqual(expected), "The rank order must match actual base-distance then lower quantity.");
                }
            var plan = SwingForwardPlan.Search(grid, [..grid.Days.Select(d => R(b*margins[d.Day.DayNumber-From.DayNumber], 3))]);
            var possible = Enumerable.Range(0,(high-low)/2+1).Select(i => low+2*i).ToArray();
            // Generate the complete plans in chronological tie preference order.
            var ordered = possible.OrderBy(q => Math.Abs(q-b)).ThenBy(q => q).ToArray();
            int[]? best = null;
            int? score = null;
            foreach (var x in ordered)
            foreach (var y in elected.HasValue ? new[]{elected.Value} : ordered)
            foreach (var z in ordered)
            {
                int[] path = [x,y,z];
                if (path.Sum() < lower || path.Sum() > upper || path.Count(q => q != b) > count) continue;
                var candidate = x*margins[0] + (elected.HasValue ? 0 : y*margins[1]) + z*margins[2];
                if (score == null || (holder == ExerciseHolder.Us ? candidate > score : candidate < score))
                { best = path; score = candidate; }
            }
            Require(score.HasValue && plan.ValueToUs == R(score.Value,3) &&
                plan.QuantityRatios.SequenceEqual(grid.Days.Select(d => R(best![d.Day.DayNumber-From.DayNumber],b))),
                "Every continuously feasible case must have the same grid completion, exact third-share score and ordered moves as enumeration.");
        }
    }

    private static void QuantityComponentAdmission()
    {
        var f = new GasInstrumentScheduleChecks.IdentityFixture();
        var one = ExactDecimal.Parse("1");
        var source = new GasPhysicalComponent("delivery", f.Physical, one, ExactDecimal.Parse("1000"), null);
        var payment = new GasCashComponent("payment", f.Cash, "consideration", "delivery", GasQuantityStage.Delivery, ExactDecimal.Parse("-1"), one);
        GasCompositionInstrument Composition(GasPhysicalComponent owner) => new(Guid.NewGuid(), [owner], [payment]);
        var component = new GasSwingQuantityComponent("delivery", ["delivery","payment"], ExerciseHolder.Us,
            ExactDecimal.Parse("850"), ExactDecimal.Parse("1150"), ExactDecimal.Parse("75"), "term", null, ExactDecimal.Parse("1050"),
            "per_period_v1", 1, "differs_from_base_v1", "best_plan_on_forwards_v1", "lead_before_first_gas_day_v1", 6);
        var composition = Composition(source);
        var right = component.Resolve(composition, Before);
        Require(right.Step == R(3,40) && right.Minimum == R(17,20) && right.Upper == R(21,20) &&
            right.QuantityPolicy == "swing_elected_quantity_v1" && right.Days[0].ExerciseAt == f.Physical.GenerateSchedule().Quantities.Rows[0].Start.AddHours(-6),
            "Normalize native amounts and bind the source choice, coverage and deadline.");
        var scheduled = component.Resolve(Composition(source with { QuantityPolicy = GasHeldQuantities.DeliverySchedulePolicy }), Before);
        Require(scheduled.QuantityPolicy == "swing_elected_delivery_quantity_v1", "The operating policy determines the swing choice.");
        var id = DefinitionIdentity.For(right);
        foreach (var different in new[]{scheduled, right with { Period = "delivery_month" }, right with { TotalBasis = "per_day_v1" },
            right with { Step = R(1,40) }, right with { ChangeCount = null, CountRule = null }, right with { LeadHours = 5 }})
            Require(DefinitionIdentity.For(different) != id, "Swing economic identity includes operational and planning choices.");
        Refuse("swing_right", () => (component with { Entries = ["delivery"] }).Resolve(composition, Before));
        Refuse("swing_right", () => (component with { Side = "payment" }).Resolve(composition, Before));
        Refuse("swing_right", () => (component with { Entries = ["delivery","payment","second"] }).Resolve(
            new(Guid.NewGuid(), [source, source with { Code = "second" }], [payment]), Before));
        Refuse("swing_right", () => component.Resolve(new(Guid.NewGuid(), [source],
            [payment with { Part = QuantityPart.Band(R(0),R(1)) }]), Before));
        Refuse("gas_composition_input", () => component.Resolve(Composition(source with { QuantityPolicy = "scheduled_only_v1" }), Before));
        var scaled = (component with { DailyMinimum = ExactDecimal.Parse("8500"), DailyMaximum = ExactDecimal.Parse("11500"),
            Upper = ExactDecimal.Parse("10500"), Step = ExactDecimal.Parse("750") }).Resolve(
                Composition(source with { Quantity = ExactDecimal.Parse("10000") }), Before);
        Require(DefinitionIdentity.For(scaled) == id, "All normalized retained quantities preserve identity across trade sizes.");
        var linked = source with { Code = "second", Quantity = null, Link = new("delivery", GasFuelConvention.None, default) };
        Refuse("swing_right", () => component.Resolve(new(Guid.NewGuid(), [source,linked], [payment]), Before));
        var q = f.Physical.Quantities;
        var periodSource = new PhysicalGasSide(f.Physical.Id, f.Physical.Location, f.Physical.Product,
            new(q.Clock,q.From,q.To,GasQuantityBasis.PeriodTotalEqualOperatingDays,q.Receipt,q.FuelConvention,q.FuelFraction),
            f.Physical.Assignments,f.Physical.CalendarPolicy);
        Refuse("swing_right", () => component.Resolve(Composition(source with {Side=periodSource}), Before));
    }

    private static void BoundedPlanningStep()
    {
        var arithmetic = new SwingPlanArithmetic(8);
        Require(arithmetic.ReciprocalOfStep(R(255,128)) == R(128,255), "Both 8-bit limbs fit the reciprocal budget.");
        foreach (var step in new[] { R(256,255), R(255,256) })
        {
            var flat = Terms(1) with { Minimum = R(1), Maximum = R(1), Step = step, Upper = null, TotalBasis = null };
            var period = SwingFeasibility.Resolve(flat, Units(1), [], Before).Single();
            Require(period.RemainingMinimum == R(1) && period.RemainingMaximum == R(1),
                "The flat day has one exact commercial completion for either positive step.");
            Refuse("swing_numerical_scope", () => SwingPlanGrid.Build(period, PlanBudget with { MaximumIntegerBits = 8 }));
            Refuse("swing_numerical_scope", () => arithmetic.ReciprocalOfStep(step));
        }
    }

    private static void SparsePlanLimits()
    {
        var right = Terms(1) with { Minimum = R(0), Maximum = R(2), Step = R(1,100000000), Lower = R(1), Upper = R(100000001,100000000) };
        var budget = SwingFeasibility.Resolve(right, Units(1000), [], Before).Single();
        var tiny = new LevelPlanBudget(2,2,3,128);
        var grid = SwingPlanGrid.Build(budget, tiny);
        Require(grid.ActionCounts.SequenceEqual([2L]) && SwingForwardPlan.Search(grid, [R(1)]).QuantityRatios.Single() == right.Upper,
            "Two feasible moves out of 200 million choices fit the exact two-visit budget.");
        Refuse("swing_numerical_scope", () => SwingPlanGrid.Build(budget, tiny with {MaximumCandidates=1}));
        Refuse("swing_numerical_scope", () => SwingPlanGrid.Build(budget, tiny with {MaximumStates=2}));
        var dense = right with { Lower=null, Upper=null, TotalBasis=null };
        Refuse("swing_numerical_scope", () => SwingPlanGrid.Build(SwingFeasibility.Resolve(dense, Units(1000), [], Before).Single(), tiny));
        Refuse("swing_numerical_scope", () => SwingForwardPlan.Search(grid, [new(BigInteger.One << 129,1)]));
        var wide = right with { Minimum = R(1), Maximum = new(BigInteger.One << 100,1), Step = R(1), Lower = null, Upper = R(1) };
        var forced = SwingPlanGrid.Build(SwingFeasibility.Resolve(wide, Units(1000), [], Before).Single(), tiny);
        Require(forced.ActionCounts.Single() == 1 && SwingForwardPlan.Search(forced, [R(-7,13)]).ValueToUs == R(-7,13),
            "A huge commercial daily range pruned to a single base action takes one candidate.");
        var subunit = Terms(3) with { Minimum = R(0), Maximum = R(1), Step = R(1,1000000000000000000), Lower = null,
            Upper = null, TotalBasis = null, ChangeCount = 0, CountRule = "differs_from_base_v1" };
        var mandatory = SwingPlanGrid.Build(SwingFeasibility.Resolve(subunit, Units(1), [], Before).Single(), PlanBudget);
        var forcedPlan = SwingForwardPlan.Search(mandatory, [R(-1),R(2),R(3)]);
        Require(forcedPlan.QuantityRatios.SequenceEqual([R(1),R(1),R(1)]) && forcedPlan.ValueToUs == R(4) &&
            mandatory.ActionCounts.SequenceEqual([1L,1,1]), "Base index 10^18, count zero, one exact choice per day.");
        var largeLevels = subunit with { Lower = R(3) - subunit.Step, Upper = R(3), TotalBasis = "per_period_v1",
            ChangeCount = null, CountRule = null };
        var nearBase = SwingPlanGrid.Build(SwingFeasibility.Resolve(largeLevels, Units(1), [], Before).Single(), PlanBudget);
        var sparse = SwingForwardPlan.Search(nearBase, [R(-1),R(-1),R(-1)]);
        Require(sparse.QuantityRatios.SequenceEqual([R(1),R(1),R(1)-subunit.Step]) && sparse.ValueToUs == R(-3)+subunit.Step,
            "A few reachable coordinates near 3 × 10^18 retain chronology, level and signed objective.");
        var single = Terms(2) with { Minimum = R(1), Maximum = R(1), Step = R(1), Lower = null, Upper = null, TotalBasis = null };
        var exact = SwingPlanGrid.Build(SwingFeasibility.Resolve(single, Units(1), [], Before).Single(),
            PlanBudget with { MaximumIntegerBits = 5000 });
        var large = new SignedRatio(BigInteger.One << 4095, 1);
        Refuse("swing_numerical_scope", () => SwingForwardPlan.Search(exact, [large, large]));
        using var stop = new CancellationTokenSource();
        stop.Cancel();
        try { SwingPlanGrid.Build(budget,tiny,stop.Token); }
        catch (OperationCanceledException) { return; }
        throw new Exception("Cancelled topology construction must stop at entry.");
    }
}
