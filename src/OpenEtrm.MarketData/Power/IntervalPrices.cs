using System.Collections.Immutable;
using OpenEtrm.MarketData.CustomCurves;

namespace OpenEtrm.MarketData.Power;

// An exact price for one delivery interval and the observations it came from.
public sealed record IntervalPrice(DeliveryInterval Interval,ExactUnitPrice Price,ImmutableHashSet<ObservationKey> Lineage)
{
    public DateTimeOffset Start=>Interval.Start;
    public DateTimeOffset End=>Interval.End;
}

public static class IntervalPrices
{
    public const int MaximumWindowIntervals=35_136;

    // The interval contracts covering [from, to), in delivery order; every interval of the window must have one.
    public static ImmutableArray<MarketContract> Within(IReadOnlyList<MarketContract> contracts,int minutes,DateTimeOffset from,DateTimeOffset to)
    {
        ImmutableArray<MarketContract> window=[..contracts.Where(c=>(c.Interval??throw new MarketDataException("interval_contract",
            "Every constituent needs an exact delivery interval.")).Start>=from && c.Interval.End<=to).OrderBy(c=>c.Interval!.Start)];
        return TimeSpan.FromMinutes(minutes)*window.Length==to-from?window
            :throw new MarketDataException("interval_curve_coverage","The curve has no interval for part of this window.");
    }

    // The built price of each interval contract, in the order given.
    public static ImmutableArray<IntervalPrice> Marked(CurveBuild build,CurveKey curve,IReadOnlyList<MarketContract> contracts)
    {
        ArgumentNullException.ThrowIfNull(build);ArgumentNullException.ThrowIfNull(contracts);
        var built=build.Values.Where(v=>v.Request.Curve==curve).ToDictionary(v=>v.Request.Contract);
        return [..contracts.Select(c=>built.TryGetValue(c.Key,out var value)
            ?new IntervalPrice(c.Interval??throw new MarketDataException("interval_contract","Every constituent needs an exact delivery interval."),
                ExactUnitPrice.FromPrice(value.Price),value.Lineage)
            :throw new MarketDataException("request_member","The build lacks a requested interval."))];
    }

    // Both ends must be cell boundaries of the clock's grid, at most 35,136 intervals apart.
    public static void RequireWindow(PowerClock clock,int minutes,DateTimeOffset from,DateTimeOffset to)
    {
        ArgumentNullException.ThrowIfNull(clock);
        var step=TimeSpan.FromMinutes(minutes);
        if(!IntervalSeriesGrid.Resolutions.Contains(minutes) || from.Offset!=TimeSpan.Zero || to.Offset!=TimeSpan.Zero || to<=from ||
            to-from>step*MaximumWindowIntervals || !PowerHours.IsCell(clock.Zone,from,step) || !PowerHours.IsCell(clock.Zone,to,step))
            throw InvalidWindow();
    }

    public static MarketDataException InvalidWindow()=>
        new("interval_curve_window","Choose UTC window ends on the curve's interval grid, at most 35,136 intervals apart.");
}
