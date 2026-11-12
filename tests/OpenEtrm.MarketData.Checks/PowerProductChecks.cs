using OpenEtrm.MarketData;
using OpenEtrm.MarketData.Power;

internal static class PowerProductChecks
{
    public static void Run()
    {
        var clock = PowerClock.Capture(TimeZoneInfo.FindSystemTimeZoneById("America/New_York"));
        var peak = TimeOfUse.WeekdayBlock(60, 7, 23, false);
        // PJM Manual 15 revisions 36/37 share this mask; NERC supplies the 2020 off-peak dates.
        DateOnly[] holidays = [new(2020, 11, 26), new(2020, 12, 25)];
        var december = PowerHours.Compile(new(2020, 12, 1), new(2021, 1, 1), clock, TimeOfUse.All(60), holidays, 60);
        var on = PowerHours.Compile(new(2020, 12, 1), new(2021, 1, 1), clock, peak, holidays, 60);
        var off = PowerHours.Compile(new(2020, 12, 1), new(2021, 1, 1), clock, peak.Complement(), holidays, 60);
        Require(december.Length == 744 && on.Length == 352 && off.Length == 392, "December 2020 exact peak/off-peak partition");
        Require(!on.Any(p => p.OperatingDate == new DateOnly(2020, 12, 25)), "Christmas is entirely off-peak");
        Require(on.Count(p => p.OperatingDate == new DateOnly(2020, 12, 24)) == 16, "Christmas Eve is not an invented holiday");
        var periods = PowerHours.Compile(new(2020, 12, 18), new(2020, 12, 20), clock, peak.Complement(), holidays, 60);
        var revision = new ReferenceRevision(Guid.NewGuid(), Guid.NewGuid());
        var index = new PowerIndex(new(revision, "test_power"), new(revision, "test_currency"), new(revision, "megawatt_hour"), new(revision, "test_energy"), "Collected", "DA");
        var rows = periods.Select(p => new PowerFixing(p.Start, p.End, index,
            ExactDecimal.Parse(p.OperatingDate.Day == 18 ? "20" : "40"), null)).ToArray();
        var mean = PowerSettlement.Average(periods, rows, index, IntervalPricing.AggregatePolicy);
        Require(mean.Numerator == "35" && mean.Denominator == "1", "8 Friday off-peak hours and 24 Saturday hours use the requested native duration mean");
        Require(PowerSettlement.Average(periods[..3], rows[..3].Select((r, i) => r with { Total = ExactDecimal.Parse(i == 2 ? "1" : "0") }).ToArray(), index,
            IntervalPricing.AggregatePolicy).ToExactString() == "1/3", "No intermediate price rounding");
        Require(PowerSettlement.Average(periods, rows.Select(r => r with { Total = default }).ToArray(), index,
            IntervalPricing.AggregatePolicy).ToExactString() == "0", "Explicit zero remains a price");
        Reject("settlement_coverage", () => PowerSettlement.Average(periods, rows[..^1], index, IntervalPricing.AggregatePolicy));
        Reject("settlement_coverage", () => PowerSettlement.Average([], [], index, IntervalPricing.AggregatePolicy));
        Reject("settlement_coverage", () => PowerSettlement.Average(periods, rows.Select((r, i) => i == 1 ? rows[0] : r).ToArray(), index, IntervalPricing.AggregatePolicy));
        Reject("power_price_average", () => PowerSettlement.Average(periods, rows, index, "invented"));
        Console.WriteLine("PASS product averages: verified December 2020 mask/date partition, native weights, exact fractions, zero and incomplete/duplicate refusal");
    }
    static void Require(bool condition, string message) { if (!condition) throw new Exception(message); }
    static void Reject(string code, Action work)
    {
        try { work(); } catch (MarketDataException e) when (e.Code == code) { return; }
        throw new Exception("Expected " + code);
    }
}
