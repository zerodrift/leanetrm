using System.Collections.Immutable;
using OpenEtrm.MarketData.Options;
using OpenEtrm.Shared.Numerics;

namespace OpenEtrm.MarketData.Trading;

// Ordinary and counterfactual occurrences retain the same contractual denominator.
internal sealed class RetainedSwingQuantityValuation
{
    internal const string Missing = "missing_election", Blocked = "period_unassessed";
    private readonly SwingQuantityRight right;
    private readonly ExactQuantity reference;
    private readonly ImmutableArray<SwingPeriodBudget> periods;
    private readonly ImmutableArray<SwingQuantityDayValue> days;
    private readonly ImmutableArray<EntryDayQuantity> ordinary, hypothetical;
    internal ImmutableArray<SwingDay> Pending { get; }
    internal ImmutableHashSet<SwingDay> MarkedDays { get; }
    internal DateTimeOffset? NextDeadline => days.Where(d => d.Election is null && d.Day.ExerciseAt > periods[0].AssessmentAt)
        .Select(d => (DateTimeOffset?)d.Day.ExerciseAt).FirstOrDefault();

    internal RetainedSwingQuantityValuation(SwingQuantityRight right, PhysicalCashState held, DateTimeOffset at)
    {
        this.right = right;
        if (held.NextSwingElectionEffectiveAt <= at)
            throw new MarketDataException("valuation_time_coverage", "Refresh swing election facts at the selected valuation instant.");
        var owner = held.OwnerDays.Where(d => d.Slot == right.PhysicalSlot).ToDictionary(d => d.Day);
        if (owner.Count != right.Days.Length || right.Days.Any(day => !owner.ContainsKey(day.Day)))
            throw PhysicalDeliveryQuantities.Invalid("Swing requires its complete physical source calendar.");
        reference = owner[right.Days[0].Day].Contract;
        if (owner.Values.Any(day => day.Contract != reference))
            throw PhysicalDeliveryQuantities.Invalid("Swing requires a constant retained reference-day quantity.");
        periods = SwingFeasibility.Resolve(right, reference, held.SwingElections, at);
        var open = ImmutableArray.CreateBuilder<SwingDay>();
        var marked = ImmutableHashSet.CreateBuilder<SwingDay>();
        var display = ImmutableArray.CreateBuilder<SwingQuantityDayValue>();
        var counted = ImmutableArray.CreateBuilder<EntryDayQuantity>();
        var parcels = ImmutableArray.CreateBuilder<EntryDayQuantity>();
        foreach (var period in periods)
        {
            var block = period.MissingCoupledPeriod;
            var needsMark = !block && !SwingChoice.Forced(period, period.Days.Count(d => d.Election is null && d.Day.ExerciseAt > at));
            foreach (var decision in period.Days)
            {
                var day = decision.Day;
                var fact = decision.Election;
                var state = fact is not null ? PhysicalLifecycle.ElectedQuantity : day.ExerciseAt <= at ? Missing :
                    block ? Blocked : RetainedSwingValuation.Undecided;
                var source = owner[day.Day];
                bool operating = fact is not null && right.QuantityPolicy == SwingRight.ElectedDeliveryQuantity &&
                    source.Source != PhysicalLifecycle.ContractQuantity;
                var selected = operating ? source.Selected : fact?.Quantity;
                ImmutableArray<DeliveryQuantityEvidence> evidence = fact is null ? [] :
                    [new(PhysicalLifecycle.ElectedQuantity, fact.RevisionId, fact.Evidence), ..operating ? source.Evidence : []];
                display.Add(new(day, state, fact, selected, evidence, null, null, null, source.OperationalRecords));
                var actual = selected is null ? SignedRatio.Zero : new SignedRatio(selected.NumeratorValue, selected.DenominatorValue);
                counted.Add(new(day.Day, period.Reference, actual, actual, operating ? source.Source : state, evidence)
                    { SwingSourceSlot = right.PhysicalSlot, SwingRight = right.Ordinal, SwingState = state });
                bool pending = state == RetainedSwingValuation.Undecided;
                if (pending)
                {
                    open.Add(day);
                    if (needsMark) marked.Add(day);
                }
                var parcel = pending ? period.Reference : SignedRatio.Zero;
                parcels.Add(new(day.Day, period.Reference, parcel, parcel, "swing_reference", []));
            }
        }
        days = display.ToImmutable();
        ordinary = counted.ToImmutable();
        hypothetical = parcels.ToImmutable();
        Pending = open.ToImmutable();
        MarkedDays = marked.ToImmutable();
    }

    internal ImmutableArray<ImmutableArray<EntryDayQuantity>> Entries(ImmutableArray<ImmutableArray<EntryDayQuantity>> other,
        bool counterfactual)
    {
        var result = other.ToBuilder();
        foreach (var slot in right.Slots) result[slot] = counterfactual ? hypothetical : ordinary;
        return result.ToImmutable();
    }

    internal SwingQuantityRightValue Evaluate(SideValuationContext context, ReferenceKey currency, SwingPricingBinding pricing,
        ExerciseMarks? marks, CurveKey curve, Func<int, RetainedProgramValuation> occurrence)
    {
        // Optimize the immutable Instrument in its own orientation. Trade's sign reverses its held result.
        var sign = ((PhysicalCashState)context.BindingState!).Quantity.Amount.ScaledValue.Sign;
        var (physical, fixedAmounts, forwards) = RetainedSwingValuation.Amounts(context, Pending, right.Slots, right.PhysicalSlot,
            new(sign, 1), occurrence);
        var monthly = MarkedDays.Select(d => new DateOnly(d.Day.Year, d.Day.Month, 1)).Distinct().Order().ToDictionary(m => m, m =>
        {
            var mark = (marks ?? throw ExerciseMarks.Missing()).Volatility(curve, m);
            return new SwingVolatilityValue(curve, m, mark.Id, context.ExerciseVolatility(mark).Format(30, MidpointRounding.ToEven));
        });
        var size = periods[0].Reference;
        var inputs = Pending.Select(day => SwingUnitChoiceInputs.FromHeld(day, physical[day.Day], fixedAmounts[day.Day], size,
            MarkedDays.Contains(day) ? LatticeDecimal.Parse(monthly[new(day.Day.Year, day.Day.Month, 1)].Volatility) : null)).ToImmutableArray();
        var result = SwingChoice.Value(new SwingUnitChoiceInputs(right, periods, inputs, size, context.UnitEvaluation.CalculationAt, pricing),
            context.UnitEvaluation.Cancellation);
        ExactMoney Held(SignedRatio value) => ExactMoney.FromFraction(value.Numerator * sign, value.Denominator, currency);
        var plan = result.Plan.ToDictionary(p => p.Day, p => ExactQuantity.FromRatio(p.QuantityRatio * size, reference.Specification));
        var unit = inputs.ToDictionary(p => p.Day);
        int missing = days.Count(d => d.State is Missing or Blocked);
        return new(right, reference, pricing, periods, [..days.Select(d => d with { Forward = forwards.GetValueOrDefault(d.Day.Day),
            PlannedQuantity = plan.GetValueOrDefault(d.Day), UnitPayoff = unit.GetValueOrDefault(d.Day) })],
            [..monthly.Values], NextDeadline, "assessed_remaining_right", Pending.Length, Pending.IsEmpty && missing > 0 ? null : Held(result.CountedToUs),
            Pending.IsEmpty ? null : new(right.PlanRule, "assessed_unelected_days", plan.Count, missing, Held(result.IntrinsicToUs)), result.Estimate, result.Work);
    }
}
