using System.Collections.Immutable;

namespace OpenEtrm.MarketData.Power;

public enum BlockMembership { All = 1, Peak, OffPeak }

// A published TOU product version's peak window: hours beginning [StartHour, EndHour),
// Monday–Friday or Monday–Saturday; holidays are off-peak.
public sealed record TouPeak(int StartHour,int EndHour,bool Saturday);

// The clock, interval length and holidays a curve's blocks are compiled on.
public sealed record DeliveryGrid(PowerClock Clock,int IntervalMinutes,ImmutableArray<DateOnly> Holidays);

// A block curve: each date-only constituent's delivery period crossed with one TOU membership.
// An all-hours block has no TOU peak window.
public sealed record IntervalBlockDefinition(CurveKey Curve,BlockMembership Membership,TouPeak? Peak,ImmutableArray<MarketContract> Contracts);

public static class IntervalBlocks
{
    public static TimeOfUse Mask(BlockMembership membership,int minutes,TouPeak? peak)=>membership switch
    {
        BlockMembership.All=>TimeOfUse.All(minutes),
        BlockMembership.Peak when peak is not null=>TimeOfUse.WeekdayBlock(minutes,peak.StartHour,peak.EndHour,peak.Saturday),
        BlockMembership.OffPeak when peak is not null=>TimeOfUse.WeekdayBlock(minutes,peak.StartHour,peak.EndHour,peak.Saturday).Complement(),
        _=>throw new MarketDataException("interval_block","A peak or off-peak block needs its TOU peak window.")
    };

    // The intervals of one block constituent on the grid, in delivery order.
    public static ImmutableArray<PowerInterval> Compile(DeliveryGrid grid,IntervalBlockDefinition block,MarketContract contract)
    {
        if(contract.Interval is not null)
            throw new MarketDataException("interval_block","A block constituent is a date-only delivery period.");
        return [..PowerHours.Compile(contract.DeliveryFrom,contract.DeliveryTo,grid.Clock,Mask(block.Membership,grid.IntervalMinutes,block.Peak),
            [..grid.Holidays.Where(d=>d>=contract.DeliveryFrom && d<contract.DeliveryTo)],grid.IntervalMinutes)];
    }
}
