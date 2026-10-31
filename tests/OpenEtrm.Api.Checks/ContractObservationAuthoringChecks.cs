using OpenEtrm.Api;
using OpenEtrm.MarketData;
using OpenEtrm.MarketData.Power;

internal static class ContractObservationAuthoringChecks
{
    internal static void Run()
    {
        var input=new SwapFixingClockInput(new(new(Guid.NewGuid(),Guid.NewGuid()),"synthetic_clock"),"16:30:00","reject","reject");
        var utc=PowerClock.Capture(TimeZoneInfo.Utc);
        var rows=SwapContractObservationAuthoring.Generate(new(2048,1,1),new(2048,2,1),input,utc);
        if(rows.Length!=31||rows[0].From!=new DateOnly(2048,1,1)||rows[^1].To!=new DateOnly(2048,2,1)||
            rows.Where((row,n)=>row.Ordinal!=n||row.FixingAt!=new DateTimeOffset(2048,1,n+1,16,30,0,TimeSpan.Zero)).Any())
            throw new Exception("Explicit local fixing time must generate every calendar-day UTC anchor in the selected month.");
        if(SwapContractObservationAuthoring.Generate(new(2048,2,1),new(2048,3,1),input,utc).Length!=29)
            throw new Exception("Leap-month observations lost a calendar day.");
        var eastern=PowerClock.Capture(TimeZoneInfo.FindSystemTimeZoneById("America/New_York"));
        var dst=SwapContractObservationAuthoring.Generate(new(2026,3,1),new(2026,4,1),input,eastern);
        if(dst[6].FixingAt.Hour!=21||dst[7].FixingAt.Hour!=20)
            throw new Exception("Fixing anchors must follow the selected captured clock across daylight-saving changes.");
        Reject("clock_rule",()=>SwapContractObservationAuthoring.Generate(new(2026,3,1),new(2026,4,1),input with{Time="02:30:00"},eastern));
        Reject("clock_rule",()=>SwapContractObservationAuthoring.Generate(new(2026,11,1),new(2026,12,1),input with{Time="01:30:00"},eastern));
        foreach(var invalid in new[]{input with{Time="16:30"},input with{Time="24:00:00"},input with{AmbiguousTime="earlier"},input with{InvalidTime="shift_forward"}})
            Reject("contract_observation_clock",()=>SwapContractObservationAuthoring.Generate(new(2048,1,1),new(2048,2,1),invalid,utc));
        Reject("contract_observation_input",()=>SwapContractObservationAuthoring.Generate(new(2048,1,2),new(2048,2,1),input,utc));
        Console.WriteLine("PASS contract observation authoring: explicit daily clock, leap month, DST transitions and no missing/ambiguous-time fallback");
    }
    private static void Reject(string code,Action action)
    {try{action();}catch(MarketDataException error)when(error.Code==code){return;}throw new Exception("Expected "+code);}
}
