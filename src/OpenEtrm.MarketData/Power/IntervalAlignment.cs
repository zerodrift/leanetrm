namespace OpenEtrm.MarketData.Power;

public static class IntervalAlignment
{
    // Joins rows to periods on exact UTC boundaries. Rows must be distinct and periods must not
    // overlap; every period needs the row that starts and ends with it. Rows outside the periods are ignored.
    public static Dictionary<DateTimeOffset,T> Align<T>(IReadOnlyList<PowerInterval> periods,IReadOnlyList<T> rows,
        Func<T,DateTimeOffset> start,Func<T,DateTimeOffset> end,string code,string rowMessage,string coverageMessage)
    {
        var byStart=ByStart(rows,start,end,code,rowMessage);
        PowerInterval? previous=null;
        foreach(var period in periods.OrderBy(p=>p.Start))
        {
            if(period.Start.Offset!=TimeSpan.Zero || period.End.Offset!=TimeSpan.Zero || period.End<=period.Start ||
                previous is {} prior && prior.End>period.Start || !byStart.TryGetValue(period.Start,out var row) || end(row)!=period.End)
                throw new MarketDataException(code,coverageMessage);
            previous=period;
        }
        return byStart;
    }

    // Rows keyed by their exact start; each must be a distinct, positive UTC interval.
    public static Dictionary<DateTimeOffset,T> ByStart<T>(IReadOnlyList<T> rows,Func<T,DateTimeOffset> start,Func<T,DateTimeOffset> end,
        string code,string message)
    {
        var byStart=new Dictionary<DateTimeOffset,T>();
        foreach(var row in rows)
        {
            DateTimeOffset first=start(row),last=end(row);
            if(first.Offset!=TimeSpan.Zero || last.Offset!=TimeSpan.Zero || last<=first || !byStart.TryAdd(first,row))
                throw new MarketDataException(code,message);
        }
        return byStart;
    }
}
