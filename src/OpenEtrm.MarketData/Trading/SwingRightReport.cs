using System.Collections.Immutable;
using OpenEtrm.MarketData.Options;

namespace OpenEtrm.MarketData.Trading;

// The remaining option is one contribution; each day shows its decision, quantity evidence and model inputs.
public sealed record SwingDayReport(DateOnly Day, DateTimeOffset ExerciseAt, string State, string? ElectedQuantity,
    string? SelectedQuantity, Guid? RevisionId, ImmutableArray<DeliveryQuantityEvidence> QuantityEvidence,
    string? Forward, string? UncertainPerLot, string? CertainPerLot, int? IntrinsicLots, string? IntrinsicQuantity);

public sealed record SwingRightReport(int Right, int PhysicalSlot, ImmutableArray<int> Slots, string Holder,
    string Increment, ReferenceKey QuantityUnit, int DailyMinimumLots, int DailyMaximumLots, int MinimumLots, int MaximumLots,
    int CommittedLots, int RemainingMinimumLots, int RemainingMaximumLots, DateTimeOffset? NextDeadline, string Counted,
    string Intrinsic, SwingPricingBinding Pricing, ImmutableArray<SwingVolatilityValue> Marks, ImmutableArray<SwingDayReport> Days,
    SwingWorkCounts Work, string DailyMinimumQuantity, string DailyMaximumQuantity, string MinimumQuantity, string MaximumQuantity,
    string CommittedQuantity, string RemainingMinimumQuantity, string RemainingMaximumQuantity)
{
    public static SwingRightReport FromValue(SwingRightValue value)
    {
        string Quantity(int lots) => value.Increment.ScaleFraction(lots, 1).ToExactString();
        var pending = (value.Inputs?.Days ?? []).Select((input,i) => (input, lots:value.IntrinsicLots[i]))
            .ToDictionary(d => d.input.Day.Day);
        var days = value.Days.Select(d =>
        {
            var hasInput = pending.TryGetValue(d.Day.Day, out var decision);
            return new SwingDayReport(d.Day.Day, d.Day.ExerciseAt, d.State, d.Election?.Quantity.ToExactString(),
                d.SelectedQuantity?.ToExactString(), d.Election?.RevisionId, d.QuantityEvidence, d.Forward,
                hasInput ? ExactNumberText.Format(decision.input.Uncertain) : null,
                hasInput ? ExactNumberText.Format(decision.input.Certain) : null, hasInput ? decision.lots : null,
                hasInput ? Quantity(decision.lots) : null);
        }).ToImmutableArray();
        var terms = value.Terms;
        return new(value.Right, terms.PhysicalSlot, terms.Slots, terms.Holder == ExerciseHolder.Us ? "us" : "counterparty",
            value.Increment.ToExactString(), value.Increment.Specification, terms.DailyMinimumLots, terms.DailyMaximumLots,
            terms.MinimumLots, terms.MaximumLots, value.CommittedLots, value.RemainingMinimumLots, value.RemainingMaximumLots,
            value.Inputs?.Days[0].Day.ExerciseAt, value.Counted.ToExactString(), value.Intrinsic.ToExactString(), value.Pricing,
            value.Marks, days, value.Work, Quantity(terms.DailyMinimumLots), Quantity(terms.DailyMaximumLots),
            Quantity(terms.MinimumLots), Quantity(terms.MaximumLots), Quantity(value.CommittedLots),
            Quantity(value.RemainingMinimumLots), Quantity(value.RemainingMaximumLots));
    }
}


// Amounts are signed held currency. A missing subtotal has no assessable open decision.
internal sealed record SwingPlanComparison(string Model, string Scope, int PlannedDays, int UnassessedDays, ExactMoney Amount);
internal sealed record SwingQuantityDayValue(SwingDay Day, string State, SwingElection? Election,
    ExactQuantity? SelectedQuantity, ImmutableArray<DeliveryQuantityEvidence> Evidence, ExactQuantity? PlannedQuantity,
    string? Forward, SwingUnitPayoffDay? UnitPayoff, ImmutableArray<HeldDeliveryRecord> OperationalRecords);
internal sealed record SwingQuantityRightValue(SwingQuantityRight Terms, ExactQuantity Reference, SwingPricingBinding Pricing,
    ImmutableArray<SwingPeriodBudget> Periods, ImmutableArray<SwingQuantityDayValue> Days,
    ImmutableArray<SwingVolatilityValue> Marks, DateTimeOffset? NextDeadline, string Scope, int ModeledDays, ExactMoney? Counted,
    SwingPlanComparison? Comparison, SwingUnitEstimate Estimate, SwingUnitWork Work)
{
    internal int UnassessedDays => Days.Count(d => d.State is RetainedSwingQuantityValuation.Missing or RetainedSwingQuantityValuation.Blocked);
}
