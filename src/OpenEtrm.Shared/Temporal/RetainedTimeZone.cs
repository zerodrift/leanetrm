using System.Runtime.Serialization;
using System.Text.RegularExpressions;

namespace OpenEtrm.Shared.Temporal;

public static class RetainedTimeZone
{
    public const int MaximumRulesLength=65_536;

    public static TimeZoneInfo Restore(string rules,string? expectedId=null)
    {
        if(string.IsNullOrWhiteSpace(rules)||rules.Length>MaximumRulesLength)
            throw new SerializationException("Supply bounded retained time-zone rules.");
        TimeZoneInfo? restored=null;
        try{restored=TimeZoneInfo.FromSerializedString(rules);}
        catch(Exception error)when(error is SerializationException or ArgumentException or InvalidTimeZoneException){}
        if(restored is not null)return Exact(restored,rules,expectedId);

        // Some IANA histories serialize with overlapping adjustment-rule dates
        // that the framework cannot deserialize. A name is only a lookup hint:
        // the entire retained payload must still match the loaded representation.
        var delimiter=rules.IndexOf(';');
        var id=expectedId??(delimiter is >0 and <=256?rules[..delimiter]:null);
        if(id is null||id.Length>256||!Regex.IsMatch(id,@"\A[A-Za-z0-9_+\-]+(/[A-Za-z0-9_+\-]+)*\z",RegexOptions.CultureInvariant))
            throw new SerializationException("The exact retained time-zone rules are unavailable.");
        try{return Exact(TimeZoneInfo.FindSystemTimeZoneById(id),rules,expectedId);}
        catch(Exception error)when(error is TimeZoneNotFoundException or InvalidTimeZoneException or ArgumentException)
        {throw new SerializationException("The exact retained time-zone rules are unavailable.",error);}
    }

    private static TimeZoneInfo Exact(TimeZoneInfo zone,string rules,string? expectedId)
    {
        if(expectedId is not null&&!StringComparer.Ordinal.Equals(zone.Id,expectedId)||
            !StringComparer.Ordinal.Equals(zone.ToSerializedString(),rules))
            throw new SerializationException("The restored time zone differs from its retained rules or identity.");
        return zone;
    }
}
