using System.Collections.Immutable;

namespace OpenEtrm.MarketData.Trading;

// Shared qualification of primitive monthly side plans. Allocation belongs to
// each versioned consumer; this helper never chooses a quantity basis.
internal static class MonthlySidePeriods
{
    internal static ImmutableArray<SidePaymentPlan> Validate(Guid id,ImmutableArray<Side> periods)
    {
        if (periods.IsDefaultOrEmpty || periods.Length > MonthlySwapSide.MaximumMonths ||
            periods.Any(p => p is null || p.Id == id) || periods.Select(p => p.Id).Distinct().Count() != periods.Length ||
            !(periods.All(p => p is FixedSide) || periods.All(p => p is FloatingSide))) throw Invalid();
        var plans = periods.Select(p => (SidePaymentSchedule)p.GenerateSchedule()).ToImmutableArray();
        if (plans.Any(p => p.Payments.Length != 1)) throw Invalid();
        var payments = plans.Select(p => p.Payments[0]).ToImmutableArray();
        var first = payments[0];
        for (var n = 0; n < payments.Length; n++)
        {
            var p = payments[n];
            if (p.DeliveryFrom.Year is < 1901 or > 2198 || p.DeliveryFrom.Day != 1 || p.DeliveryFrom.AddMonths(1) != p.DeliveryTo ||
                p.PaymentAt.Year is < 1901 or > 2199 || p.Currency != first.Currency || p.UnitQuantity != first.UnitQuantity ||
                n > 0 && payments[n - 1].DeliveryTo != p.DeliveryFrom) throw Invalid();
        }
        if (periods[0] is FixedSide fixedFirst && periods.Cast<FixedSide>().Any(p => p.QuoteKey != fixedFirst.QuoteKey || p.Price.Unit != fixedFirst.Price.Unit) ||
            periods[0] is FloatingSide floatingFirst && periods.Cast<FloatingSide>().Any(p => p.Index != floatingFirst.Index || p.Quote != floatingFirst.Quote)) throw Invalid();
        return payments;
    }
    private static MarketDataException Invalid()=>new("monthly_swap_periods",
        "Supply one to 1200 complete, ordered, contiguous months of independent fixed or floating sides with exact common quote and quantity references.");
}
