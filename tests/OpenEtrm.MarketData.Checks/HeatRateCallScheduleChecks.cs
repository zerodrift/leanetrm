using System.Collections.Immutable;
using OpenEtrm.MarketData;
using OpenEtrm.MarketData.Power;
using OpenEtrm.MarketData.Power.Structured;
using OpenEtrm.MarketData.Trading;
using OpenEtrm.Shared.Temporal;

// Synthetic calendar: weekdays are business days except 1 January 2041; the peak
// block is hours beginning 7 to 22, Monday to Friday, on a New York clock.
internal static class HeatRateCallScheduleChecks
{
    public static void Run()
    {
        Check("a peak block exercises each delivery weekday on the prior business day's cutoff", PeakBlock);
        Check("a period across two months takes each day's own contracts, bucket and payment", TwoMonths);
        Check("an all-hours block exercises weekend days on Friday and keeps a 23-hour day", AllHours);
        Check("an expanded strip is a valid economic definition", ValidTerms);
        Check("a period, rule or curve that leaves a day undetermined is refused", Refusals);
    }

    private sealed class Calendar
    {
        public Fixture F { get; } = new();
        public PowerClock Clock { get; } = PowerClock.Capture(TimeZoneInfo.FindSystemTimeZoneById("America/New_York"));
        public QuantitySpecification Capacity { get; }
        public QuantitySpecification Energy { get; }
        public PaymentCalendar Business { get; }
        public HeatRateCallExerciseRule Rule { get; }
        public PaymentRule Payment { get; }
        public HeatRateCallCurve Power { get; }
        public HeatRateCallCurve Fuel { get; }
        public static DateOnly D(string text) => DateOnly.ParseExact(text, "yyyy-MM-dd");

        public Calendar()
        {
            Capacity = new(F.R("power_capacity"), F.R("power"), F.R("delivery_rate"), F.R("megawatt"), F.R("power"));
            Energy = new(F.R("power_quantity"), F.R("power"), F.R("delivered_energy"), F.R("megawatt_hour"), F.R("energy"));
            var from = D("2040-12-01"); var to = D("2041-06-01");
            var days = Enumerable.Range(0, to.DayNumber - from.DayNumber).Select(from.AddDays)
                .Where(d => d.DayOfWeek is not (DayOfWeek.Saturday or DayOfWeek.Sunday) && d != D("2041-01-01"));
            Business = new(F.R("synthetic_business_days"), DateSet.Create(from, to, days));
            Rule = new(1, new(10, 0), Business, HeatRateCallExerciseRule.BusinessDayLead);
            Payment = new(PaymentFrequency.Monthly, PaymentDateRule.DayOfFollowingMonth, null, 10, null, ShortMonth.Reject,
                DateRoll.Following, Business, PaymentRule.Implementation);
            Power = new(F.C("power_forward"), [Month(11, 1), Month(12, 2), Month(13, 3)]);
            Fuel = new(F.C("fuel_forward"), [Month(21, 1), Month(22, 2), Month(23, 3)]);
        }
        public MarketContract Month(int key, int month) => new(F.K(key), ContractKind.Forward, new(2041, month, 1),
            new DateOnly(2041, month, 1).AddMonths(1), Fixture.Utc($"2041-{month:00}-01T00:00:00Z").AddMonths(1), null);
        public DeliveryHours Peak => new(BlockMembership.Peak, new(7, 23, false), [D("2041-01-01")]);
        public HeatRateCallDelivery Delivery(string from, string to, DeliveryHours? hours = null) =>
            new(D(from), D(to), Clock, 60, hours ?? Peak, Capacity, Energy);
        public ImmutableArray<HeatRateCallExercise> Expand(string from, string to, DeliveryHours? hours = null) =>
            HeatRateCallSchedule.Expand(Delivery(from, to, hours), Rule, Payment, Power, Fuel);
    }

    private static void PeakBlock()
    {
        var c = new Calendar(); var rows = c.Expand("2041-01-01", "2041-01-12");
        Equal(string.Join(",", rows.Select(r => r.DeliveryDay.Day)), "2,3,4,7,8,9,10,11");
        if (rows.Any(r => r.EnergyPerUnit != ExactDecimal.Parse("16") || r.PaymentAt != Fixture.Utc("2041-02-11T00:00:00Z") ||
                r.Power != new CurveRequest(c.F.C("power_forward"), c.F.K(11)) || r.Fuel != new CurveRequest(c.F.C("fuel_forward"), c.F.K(21)) ||
                r.MarkBucket != HeatRateCallSchedule.MonthBucket(new(2041, 1, 1))))
            throw new Exception("Each January weekday has sixteen peak hours, January's contracts and bucket, and payment on 11 February.");
        // 2 January follows a holiday: its prior business day is Monday 31 December, 10:00 in New York.
        Equal(rows[0].ExerciseAt.ToString("O"), Fixture.Utc("2040-12-31T15:00:00Z").ToString("O"));
        Equal(rows[1].ExerciseAt.ToString("O"), Fixture.Utc("2041-01-02T15:00:00Z").ToString("O"));
        Equal(rows[3].ExerciseAt.ToString("O"), Fixture.Utc("2041-01-04T15:00:00Z").ToString("O"));
    }

    private static void TwoMonths()
    {
        var c = new Calendar(); var rows = c.Expand("2041-01-30", "2041-02-03");
        Equal(string.Join(",", rows.Select(r => r.DeliveryDay.ToString("MM-dd"))), "01-30,01-31,02-01");
        var february = rows[2];
        if (february.Power.Contract != c.F.K(12) || february.Fuel.Contract != c.F.K(22) ||
            february.MarkBucket != HeatRateCallSchedule.MonthBucket(new(2041, 2, 1)) || february.MarkBucket == rows[0].MarkBucket ||
            february.PaymentAt != Fixture.Utc("2041-03-11T00:00:00Z") || rows[0].PaymentAt != Fixture.Utc("2041-02-11T00:00:00Z"))
            throw new Exception("A February day takes February's contracts, bucket and payment.");
    }

    private static void AllHours()
    {
        var c = new Calendar(); var rows = c.Expand("2041-03-08", "2041-03-12", new(BlockMembership.All, null, []));
        Equal(string.Join(",", rows.Select(r => r.EnergyPerUnit.ToString())), "24,24,23,24");
        // Saturday and Sunday are scheduled on Friday; Monday, after the clock change, at 10:00 daylight time on Friday too.
        var friday = Fixture.Utc("2041-03-08T15:00:00Z");
        if (rows[1].ExerciseAt != friday || rows[2].ExerciseAt != friday || rows[3].ExerciseAt != friday ||
            rows[0].ExerciseAt != Fixture.Utc("2041-03-07T15:00:00Z"))
            throw new Exception("Days after a non-business day share the last business day's cutoff.");
    }

    private static void ValidTerms()
    {
        var c = new Calendar(); var f = c.F;
        var terms = new HeatRateCallTerms(c.Capacity.Key, f.Quotes[2], f.Quotes[0],
            new(ExactDecimal.Parse("7"), f.Quotes[0].Quantity, f.Quotes[2].Quantity, QuantityFactor.Implementation),
            new(ExactDecimal.Parse("3"), f.Quotes[2]), HeatRateCallTerms.DailyAutomaticCash, OpenEtrm.MarketData.Options.KirkSpreadCall.Implementation,
            HeatRateCallTerms.ActualOver365, c.Expand("2041-03-08", "2041-03-12", new(BlockMembership.All, null, [])));
        if (new HeatRateCallInstrument(Guid.NewGuid(), terms, null).Terms.Exercises.Length != 4) throw new Exception("Four retained exercises.");
    }

    private static void Refusals()
    {
        var c = new Calendar();
        void Refuse(Action action)
        {
            try { action(); } catch (MarketDataException e) when (e.Code == "heat_rate_call_schedule") { return; }
            throw new Exception("Expected heat_rate_call_schedule.");
        }
        Refuse(() => c.Expand("2041-01-05", "2041-01-05")); Refuse(() => c.Expand("2041-01-06", "2041-01-05"));
        Refuse(() => c.Expand("2041-01-05", "2041-01-07"));                       // a weekend has no peak hour
        Refuse(() => c.Expand("2041-03-28", "2041-04-03"));                       // no April contract
        Refuse(() => c.Expand("2040-12-02", "2040-12-04"));                       // no December contract
        Refuse(() => HeatRateCallSchedule.Expand(c.Delivery("2041-01-02", "2041-01-04"), c.Rule, c.Payment,
            c.Power with { Contracts = c.Power.Contracts.Add(c.Month(14, 1)) }, c.Fuel));
        Refuse(() => HeatRateCallSchedule.Expand(c.Delivery("2041-01-02", "2041-01-04"), c.Rule with { LeadBusinessDays = 0 }, c.Payment, c.Power, c.Fuel));
        Refuse(() => HeatRateCallSchedule.Expand(c.Delivery("2041-01-02", "2041-01-04"), c.Rule with { Implementation = "same_day" }, c.Payment, c.Power, c.Fuel));
        var narrow = new PaymentCalendar(c.Business.Key, DateSet.Create(new(2041, 1, 2), new(2041, 6, 1),
            c.Business.BusinessDays.Dates.Where(d => d >= new DateOnly(2041, 1, 2))));
        Refuse(() => HeatRateCallSchedule.Expand(c.Delivery("2041-01-02", "2041-01-04"), c.Rule with { BusinessDays = narrow }, c.Payment, c.Power, c.Fuel));
        Refuse(() => HeatRateCallSchedule.Expand(null!, c.Rule, c.Payment, c.Power, c.Fuel));
    }

    private static void Equal(string actual, string expected)
    {
        if (actual != expected) throw new Exception($"Expected {expected}, got {actual}.");
    }
    private static void Check(string label, Action run) { run(); Console.WriteLine($"PASS heat-rate call schedule: {label}"); }
}
