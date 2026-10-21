namespace OpenEtrm.MarketData.Power;

public static class IntervalContracts
{
    // Every contract's interval must be one distinct cell of the pinned clock's grid, labelled exactly as PowerHours labels it.
    public static void Validate(PowerClock clock,int intervalMinutes,IReadOnlyList<MarketContract> contracts)
    {
        ArgumentNullException.ThrowIfNull(clock);ArgumentNullException.ThrowIfNull(contracts);
        var intervals=contracts.Select(c=>c?.Interval??throw Invalid("Every constituent needs an exact delivery interval.")).ToArray();
        var all=TimeOfUse.All(intervalMinutes);
        var cells=intervals.Select(i=>i.OperatingDate).Distinct().SelectMany(day=>
            PowerHours.Compile(day,day.AddDays(1),clock,all,[],intervalMinutes)).ToDictionary(c=>c.Start);
        var seen=new HashSet<DateTimeOffset>();
        foreach(var i in intervals)
            if(!cells.TryGetValue(i.Start,out var cell) || cell.Delivery!=i || !seen.Add(i.Start))
                throw Invalid("Each interval must be one distinct cell of the curve's clock, labelled as that clock labels it.");
    }

    private static MarketDataException Invalid(string message)=>new("interval_contract",message);
}
