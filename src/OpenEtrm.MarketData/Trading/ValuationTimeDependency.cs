namespace OpenEtrm.MarketData.Trading;

// A complete declaration: independent of calculation time, pinned to source
// lifecycle, or supported within one explicit event-free interval.
public sealed record ValuationTimeDependency
{
    public enum TimeUse { Independent=1, Pinned, Interval }
    public TimeUse Use { get; }
    public DateTimeOffset? From { get; }
    public DateTimeOffset? To { get; }
    [System.Text.Json.Serialization.JsonConstructor]
    public ValuationTimeDependency(TimeUse use,DateTimeOffset? from=null,DateTimeOffset? to=null)
    {
        if(!Enum.IsDefined(use)||use==TimeUse.Interval&&(from is null||to is null||!TradingInstant.IsSupported(from.Value)||!TradingInstant.IsSupported(to.Value)||from>=to)||
            use!=TimeUse.Interval&&(from is not null||to is not null))
            throw new MarketDataException("valuation_time_coverage","Supply a complete supported time-dependency contract.");
        Use=use;From=from;To=to;
    }
    public static ValuationTimeDependency Independent { get; }=new(TimeUse.Independent);
    public static ValuationTimeDependency Pinned { get; }=new(TimeUse.Pinned);
    public static ValuationTimeDependency Between(DateTimeOffset from,DateTimeOffset to)
    {
        if(!TradingInstant.IsSupported(from)||!TradingInstant.IsSupported(to)||from>=to)
            throw new MarketDataException("valuation_time_coverage","Supply an ordered supported event-free calculation interval.");
        return new(TimeUse.Interval,from,to);
    }
    internal void Require(DateTimeOffset source,DateTimeOffset target)
    {
        if(Use==TimeUse.Interval&&(source<From||source>=To||target<From||target>=To))
            throw new MarketDataException("valuation_time_coverage","The source and calculation clocks must be within the declared event-free valuation interval.");
        if(target==source)return;
        if(Use==TimeUse.Pinned)throw new MarketDataException("valuation_time_unsupported","The selected valuation contract requires its source lifecycle instant.");
    }
}
