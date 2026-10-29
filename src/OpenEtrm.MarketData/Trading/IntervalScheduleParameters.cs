using System.Collections.Immutable;
using OpenEtrm.MarketData.Power;

namespace OpenEtrm.MarketData.Trading;

public enum IntervalScheduleCadence { NativeIntervals = 1, OperatingMonths }
public sealed record IntervalScheduleMonth(DateOnly Month, int FirstRowOrdinal,
    ImmutableArray<DeliveryInterval> Intervals, SidePaymentKey Payment);

// Resolve the exact clock before constructing a side. Generation consumes this
// captured rule payload; it performs no reference lookup or market-data read.
[EconomicDefinition("interval_side_schedule_parameters_v1")]
public sealed record IntervalScheduleParameters
{
    [IdentityField] public PpaDelivery Delivery { get; }
    public string ClockRules => Clock.Rules;
    [IdentityField] public IntervalScheduleCadence Cadence { get; }
    [IdentityField] public DeliveryHours? Hours { get; }
    public PowerClock Clock { get; }
    private readonly Lazy<ImmutableArray<(DateOnly Month, ImmutableArray<DeliveryInterval> Intervals)>> grids;

    public IntervalScheduleParameters(PpaDelivery delivery, PowerClock clock, IntervalScheduleCadence cadence, DeliveryHours? hours)
    {
        if (delivery is null || clock is null || !Enum.IsDefined(cadence) ||
            delivery.IntervalMinutes is not (15 or 60) || delivery.From.Day != 1 || delivery.To.Day != 1 ||
            delivery.From >= delivery.To || delivery.From.Year < 2000 || delivery.To > new DateOnly(2100, 1, 1) ||
            (delivery.To.Year - delivery.From.Year) * 12 + delivery.To.Month - delivery.From.Month > (delivery.IntervalMinutes == 60 ? 360 : 120) ||
            cadence == IntervalScheduleCadence.OperatingMonths && hours is not null ||
            cadence == IntervalScheduleCadence.NativeIntervals && (hours is null || hours.Holidays.IsDefault ||
                !Enum.IsDefined(hours.Membership) || hours.Membership == BlockMembership.All && hours.Peak is not null ||
                hours.Membership != BlockMembership.All && hours.Peak is null || hours.Holidays.Distinct().Count() != hours.Holidays.Length))
            throw new MarketDataException("side_schedule_parameters", "Supply a complete term, captured exact clock, cadence and explicit native delivery mask.");
        Money.RequireKey(delivery.Clock);
        Delivery = delivery; Clock = clock; Cadence = cadence; Hours = hours;
        grids = new(BuildGrids);
    }

    private ImmutableArray<(DateOnly Month, ImmutableArray<DeliveryInterval> Intervals)> BuildGrids()
    {
        var result = ImmutableArray.CreateBuilder<(DateOnly, ImmutableArray<DeliveryInterval>)>();
        for (var month = Delivery.From; month < Delivery.To; month = month.AddMonths(1))
        {
            ImmutableArray<DeliveryInterval> intervals = Cadence == IntervalScheduleCadence.OperatingMonths
                ? [new(Clock.StartOfDay(month).ToUniversalTime(), Clock.StartOfDay(month.AddMonths(1)).ToUniversalTime(), month, 0, 0, 1)]
                : [..PowerHours.Compile(month, month.AddMonths(1), Clock,
                    IntervalBlocks.Mask(Hours!.Membership, Delivery.IntervalMinutes, Hours.Peak),
                    [..Hours.Holidays.Where(day => day >= month && day < month.AddMonths(1))], Delivery.IntervalMinutes).Select(i => i.Delivery)];
            result.Add((month, intervals));
        }
        return result.ToImmutable();
    }

    public ImmutableArray<IntervalScheduleMonth> Generate(Guid side, ReferenceKey currency, PaymentRule paymentRule)
    {
        if (side == Guid.Empty || paymentRule is null || currency.Revision != Delivery.Clock.Revision ||
            paymentRule.Calendar is { } calendar && calendar.Key.Revision != Delivery.Clock.Revision)
            throw new MarketDataException("side_schedule_parameters", "Schedule generation requires an exact side, currency and payment rule under its pinned reference revision.");
        Money.RequireKey(currency);
        var result = ImmutableArray.CreateBuilder<IntervalScheduleMonth>(); var ordinal = 0;
        foreach (var (month, intervals) in grids.Value)
        {
            var valueAt = FixedSide.PaymentBoundary(paymentRule.Generate(month, month.AddMonths(1)).Date);
            result.Add(new(month, ordinal, intervals, new(side, result.Count, valueAt, currency)));
            ordinal = checked(ordinal + intervals.Length);
        }
        return result.ToImmutable();
    }
}
