using System.Collections.Immutable;
using System.Text.Json.Serialization;
using OpenEtrm.MarketData;
using OpenEtrm.MarketData.Trading;
using OpenEtrm.Shared.Temporal;

namespace OpenEtrm.Api;

// Supported algorithms, not reference data. Calendars and their exceptions come
// from the selected reference revision; rule parameters are retained on each side.
public sealed record PaymentChoice<T>([property:JsonIgnore] T Policy,string Value,string Label) where T:struct,Enum;
public static class SwapPaymentOptions
{
    public static readonly ImmutableArray<PaymentChoice<PaymentFrequency>> Frequencies=[new(PaymentFrequency.Monthly,"monthly","Monthly")];
    public static readonly ImmutableArray<PaymentChoice<PaymentDateRule>> Rules=[
        new(PaymentDateRule.DaysAfterPeriodEnd,"days_after_period_end","Days after last delivery day"),
        new(PaymentDateRule.DayOfFollowingMonth,"day_of_following_month","Day of the following month")];
    public static readonly ImmutableArray<PaymentChoice<PaymentDayBasis>> DayBases=[
        new(PaymentDayBasis.CalendarDays,"calendar_days","Calendar days"),new(PaymentDayBasis.BusinessDays,"business_days","Business days")];
    public static readonly ImmutableArray<PaymentChoice<ShortMonth>> ShortMonths=[
        new(ShortMonth.Reject,"reject","Reject a missing day"),new(ShortMonth.Clamp,"clamp","Use last day of month")];
    public static readonly ImmutableArray<PaymentChoice<DateRoll>> Adjustments=[
        new(DateRoll.Unadjusted,"unadjusted","Unadjusted"),new(DateRoll.Following,"following","Following"),
        new(DateRoll.Preceding,"preceding","Preceding"),new(DateRoll.ModifiedFollowing,"modified_following","Modified following")];
    public static T Parse<T>(ImmutableArray<PaymentChoice<T>> choices,string? value) where T:struct,Enum =>
        choices.FirstOrDefault(c=>c.Value==value)?.Policy ?? throw new MarketDataException("payment_rule","Select an explicitly supported payment convention.");
    public static object EntryTemplate=>new{version="monthly_18_calendar_days_v1",firstCoefficient="-1",spread="0",
        description="Starting terms: pay the first side, receive the second; zero floating spreads; monthly payments 18 calendar days after the last delivery day, without date adjustment. Review and edit each side.",
        payment=new SwapPaymentInput("monthly","days_after_period_end",18,null,"calendar_days",null,"unadjusted",null,PaymentRule.Implementation)};
    public static object Catalog=>new{frequencies=Frequencies,rules=Rules,dayBases=DayBases,shortMonths=ShortMonths,adjustments=Adjustments,implementation=PaymentRule.Implementation};
}
