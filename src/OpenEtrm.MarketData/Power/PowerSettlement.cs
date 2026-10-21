using System.Globalization;
using System.Numerics;

namespace OpenEtrm.MarketData.Power;

public sealed record PowerIndex(ReferenceKey Definition,ReferenceKey Currency,ReferenceKey EnergyUnit,ReferenceKey EnergyQuantity,string PriceType,string Timing);
public sealed record LmpComponents(ExactDecimal Energy,ExactDecimal Congestion,ExactDecimal Loss);
public sealed record PowerFixing(DateTimeOffset Start,DateTimeOffset End,PowerIndex Index,ExactDecimal Total,LmpComponents? Components);
public sealed record PowerSettlementPolicy(string Implementation,int PriceScale,int AmountScale)
{
    public static PowerSettlementPolicy Preview { get; }=new("duration_weighted_power_quantity_to_even_v2",18,2);
    public void Validate()
    {
        if(this!=Preview)throw new MarketDataException("settlement_policy","Choose the supported explicit preview numerical policy.");
    }
}
public sealed record PowerSettlementResult(string Hours,string Energy,string AveragePrice,string Amount,
    string ExactAmountNumerator,string ExactAmountDenominator,PowerIndex Index,PowerSettlementPolicy Policy);
public sealed record PowerQuantitySettlement(PowerSettlementResult Summary,ExactQuantity Energy,Money Cash);

public static class PowerSettlement
{
    private static readonly BigInteger Scale=ExactDecimal.WorkingScale;
    private static string Rounded(BigInteger numerator,BigInteger denominator,int decimals)=>
        ExactDecimal.Parse(new AmountFormat(decimals,CashTieRule.ToEven,false,false).FormatFraction(numerator,denominator)).ToString();
    private static Dictionary<DateTimeOffset,PowerFixing> Validate(PowerInterval[] periods,PowerFixing[] rows,PowerIndex index)
    {
        if(index is null)throw new MarketDataException("settlement_index","An exact index specification is required.");
        Money.RequireKey(index.Definition);Money.RequireKey(index.Currency);Money.RequireKey(index.EnergyUnit);Money.RequireKey(index.EnergyQuantity);
        if(new[]{index.PriceType,index.Timing}.Any(string.IsNullOrWhiteSpace) ||
            index.EnergyUnit.Code!="megawatt_hour" || index.Definition.Revision!=index.Currency.Revision || index.Currency.Revision!=index.EnergyUnit.Revision || index.EnergyQuantity.Revision!=index.EnergyUnit.Revision)
            throw new MarketDataException("settlement_index","Every index specification and classification is required.");
        if(periods is null || rows is null || periods.Length==0 || periods.Length>110000 || rows.Length!=periods.Length || periods.Any(p=>p is null) || rows.Any(r=>r is null))
            throw new MarketDataException("settlement_coverage","Supply exactly one fixing per selected interval.");
        foreach(var row in rows)
            if(row.Index!=index)throw new MarketDataException("settlement_index","Fixings must share the exact selected index, units and classifications.");
        var result=IntervalAlignment.Align(periods,rows,r=>r.Start,r=>r.End,"settlement_coverage",
            "Fixing intervals must be distinct, ordered UTC boundaries.","Fixings must cover the selected UTC intervals without overlap.");
        foreach(var row in rows)
            if(row.Components is {} c && c.Energy+c.Congestion+c.Loss!=row.Total)
                throw new MarketDataException("lmp_components","LMP components must sum exactly to the total.");
        return result;
    }
    public static PowerQuantitySettlement Calculate(PowerQuantitySchedule schedule,PowerFixing[] rows,PowerIndex index,PowerSettlementPolicy policy)
    {
        if(schedule is null || policy is null)throw new MarketDataException("power_quantity","Supply a typed quantity schedule and explicit settlement policy.");
        return Calculate(schedule.Intervals.ToArray(),schedule.EnergyDefinition,rows,index,policy);
    }
    public static PowerQuantitySettlement Calculate(PowerEnergyInterval[] quantities,QuantitySpecification energyDefinition,
        PowerFixing[] rows,PowerIndex index,PowerSettlementPolicy policy)
    {
        if(policy is null || !PowerIntervalQuantities.IsEnergy(energyDefinition) || quantities is null ||
            quantities.Any(q=>q is null || q.Interval is null || q.Energy is null))
            throw new MarketDataException("power_quantity","Supply typed interval energy, its exact specification and an explicit settlement policy.");
        policy.Validate();
        var periods=quantities.Select(p=>p.Interval).ToArray();
        var fixings=Validate(periods,rows,index);
        if(index.EnergyUnit!=energyDefinition.Unit || index.EnergyQuantity!=energyDefinition.Key ||
            quantities.Any(q=>q.Energy.Specification!=energyDefinition.Key))
            throw new MarketDataException("settlement_index","The scheduled energy specification must match the selected index's exact commodity and measurement basis.");
        var one=ExactDecimal.Parse("1");BigInteger priceDuration=0,duration=0;
        var cash=ExactMoney.FromMoney(new(default,index.Currency));
        var total=new ExactQuantity(0,1,energyDefinition.Key);
        foreach(var p in quantities)
        {
            var price=new UnitPrice(fixings[p.Interval.Start].Total,new PriceUnit(index.Currency,energyDefinition.Key,one));
            cash=cash.Add(p.Energy.Times(price));
            total=total.Add(p.Energy);
            duration+=(p.Interval.End-p.Interval.Start).Ticks;
            priceDuration+=price.Value.ScaledValue*(p.Interval.End-p.Interval.Start).Ticks;
        }
        var rounded=cash.ToMoney(new(policy.AmountScale,CashTieRule.ToEven,false,false));
        var format=new AmountFormat(18,CashTieRule.ToEven,false,true);
        return new(new(format.FormatFraction(duration,TimeSpan.TicksPerHour),total.FormatValue(format),Rounded(priceDuration,Scale*duration,policy.PriceScale),rounded.Amount.ToString(),
            cash.Numerator,cash.Denominator,index,policy),total,rounded);
    }
    public static PowerSettlementResult Calculate(PowerInterval[] periods,PowerFixing[] rows,PowerIndex index,ExactDecimal flatMegawatts,PowerSettlementPolicy policy)
    {
        policy.Validate();var fixings=Validate(periods,rows,index);
        BigInteger duration=0,priceDuration=0;
        foreach(var p in periods)
        {
            var ticks=(p.End-p.Start).Ticks; duration+=ticks;priceDuration+=fixings[p.Start].Total.ScaledValue*ticks;
        }
        var hourTicks=new BigInteger(TimeSpan.TicksPerHour);
        var numerator=priceDuration*flatMegawatts.ScaledValue; var denominator=Scale*Scale*hourTicks;
        var gcd=BigInteger.GreatestCommonDivisor(numerator,denominator);
        return new(Rounded(duration,hourTicks,18),Rounded(duration*flatMegawatts.ScaledValue,Scale*hourTicks,18),
            Rounded(priceDuration,Scale*duration,policy.PriceScale),Rounded(numerator,denominator,policy.AmountScale),
            (numerator/gcd).ToString(CultureInfo.InvariantCulture),(denominator/gcd).ToString(CultureInfo.InvariantCulture),index,policy);
    }
    // Hour grouping uses actual UTC intervals, preserving the repeated local hour.
    // This is a diagnostic for an explicitly flat-within-hour quantity, not a
    // substitute for a contractually named published hourly index.
    public static Dictionary<DateTimeOffset,string> Hourly(PowerInterval[] periods,PowerFixing[] rows,PowerIndex index,PowerSettlementPolicy policy)
    {
        policy.Validate();var fixings=Validate(periods,rows,index);
        return periods.GroupBy(p=>new DateTimeOffset(p.Start.Year,p.Start.Month,p.Start.Day,p.Start.Hour,0,0,TimeSpan.Zero)).ToDictionary(g=>g.Key,g=>
        {
            if(g.Sum(p=>(p.End-p.Start).Ticks)!=TimeSpan.TicksPerHour ||g.Any(p=>p.Start<g.Key||p.End>g.Key.AddHours(1)))
                throw new MarketDataException("settlement_hour","Hourly diagnostics require full, aligned UTC hours.");
            BigInteger sum=0;foreach(var p in g)sum+=fixings[p.Start].Total.ScaledValue*(p.End-p.Start).Ticks;
            return Rounded(sum,Scale*TimeSpan.TicksPerHour,policy.PriceScale);
        });
    }
}
