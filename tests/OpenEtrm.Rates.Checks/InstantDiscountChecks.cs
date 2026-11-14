using System.Collections.Immutable;
using OpenEtrm.MarketData;

internal static class InstantDiscountChecks
{
    public static int Run()
    {
        var date=new DateOnly(2031,1,1);var midnight=new DateTimeOffset(date.ToDateTime(TimeOnly.MinValue),TimeSpan.Zero);
        var conventions=RateConventionSet.Parse("actual_360","simple","log_linear_discount","none","decimal_per_year");
        ImmutableArray<RatePillarRate> rates=[new(date.AddDays(1),ExactDecimal.Parse("0.36"))];
        RateInstantDiscount Value(DateTimeOffset at,DateTimeOffset payment,ImmutableArray<RatePillarRate>? selected=null)=>
            RateCurveMath.InstantDiscount(date,selected??rates,new(at,payment,RateDiscountTiming.Version),conventions);
        // The dated endpoint is round_18(1000/1001). Intraday factors use exact
        // rational UTC-day weights and one final half-even ratio rounding.
        Equal(Value(midnight.AddHours(6),midnight.AddHours(18)).Factor,"0.999500374718960779");
        Equal(Value(midnight,midnight.AddDays(1)).Factor,"0.999000999000999001");
        Equal(Value(midnight.AddHours(12),midnight.AddDays(1)).Factor,"0.999500249875062469");
        Equal(Value(midnight.AddHours(6),midnight.AddHours(18),[new(date.AddDays(1),ExactDecimal.Parse("-0.036"))]).Factor,"1.000050003750281271");
        Equal(Value(midnight.AddHours(6),midnight.AddHours(18),[new(date.AddDays(1),ExactDecimal.Parse("0"))]).Factor,"1");
        var result=Value(midnight.AddHours(6),midnight.AddHours(18));
        if(!result.UsedPillars.SequenceEqual([0])||!result.Nodes.Select(n=>n.Date).SequenceEqual([date,date.AddDays(1)]))
            throw new Exception("Exact-instant discount evidence must retain every consumed dated node and pillar.");
        Reject("discount_coverage",()=>Value(midnight.AddHours(6),midnight.AddDays(1).AddMicroseconds(1)));
        Reject("discount_context",()=>Value(midnight.AddHours(6),midnight.AddHours(6)));
        Reject("discount_context",()=>Value(midnight.AddHours(-1),midnight.AddHours(6)));
        Reject("discount_context",()=>Value(midnight.AddTicks(1),midnight.AddHours(6)));
        Reject("discount_timing",()=>RateCurveMath.InstantDiscount(date,rates,new(midnight,midnight.AddHours(1),"guess"),conventions));
        ImmutableArray<DateOnly> geometry=[date.AddDays(1),date.AddDays(2),date.AddDays(4),date.AddDays(8)];
        void Demand(DateOnly query,int[] expected,RateConventionSet? profile=null)
        {if(!RateCurveMath.RequiredPillars(date,geometry,query,profile??conventions).SequenceEqual(expected))throw new Exception("Incorrect native rate bracket selection.");}
        Demand(date,[]);Demand(date.AddDays(1),[0]);Demand(date.AddDays(2),[1]);Demand(date.AddDays(3),[1,2]);
        Demand(date.AddDays(9),[3],conventions with{Extrapolation="flat_zero"});
        Demand(date.AddDays(9),[2,3],conventions with{Extrapolation="flat_forward"});
        Reject("discount_coverage",()=>Demand(date.AddDays(9),[]));
        Console.WriteLine("PASS exact-instant discounts: explicit UTC-day factors, same-day carry, exact endpoints, negative rates, complete evidence and coverage boundaries");return 1;
    }
    static void Equal(ExactDecimal value,string expected)
    {if(value.ToString()!=expected)throw new Exception($"Expected {expected}, got {value}.");}
    static void Reject(string code,Action action)
    {try{action();}catch(MarketDataException e)when(e.Code==code){return;}throw new Exception("Expected "+code);}
}
