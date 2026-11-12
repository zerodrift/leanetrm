using System.Collections.Immutable;

namespace OpenEtrm.MarketData.Power;

public sealed record DeliveryHours(BlockMembership Membership,TouPeak? Peak,ImmutableArray<DateOnly> Holidays);
public sealed record UnitQuantityRow(DeliveryInterval Interval,ExactQuantity PerUnit,VolumeState State,ImmutableArray<NormalizedContribution> Contributions);
public interface IIntervalQuantitySource { ImmutableArray<UnitQuantityRow> Rows(DateOnly operatingMonth); }

public sealed class ContractProfileQuantity : IIntervalQuantitySource
{
    private readonly PowerClock clock;
    private readonly int minutes;
    private readonly DeliveryHours hours;
    private readonly QuantitySpecification capacity,energy;
    private readonly ImmutableArray<ExactDecimal> hourlyFactors;
    public ContractProfileQuantity(PowerClock clock,int intervalMinutes,DeliveryHours hours,QuantitySpecification capacity,QuantitySpecification energy,ImmutableArray<ExactDecimal> hourlyFactors=default)
    {
        if(clock is null || hours is null || capacity is null || energy is null || intervalMinutes is not(15 or 60) || hours.Holidays.IsDefault || !Enum.IsDefined(hours.Membership) ||
            hours.Membership==BlockMembership.All && hours.Peak is not null || hours.Membership!=BlockMembership.All && hours.Peak is null)
            throw new MarketDataException("ppa_term_unsupported","Select a native all-hours or explicit published peak/off-peak contract profile.");
        AssetDefinitions.Specification(capacity,energy.Key.Revision);AssetDefinitions.Energy(capacity,energy,energy.Key.Revision);
        if(capacity.Unit.Code!="megawatt" || capacity.Dimension.Code!="power")throw new MarketDataException("volume_source","Contract capacity must use the exact MW specification.");
        if(hours.Holidays.Distinct().Count()!=hours.Holidays.Length)throw new MarketDataException("power_holidays","Holiday dates must be unique.");
        ValidateHourlyFactors(hourlyFactors,hours,intervalMinutes);
        this.hourlyFactors=hourlyFactors.IsDefault?[]:hourlyFactors;
        this.clock=clock;minutes=intervalMinutes;this.hours=hours;this.capacity=capacity;this.energy=energy;
    }
    public const string HourlyProfile="local_hour_contractual_mw_factors_v1";
    public static void ValidateHourlyFactors(ImmutableArray<ExactDecimal> factors,DeliveryHours hours,int minutes)
    {
        if(factors.IsDefaultOrEmpty)return;
        if(factors.Length!=24||minutes!=60||hours.Membership!=BlockMembership.All||
            factors.Any(f=>f.CompareTo(default)<0)||!factors.Any(f=>f.CompareTo(default)>0))
            throw new MarketDataException("ppa_profile_shape","Select all-hours hourly delivery and 24 nonnegative local-hour MW factors with positive total volume.");
    }
    // The contract unit is one MW. Both repeated clock hours receive the same
    // declared local-hour factor; a missing spring hour creates no delivery.
    public static ExactQuantity PerUnit(DeliveryInterval interval,ReferenceKey energy,PowerClock clock,ImmutableArray<ExactDecimal> factors)
    {
        var quantity=new ExactQuantity((interval.End-interval.Start).Ticks,TimeSpan.TicksPerHour,energy);
        return factors.IsDefaultOrEmpty?quantity:quantity.Scale(factors[TimeZoneInfo.ConvertTime(interval.Start,clock.Zone).Hour]);
    }
    public ImmutableArray<UnitQuantityRow> Rows(DateOnly operatingMonth)
    {
        if(operatingMonth.Day!=1)throw new MarketDataException("volume_grid","Quantity sources resolve explicit whole operating months.");
        var mask=IntervalBlocks.Mask(hours.Membership,minutes,hours.Peak);
        var schedule=PowerQuantitySchedule.Compile(new(ExactDecimal.Parse("1"),capacity.Key),PowerQuantityBasis.FlatPower,capacity,energy,
            operatingMonth,operatingMonth.AddMonths(1),clock,mask,[..hours.Holidays.Where(d=>d>=operatingMonth && d<operatingMonth.AddMonths(1))],minutes,null);
        return [..schedule.Intervals.Select(p=>new UnitQuantityRow(p.Interval.Delivery,PerUnit(p.Interval.Delivery,energy.Key,clock,hourlyFactors),VolumeState.Contractual,[]))];
    }
}

public sealed class AssetVolumeQuantity(IReadOnlyList<NormalizedQuantity> rows) : IIntervalQuantitySource
{
    private readonly ImmutableArray<NormalizedQuantity> prepared=[..rows];
    public ImmutableArray<UnitQuantityRow> Rows(DateOnly operatingMonth)
    {
        if(operatingMonth.Day!=1)throw new MarketDataException("volume_grid","Quantity sources resolve explicit operating months.");
        return [..prepared.Where(r=>r.Interval.OperatingDate>=operatingMonth && r.Interval.OperatingDate<operatingMonth.AddMonths(1))
            .Select(r=>new UnitQuantityRow(r.Interval,r.PerUnit,r.State,r.Contributions))];
    }
}
