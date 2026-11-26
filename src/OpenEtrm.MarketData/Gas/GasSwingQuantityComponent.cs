using System.Collections.Immutable;
using OpenEtrm.MarketData.Options;
using OpenEtrm.MarketData.Trading;
using OpenEtrm.Shared.Numerics;

namespace OpenEtrm.MarketData.Gas;

public sealed record GasSwingQuantityComponent(string Side, ImmutableArray<string> Entries, ExerciseHolder Holder,
    ExactDecimal DailyMinimum, ExactDecimal DailyMaximum, ExactDecimal Step, string Period, ExactDecimal? Lower,
    ExactDecimal? Upper, string? TotalBasis, int? ChangeCount, string? CountRule, string PlanRule, string DeadlineRule, int LeadHours)
{
    internal SwingQuantityRight Resolve(GasCompositionInstrument instrument, DateTimeOffset bookingAt) =>
        Resolve(instrument,bookingAt,out _);

    internal SwingQuantityRight Resolve(GasCompositionInstrument instrument, DateTimeOffset bookingAt,
        out SwingUnitEstimate structuralEstimate)
    {
        var owner = GasSwingCoverage.SourceFor(instrument, Side);
        var covered = GasSwingCoverage.Resolve(instrument, owner, Entries, LeadHours);
        var selection = covered.Source.QuantityPolicy switch
        {
            null => SwingQuantityRight.Elected,
            GasHeldQuantities.DeliverySchedulePolicy => SwingQuantityRight.ElectedDeliveryQuantity,
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
        try { structuralEstimate = SwingUnitResources.Booking(right,reference,bookingAt); }
        catch (MarketDataException error) when (error.Code=="swing_numerical_scope")
        { throw new SwingTermFieldException(error.Code,"numericalPolicy",error.Message); }
        return right;
    }
}
