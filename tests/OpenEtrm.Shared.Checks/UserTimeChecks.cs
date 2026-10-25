using OpenEtrm.Shared.Temporal;

internal static class UserTimeChecks
{
    public static void Run()
    {
        void Equal<T>(T actual,T expected) where T:notnull{if(!actual.Equals(expected))throw new Exception($"Expected {expected}; got {actual}.");}
        void Reject(string code,Action run){try{run();}catch(TemporalException e)when(e.Code==code){return;}throw new Exception($"Expected {code}.");}
        var winter=UserTime.Inspect("2020-12-18T10:00","America/Chicago");
        Equal(winter.Length,1);Equal(winter[0].Instant,new DateTimeOffset(2020,12,18,16,0,0,TimeSpan.Zero));
        Equal(winter[0].Offset,"-06:00");
        var fold=UserTime.Inspect("2026-11-01T01:30","America/Chicago");
        Equal(fold.Length,2);Equal(fold[0].Instant,new DateTimeOffset(2026,11,1,6,30,0,TimeSpan.Zero));
        Equal(fold[1].Instant,new DateTimeOffset(2026,11,1,7,30,0,TimeSpan.Zero));
        Reject("time_nonexistent",()=>UserTime.Inspect("2026-03-08T02:30","America/Chicago"));
        Reject("time_zone",()=>UserTime.Inspect("2020-12-18T10:00","missing/zone"));
        Reject("time_format",()=>UserTime.Inspect("2020-12-18T10:00Z","America/Chicago"));
        Equal(UserTime.Inspect("2020-12-19T00:15:30","Asia/Kathmandu")[0].Instant,new DateTimeOffset(2020,12,18,18,30,30,TimeSpan.Zero));
        // Independent UTC boundaries: the short and long local days end at different offsets.
        Equal(UserTime.EndOfDay(new DateOnly(2026,3,8),"America/Chicago"),
            new DateTimeOffset(2026,3,9,5,0,0,TimeSpan.Zero).AddTicks(-10));
        Equal(UserTime.EndOfDay(new DateOnly(2026,11,1),"America/Chicago"),
            new DateTimeOffset(2026,11,2,6,0,0,TimeSpan.Zero).AddTicks(-10));
        Reject("time_zone",()=>UserTime.EndOfDay(new DateOnly(2026,1,1),"missing/zone"));
        Console.WriteLine("11 user time conversion checks passed.");
    }
}
