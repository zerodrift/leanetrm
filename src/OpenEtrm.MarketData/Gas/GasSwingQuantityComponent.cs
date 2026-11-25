using System.Collections.Immutable;
using OpenEtrm.MarketData.Trading;
using OpenEtrm.Shared.Numerics;

namespace OpenEtrm.MarketData.Gas;

// Internal until the retained/native booking contract can admit the same terms.
internal sealed record GasSwingQuantityComponent(string Side, ImmutableArray<string> Entries, ExerciseHolder Holder,
    ExactDecimal DailyMinimum, ExactDecimal DailyMaximum, ExactDecimal Step, string Period, ExactDecimal? Lower,
    ExactDecimal? Upper, string? TotalBasis, int? ChangeCount, string? CountRule, string PlanRule, string DeadlineRule, int LeadHours)
{
    internal SwingQuantityRight Resolve(GasCompositionInstrument instrument, DateTimeOffset bookingAt)
    {
        var owner = GasSwingCoverage.SourceFor(instrument, Side);
        var covered = GasSwingCoverage.Resolve(instrument, owner, Entries, LeadHours);
        if (!instrument.SwingRights.IsDefaultOrEmpty)
            throw SwingFeasibility.InvalidRight("Retain exactly one physical swing right.");
        var selection = covered.Source.QuantityPolicy switch
        {
            null => SwingQuantityRight.Elected,
            GasHeldQuantities.DeliverySchedulePolicy => SwingRight.ElectedDeliveryQuantity,
            _ => throw SwingFeasibility.InvalidRight("Select contract quantity or the delivery schedule on the swing source.")
        };
        if (covered.Source.Side.Assignments.Any(a => a.To != a.From.AddDays(1) &&
                (a.From.Day != 1 || a.To != a.From.AddMonths(1))))
            throw new MarketDataException("swing_model_scope", "Swing requires a daily or calendar-month point lookup.");

        SignedRatio Normalize(ExactDecimal amount) => new(amount.ScaledValue, covered.Reference.ScaledValue);
        var right = new SwingQuantityRight(0, covered.Slot, covered.Slots, Holder,
            Normalize(DailyMinimum), Normalize(DailyMaximum), Normalize(Step), Period,
            Lower is {} lower ? Normalize(lower) : null, Upper is {} upper ? Normalize(upper) : null,
            TotalBasis, ChangeCount, CountRule, selection, PlanRule, DeadlineRule, LeadHours, covered.Days);
        var reference = ExactQuantity.FromQuantity(new(covered.Reference, instrument.UnitQuantity.Specification));
        _ = SwingFeasibility.Resolve(right, reference, [], bookingAt);
        return right;
    }
}
