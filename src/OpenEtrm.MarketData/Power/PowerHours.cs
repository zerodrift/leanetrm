using System.Text.RegularExpressions;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;

namespace OpenEtrm.MarketData.Power;

// Captures installed rules once for an unsaved preview. Durable publication and
// historical rule selection must retain this payload, not resolve its name again.
public sealed class PowerClock
{
    internal TimeZoneInfo Zone { get; }
    public string Id => Zone.Id;
    public string Rules { get; }
    public string Fingerprint { get; }
    private PowerClock(TimeZoneInfo zone)
    {
        Zone=zone; Rules=zone.ToSerializedString();
        Fingerprint=Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(Rules))).ToLowerInvariant();
    }
    public DateTimeOffset StartOfDay(DateOnly date)
    {
        var local=date.ToDateTime(TimeOnly.MinValue,DateTimeKind.Unspecified);
        if(Zone.IsInvalidTime(local)||Zone.IsAmbiguousTime(local))throw new MarketDataException("clock_rule","The selected day has no unambiguous midnight in the pinned clock.");
        return new(local,Zone.GetUtcOffset(local));
    }
    public static PowerClock Capture(TimeZoneInfo zone) => new(zone ?? throw new ArgumentNullException(nameof(zone)));
    // A reference clock rule: an exact fixed offset .NET can represent, or an installed IANA
    // zone file whose tzdb release equals the declared one before and after loading it.
    public static PowerClock FromRule(ClockRule rule,TimeZoneSource source)
    {
        switch(rule.Kind)
        {
            case "fixed_offset" when rule.FixedOffsetSeconds is int seconds && seconds%60==0 && Math.Abs(seconds)<=14*3600 && rule.IanaZone is null && rule.TzdbVersion is null:
                return Capture(TimeZoneInfo.CreateCustomTimeZone(rule.Code,TimeSpan.FromSeconds(seconds),rule.Code,rule.Code));
            case "iana" when rule.FixedOffsetSeconds is null && rule.IanaZone is {} zone && !string.IsNullOrWhiteSpace(rule.TzdbVersion):
                if(!Regex.IsMatch(zone,@"\A[A-Za-z0-9_+\-]+(/[A-Za-z0-9_+\-]+)*\z",RegexOptions.CultureInvariant) || !source.HasZoneFile(zone))
                    throw new MarketDataException("clock_rule",$"Clock rule {rule.Code} does not name an installed IANA zone.");
                var before=source.Version();
                if(before!=rule.TzdbVersion)
                    throw new MarketDataException("clock_tzdb",$"Clock rule {rule.Code} declares tzdb {rule.TzdbVersion}; the installed rules are {before}.");
                // Drop cached zones so the loaded rules come from the release just verified.
                TimeZoneInfo.ClearCachedData();
                PowerClock clock;
                try { clock=Capture(TimeZoneInfo.FindSystemTimeZoneById(zone)); }
                catch(Exception e) when(e is TimeZoneNotFoundException or InvalidTimeZoneException) { throw new MarketDataException("clock_rule",$"Clock rule {rule.Code} names an unreadable IANA zone."); }
                if(source.Version()!=before)
                    throw new MarketDataException("clock_tzdb","The installed time-zone database changed while loading the clock; try again.");
                return clock;
            default:
                throw new MarketDataException("clock_rule",$"Clock rule {rule.Code} is not a complete fixed-offset or IANA rule.");
        }
    }
}

public sealed record ClockRule(string Code,string Kind,int? FixedOffsetSeconds,string? IanaZone,string? TzdbVersion);

public sealed record PowerInterval(DateTimeOffset Start, DateTimeOffset End, DateOnly OperatingDate,
    int HourBeginning, int HourEnding, int Minute, int OffsetMinutes, int Occurrence, bool Holiday);

public static class PowerHours
{
    public static PowerInterval[] Compile(DateOnly from,DateOnly to,PowerClock clock,TimeOfUse mask,
        DateOnly[] holidays,int intervalMinutes)
    {
        if(to<=from || to.DayNumber-from.DayNumber>366 || from.Year<2000 || to.Year>2100)
            throw new MarketDataException("power_period","Choose a period of 1–366 days within 2000–2099.");
        if(intervalMinutes is not (5 or 15 or 30 or 60) || mask.Minutes%intervalMinutes!=0)
            throw new MarketDataException("tou_resolution","The interval must divide each mask cell exactly.");
        if(holidays is null || holidays.Length>366 || holidays.Distinct().Count()!=holidays.Length)
            throw new MarketDataException("power_holidays","Supply an explicit list of distinct holiday dates.");
        var zone=clock.Zone;
        DateTimeOffset Midnight(DateOnly day)
        {
            var local=day.ToDateTime(TimeOnly.MinValue,DateTimeKind.Unspecified);
            if(zone.IsAmbiguousTime(local)||zone.IsInvalidTime(local))
                throw new MarketDataException("power_clock","Ambiguous or missing midnight needs an explicit boundary.");
            return new DateTimeOffset(TimeZoneInfo.ConvertTimeToUtc(local,zone),TimeSpan.Zero);
        }
        var start=Midnight(from); var end=Midnight(to); var step=TimeSpan.FromMinutes(intervalMinutes);
        if((end-start).Ticks%step.Ticks!=0)
            throw new MarketDataException("power_clock","Clock transitions must align with the selected intervals.");
        var closed=holidays.ToHashSet(); var intervals=new List<PowerInterval>();
        var occurrences=new Dictionary<(DateOnly,int,int),int>();
        for(var at=start;at<end;at+=step)
        {
            var local=TimeZoneInfo.ConvertTime(at,zone);
            var last=TimeZoneInfo.ConvertTime(at+step-TimeSpan.FromTicks(1),zone);
            // Reject transitions cutting through a cell; never stretch a local label.
            if(local.Offset!=last.Offset || local.Minute%intervalMinutes!=0 || local.Second!=0)
                throw new MarketDataException("power_clock","A clock transition cuts through a selected interval.");
            var day=DateOnly.FromDateTime(local.DateTime);
            var key=(day,local.Hour,local.Minute);
            var occurrence=occurrences.GetValueOrDefault(key)+1;occurrences[key]=occurrence;
            var holiday=closed.Contains(day);
            if(mask.Contains(local.DateTime,holiday))
                intervals.Add(new(at,at+step,day,local.Hour,local.Hour+1,local.Minute,(int)local.Offset.TotalMinutes,occurrence,holiday));
        }
        return intervals.ToArray();
    }

    public static string Label(PowerInterval interval,bool hourEnding) =>
        $"{interval.OperatingDate.ToString("yyyy-MM-dd",CultureInfo.InvariantCulture)} · {(hourEnding?"HE":"HB")}{(hourEnding?interval.HourEnding:interval.HourBeginning):D2}"+
        (interval.Minute==0?"":$" +{interval.Minute:D2}m")+
        (interval.Occurrence>1?" · repeat":"");
}
