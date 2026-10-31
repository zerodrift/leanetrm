using OpenEtrm.MarketData;
using OpenEtrm.MarketData.Gas;
using OpenEtrm.MarketData.Trading;

internal static class PhysicalGasQuantityChecks
{
    internal static void Run()
    {
        var revision = new ReferenceRevision(Guid.NewGuid(), Guid.NewGuid());
        ReferenceKey R(string code) => new(revision, code);
        var clock = GasDayClock.Capture(R("gas_clock"), Zone(), 9 * 60);
        var unit = R("synthetic_gas_energy");
        Quantity Q(string value) => new(ExactDecimal.Parse(value), unit);
        GasQuantitySchedule Schedule(DateOnly from, DateOnly to, GasQuantityBasis basis, string value,
            GasFuelConvention fuel = GasFuelConvention.None, string fraction = "0") =>
            GasQuantitySchedule.Generate(new(clock, from, to, basis, Q(value), fuel, ExactDecimal.Parse(fraction)));

        // The transition occurs inside the gas day starting at 09:00 the day before.
        var spring = new DateOnly(2031, 3, 8);
        var autumn = new DateOnly(2031, 11, 1);
        foreach (var day in new[] { spring, autumn })
        {
            var daily = Schedule(day, day.AddDays(1), GasQuantityBasis.PerOperatingDay, "1000");
            Equal(daily.Rows[0].Receipt, "1000");
            if (daily.Rows[0].Start.Offset != TimeSpan.Zero || daily.Rows[0].End.Offset != TimeSpan.Zero ||
                daily.Rows[0].OperatingDay != day)
                throw new Exception("Gas quantities must retain exact UTC bounds and their operating date.");
        }
        var shortDay = Schedule(spring, spring.AddDays(1), GasQuantityBasis.PerElapsedHour, "40");
        var longDay = Schedule(autumn, autumn.AddDays(1), GasQuantityBasis.PerElapsedHour, "40");
        Equal(shortDay.TotalReceipt, "920"); Equal(longDay.TotalReceipt, "1000");
        if (shortDay.Rows[0].End - shortDay.Rows[0].Start != TimeSpan.FromHours(23) ||
            longDay.Rows[0].End - longDay.Rows[0].Start != TimeSpan.FromHours(25))
            throw new Exception("Hourly gas quantities must integrate the actual pinned gas-day duration.");

        var term = Schedule(spring, spring.AddDays(3), GasQuantityBasis.PeriodTotalEqualOperatingDays, "1000");
        foreach (var row in term.Rows) Equal(row.Receipt, "1000", "3");
        Equal(term.TotalReceipt, "1000");
        var maximum = Schedule(spring, spring.AddDays(GasQuantityTerms.MaximumOperatingDays),
            GasQuantityBasis.PeriodTotalEqualOperatingDays, "1000");
        if (maximum.Rows.Length != GasQuantityTerms.MaximumOperatingDays)
            throw new Exception("The admitted operating-day limit must preserve every day.");
        Equal(maximum.TotalReceipt, "1000");
        var standard = Schedule(spring, spring.AddDays(1), GasQuantityBasis.PerOperatingDay, "1000",
            GasFuelConvention.NetEqualsGrossTimesOneMinusFuel, "0.02");
        Equal(standard.TotalReceipt, "1000"); Equal(standard.TotalDelivery, "980"); Equal(standard.TotalFuel, "20");
        var alternate = Schedule(spring, spring.AddDays(1), GasQuantityBasis.PerOperatingDay, "1000",
            GasFuelConvention.NetEqualsGrossDividedByOnePlusFuel, "0.02");
        Equal(alternate.TotalDelivery, "50000", "51"); Equal(alternate.TotalFuel, "1000", "51");
        Equal(alternate.TotalDelivery.Add(alternate.TotalFuel), "1000");
        Equal(Schedule(spring, spring.AddDays(1), GasQuantityBasis.PerOperatingDay, "0").TotalDelivery, "0");

        var restored = new GasDayClock(clock.Key, clock.Rules, clock.StartMinute);
        if (restored.Boundary(spring) != clock.Boundary(spring) ||
            DefinitionIdentity.For(restored) != DefinitionIdentity.For(clock))
            throw new Exception("Captured clock rules and their economic identity must survive reconstruction.");
        if (DefinitionIdentity.For(clock) == DefinitionIdentity.For(new GasDayClock(clock.Key, clock.Rules, 0)))
            throw new Exception("The gas-day start is an economic term.");
        var original = DefinitionIdentity.For(standard.Terms);
        foreach (var changed in new[] {
            new GasQuantityTerms(clock, spring, spring.AddDays(1), GasQuantityBasis.PerElapsedHour, Q("1000"), standard.Terms.FuelConvention, ExactDecimal.Parse("0.02")),
            new GasQuantityTerms(clock, spring, spring.AddDays(1), GasQuantityBasis.PerOperatingDay, Q("1000"), alternate.Terms.FuelConvention, ExactDecimal.Parse("0.02")),
            new GasQuantityTerms(clock, spring, spring.AddDays(1), GasQuantityBasis.PerOperatingDay, Q("1000"), standard.Terms.FuelConvention, ExactDecimal.Parse("0.03")) })
            if (DefinitionIdentity.For(changed) == original)
                throw new Exception("Quantity basis and fuel terms must participate in economic identity.");

        // Independently specified arithmetic consumer; this does not claim physical PV integration.
        var price = new UnitPrice(ExactDecimal.Parse("3.20"), new PriceUnit(R("usd"), unit, ExactDecimal.Parse("1")));
        if (standard.TotalReceipt.Times(price).ToExactString() != "3200")
            throw new Exception("Receipt energy lost its native quote denominator.");
        Reject("quantity_mismatch", () => standard.TotalReceipt.Times(new UnitPrice(ExactDecimal.Parse("3"),
            new PriceUnit(R("usd"), R("different_quantity"), ExactDecimal.Parse("1")))));

        Reject("gas_day_clock", () => new GasDayClock(clock.Key, "invalid clock rules", 0));
        Reject("gas_day_clock", () => new GasDayClock(clock.Key, clock.Rules, 1440));
        var east = TimeZoneInfo.CreateCustomTimeZone("synthetic_east", TimeSpan.FromHours(14), "East", "East");
        var west = TimeZoneInfo.CreateCustomTimeZone("synthetic_west", TimeSpan.FromHours(-14), "West", "West");
        Reject("gas_day_boundary", () => GasDayClock.Capture(clock.Key, east, 0).Boundary(DateOnly.MinValue));
        Reject("gas_day_boundary", () => GasDayClock.Capture(clock.Key, west, 1439).Boundary(DateOnly.MaxValue));
        Reject("gas_day_boundary", () => GasDayClock.Capture(clock.Key, Zone(), 150).Boundary(new(2031, 3, 9)));
        Reject("gas_day_boundary", () => GasDayClock.Capture(clock.Key, Zone(), 90).Boundary(new(2031, 11, 2)));
        Reject("gas_quantity_terms", () => Schedule(spring, spring, GasQuantityBasis.PerOperatingDay, "1000"));
        Reject("gas_quantity_terms", () => Schedule(spring, spring.AddDays(1), (GasQuantityBasis)0, "1000"));
        Reject("gas_quantity_terms", () => Schedule(spring, spring.AddDays(1), GasQuantityBasis.PerOperatingDay, "-1"));
        Reject("gas_quantity_terms", () => Schedule(spring, spring.AddDays(GasQuantityTerms.MaximumOperatingDays + 1), GasQuantityBasis.PerOperatingDay, "1"));
        using var canceled = new CancellationTokenSource();
        canceled.Cancel();
        try
        {
            GasQuantitySchedule.Generate(term.Terms, canceled.Token);
            throw new Exception("Canceled gas schedule generation must refuse.");
        }
        catch (OperationCanceledException) when (canceled.IsCancellationRequested) { }
        Reject("gas_fuel_terms", () => Schedule(spring, spring.AddDays(1), GasQuantityBasis.PerOperatingDay, "1000", (GasFuelConvention)0));
        Reject("gas_fuel_terms", () => Schedule(spring, spring.AddDays(1), GasQuantityBasis.PerOperatingDay, "1000", GasFuelConvention.None, "0.02"));
        Reject("gas_fuel_terms", () => Schedule(spring, spring.AddDays(1), GasQuantityBasis.PerOperatingDay, "1000", GasFuelConvention.NetEqualsGrossTimesOneMinusFuel, "1"));
        Reject("gas_fuel_terms", () => Schedule(spring, spring.AddDays(1), GasQuantityBasis.PerOperatingDay, "1000", GasFuelConvention.NetEqualsGrossDividedByOnePlusFuel, "-0.02"));
        Console.WriteLine("PASS physical gas quantities: pinned gas days, daily/rate/term bases, exact fuel conservation and explicit refusals");
    }

    private static TimeZoneInfo Zone()
    {
        var rule = TimeZoneInfo.AdjustmentRule.CreateAdjustmentRule(new(2030, 1, 1), new(2040, 12, 31), TimeSpan.FromHours(1),
            TimeZoneInfo.TransitionTime.CreateFloatingDateRule(new DateTime(1, 1, 1, 2, 0, 0), 3, 2, DayOfWeek.Sunday),
            TimeZoneInfo.TransitionTime.CreateFloatingDateRule(new DateTime(1, 1, 1, 2, 0, 0), 11, 1, DayOfWeek.Sunday));
        return TimeZoneInfo.CreateCustomTimeZone("synthetic_gas_zone", TimeSpan.FromHours(-6), "Synthetic gas zone", "Standard", "Summer", [rule]);
    }
    private static void Equal(ExactQuantity value, string numerator, string denominator = "1")
    {
        if (value.Numerator != numerator || value.Denominator != denominator)
            throw new Exception($"Expected {numerator}/{denominator}; received {value.Numerator}/{value.Denominator}.");
    }
    private static void Reject(string code, Action action)
    {
        try { action(); } catch (MarketDataException error) when (error.Code == code) { return; }
        throw new Exception("Expected " + code);
    }
}
