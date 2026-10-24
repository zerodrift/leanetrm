using OpenEtrm.Api;
using OpenEtrm.Shared.Temporal;

internal static class DateWorkspaceChecks
{
    public static void Run()
    {
        var passed=0;
        void Check(string name,Action work){work();passed++;Console.WriteLine($"PASS {name}");}
        void Reject(string code,Action work){try{work();}catch(TemporalException e)when(e.Code==code){return;}throw new Exception($"Expected {code}");}
        var calendar=new CalendarDraft("Synthetic weekdays","2020-12-01","2021-06-01",[0,6],["2021-01-01"]);
        var request=new MonthlyDraft("Delivery months","oe.ng","2021-01","2021-04","day",1,"reject","unadjusted",calendar);
        var workspace=new DateWorkspace();
        var saved=DateWorkspace.Compile(request);
        Check("calendar rules are explicit, bounded and closures cannot escape coverage",()=>{
            Reject("calendar_definition",()=>DateWorkspace.Calendar(calendar with{WeekendDays=null}));
            Reject("date_coverage",()=>DateWorkspace.Calendar(calendar with{ClosedDates=["2022-01-01"]}));
            Reject("calendar_definition",()=>DateWorkspace.Calendar(calendar with{From="2020-12-1"}));
        });
        Check("saved schedules preserve individual occurrences and share equal date payloads",()=>{
            var a=workspace.Keep("viewer",saved);
            var b=workspace.Keep("viewer",DateWorkspace.Compile(request with{Name="Other label",Symbol="oe.other"}));
            if(!ReferenceEquals(a.Dates,b.Dates))throw new Exception("Equal dates not shared");
            Reject("date_reference_missing",()=>workspace.Find("editor","oe.ng"));
            Reject("date_definition_conflict",()=>workspace.Keep("viewer",DateWorkspace.Compile(request with{Adjustment="following"})));
        });
        Check("contract fields consume the same expressions and pinned calendar",()=>{
            DateReceipt Resolve(string s)=>workspace.Resolve("viewer",s,null,null);
            var contract=DateWorkspace.Contract(new("January","oe.ng[0]","oe.ng[1]","oe.ng[0] - 3bd","15:30"),Resolve);
            if(contract.DeliveryFrom!=new DateOnly(2021,1,1)||contract.DeliveryTo!=new DateOnly(2021,2,1)||contract.Expiry!=new DateTimeOffset(2020,12,29,15,30,0,TimeSpan.Zero))throw new Exception("Wrong contract dates");
            if(System.Text.Json.JsonSerializer.Serialize(contract.LastTrading.Calendar)!=System.Text.Json.JsonSerializer.Serialize(calendar) || contract.DeliveryStart.Expression!="oe.ng[0]")throw new Exception("Receipt lost");
            Reject("date_index",()=>Resolve("oe.ng[4]"));Reject("date_index",()=>Resolve("oe.ng"));
            Reject("contract_dates",()=>DateWorkspace.Contract(new("Bad","oe.ng[1]","oe.ng[0]","oe.ng[0]","15:30"),Resolve));
            Reject("contract_dates",()=>DateWorkspace.Contract(new("Bad","oe.ng[0]","oe.ng[1]","oe.ng[0]",null),Resolve));
        });
        Check("reference calendars decode ISO weekend bits, closures and explicit openings",()=>{
            var reference=new CalendarReference(Guid.Empty,Guid.Empty,"synthetic_business");
            ReferenceCalendar Make(int mask,params (DateOnly,bool)[] days)=>new(reference,new(2021,1,1),new(2021,2,1),mask,[..days]);
            // 96 = bits 5 and 6 = Saturday and Sunday when bit 0 is Monday.
            var weekdays=DateWorkspace.Calendar(Make(96,(new(2021,1,1),false),(new(2021,1,9),true)));
            if(weekdays.Contains(new(2021,1,1))||weekdays.Contains(new(2021,1,2))||weekdays.Contains(new(2021,1,3)))throw new Exception("Closure or weekend open");
            if(!weekdays.Contains(new(2021,1,4))||!weekdays.Contains(new(2021,1,8))||!weekdays.Contains(new(2021,1,9)))throw new Exception("Weekday or explicit opening closed");
            var fridays=DateWorkspace.Calendar(Make(16));
            if(fridays.Contains(new(2021,1,1))||!fridays.Contains(new(2021,1,2)))throw new Exception("Bit 4 is Friday");
            if(DateWorkspace.Calendar(Make(0)).Count!=31)throw new Exception("Empty mask closes days");
            Reject("calendar_definition",()=>DateWorkspace.Calendar(Make(128)));
            Reject("calendar_definition",()=>DateWorkspace.Calendar(Make(-1)));
            Reject("date_coverage",()=>DateWorkspace.Calendar(Make(96,(new(2021,2,1),false))));
        });
        Check("explicit last trading may occur after the exclusive delivery end",()=>{
            var context=new ScheduleContext("2021-01-15",0);
            DateReceipt Literal(string expression)=>PublishedSchedules.Resolve([],expression,context);
            var atEnd=DateWorkspace.Contract(new("Synthetic next-day cutoff","2021-01-05","2021-01-06","2021-01-06","04:00"),Literal);
            var afterEnd=DateWorkspace.Contract(new("Synthetic later cutoff","2021-01-06","2021-01-07","2021-01-08","04:00"),Literal);
            if(atEnd.Expiry!=new DateTimeOffset(2021,1,6,4,0,0,TimeSpan.Zero)||afterEnd.Expiry!=new DateTimeOffset(2021,1,8,4,0,0,TimeSpan.Zero))
                throw new Exception("A literal last-trading instant was clipped to the delivery period.");
        });
        Check("a referenced calendar replaces, and never mixes with, an inline calendar",()=>{
            var reference=new ReferenceCalendar(new(Guid.Empty,Guid.Empty,"synthetic_business"),new(2020,12,1),new(2021,6,1),96,[(new DateOnly(2021,1,1),false)]);
            var bound=DateWorkspace.Compile(request with{Calendar=null},reference);
            if(!bound.Dates.Select(d=>d.Date).SequenceEqual(saved.Dates.Select(d=>d.Date)))throw new Exception("Reference calendar changed equal dates");
            Reject("calendar_binding",()=>DateWorkspace.Compile(request,reference));
            Reject("calendar_definition",()=>DateWorkspace.Compile(request with{Calendar=null}));
        });
        Check("monthly gas example retains explicit monthly policies and published references",()=>{
            var sample=ScheduleExamples.MonthlyGas;
            if(sample.Definition.Symbol!="oe.sample_gas_months"||sample.Selection!="context_month"||sample.Definition.Calendar is not null||sample.Definition.Anchor!="day"||sample.Definition.Day!=1||sample.Definition.Adjustment!="unadjusted"||sample.Definition.ShortMonth!="reject")throw new Exception("Example silently changed financial terms");
            var version=new ScheduleVersion(Guid.NewGuid(),1,1,"published",request,"context_month",new(2021,1,1),new(2021,5,1),1,new DateTimeOffset(2020,12,1,0,0,0,TimeSpan.Zero));
            var context=new ScheduleContext("2021-01-15",1);
            var example=ScheduleExamples.Contracts(new("monthly_gas","oe.ng",context),[version],context);
            if(example.Definition.Contracts is not [{Start:"oe.ng[0]",End:"oe.ng[1]",LastTrading:"oe.ng[0] - 3bd",CutoffUtc:"15:30"}, {Start:"oe.ng[1]",End:"oe.ng[2]"}])throw new Exception("Monthly example lost reusable date references");
            Reject("schedule_example",()=>ScheduleExamples.Contracts(new("monthly_gas",null,context),[version],context));
            Reject("schedule_unpublished",()=>ScheduleExamples.Contracts(new("monthly_gas","oe.ng",context),[version with{State="draft"}],context));
        });
        Check("daily power example is explicit literal candidate dates without a daily generator",()=>{
            var context=new ScheduleContext("2021-01-15",0);
            var example=ScheduleExamples.Contracts(new("daily_power",null,context),[],context);
            if(example.Definition.Contracts is not [{Start:"2021-01-05",End:"2021-01-06",LastTrading:"2021-01-04",CutoffUtc:"16:00"}, {Start:"2021-01-06",End:"2021-01-07",LastTrading:"2021-01-05",CutoffUtc:"16:00"}])throw new Exception("Daily example must retain exact supplied date literals");
            foreach(var row in example.Definition.Contracts) {
                var value=DateWorkspace.Contract(new(row.Label,row.Start,row.End,row.LastTrading,row.CutoffUtc),expression=>PublishedSchedules.Resolve([],expression,context));
                if(value.DeliveryTo.DayNumber-value.DeliveryFrom.DayNumber!=1||value.Expiry.Offset!=TimeSpan.Zero)throw new Exception("Daily sample period or UTC cutoff changed");
            }
            Reject("schedule_example",()=>ScheduleExamples.Contracts(new("missing",null,context),[],context));
            Reject("schedule_example",()=>ScheduleExamples.Contracts(new("daily_power","oe.ng",context),[],context));
        });
        Console.WriteLine($"{passed} date workspace checks passed.");
    }
}
