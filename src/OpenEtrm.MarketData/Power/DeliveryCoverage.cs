using System.Collections.Immutable;

namespace OpenEtrm.MarketData.Power;

public sealed record DeliveryCoverage
{
    public DateTimeOffset From { get; }
    public DateTimeOffset To { get; }
    public DeliveryCoverage(DateTimeOffset from,DateTimeOffset to)
    {
        if(from.Offset!=TimeSpan.Zero || to.Offset!=TimeSpan.Zero || from>=to || from.Ticks%10!=0 || to.Ticks%10!=0)
            throw new MarketDataException("ppa_segment_coverage","Coverage requires ordered, exact half-open UTC boundaries.");
        From=from;To=to;
    }
    public bool Contains(DateTimeOffset from,DateTimeOffset to)=>From<=from && to<=To;

    // The caller declares the intended envelope, including any deliberate prospective termination.
    public static void ValidatePartition(DeliveryCoverage expected,ImmutableArray<DeliveryCoverage> parts)
    {
        if(expected is null || parts.IsDefaultOrEmpty || parts.Any(p=>p is null))
            throw new MarketDataException("ppa_segment_coverage","Declare the intended coverage and its nonempty partition.");
        var cursor=expected.From;
        foreach(var part in parts.OrderBy(p=>p.From))
        {
            if(part.From!=cursor || part.To>expected.To)
                throw new MarketDataException("ppa_segment_coverage","Coverage must partition the declared envelope without overlap, gap or extension.");
            cursor=part.To;
        }
        if(cursor!=expected.To)throw new MarketDataException("ppa_segment_coverage","Coverage must reach the declared envelope end.");
    }

    public static void ValidateForwardBoundary(DateTimeOffset boundary,DateTimeOffset effectiveAt,PowerClock clock,int intervalMinutes)
    {
        if(clock is null || intervalMinutes is not(15 or 60) || boundary.Offset!=TimeSpan.Zero || effectiveAt.Offset!=TimeSpan.Zero ||
            boundary.Ticks%10!=0 || effectiveAt.Ticks%10!=0 || boundary<effectiveAt || !PowerHours.IsCell(clock.Zone,boundary,TimeSpan.FromMinutes(intervalMinutes)))
            throw new MarketDataException("ppa_segment_boundary","A forward change requires a native boundary at or after its effective instant; elapsed delivery cannot be restated.");
    }
}
