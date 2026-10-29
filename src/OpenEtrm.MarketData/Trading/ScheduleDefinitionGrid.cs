using System.Collections.Immutable;
using OpenEtrm.MarketData.Power;

namespace OpenEtrm.MarketData.Trading;

// Immutable contractual geometry. A selected cell is an exact interval on one
// UTC axis; local labels are retained columns, never reconstructed from a current
// time-zone database. No quantity, price, rate or other market sample lives here.
public sealed record ScheduleDefinitionGrid(int Ordinal,string Key,ReferenceKey Clock,DateOnly Month,
    int? FirstRowOrdinal,DateTimeOffset Origin,long StepTicks,int AxisCount,
    ImmutableArray<int> Selection,ImmutableArray<int> Lengths,ImmutableArray<int> OperatingDays,
    ImmutableArray<int> LocalMinutes,ImmutableArray<int> Occurrences)
{
    [System.Text.Json.Serialization.JsonIgnore] public int CellCount=>Selection.Length;
    internal long EndTicks(int cell)=>Origin.Ticks+((long)Selection[cell]+Lengths[cell])*StepTicks;
    public DeliveryInterval Interval(int cell)
    {
        if((uint)cell>=(uint)CellCount)throw ScheduleDefinition.Error("A retained interval cell must belong to its exact grid.");
        var start=Origin.AddTicks(checked((long)Selection[cell]*StepTicks));
        return new(start,start.AddTicks(checked((long)Lengths[cell]*StepTicks)),DateOnly.FromDayNumber(OperatingDays[cell]),
            LocalMinutes[cell]/60,LocalMinutes[cell]%60,Occurrences[cell]);
    }
    internal void Require()
    {
        Money.RequireKey(Clock);
        if(Ordinal<0||string.IsNullOrWhiteSpace(Key)||Month.Day!=1||Month<new DateOnly(2000,1,1)||Month>=new DateOnly(2100,1,1)||FirstRowOrdinal<0||!TradingInstant.IsSupported(Origin)||
            Origin.Ticks%10!=0||StepTicks<=0||StepTicks%10!=0||AxisCount<=0||
            Selection.IsDefaultOrEmpty||Lengths.IsDefault||OperatingDays.IsDefault||LocalMinutes.IsDefault||Occurrences.IsDefault||
            Lengths.Length!=CellCount||OperatingDays.Length!=CellCount||LocalMinutes.Length!=CellCount||Occurrences.Length!=CellCount)
            throw ScheduleDefinition.Error("A retained grid requires an exact clock, UTC axis, selected cells and complete local metadata columns.");
        // A local Jan1 2100 boundary may lie later on the UTC clock. One UTC day
        // covers every supported timezone offset without changing the local term.
        if(StepTicks>(new DateTimeOffset(2100,1,2,0,0,0,TimeSpan.Zero).Ticks-Origin.Ticks)/AxisCount)
            throw ScheduleDefinition.Error("A retained UTC axis exceeds the supported horizon or arithmetic bounds.");
        for(var n=0;n<CellCount;n++)
        {
            if(Selection[n]<0||Lengths[n]<=0||(long)Selection[n]+Lengths[n]>AxisCount||
                n>0&&(long)Selection[n-1]+Lengths[n-1]>Selection[n]||LocalMinutes[n] is <0 or >=1440||
                Occurrences[n] is not(1 or 2)||OperatingDays[n]<Month.DayNumber||OperatingDays[n]>=Month.AddMonths(1).DayNumber)
                throw ScheduleDefinition.Error("Retained selected intervals must be ordered, nonoverlapping and preserve their exact native month and local labels.");
        }
    }
    internal static ScheduleDefinitionGrid Capture(int ordinal,string key,ReferenceKey clock,DateOnly month,
        int? firstRowOrdinal,IReadOnlyList<DeliveryInterval> intervals)
    {
        if(intervals.Count==0)throw ScheduleDefinition.Error("A retained grid requires at least one explicit interval.");
        var origin=intervals[0].Start;var step=intervals[0].End.Ticks-origin.Ticks;
        static long Gcd(long a,long b){while(b!=0){var rest=a%b;a=b;b=rest;}return Math.Abs(a);}
        foreach(var interval in intervals){step=Gcd(step,interval.End.Ticks-interval.Start.Ticks);step=Gcd(step,interval.Start.Ticks-origin.Ticks);}
        if(step<=0)throw ScheduleDefinition.Error("A retained grid requires positive exact intervals.");
        var result=new ScheduleDefinitionGrid(ordinal,key,clock,month,firstRowOrdinal,origin,step,
            checked((int)((intervals[^1].End.Ticks-origin.Ticks)/step)),
            [..intervals.Select(i=>checked((int)((i.Start.Ticks-origin.Ticks)/step)))],
            [..intervals.Select(i=>checked((int)((i.End.Ticks-i.Start.Ticks)/step)))],
            [..intervals.Select(i=>i.OperatingDate.DayNumber)],[..intervals.Select(i=>60*i.HourBeginning+i.Minute)],[..intervals.Select(i=>i.Occurrence)]);
        result.Require();return result;
    }
    // Used by both domain attachment and authorized booking admission. Partition
    // boundaries may differ (for example a stepped fixed strike), but each port
    // consumes one identical source cell, including its retained local labels.
    public static void RequireCoverage(IEnumerable<ScheduleDefinitionGrid> required,IEnumerable<ScheduleDefinitionGrid> supplied)
    {
        var targets=required.ToArray();var sources=supplied.ToArray();
        foreach(var grid in targets.Concat(sources))grid.Require();
        if(targets.Length==0||sources.Length==0)throw ScheduleDefinition.Error("A sibling port requires nonempty retained input and output grids.");
        targets=targets.OrderBy(grid=>grid.Origin.Ticks+(long)grid.Selection[0]*grid.StepTicks).ToArray();
        sources=sources.OrderBy(grid=>grid.Origin.Ticks+(long)grid.Selection[0]*grid.StepTicks).ToArray();
        static IEnumerable<(ReferenceKey Clock,long Start,long End,int Day,int Minute,int Occurrence)> Cells(IEnumerable<ScheduleDefinitionGrid> grids)
        {
            foreach(var grid in grids)for(var n=0;n<grid.CellCount;n++)
            {
                var start=grid.Origin.Ticks+(long)grid.Selection[n]*grid.StepTicks;
                yield return(grid.Clock,start,start+(long)grid.Lengths[n]*grid.StepTicks,grid.OperatingDays[n],grid.LocalMinutes[n],grid.Occurrences[n]);
            }
        }
        foreach(var grids in new[]{targets,sources})
        {
            long? end=null;
            foreach(var cell in Cells(grids))
            {
                if(end is {} previous&&previous>cell.Start)throw ScheduleDefinition.Error("A retained sibling shape cannot contain overlapping or repeated cells.");
                end=cell.End;
            }
        }
        using var source=Cells(sources).GetEnumerator();var exists=source.MoveNext();
        foreach(var target in Cells(targets))
        {
            while(exists&&source.Current.Start<target.Start)exists=source.MoveNext();
            if(!exists||source.Current!=target)throw ScheduleDefinition.Error("A retained sibling port requires exact clock, interval and local-label coverage from its quantity output.");
        }
    }
}
