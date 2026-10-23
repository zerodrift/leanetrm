using System.Collections.Immutable;
using OpenEtrm.MarketData.CustomCurves;

namespace OpenEtrm.MarketData.Power;

public enum IntervalPriceState { Projected = 1, Actual, Unresolved, Modeled, PublishedFixing }
public sealed record IntervalAnchor(CurveRequest Request,InputUse Use,ImmutableHashSet<ObservationKey> Lineage);
public sealed record PriceUnavailable(string Code,DateTimeOffset? DueAt);

// A collected actual price for one interval, as the collected-price reader returns it at a knowledge cutoff.
public sealed record IntervalActual(DateTimeOffset Start,DateTimeOffset End,UnitPrice Price,DateTimeOffset KnownAt,Guid BatchId,int? Revision);

public enum IntervalAggregation { Hour = 1, Day }

// When an interval's actual is due: its start or end, plus an offset.
public sealed record IntervalActualRule(string Anchor,int OffsetMinutes,string Version)
{
    public const string SupportedVersion="interval_actual_due_v1";
    public DateTimeOffset DueAt(DateTimeOffset start,DateTimeOffset end)=>(Anchor=="interval_start"?start:end).AddMinutes(OffsetMinutes);
}

// One interval's price and where it came from: its actual, its live forward, or nothing yet.
public sealed record IntervalPriceResolution(DeliveryInterval Interval,IntervalPriceState State,ExactUnitPrice? Price,
    ImmutableHashSet<ObservationKey> ForwardLineage,IntervalActual? Actual,DateTimeOffset DueAt)
{
    public DateTimeOffset Start=>Interval.Start;
    public DateTimeOffset End=>Interval.End;
    public IntervalAnchor? Anchor { get; init; }
    public ExactUnitPrice? Estimate { get; init; }
    public string Publication { get; init; } = "mark";
    public PriceUnavailable? Reason { get; init; }
    public string LiveMarketRiskCoverage { get; init; } = "live_anchor";
}

// An explicit duration-weighted mean over native intervals that keeps every constituent.
public sealed record IntervalAggregate(DateTimeOffset Start,DateTimeOffset End,ExactUnitPrice? Mean,ImmutableArray<IntervalPriceResolution> Constituents);

public static class IntervalPricing
{
    public const string AggregatePolicy="duration_weighted_price_mean_v1";

    // An actual known at the cutoff prices its interval once the interval has ended at the valuation instant; before
    // then only an actual already known at that instant (a day-ahead fixing) does, so no later publication is used in
    // hindsight. Otherwise a due interval is an error, an ended one is unresolved, and a live one takes its forward;
    // an ended interval's forward is never used.
    public static ImmutableArray<IntervalPriceResolution> Resolve(IReadOnlyList<DeliveryInterval> intervals,PriceUnit unit,
        IReadOnlyDictionary<DateTimeOffset,IntervalPrice> forwards,IReadOnlyList<IntervalActual> actuals,IntervalActualRule rule,
        DateTimeOffset valuationAt,DateTimeOffset knownAt)
    {
        ArgumentNullException.ThrowIfNull(intervals);ArgumentNullException.ThrowIfNull(unit);ArgumentNullException.ThrowIfNull(forwards);ArgumentNullException.ThrowIfNull(actuals);
        var byStart=ValidateInputs(intervals,unit,actuals,rule,knownAt);
        if(forwards.Values.Any(f=>f.Price.Unit!=unit))throw Invalid("Actual and forward prices must share the curve's exact price unit.");
        return [..intervals.OrderBy(i=>i.Start).Select(i=>
        {
            var due=rule.DueAt(i.Start,i.End);
            if(byStart.TryGetValue(i.Start,out var actual) && (valuationAt>=i.End || actual.KnownAt<=valuationAt))
                return new IntervalPriceResolution(i,IntervalPriceState.Actual,ExactUnitPrice.FromPrice(actual.Price),[],actual,due)
                    {Publication="collected",LiveMarketRiskCoverage="observed"};
            if(valuationAt>=due)
                throw new MarketDataException("interval_actual_missing",$"The actual for the interval starting {i.Start:O} was due at {due:O} and is missing.");
            if(valuationAt>=i.End)
                return new IntervalPriceResolution(i,IntervalPriceState.Unresolved,null,[],null,due)
                    {Reason=new("no_estimate",due),Publication="unavailable",LiveMarketRiskCoverage="unavailable"};
            return forwards.TryGetValue(i.Start,out var live)
                ?new IntervalPriceResolution(i,IntervalPriceState.Projected,live.Price,live.Lineage,null,due)
                :throw new MarketDataException("request_member","A live interval needs its forward price.");
        })];
    }

    // One aggregate per whole local hour (the repeated hour twice) or whole operating day on the clock, from the intervals'
    // own labels; its mean is duration-weighted, and absent while any constituent is unresolved.
    public static ImmutableArray<IntervalAggregate> Aggregate(IReadOnlyList<IntervalPriceResolution> resolutions,IntervalAggregation aggregation,PowerClock clock)
    {
        ArgumentNullException.ThrowIfNull(resolutions);ArgumentNullException.ThrowIfNull(clock);
        Func<DeliveryInterval,(DateOnly,int,int)> group=aggregation switch
        {
            IntervalAggregation.Hour=>i=>(i.OperatingDate,i.HourBeginning,i.Occurrence),
            IntervalAggregation.Day=>i=>(i.OperatingDate,0,0),
            _=>throw new MarketDataException("interval_aggregate","Aggregate by hour or by day.")
        };
        return [..resolutions.OrderBy(r=>r.Start).GroupBy(r=>group(r.Interval)).Select(g=>
        {
            ImmutableArray<IntervalPriceResolution> constituents=[..g];
            var day=constituents[0].Interval.OperatingDate;
            var whole=TimeSpan.FromTicks(constituents.Sum(r=>(r.End-r.Start).Ticks))==(aggregation==IntervalAggregation.Hour?TimeSpan.FromHours(1)
                :clock.StartOfDay(day.AddDays(1))-clock.StartOfDay(day));
            if(!whole)throw new MarketDataException("interval_aggregate","Aggregate whole local hours or operating days; choose a window on their boundaries.");
            if(constituents.Any(r=>r.Price is null))return new IntervalAggregate(constituents[0].Start,constituents[^1].End,null,constituents);
            var unit=constituents[0].Price!.Unit;
            if(constituents.Any(r=>r.Price!.Unit!=unit))throw new MarketDataException("interval_aggregate","Aggregated prices must share one price unit.");
            var (numerator,denominator)=DurationWeights.Mean([..constituents.Select(r=>(r.End-r.Start,r.Price!.NumeratorValue,r.Price.DenominatorValue))]);
            return new IntervalAggregate(constituents[0].Start,constituents[^1].End,ExactUnitPrice.FromFraction(numerator,denominator,unit),constituents);
        })];
    }

    internal static Dictionary<DateTimeOffset,IntervalActual> ValidateInputs(IReadOnlyList<DeliveryInterval> intervals,PriceUnit unit,
        IReadOnlyList<IntervalActual> actuals,IntervalActualRule rule,DateTimeOffset knownAt)
    {
        if(rule is null || rule.Version!=IntervalActualRule.SupportedVersion || rule.Anchor is not ("interval_start" or "interval_end") ||
            rule.OffsetMinutes is <0 or >10080)
            throw Invalid("Choose the supported actual-due rule.");
        var byStart=IntervalAlignment.ByStart(actuals,a=>a.Start,a=>a.End,"interval_actual","Actual prices must be distinct UTC intervals.");
        if(actuals.Any(a=>a.KnownAt>knownAt))throw Invalid("Actual prices must be known by the cutoff.");
        if(actuals.Any(a=>a.Price.Unit!=unit))
            throw Invalid("Actual and forward prices must share the curve's exact price unit.");
        var covered=intervals.ToDictionary(i=>i.Start);
        foreach(var actual in actuals)
            if(covered.TryGetValue(actual.Start,out var interval)?interval.End!=actual.End:intervals.Any(i=>i.Start<actual.End && actual.Start<i.End))
                throw Invalid("Actual prices must lie exactly on the curve's intervals.");
        return byStart;
    }
    internal static bool Eligible(IntervalActual actual,DeliveryInterval interval,DateTimeOffset valuationAt)=>
        valuationAt>=interval.End || actual.KnownAt<=valuationAt;

    private static MarketDataException Invalid(string message)=>new("interval_actual",message);
}
