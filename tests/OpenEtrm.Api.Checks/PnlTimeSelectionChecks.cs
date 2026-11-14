using OpenEtrm.Api;
using OpenEtrm.MarketData;
using OpenEtrm.MarketData.Calculation;
using OpenEtrm.MarketData.Power;

internal static class PnlTimeSelectionChecks
{
    internal static void Run()
    {
        static void Reject(string code,Action action)
        {try{action();}catch(MarketDataException e)when(e.Code==code){return;}throw new Exception("Expected "+code);}
        static DateTimeOffset At(int month,int day,int hour)=>new(2026,month,day,hour,0,0,TimeSpan.Zero);
        var installed=TimeZoneDatabase.Installed().Version();
        ClockRule utc=new("UTC","fixed_offset",0,null,null),newYork=new("New York","iana",null,"America/New_York",installed);
        PnlTimeSelection oneDay=new(TradingPnlReport.OneDay,ClockChange.FrozenInputs,null),roll=new(TradingPnlReport.Roll,ClockChange.FrozenInputs,null);
        // 17:00 in New York on Saturday 31 October 2026, the eve of the autumn clock change: 21:00 UTC. Only a calendar that
        // has Saturdays has such a close; from a weekday close in New York the next calendar day is always 24 hours.
        DateTimeOffset from=At(10,31,21),to=At(11,3,22);

        // What a request decides by itself is decided before any endpoint is read. A working snapshot has no convention, so
        // its request names the clock: one calendar day is 24 hours on UTC and 25 on New York, and the report says the
        // request named it. Nothing fills a clock in.
        var named=TradingPnlReport.RequestTime(oneDay with{Clock=utc},recordedPrior:false,from,to);
        if(named is not {Target:{Calendar:{Convention:CalendarClockRoll.NextDay}}}||named.Target.At!=At(11,1,21)||named.Clock!=new TradingPnlReport.CalendarClock("request",null,null,null,null,utc,"UTC"))
            throw new Exception("One calendar day from a working snapshot is counted on the clock its request names.");
        if(TradingPnlReport.RequestTime(oneDay with{Clock=newYork},false,from,to) is not {Clock:{Zone:"America/New_York"}} eastern||eastern.Target.At!=At(11,1,22))
            throw new Exception("A named clock that changes its offset that night counts 25 hours.");
        Reject("pnl_time_policy",()=>TradingPnlReport.RequestTime(oneDay,false,from,to));
        // A named rule that is no clock on this host is refused there too, before anything is read for it.
        Reject("clock_tzdb",()=>TradingPnlReport.RequestTime(oneDay with{Clock=newYork with{TzdbVersion=installed+"-other"}},false,from,to));
        Reject("clock_rule",()=>TradingPnlReport.RequestTime(oneDay with{Clock=new("Nowhere","iana",null,"Nowhere/None",installed)},false,from,to));
        // A recorded close states its convention's calendar clock: the request names none, and one that names another is
        // refused rather than obeyed or ignored. Nothing is decided until that close's convention is read.
        if(TradingPnlReport.RequestTime(oneDay,recordedPrior:true,from,to) is not null)throw new Exception("A recorded prior close leaves the clock to its convention.");
        foreach(var other in new[]{utc,newYork})Reject("pnl_time_policy",()=>TradingPnlReport.RequestTime(oneDay with{Clock=other},true,from,to));
        foreach(var recorded in new[]{true,false})
        {
            // The roll values the opening holdings at the closing instant and counts on no clock, whatever the prior date
            // is: it needs no convention read.
            if(TradingPnlReport.RequestTime(roll,recorded,from,to) is not {Clock:null,Target:{Calendar:null} target}||target.At!=to||target.Policy!=ClockChange.FrozenInputs)
                throw new Exception("The roll to the closing instant is that instant, on no calendar clock.");
            Reject("pnl_time_policy",()=>TradingPnlReport.RequestTime(roll with{Clock=utc},recorded,from,to));
            // Nothing is assumed for a missing selection, another clock policy or an unknown mode.
            Reject("pnl_time_policy",()=>TradingPnlReport.RequestTime(null!,recorded,from,to));
            Reject("pnl_time_policy",()=>TradingPnlReport.RequestTime(oneDay with{Policy="frozen_inputs"},recorded,from,to));
            Reject("pnl_time_policy",()=>TradingPnlReport.RequestTime(oneDay with{Mode="one_business_day_model_decay_v1"},recorded,from,to));
        }

        // The same local time on the next calendar day is 25 hours later on the convention's clock, and the report says
        // whose clock that is.
        var convention=new TradingPnlReport.CloseConvention("new_york_every_day",2,"New York every day","new_york_all_days",newYork);
        var counted=TradingPnlReport.ConventionTime(convention,from);
        if(counted.Target.At!=At(11,1,22)||counted.Target.Policy!=ClockChange.FrozenInputs||counted.Target.Calendar is not {Zone:"America/New_York",Convention:CalendarClockRoll.NextDay} calendar||calendar.From!=from)
            throw new Exception("One calendar day from a recorded close is counted on its convention's calendar clock.");
        if(counted.Clock!=new TradingPnlReport.CalendarClock("prior_close_convention","new_york_every_day",2,"New York every day","new_york_all_days",newYork,"America/New_York"))
            throw new Exception("The report states the convention whose calendar clock one calendar day was counted on.");
        // A fixed-offset clock is stated as its offset: its code is a name, which need not say what the clock is.
        if(TradingPnlReport.ConventionTime(convention with{Clock=new("eastern_standard","fixed_offset",-18000,null,null)},from) is not {Clock:{Zone:"UTC-05:00"}} fixedOffset||fixedOffset.Target.At!=At(11,1,21))
            throw new Exception("A fixed-offset convention clock states its offset and counts 24 hours.");
        // A convention whose calendar clock cannot be read has no clock: no other clock stands in for it.
        Reject("pnl_time_clock",()=>TradingPnlReport.ConventionTime(convention with{Clock=null},from));
        Console.WriteLine("PASS P&L time selection: a request's own clock and the roll are decided before any read; a recorded prior close brings its convention's calendar clock and the request names none; a missing, conflicting or unusable clock is refused.");
    }
}
