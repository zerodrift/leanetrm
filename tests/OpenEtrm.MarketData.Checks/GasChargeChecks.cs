using OpenEtrm.MarketData;
using OpenEtrm.MarketData.Gas;
using OpenEtrm.MarketData.Trading;

internal static class GasChargeChecks
{
    internal static void Run()
    {
        var revision = SyntheticSideCalendars.AllDates.Key.Revision;
        var quote = new QuoteSpecification(new(revision, "synthetic_gas_charge_quote"), QuoteKind.Outright,
            new(revision, "synthetic_currency"), new(revision, "synthetic_gas_quantity"),
            Number("1"), Number("0.001"), Number("0.001"), Number("-10000"), Number("10000"));
        var unit = new Quantity(Number("1"), quote.Quantity);
        var calendars = SyntheticSideCalendars.Dated(false);
        var clock = GasDayClock.Capture(new(revision, "synthetic_afternoon_gas_clock"), TimeZoneInfo.Utc, 15 * 60);
        var midnightClock = GasDayClock.Capture(new(revision, "synthetic_midnight_gas_clock"), TimeZoneInfo.Utc, 0);
        var payment = At(1, 10);
        foreach (var instant in new[] { payment, payment.AddHours(12), payment.AddDays(1).AddTicks(-10) })
        {
            Require(GasChargeSides.LumpSumDay(clock, instant) == new DateOnly(2027, 1, 8),
                "A 15:00 gas day must already have ended at the start of the UTC payment date.");
            Require(GasChargeSides.LumpSumDay(midnightClock, instant) == new DateOnly(2027, 1, 9),
                "A gas day ending exactly at the start of the UTC payment date is included.");
        }

        var west = TimeZoneInfo.CreateCustomTimeZone("synthetic_west", TimeSpan.FromHours(-6), "Synthetic west", "Synthetic west");
        var lateClock = GasDayClock.Capture(new(revision, "synthetic_late_gas_clock"), west, 23 * 60);
        Require(GasChargeSides.LumpSumDay(lateClock, payment) == new DateOnly(2027, 1, 7),
            "The previous local date's 23:00 boundary can still fall after the start of the UTC payment date.");

        var recordedPayment = payment.AddHours(17);
        var lumpSum = GasChargeSides.LumpSum(Guid.NewGuid(), new UnitPrice(Number("5000"), quote), unit, clock, recordedPayment, calendars, quote);
        Require(lumpSum.QuoteKey == quote.Key && lumpSum.GasDeliveryClock == clock,
            "The fixed charge retains its exact quote and gas clock.");
        CheckRow(lumpSum.Periods.Single(), lumpSum.GenerateDatedSchedule().Rows.Single(),
            new(2027, 1, 8), new(2027, 1, 9), "1", recordedPayment, At(1, 9, 15));

        var start = new DateOnly(2027, 1, 20);
        var end = new DateOnly(2027, 3, 11);
        FixedSide Capacity(string rate, DateOnly to, int day) =>
            GasChargeSides.Capacity(Guid.NewGuid(), new UnitPrice(Number(rate), quote), unit, clock, start, to, day, calendars, quote);
        var capacity = Capacity("0.02", end, 2);
        var rows = capacity.GenerateDatedSchedule().Rows;
        Require(rows.Count == 3 && capacity.Periods.Length == 3, "The charge has one row per covered calendar month.");
        CheckRow(capacity.Periods[0], rows[0], start, new(2027, 2, 1), "12", At(2, 2), At(2, 1, 15));
        CheckRow(capacity.Periods[1], rows[1], new(2027, 2, 1), new(2027, 3, 1), "28", At(3, 2), At(3, 1, 15));
        CheckRow(capacity.Periods[2], rows[2], new(2027, 3, 1), end, "10", At(4, 2), At(3, 11, 15));

        var onFirst = Capacity("0.02", end, 1);
        CheckRow(onFirst.Periods[0], onFirst.GenerateDatedSchedule().Rows[0],
            start, new(2027, 2, 1), "12", At(2, 1), At(2, 1, 15));

        Refused(() => Capacity("0.02", end, 0));
        Refused(() => Capacity("0.02", end, 29));
        Refused(() => Capacity("0.02", start, 2));
        Refused(() => Capacity("0.02", start.AddDays(-1), 2));
        Refused(() => Capacity("0", end, 2));
        Refused(() => Capacity("-0.02", end, 2));
        Console.WriteLine("PASS gas charge rows: UTC payment date, monthly day counts, exact completion and invalid capacity terms");
    }

    private static void CheckRow(FixedSidePeriod period, ScheduleRow row, DateOnly from, DateOnly to,
        string quantity, DateTimeOffset payment, DateTimeOffset completion)
    {
        Require(row.DeliveryFrom == from && row.DeliveryTo == to && row.PaymentAt == payment,
            $"Expected charge row {from:yyyy-MM-dd} through {to:yyyy-MM-dd} (exclusive) with payment at {payment:O}.");
        Require(row.AllocatedQuantity.Numerator == quantity && row.AllocatedQuantity.Denominator == "1",
            $"Expected exact unit allocation {quantity} for {from:yyyy-MM-dd}.");
        Require(period.DeliveryCompletionAt == completion, $"Expected retained completion at {completion:O}.");
        PaymentDatePolicy.RequireCompletion(row.PaymentAt, completion);
    }

    private static DateTimeOffset At(int month, int day, int hour = 0) => new(2027, month, day, hour, 0, 0, TimeSpan.Zero);
    private static ExactDecimal Number(string value) => ExactDecimal.Parse(value);

    private static void Require(bool condition, string message)
    {
        if (!condition) throw new Exception(message);
    }

    private static void Refused(Action action)
    {
        try { action(); }
        catch (MarketDataException error) when (error.Code == "gas_composition_input") { return; }
        throw new Exception("Expected gas_composition_input refusal.");
    }
}
