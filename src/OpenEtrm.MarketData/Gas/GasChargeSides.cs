using System.Collections.Immutable;
using OpenEtrm.MarketData.Trading;
using OpenEtrm.Shared.Numerics;

namespace OpenEtrm.MarketData.Gas;

public enum ChargeBasis { LumpSum = 1, Capacity }

public static class GasChargeSides
{
    public static DateOnly LumpSumDay(GasDayClock clock, DateTimeOffset paymentAt)
    {
        var end = PaymentDatePolicy.Date(paymentAt);
        var paymentStart = PaymentDatePolicy.Start(end);
        // The next local date's boundary cannot reach this UTC midnight, even at UTC+14.
        for (; end > DateOnly.MinValue; end = end.AddDays(-1))
            if (clock.Boundary(end) <= paymentStart)
                return end.AddDays(-1);
        throw new MarketDataException("gas_composition_input", "The payment date must have a completed gas day within the supported date range.");
    }

    public static FixedSide LumpSum(Guid id, UnitPrice amount, Quantity unitQuantity, GasDayClock clock,
        DateTimeOffset paymentAt, SideCalendarPolicy calendars, QuoteSpecification quote)
    {
        var from = LumpSumDay(clock, paymentAt);
        var to = from.AddDays(1);
        return new(id, new FlatPrice(amount), quote, unitQuantity,
            [new FixedSidePeriod(from, to, paymentAt, ExactRatio.One, null) { DeliveryCompletionAt = clock.Boundary(to) }],
            calendars, gasDeliveryClock: clock);
    }

    public static FixedSide Capacity(Guid id, UnitPrice rate, Quantity unitQuantity, GasDayClock clock,
        DateOnly from, DateOnly to, int paymentDay, SideCalendarPolicy calendars, QuoteSpecification quote)
    {
        if (from >= to || paymentDay is < 1 or > 28 || rate is null || rate.Value.CompareTo(default) <= 0)
            throw new MarketDataException("gas_composition_input", "A capacity charge requires an ordered term, a positive rate and a following-month payment day from 1 through 28.");
        if (to > new DateOnly(9999, 12, 1))
            throw new MarketDataException("gas_composition_input", "A capacity charge requires a following-month payment date within the supported date range.");

        var periods = ImmutableArray.CreateBuilder<FixedSidePeriod>();
        for (var start = from; start < to;)
        {
            var nextMonth = new DateOnly(start.Year, start.Month, 1).AddMonths(1);
            var end = nextMonth < to ? nextMonth : to;
            var paymentAt = PaymentDatePolicy.Start(nextMonth.AddDays(paymentDay - 1));
            periods.Add(new(start, end, paymentAt, new ExactRatio(end.DayNumber - start.DayNumber, 1), null)
                { DeliveryCompletionAt = clock.Boundary(end) });
            start = end;
        }
        return new(id, new FlatPrice(rate), quote, unitQuantity, periods.ToImmutable(), calendars, gasDeliveryClock: clock);
    }
}
