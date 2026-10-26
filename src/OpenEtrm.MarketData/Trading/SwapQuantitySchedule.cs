using System.Collections.Immutable;
using OpenEtrm.MarketData.Power;
using OpenEtrm.Shared.Numerics;

namespace OpenEtrm.MarketData.Trading;

public enum SwapQuantityBasis { PeriodTotal=1, CalendarDayRate, ContractTotal, SelectedIntervalPower }
public enum SwapQuantityAllocation { EqualPeriods=1, CalendarDays }
public sealed record QuantityPeriod(DateOnly From,DateOnly To);

public sealed record SwapQuantitySchedule
{
    public const string Implementation="explicit_swap_quantities_v1";
    public ImmutableArray<ExactQuantity> Periods { get; }
    public ExactQuantity Total { get; }
    public ImmutableArray<ExactRatio> Shares { get; }
    private SwapQuantitySchedule(ImmutableArray<ExactQuantity> periods)
    {
        if(periods.IsDefaultOrEmpty||periods.Any(q=>q.NumeratorValue.Sign<=0))throw Invalid();
        Periods=periods;Total=periods.Skip(1).Aggregate(periods[0],(a,b)=>a.Add(b));
        Shares=periods.Select(q=>Ratio(q,Total)).ToImmutableArray();
    }
    public static ExactRatio Ratio(ExactQuantity numerator,ExactQuantity denominator)
    {
        if(numerator.Specification!=denominator.Specification||numerator.NumeratorValue.Sign<=0||denominator.NumeratorValue.Sign<=0)throw Invalid();
        return new(numerator.NumeratorValue*denominator.DenominatorValue,numerator.DenominatorValue*denominator.NumeratorValue);
    }
    public SwapQuantitySchedule ConvertTo(ReferenceKey target,UnitConverter converter)=>new(Periods.Select(q=>converter.Convert(q,target)).ToImmutableArray());
    public SwapQuantitySchedule Times(QuantityFactor factor,ReferenceKey target,UnitConverter converter)=>
        new(Periods.Select(q=>q.Times(factor,target,converter)).ToImmutableArray());
    public static SwapQuantitySchedule Resolve(ImmutableArray<QuantityPeriod> periods,ImmutableArray<ExactDecimal> amounts,
        SwapQuantityBasis basis,SwapQuantityAllocation? allocation,ReferenceKey specification,ImmutableArray<PowerQuantitySchedule> power=default)
    {
        ValidateAuthoring(periods,amounts,basis,allocation);
        if(basis==SwapQuantityBasis.SelectedIntervalPower)
        {
            if(power.IsDefault||power.Length!=periods.Length||power.Where((p,n)=>p is null||p.Basis!=PowerQuantityBasis.FlatPower||p.From!=periods[n].From||p.To!=periods[n].To||p.Input.Amount!=amounts[n]||p.Total.Specification!=specification).Any())throw Invalid();
            return new(power.Select(p=>p.Total).ToImmutableArray());
        }
        if(!power.IsDefaultOrEmpty)throw Invalid();
        var totalDays=periods.Sum(p=>p.To.DayNumber-p.From.DayNumber);
        return new(periods.Select((p,n)=>{
            var quantity=ExactQuantity.FromQuantity(new(amounts[basis==SwapQuantityBasis.ContractTotal?0:n],specification));
            return basis switch {
                SwapQuantityBasis.PeriodTotal=>quantity,
                SwapQuantityBasis.CalendarDayRate=>quantity.Scale(ExactDecimal.Parse((p.To.DayNumber-p.From.DayNumber).ToString(System.Globalization.CultureInfo.InvariantCulture))),
                SwapQuantityBasis.ContractTotal=>quantity.Scale(allocation==SwapQuantityAllocation.EqualPeriods?new ExactRatio(1,periods.Length):new ExactRatio(p.To.DayNumber-p.From.DayNumber,totalDays)),
                _=>throw Invalid()
            };
        }).ToImmutableArray());
    }
    public static void ValidateAuthoring(ImmutableArray<QuantityPeriod> periods,ImmutableArray<ExactDecimal> amounts,SwapQuantityBasis basis,SwapQuantityAllocation? allocation)
    {
        if(periods.IsDefaultOrEmpty||periods.Length>MonthlySwapSide.MaximumMonths||periods.Where((p,n)=>p is null||p.From.Day!=1||p.From.Year is <1901 or >2198||p.From.AddMonths(1)!=p.To||n>0&&periods[n-1].To!=p.From).Any()||
            !Enum.IsDefined(basis)||amounts.IsDefaultOrEmpty||amounts.Any(a=>a.CompareTo(default)<=0)||
            (basis==SwapQuantityBasis.ContractTotal?(amounts.Length!=1||allocation is null||!Enum.IsDefined(allocation.Value)):(amounts.Length!=periods.Length||allocation is not null)))throw Invalid();
    }
    private static MarketDataException Invalid()=>new("scheduled_quantity","Supply positive explicit period totals, calendar-day rates, an explicitly allocated contract total, or complete integrated power schedules. No quantity or allocation is inferred.");
}
