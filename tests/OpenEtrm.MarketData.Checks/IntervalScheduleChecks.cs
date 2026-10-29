using System.Collections.Immutable;
using OpenEtrm.MarketData;
using OpenEtrm.MarketData.Power;
using OpenEtrm.MarketData.Trading;

internal static class IntervalScheduleChecks
{
    internal static int Run()
    {
        var f = new PpaAcceptanceFixture();
        if((Side)f.Physical().Memberships.Single(m=>m.Slot.Role==PpaRole.StrikePayment).Side is not FixedSide)
            throw new Exception("PPA payments must use the same FixedSide family as whole-term swaps.");
        var all = new IntervalScheduleParameters(f.Delivery, f.Clock, IntervalScheduleCadence.NativeIntervals, new(BlockMembership.All, null, []));
        var side = Guid.NewGuid();
        var months = all.Generate(side, f.R("synthetic_usd"), f.Payment());
        if (months.Length != 1 || months[0].Intervals.Length != 721 ||
            !months[0].Intervals.SequenceEqual(f.Hours.Select(i => i.Delivery)))
            throw new Exception("A parameter-only schedule must retain the complete DST delivery grid.");
        if (months[0].Payment != new SidePaymentKey(side, 0, PpaAcceptanceFixture.At("2031-12-10T00:00:00Z"), f.R("synthetic_usd")))
            throw new Exception("Schedule generation must retain the monthly payment key independently of native row ordinals.");
        var repeated = months[0].Intervals.GroupBy(i => (i.OperatingDate, i.HourBeginning, i.Minute)).Single(g => g.Count() == 2);
        if (!repeated.Select(i => i.Occurrence).SequenceEqual(new[] { 1, 2 })) throw new Exception("A repeated hour needs two distinct rows.");
        var capacity = new IntervalScheduleParameters(f.Delivery, f.Clock, IntervalScheduleCadence.OperatingMonths, null);
        var capacityRows = capacity.Generate(side, f.R("synthetic_usd"), f.Payment());
        if (capacityRows[0].Intervals.Length != 1 || capacityRows[0].Intervals[0].Start != f.From || capacityRows[0].Intervals[0].End != f.To)
            throw new Exception("Capacity generates a complete monthly row, not an hourly expansion.");
        var peak = new IntervalScheduleParameters(f.Delivery, f.Clock, IntervalScheduleCadence.NativeIntervals, new(BlockMembership.Peak, new(7, 23, false), [new(2031, 11, 11)]));
        var selected = peak.Generate(side, f.R("synthetic_usd"), f.Payment())[0].Intervals;
        if (selected.Any(i => i.OperatingDate == new DateOnly(2031, 11, 11) || i.OperatingDate.DayOfWeek is DayOfWeek.Saturday or DayOfWeek.Sunday || i.HourBeginning is <7 or >=23))
            throw new Exception("The side's explicit delivery mask must govern its generated rows.");
        try { _ = new IntervalScheduleParameters(f.Delivery, null!, IntervalScheduleCadence.NativeIntervals, new(BlockMembership.All, null, [])); }
        catch (MarketDataException error) when (error.Code == "side_schedule_parameters")
        { Console.WriteLine("PASS interval schedule: standalone parameters, DST, native masks, monthly capacity and exact payment keys"); return 1; }
        throw new Exception("A missing pinned clock must fail before valuation.");
    }
}
