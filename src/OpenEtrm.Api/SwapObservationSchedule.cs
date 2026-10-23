using System.Collections.Immutable;
using System.Text.Json.Serialization;
using OpenEtrm.MarketData;
using OpenEtrm.MarketData.Trading;
using OpenEtrm.Shared.Temporal;

namespace OpenEtrm.Api;

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record SwapObservationQuantityInput(string From,string To,QuantityInput Quantity);
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record SwapObservationScheduleInput(string Implementation,
    [property:JsonRequired]ReferenceKey? Calendar,[property:JsonRequired]ImmutableArray<SwapObservationQuantityInput>? Quantities);

public sealed partial class SwapStore
{
    public const string ObservationScheduleImplementation="side_observation_schedule_v1";

    private static FloatingAveraging StoredAveraging(string value)=>value switch
    {
        "monthly_index_price"=>FloatingAveraging.MonthlyIndexPrice,
        "calendar_day_arithmetic"=>FloatingAveraging.CalendarDayArithmetic,
        _=>SwapObservationCompatibility.Parse("daily",value)
    };

    private static (SwapObservationCalendar? Calendar,ImmutableArray<FloatingObservation> Quantities) ObservationTerms(
        PreviewData data,SwapFloatingInput input,FloatingAveraging averaging)
    {
        var mode=SwapObservationCompatibility.Mode(averaging);
        var required=mode.RequiresObservationCalendar||mode.RequiresQuantityWeights;
        if(required!=(input.ObservationSchedule is not null))
            throw Error("swap_averaging","Supply the explicit observation schedule only for a calendar-based or quantity-weighted convention.");
        if(input.ObservationSchedule is not {} schedule)return(null,[]);
        if(schedule.Implementation!=ObservationScheduleImplementation||
            mode.RequiresObservationCalendar!=(schedule.Calendar is not null)||
            mode.RequiresQuantityWeights!=(schedule.Quantities is not null)||input.FirstFixingAt is not null)
            throw Error("swap_averaging","The observation schedule must match its explicit convention and implementation.");
        var calendar=schedule.Calendar is {} key?ObservationCalendar(data,key):null;
        if(schedule.Quantities is not {} entries)return(calendar,[]);
        if(entries.IsDefaultOrEmpty||entries.Length>FloatingSide.MaximumDeliveryDays||entries.Any(row=>row is null||row.Quantity is null))
            throw Error("swap_averaging","Supply one positive typed quantity for every required observation interval.");
        return(calendar,entries.Select(row=>new FloatingObservation(Date(row.From),Date(row.To),
            new Quantity(ExactDecimal.Parse(Required(row.Quantity.Value)),row.Quantity.Specification))).ToImmutableArray());
    }

    private static object? ObservationInspection(SidePaymentValue payment)
    {
        if(payment.Plan.Averaging is not {} averaging)return null;
        var mode=SwapObservationCompatibility.Mode(averaging);
        return new
        {
            implementation="side_observation_schedule_inspection_v1",mode.ResetFrequency,mode.AveragingMethod,averaging=averaging.ToString(),
            denominator=payment.ObservationWeightTotal.ToString(),deliveryFrom=Date(payment.Plan.DeliveryFrom),deliveryTo=Date(payment.Plan.DeliveryTo),
            mappingRule=payment.Plan.Applications[0].MappingRule,calendar=payment.Plan.Calendar?.Key,
            observations=payment.Resets.Select((reset,n)=>new
            {
                ordinal=n,from=Date(reset.Price.Term.Lookup!.Contract.DeliveryFrom),to=Date(reset.Price.Term.Lookup.Contract.DeliveryTo),
                isGuard=reset.Price.Term.Lookup.Contract.DeliveryFrom<payment.Plan.DeliveryFrom||reset.Price.Term.Lookup.Contract.DeliveryTo>payment.Plan.DeliveryTo,
                averagingQuantity=reset.Price.Term.Lookup.AveragingQuantity,weight=reset.Price.Term.Weight.ToString(),
                price=ExactPrice(reset.Price.ExactPrice),exactPrice=new{reset.Price.ExactPrice.Numerator,reset.Price.ExactPrice.Denominator},
                cashContribution=Exact(reset.CashContribution),fixingState=reset.Price.State.ToString(),
                expectedPublication=reset.Price.Term.Lookup.Fixing?.FixingAt,fixing=reset.Price.Term.Lookup.Fixing,lookup=reset.Price.Term.Lookup,lineage=reset.Price.Lineage
            }).ToArray(),
            applications=payment.Applications.Select(value=>new
            {
                paymentOrdinal=value.Application.Payment.Ordinal,targetFrom=Date(value.Application.TargetFrom),targetTo=Date(value.Application.TargetTo),
                observationOrdinal=value.Application.PriceOrdinal,weight=value.Application.Weight.ToString(),value.Application.MappingRule,
                price=ExactPrice(value.Price.ExactPrice),exactPrice=new{value.Price.ExactPrice.Numerator,value.Price.ExactPrice.Denominator},
                cashContribution=Exact(value.CashContribution),fixingState=value.Price.State.ToString(),
                expectedPublication=value.Price.Term.Lookup?.Fixing?.FixingAt,fixing=value.Price.Term.Lookup?.Fixing,lookup=value.Price.Term.Lookup,lineage=value.Price.Lineage
            }).ToArray()
        };
    }

    internal static SwapObservationCalendar ObservationCalendar(PreviewData data,ReferenceKey key)
    {
        var calendar=ReferenceCalendar(data,key);
        return new(calendar.Key,calendar.BusinessDays);
    }

    private static PaymentCalendar ReferenceCalendar(PreviewData data,ReferenceKey key)
    {
        if(!data.CalendarDaysRead)throw Error("swap_observation_calendar","Load the exact calendar exceptions before resolving observation dates.");
        if(key.Revision!=data.ReferenceRevision)throw Error("swap_averaging","The observation calendar must use the selected exact reference revision.");
        var row=One(data.References["calendars"].Where(c=>c.Text("code")==key.Code));
        var from=Date(row.Text("coverage_from"));var to=Date(row.Text("coverage_to"));
        if(to.DayNumber-from.DayNumber>PaymentRule.MaximumCalendarDays)
            throw Error("payment_calendar","The selected observation calendar exceeds supported coverage.");
        var exceptions=data.References["calendar_days"].Where(c=>c.Text("calendar_code")==key.Code)
            .ToDictionary(c=>Date(c.Text("business_date")),c=>c.GetProperty("is_business_day").GetBoolean());
        var mask=int.Parse(row.Text("weekend_mask"),System.Globalization.CultureInfo.InvariantCulture);
        return new(key,DateSet.Create(from,to,Enumerable.Range(from.DayNumber,to.DayNumber-from.DayNumber).Select(DateOnly.FromDayNumber)
            .Where(d=>exceptions.TryGetValue(d,out var open)?open:(mask&(1<<(((int)d.DayOfWeek+6)%7)))==0)));
    }
}
