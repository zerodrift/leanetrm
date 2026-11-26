using System.Collections.Immutable;
using OpenEtrm.MarketData.Gas;
using OpenEtrm.MarketData.Trading;
using OpenEtrm.Shared.Numerics;

namespace OpenEtrm.MarketData.Options;

public sealed record SwingUnitPayoffDay(SwingDay Day, SignedRatio PhysicalToUs, SignedRatio FixedToUs, LatticeDecimal? Volatility);

// Days carry signed amounts to us for one native reference unit, including
// membership shares. HeldReference is the positive native reference-day size.
internal sealed record SwingUnitChoiceInputs(SwingQuantityRight Right, ImmutableArray<SwingPeriodBudget> Periods,
    ImmutableArray<SwingUnitPayoffDay> Days, SignedRatio HeldReference, DateTimeOffset CalculationAt, SwingPricingBinding Pricing)
{
    internal static SwingUnitPayoffDay FromHeld(SwingDay day, SignedRatio heldPhysicalToUs, SignedRatio heldFixedToUs,
        SignedRatio heldReference, LatticeDecimal? volatility)
    {
        if (heldReference.Numerator.Sign <= 0 || heldReference.Denominator.Sign <= 0)
            throw new MarketDataException("swing_model_scope", "Supply the positive held reference-day quantity.");
        var arithmetic = new SwingPlanArithmetic(SwingUnitResources.IntegerBits);
        var inverse = arithmetic.Reciprocal(heldReference);
        return new(day, arithmetic.Multiply(heldPhysicalToUs, inverse), arithmetic.Multiply(heldFixedToUs, inverse), volatility);
    }
}

internal sealed record SwingUnitResult(SignedRatio HeldReference, SignedRatio CountedToUs, SignedRatio IntrinsicToUs,
    ImmutableArray<(SwingDay Day, SignedRatio QuantityRatio)> Plan, SwingUnitEstimate Estimate, SwingUnitWork Work);

public sealed class SwingUnitWork
{
    // The monotone branch counter is observable by the caller while its own
    // synchronous valuation runs. Publish only at blocks small enough to cancel.
    public long Branches { get; private set; }
    public long PricedChoices { get; internal set; }
    public long Payoffs { get; internal set; }
    public long Normalizers { get; internal set; }
    public long GrowthNodes { get; internal set; }
    public long PreflightVisits { get; internal set; }
    public int FactorSteps { get; internal set; }
    public int Days { get; internal set; }
    public int MaximumStates { get; internal set; }
    public int MaximumChoices { get; internal set; }
    public long CompiledMoves { get; internal set; }
    public int MaximumEdges { get; internal set; }
    public int RollingCapacity { get; internal set; }
    public long PeakBytes => SwingUnitResources.ChargeBytes(StateSlots, Days, FactorSteps, GrowthNodes,
        RollingCapacity, MaximumEdges);
    public long Operations => PreflightVisits + 4 * StateSlots + 2 * Moves + CompiledMoves +
        Branches + PricedChoices + Payoffs + Normalizers + GrowthNodes + FactorSteps + Days;
    public long Moves { get; internal set; }
    public long StateSlots { get; internal set; }
    internal void BranchBlock(int count, Action<long>? report)
    {
        Branches += count;
        if (count != 0) report?.Invoke(Branches);
    }
}

internal enum SwingWorkStage { Graph, ExactPlan, Payoffs }

public static class SwingChoice
{
    internal static MarketDataException Scope(string detail) => new("swing_numerical_scope", detail);
    internal static SwingUnitResult Value(SwingUnitChoiceInputs inputs, CancellationToken token = default,
        Action<long>? reportBranches = null, Action<SwingWorkStage>? observeStage = null)
    {
        token.ThrowIfCancellationRequested();
        var work = new SwingUnitWork();
        if (inputs.Pricing.NumericalPolicy != SwingUnitResources.Policy || inputs.Pricing.Model != SwingPricingBinding.SupportedModel ||
            inputs.Pricing.DayCount != OptionNumericalPolicies.ActualOver365FixedInstant ||
            inputs.Pricing.MarksConvention != "curve_marks_log_return_held_v1" || inputs.Pricing.Right != inputs.Right.Ordinal)
            throw Scope("Select the retained swing one-factor model, instant clock, held marks and unit-reference numerical policy.");
        if (inputs.HeldReference.Numerator.Sign <= 0 || inputs.HeldReference.Denominator.Sign <= 0 ||
            inputs.CalculationAt == default || !TradingInstant.IsSupported(inputs.CalculationAt) ||
            inputs.Right.Days.IsDefaultOrEmpty || inputs.Right.Days.Length > GasQuantityTerms.MaximumOperatingDays ||
            inputs.Periods.IsDefault || inputs.Days.IsDefault || inputs.Days.Length > inputs.Right.Days.Length ||
            inputs.Periods.Length > inputs.Right.Days.Length ||
            inputs.Periods.Any(p => p.Right != inputs.Right || p.AssessmentAt != inputs.CalculationAt || p.Reference != inputs.HeldReference) ||
            !inputs.Periods.SelectMany(p => p.Days.Select(d => d.Day)).SequenceEqual(inputs.Right.Days))
            throw new MarketDataException("swing_model_scope", "Supply the exact retained period geometry, assessment instant and positive held size.");
        var periods = inputs.Periods.Where(p => !p.MissingCoupledPeriod).ToImmutableArray();
        var indexed = new Dictionary<SwingDay, SwingUnitPayoffDay>();
        var marked = new Dictionary<(int, int), LatticeDecimal>();
        foreach (var input in inputs.Days)
        {
            token.ThrowIfCancellationRequested();
            if (input.PhysicalToUs.Denominator.Sign <= 0 || input.FixedToUs.Denominator.Sign <= 0 || !indexed.TryAdd(input.Day, input))
                throw new MarketDataException("swing_model_scope", "Supply each assessed day with exact physical and fixed unit-reference amounts.");
            if (input.Volatility is not { } sigma) continue;
            var month = (input.Day.Day.Year, input.Day.Day.Month);
            if (sigma.CompareTo(default) < 0 || marked.TryGetValue(month, out var saved) && saved != sigma)
                throw new MarketDataException("exercise_marks", "Supply one consistent nonnegative volatility per delivery month.");
            marked[month] = sigma;
        }
        var stochastic = new HashSet<SwingPeriodBudget>();
        int open = 0;
        foreach (var period in periods)
        {
            var pending = period.Days.Where(d => d.Election == null && d.Day.ExerciseAt > inputs.CalculationAt);
            bool forced = Forced(period, pending.Count());
            foreach (var day in pending)
            {
                token.ThrowIfCancellationRequested();
                open++;
                if (!indexed.TryGetValue(day.Day, out var payoff))
                    throw new MarketDataException("swing_model_scope", "Each assessed open day requires both retained Side amounts.");
                if (forced) continue;
                if (payoff.Volatility is not { } vol) throw ExerciseMarks.Missing();
                if (vol != default) stochastic.Add(period);
            }
        }
        if (open != indexed.Count) throw new MarketDataException("swing_model_scope", "A unit-reference amount has no assessed open day.");
        var estimate = SwingUnitResources.Estimate(inputs.Right, periods, inputs.CalculationAt, stochastic, token);
        work.PreflightVisits = estimate.PreflightVisits;
        var budget = new LevelPlanBudget(estimate.MaximumStates, estimate.Moves, estimate.StateSlots, SwingUnitResources.IntegerBits);
        var arithmetic = new SwingPlanArithmetic(budget.MaximumIntegerBits);
        var plans = ImmutableArray.CreateBuilder<(SwingDay, SignedRatio)>(open);
        var decisions = new List<SwingUnitDecision>();
        var intrinsic = SignedRatio.Zero;
        var strip = SignedRatio.Zero;
        int sign = inputs.Right.Holder == ExerciseHolder.Us ? 1 : -1;
        foreach (var period in periods)
        {
            if (!period.Days.Any(d => d.Election == null && d.Day.ExerciseAt > inputs.CalculationAt)) continue;
            var grid = SwingPlanGrid.Build(period, budget with
            {
                MaximumCandidates = estimate.Moves - work.Moves,
                MaximumStates = estimate.StateSlots - work.StateSlots
            }, token, observeStage);
            work.MaximumChoices = Math.Max(work.MaximumChoices, grid.MaximumChoices);
            work.Days += grid.Days.Length;
            work.MaximumStates = Math.Max(work.MaximumStates, grid.Coordinates.Max(layer => layer.Length));
            work.Moves += grid.ActionCounts.Sum();
            work.StateSlots += grid.Coordinates.Sum(s => (long)s.Length);
            var plan = SwingForwardPlan.Search(grid, [.. grid.Days.Select(d => arithmetic.Add(indexed[d].FixedToUs, indexed[d].PhysicalToUs))], token, observeStage);
            intrinsic = arithmetic.Add(intrinsic, plan.ValueToUs);
            if (!stochastic.Contains(period)) strip = arithmetic.Add(strip, plan.ValueToUs);
            for (int d = 0; d < grid.Days.Length; d++)
            {
                plans.Add((grid.Days[d], plan.QuantityRatios[d]));
                if (stochastic.Contains(period)) decisions.Add(new(grid, d, indexed[grid.Days[d]]));
            }
        }
        SignedRatio root = SignedRatio.Zero;
        if (decisions.Count != 0)
        {
            try { root = SwingPriceLattice.UnitRoot(inputs, decisions, work, reportBranches, token, observeStage); }
            catch (MarketDataException e) when (e.Code == "numeric_range")
            { throw new MarketDataException("swing_numerical_scope", "The unit-reference lattice exceeds its numerical magnitude.", e); }
        }
        var counted = arithmetic.Add(arithmetic.Multiply(root, new(sign, 1)), strip);
        return new(inputs.HeldReference, arithmetic.Multiply(counted, inputs.HeldReference),
            arithmetic.Multiply(intrinsic, inputs.HeldReference), plans.MoveToImmutable(), estimate, work);
    }

    // All unelected days have symmetric constraints. The projection of one day
    // has a base point and one interval for the branch spending a change. Only
    // a singleton union proves that every chronological quantity is fixed.
    internal static bool Forced(SwingPeriodBudget period, int days)
    {
        if (days == 0 || period.AvailableChanges == 0 || period.Right.Minimum == period.Right.Maximum) return true;
        if (period.AppliedLower == null && period.AppliedUpper == null) return false;
        var arithmetic = new SwingPlanArithmetic(SwingUnitResources.IntegerBits);
        var one = SwingQuantityRight.Base;
        var n = new SignedRatio(days - 1, 1);
        var down = arithmetic.Subtract(one, period.Right.Minimum);
        var up = arithmetic.Subtract(period.Right.Maximum, one);
        var remainingLower = period.AppliedLower is { } l ? arithmetic.Subtract(l, period.Elected) : (SignedRatio?)null;
        var remainingUpper = period.AppliedUpper is { } u ? arithmetic.Subtract(u, period.Elected) : (SignedRatio?)null;
        (SignedRatio Low, SignedRatio High) Suffix(int allowance)
        {
            var k = new SignedRatio(Math.Min(days - 1, allowance), 1);
            return (arithmetic.Subtract(n, arithmetic.Multiply(k, down)), arithmetic.Add(n, arithmetic.Multiply(k, up)));
        }
        var changed = Suffix((period.AvailableChanges ?? days) - 1);
        var low = period.Right.Minimum;
        var high = period.Right.Maximum;
        if (remainingLower is { } minimum)
        {
            var bound = arithmetic.Subtract(minimum, changed.High);
            if (arithmetic.Compare(bound, low) > 0) low = bound;
        }
        if (remainingUpper is { } maximum)
        {
            var bound = arithmetic.Subtract(maximum, changed.Low);
            if (arithmetic.Compare(bound, high) < 0) high = bound;
        }
        if (arithmetic.Compare(low, high) > 0 || low == one && high == one) return true;
        if (low != high) return false;
        var unchanged = Suffix(period.AvailableChanges ?? days);
        return remainingLower is { } min && arithmetic.Compare(min, arithmetic.Add(one, unchanged.High)) > 0 ||
            remainingUpper is { } max && arithmetic.Compare(max, arithmetic.Add(one, unchanged.Low)) < 0;
    }
}

internal sealed record SwingUnitDecision(SwingPlanGrid Grid, int Day, SwingUnitPayoffDay Payoff);
