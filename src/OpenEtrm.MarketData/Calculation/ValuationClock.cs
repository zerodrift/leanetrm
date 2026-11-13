using OpenEtrm.MarketData.Power;
using OpenEtrm.MarketData.Trading;

namespace OpenEtrm.MarketData.Calculation;

public sealed record CalendarClockRoll(DateTimeOffset From,string Zone,string Rules,string Fingerprint,string Convention)
{
    public const string NextDay="same_local_time_next_calendar_day_v1";
}
public sealed record ClockChange(DateTimeOffset At,string Policy)
{
    public const string FrozenInputs="frozen_absolute_inputs_forward_clock_v1";
    public CalendarClockRoll? Calendar{get;init;}
    public static ClockChange NextCalendarDay(DateTimeOffset from,PowerClock clock)
    {
        if(!TradingInstant.IsSupported(from)||clock is null)throw new MarketDataException("scenario_clock","Supply a supported source instant and retained calendar clock.");
        var local=TimeZoneInfo.ConvertTime(from,clock.Zone);
        var at=clock.AtLocal(DateOnly.FromDateTime(local.DateTime).AddDays(1),TimeOnly.FromDateTime(local.DateTime)).ToUniversalTime();
        return new(at,FrozenInputs){Calendar=new(from,clock.Id,clock.Rules,clock.Fingerprint,CalendarClockRoll.NextDay)};
    }
    internal void Require(DateTimeOffset source,DateTimeOffset parent)
    {
        if(Policy!=FrozenInputs||!TradingInstant.IsSupported(At)||At<source)
            throw new MarketDataException("scenario_clock","Select a supported forward calculation instant and explicit frozen-input clock policy.");
        if(Calendar is not {} calendar)return;
        try
        {
            if(calendar.Rules is null||calendar.Rules.Length>65536||calendar.From!=parent||calendar.Convention!=CalendarClockRoll.NextDay)
                throw new ArgumentException();
            var clock=PowerClock.Capture(TimeZoneInfo.FromSerializedString(calendar.Rules));
            if(clock.Id!=calendar.Zone||clock.Fingerprint!=calendar.Fingerprint||NextCalendarDay(calendar.From,clock).At!=At)
                throw new ArgumentException();
        }
        catch(Exception e)when(e is ArgumentException or System.Runtime.Serialization.SerializationException)
        {throw new MarketDataException("scenario_clock","The calendar target must match its exact retained clock rules and source instant.",e);}
    }
}
