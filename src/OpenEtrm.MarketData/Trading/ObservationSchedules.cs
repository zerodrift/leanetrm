using System.Collections.Immutable;
using System.Numerics;

namespace OpenEtrm.MarketData.Trading;

public sealed record FloatingObservationApplication(DateOnly TargetFrom,DateOnly TargetTo,
    int ObservationOrdinal,ExactDecimal Weight,string MappingRule);

// Select observations and retain their original applications before resolving
// prices. The same plan serves side generation and authorized store resolution.
public sealed class FloatingObservationPlan
{
    public ImmutableArray<FloatingObservation> Observations { get; }
    public ImmutableArray<FloatingObservationApplication> Applications { get; }
    public ImmutableArray<ExactDecimal> Weights { get; }
    public string MappingRule { get; }
    private FloatingObservationPlan(ImmutableArray<FloatingObservation> observations,
        ImmutableArray<FloatingObservationApplication> applications,string mappingRule)
    {
        Observations=observations;Applications=applications;MappingRule=mappingRule;
        Weights=observations.Select((_,n)=>ExactDecimal.FromScaled(applications.Where(a=>a.ObservationOrdinal==n)
            .Aggregate(BigInteger.Zero,(sum,a)=>sum+a.Weight.ScaledValue))).ToImmutableArray();
    }
    // Structural entry requirements are not a valid weighted plan. In particular,
    // quantity mode supplies no weights until every typed quantity is entered.
    public static ImmutableArray<FloatingObservation> RequiredObservations(DateOnly from,DateOnly to,
        FloatingAveraging averaging,QuoteSpecification quote,SwapObservationCalendar? calendar)=>
        Generate(from,to,averaging==FloatingAveraging.QuantityWeighted?FloatingAveraging.CalendarDayArithmetic:averaging,
            quote,calendar,[]).Observations;
    public static FloatingObservationPlan Generate(DateOnly from,DateOnly to,FloatingAveraging averaging,
        QuoteSpecification quote,SwapObservationCalendar? calendar,ImmutableArray<FloatingObservation> quantities)
    {
        if(quote is null||from>=to||to.DayNumber-from.DayNumber>FloatingSide.MaximumDeliveryDays||
            !Enum.IsDefined(averaging)||quantities.IsDefault)throw Invalid();
        var one=ExactDecimal.Parse("1");
        var calendarRequired=averaging is FloatingAveraging.ObservationArithmetic or FloatingAveraging.CalendarDaysPrevious or FloatingAveraging.CalendarDaysNext;
        if(calendarRequired!=(calendar is not null)||averaging!=FloatingAveraging.QuantityWeighted&&!quantities.IsEmpty)throw Invalid();
        var rule=averaging switch{
            FloatingAveraging.MonthlyIndexPrice=>"monthly_index_price_v1",
            FloatingAveraging.CalendarDayArithmetic=>"calendar_day_arithmetic_v1",
            FloatingAveraging.ObservationArithmetic=>"observation_arithmetic_v1",
            FloatingAveraging.CalendarDaysPrevious=>"calendar_days_previous_v1",
            FloatingAveraging.CalendarDaysNext=>"calendar_days_next_v1",
            FloatingAveraging.QuantityWeighted=>"positive_quantity_weighted_v1",
            _=>throw Invalid()};
        if(averaging==FloatingAveraging.MonthlyIndexPrice)
        {
            if(from.Day!=1||from.Year==9999||from.AddMonths(1)!=to)throw Invalid();
            return new([new(from,to,null)],[new(from,to,0,one,rule)],rule);
        }
        var days=Enumerable.Range(from.DayNumber,to.DayNumber-from.DayNumber).Select(DateOnly.FromDayNumber).ToArray();
        if(days.Any(d=>d.Year==9999))throw Invalid();
        if(averaging==FloatingAveraging.QuantityWeighted)
        {
            if(quantities.Length!=days.Length||quantities.Where((o,n)=>o is null||o.From!=days[n]||o.To!=days[n].AddDays(1)||
                o.AveragingQuantity is not {} q||q.Specification!=quote.Quantity||q.Amount.CompareTo(default)<=0).Any())throw Invalid();
            return new(quantities,quantities.Select((o,n)=>new FloatingObservationApplication(o.From,o.To,n,o.AveragingQuantity!.Amount,rule)).ToImmutableArray(),rule);
        }
        if(!calendarRequired)
            return new(days.Select(d=>new FloatingObservation(d,d.AddDays(1),null)).ToImmutableArray(),
                days.Select((d,n)=>new FloatingObservationApplication(d,d.AddDays(1),n,one,rule)).ToImmutableArray(),rule);
        if(calendar!.BusinessDays is null||calendar.Key.Revision!=quote.Key.Revision||string.IsNullOrWhiteSpace(calendar.Key.Code)||
            calendar.BusinessDays.From>from||calendar.BusinessDays.To<to)throw Invalid();
        var observed=calendar.BusinessDays;
        int LowerBound(DateOnly date)
        {
            var low=0;var high=observed.Count;
            while(low<high){var middle=low+(high-low)/2;if(observed[middle]<date)low=middle+1;else high=middle;}
            return low;
        }
        var selected=new List<(DateOnly Target,DateOnly Observation)>();
        if(averaging==FloatingAveraging.ObservationArithmetic)
        {
            for(var n=LowerBound(from);n<observed.Count&&observed[n]<to;n++)selected.Add((observed[n],observed[n]));
        }
        else foreach(var day in days)
        {
            var n=LowerBound(day);
            if(averaging==FloatingAveraging.CalendarDaysPrevious&&(n==observed.Count||observed[n]!=day))n--;
            if(n<0||n>=observed.Count)throw Invalid();
            selected.Add((day,observed[n]));
        }
        if(selected.Count==0||selected.Any(p=>p.Observation.Year==9999))throw Invalid();
        var observations=selected.Select(p=>p.Observation).Distinct().Order().Select(d=>new FloatingObservation(d,d.AddDays(1),null)).ToImmutableArray();
        var ordinals=observations.Select((o,n)=>(o.From,n)).ToDictionary(p=>p.From,p=>p.n);
        return new(observations,selected.Select(p=>new FloatingObservationApplication(p.Target,p.Target.AddDays(1),ordinals[p.Observation],one,rule)).ToImmutableArray(),rule);
    }
    private static MarketDataException Invalid()=>new("swap_averaging",
        "Select complete ordered observations, an explicit covered calendar where required, and compatible positive quantities for the chosen averaging rule.");
}
