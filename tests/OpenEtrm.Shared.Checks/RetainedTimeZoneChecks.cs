using System.Runtime.Serialization;
using OpenEtrm.Shared.Temporal;

internal static class RetainedTimeZoneChecks
{
    internal static void Run()
    {
        void Equal<T>(T actual,T expected)where T:notnull
        {if(!actual.Equals(expected))throw new Exception($"Expected {expected}; got {actual}.");}
        void Reject(Action action)
        {try{action();}catch(SerializationException){return;}throw new Exception("Expected retained-clock refusal.");}
        var ny=TimeZoneInfo.FindSystemTimeZoneById("America/New_York");
        var rules=ny.ToSerializedString();
        var restored=RetainedTimeZone.Restore(rules,ny.Id);
        Equal(restored.ToSerializedString(),rules);
        Equal(TimeZoneInfo.ConvertTime(new DateTimeOffset(2020,12,18,21,0,0,TimeSpan.Zero),restored),
            new DateTimeOffset(2020,12,18,16,0,0,TimeSpan.FromHours(-5)));
        Equal(restored.GetUtcOffset(new DateTimeOffset(2026,3,8,20,0,0,TimeSpan.Zero)),TimeSpan.FromHours(-4));
        Equal(restored.GetUtcOffset(new DateTimeOffset(2026,11,1,21,0,0,TimeSpan.Zero)),TimeSpan.FromHours(-5));
        Equal(RetainedTimeZone.Restore(rules).ToSerializedString(),rules);
        Reject(()=>RetainedTimeZone.Restore(rules,"UTC"));
        Reject(()=>RetainedTimeZone.Restore(rules+"invalid",ny.Id));
        Reject(()=>RetainedTimeZone.Restore("Missing/Zone"+rules[rules.IndexOf(';')..]+"invalid"));
        Reject(()=>RetainedTimeZone.Restore(new string('x',RetainedTimeZone.MaximumRulesLength+1)));
        Reject(()=>RetainedTimeZone.Restore("invalid"));
        var custom=TimeZoneInfo.CreateCustomTimeZone("Fixed;Zone\\A",TimeSpan.FromMinutes(345),"Fixed zone","Fixed zone");
        var customRules=custom.ToSerializedString();
        Equal(RetainedTimeZone.Restore(customRules,custom.Id).ToSerializedString(),customRules);
        Equal(RetainedTimeZone.Restore(customRules).GetUtcOffset(DateTimeOffset.UnixEpoch),TimeSpan.FromMinutes(345));
        Console.WriteLine("Retained clocks preserve exact IANA/custom rules, DST offsets and declared identities; changed or unavailable rules refuse.");
    }
}
