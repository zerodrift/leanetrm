using System.Collections.Immutable;

namespace OpenEtrm.MarketData.Power;

// Consecutive values, one per interval, starting at a UTC instant. A null value withdraws its interval.
public sealed record IntervalSeriesSegment(DateTimeOffset Start,ImmutableArray<string?> Values);

public sealed record IntervalSeriesValue(DateTimeOffset Start,DateTimeOffset End,ExactDecimal? Value);

// Expands imported segments onto the cell grid of one pinned clock. Nothing is aligned, shifted or
// defaulted: a value that is not exactly one clock cell is an error.
public static class IntervalSeriesGrid
{
    public const int MaximumRevisionIntervals=200_000;
    public const string Implementation="interval_series_grid_v1";
    // The resolutions a series may have, in minutes; the catalog offers exactly these and storage constrains to them.
    public static ImmutableArray<int> Resolutions { get; } = [5, 15, 30, 60];
    private static readonly DateTimeOffset FirstStart=new(2000,1,1,0,0,0,TimeSpan.Zero);
    private static readonly DateTimeOffset StartLimit=new(2100,1,1,0,0,0,TimeSpan.Zero);

    public static ImmutableArray<IntervalSeriesValue> Expand(PowerClock clock,int intervalMinutes,IReadOnlyList<IntervalSeriesSegment> segments)
    {
        ArgumentNullException.ThrowIfNull(clock);
        if(!Resolutions.Contains(intervalMinutes))
            throw new MarketDataException("interval_series_resolution","Choose a 5, 15, 30 or 60 minute resolution.");
        if(segments is null || segments.Count==0 || segments.Any(s=>s is null || s.Values.IsDefaultOrEmpty))
            throw new MarketDataException("interval_series_empty","Supply at least one segment, each with at least one value.");
        var total=segments.Sum(s=>(long)s.Values.Length);
        if(total>MaximumRevisionIntervals)
            throw new MarketDataException("interval_series_size",$"A revision holds at most {MaximumRevisionIntervals} intervals.");
        var step=TimeSpan.FromMinutes(intervalMinutes);
        // Starts rise by one step within a segment, so its first and last start bound all of them.
        foreach(var segment in segments)
            if(segment.Start.Offset!=TimeSpan.Zero || !InStartRange(segment.Start) ||
              !InStartRange(segment.Start+TimeSpan.FromTicks(step.Ticks*(segment.Values.Length-1))))
                throw new MarketDataException("interval_series_instant","Segment starts must be UTC instants, and every interval must start within 2000-2099.");
        var cells=new (DateTimeOffset Start,string? Text)[total];
        var next=0;
        foreach(var segment in segments)
            for(var i=0;i<segment.Values.Length;i++)
                cells[next++]=(segment.Start+TimeSpan.FromTicks(step.Ticks*i),segment.Values[i]);
        foreach(var cell in cells)
            if(!PowerHours.IsCell(clock.Zone,cell.Start,step))
                throw new MarketDataException("interval_series_grid",$"The interval starting {cell.Start:O} is not a cell of the pinned clock.");
        Array.Sort(cells,(a,b)=>a.Start.CompareTo(b.Start));
        for(var i=1;i<cells.Length;i++)
            if(cells[i].Start-cells[i-1].Start<step)
                throw new MarketDataException("interval_series_overlap",$"The interval starting {cells[i].Start:O} overlaps the one before it.");
        var values=ImmutableArray.CreateBuilder<IntervalSeriesValue>(cells.Length);
        foreach(var (start,text) in cells)
            values.Add(new(start,start+step,text is null?null:ExactDecimal.Parse(text)));
        return values.MoveToImmutable();
    }

    private static bool InStartRange(DateTimeOffset start)=>start>=FirstStart && start<StartLimit;
}
