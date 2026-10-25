using System.Collections.Immutable;
using System.Globalization;

namespace OpenEtrm.Shared.Temporal;

public sealed record LocalTimeCandidate(DateTimeOffset Instant,string Offset);

// User-entry conversion only. Contract clocks/calendars retain their own terms.
public static class UserTime
{
    // The explicit same-day browsing policy chooses the latest occurrence of the
    // day's final second and retains PostgreSQL microsecond precision.
    public static DateTimeOffset EndOfDay(DateOnly day,string? zone)=>
        Inspect(day.ToString("yyyy-MM-dd",CultureInfo.InvariantCulture)+"T23:59:59",zone)
            .Max(candidate=>candidate.Instant).AddTicks(9_999_990);

    public static ImmutableArray<LocalTimeCandidate> Inspect(string? local,string? zone)
    {
        if(!DateTime.TryParseExact(local,["yyyy-MM-dd'T'HH:mm","yyyy-MM-dd'T'HH:mm:ss"],CultureInfo.InvariantCulture,
            DateTimeStyles.None,out var time) || time.Year>=9999)
            throw new TemporalException("time_format","Enter a local date and time without an offset; select the display time zone separately.");
        TimeZoneInfo timezone;
        try{timezone=TimeZoneInfo.FindSystemTimeZoneById(zone??"");}
        catch(Exception e)when(e is TimeZoneNotFoundException or InvalidTimeZoneException or ArgumentException)
        {throw new TemporalException("time_zone","Choose an available time zone explicitly.");}
        if(timezone.IsInvalidTime(time))throw new TemporalException("time_nonexistent","That local time does not exist because the clock moved forward.");
        var offsets=timezone.IsAmbiguousTime(time)?timezone.GetAmbiguousTimeOffsets(time):[timezone.GetUtcOffset(time)];
        try{return offsets.Select(offset=>new LocalTimeCandidate(new DateTimeOffset(time,offset).ToUniversalTime(),
            (offset<TimeSpan.Zero?"-":"+")+offset.Duration().ToString(@"hh\:mm",CultureInfo.InvariantCulture))).OrderBy(c=>c.Instant).ToImmutableArray();}
        catch(ArgumentException){throw new TemporalException("time_format","This local time is outside the supported instant range.");}
    }
}
