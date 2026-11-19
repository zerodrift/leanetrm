using System.Collections.Immutable;
using OpenEtrm.MarketData.Trading;
using OpenEtrm.Shared.Commands;
using OpenEtrm.Shared.Temporal;

namespace OpenEtrm.MarketData.Power.Structured;

// The holder schedules a delivery day at a local cutoff a whole number of
// business days before it. Days after a non-business day share one cutoff.
public sealed record HeatRateCallExerciseRule(int LeadBusinessDays, TimeOnly LocalCutoff, PaymentCalendar BusinessDays, string Implementation)
{
    public const string BusinessDayLead = "business_day_lead_local_cutoff_v1";
    public const int MaximumLead = 10;
}
public sealed record HeatRateCallDelivery(DateOnly From, DateOnly To, PowerClock Clock, int IntervalMinutes, DeliveryHours Hours,
    QuantitySpecification Capacity, QuantitySpecification Energy);
public sealed record HeatRateCallCurve(CurveKey Curve, ImmutableArray<MarketContract> Contracts);

// Expands a booking's delivery period into the exact exercises the Side retains.
// Every date here is derived once, at booking; valuation never regenerates it.
public static class HeatRateCallSchedule
{
    private static readonly Guid BucketScope = Guid.Parse("6d0f5f0e-7d0a-4a55-9d7c-2c1b7c0a43e1");
    public static Guid MonthBucket(DateOnly month) =>
        OperationIdentity.For("heat_rate_call_month_bucket_v1", BucketScope, month.Year * 100 + month.Month);

    public static ImmutableArray<HeatRateCallExercise> Expand(HeatRateCallDelivery delivery, HeatRateCallExerciseRule exercise,
        PaymentRule payment, HeatRateCallCurve power, HeatRateCallCurve fuel)
    {
        if (delivery is null || exercise is null || payment is null || power is null || fuel is null || delivery.From >= delivery.To ||
            delivery.To.DayNumber - delivery.From.DayNumber > HeatRateCallTerms.MaximumExercises ||
            exercise.Implementation != HeatRateCallExerciseRule.BusinessDayLead || exercise.BusinessDays is null ||
            exercise.LeadBusinessDays is < 1 or > HeatRateCallExerciseRule.MaximumLead ||
            power.Contracts.IsDefaultOrEmpty || fuel.Contracts.IsDefaultOrEmpty)
            throw Error("Supply an ordered delivery period, a business-day exercise lead of at least one day with its calendar, " +
                "a payment rule and the dated contracts of both curves.");
        var quantity = new ContractProfileQuantity(delivery.Clock, delivery.IntervalMinutes, delivery.Hours, delivery.Capacity, delivery.Energy);
        var rows = ImmutableArray.CreateBuilder<HeatRateCallExercise>();
        for (var month = new DateOnly(delivery.From.Year, delivery.From.Month, 1); month < delivery.To; month = month.AddMonths(1))
        {
            DateTimeOffset? paymentAt = null;
            foreach (var day in quantity.Rows(month).Where(r => r.Interval.OperatingDate >= delivery.From && r.Interval.OperatingDate < delivery.To)
                         .GroupBy(r => r.Interval.OperatingDate).OrderBy(g => g.Key))
            {
                var energy = day.Select(r => r.PerUnit).Aggregate((a, b) => a.Add(b)).ToQuantity().Amount;
                paymentAt ??= FixedSide.PaymentBoundary(payment.Generate(month, month.AddMonths(1)).Date);
                var exerciseAt = delivery.Clock.AtLocal(PriorBusinessDay(day.Key, exercise), exercise.LocalCutoff).ToUniversalTime();
                if (paymentAt < exerciseAt) throw Error("A day's payment cannot precede its exercise.");
                rows.Add(new(day.Key, exerciseAt, paymentAt.Value, energy, Request(power, day.Key), Request(fuel, day.Key), MonthBucket(month)));
            }
        }
        if (rows.Count == 0) throw Error("The delivery period contains no hour of the selected block.");
        return rows.ToImmutable();
    }

    private static DateOnly PriorBusinessDay(DateOnly delivery, HeatRateCallExerciseRule rule)
    {
        try
        {
            var day = delivery;
            for (var remaining = rule.LeadBusinessDays; remaining > 0;)
                if (rule.BusinessDays.BusinessDays.Contains(day = day.AddDays(-1))) remaining--;
            return day;
        }
        catch (TemporalException) { throw Error("The exercise calendar does not cover every delivery day's lead."); }
    }

    private static CurveRequest Request(HeatRateCallCurve curve, DateOnly day)
    {
        var covering = curve.Contracts.Where(c => c is not null && c.Interval is null && c.DeliveryFrom <= day && day < c.DeliveryTo).ToArray();
        return covering.Length == 1 ? new(curve.Curve, covering[0].Key) :
            throw Error($"Curve {curve.Curve.Code} needs exactly one dated contract delivering {day:yyyy-MM-dd}.");
    }

    private static MarketDataException Error(string message) => new("heat_rate_call_schedule", message);
}
