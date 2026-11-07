using OpenEtrm.Api;
using OpenEtrm.Shared.Temporal;

internal static class SchedulePublicationChecks
{
    public static void Run()
    {
        void Eq<T>(T a,T b){if(!Equals(a,b))throw new Exception($"Expected {b}, got {a}");}
        void Reject(string code,Action work){try{work();}catch(TemporalException e)when(e.Code==code){return;}throw new Exception($"Expected {code}");}
        var definition=new MonthlyDraft("Monthly dates","oe.ng","2021-01","2021-04","day",1,"reject","unadjusted",new("Weekdays","2020-12-01","2021-06-01",[0,6],[]));
        var version=new ScheduleVersion(Guid.NewGuid(),1,1,"published",definition,"context_month",new(2020,12,1),new(2021,5,1),1,DateTimeOffset.UtcNow);
        var context=new ScheduleContext("2021-01-15",1);
        Eq(PublishedSchedules.Resolve([version],"oe.ng[0]",context).Date,new DateOnly(2021,1,1));
        Eq(PublishedSchedules.Resolve([version],"oe.ng[0]",context with{EffectiveOn="2021-02-15"}).Date,new DateOnly(2021,2,1));
        Eq(PublishedSchedules.Resolve([version],"oe.ng[1]",context).Date,new DateOnly(2021,2,1));
        Eq(PublishedSchedules.Resolve([version],"2021-02-20",context).Date,new DateOnly(2021,2,20));
        Reject("schedule_unpublished",()=>PublishedSchedules.Resolve([version with{State="draft"}],"oe.ng[0]",context));
        Reject("schedule_unpublished",()=>PublishedSchedules.Resolve([version],"oe.ng[0]",context with{KnownThrough=0}));
        var revised=version with{Version=2,PublicationOrder=2,Definition=definition with{Name="Renamed monthly dates",Day=2}};
        Eq(PublishedSchedules.Catalog([version,revised],context).Single().Name,"Monthly dates");
        Eq(PublishedSchedules.Catalog([version,revised],context with{KnownThrough=2}).Single().Version,2);
        Eq(PublishedSchedules.Catalog([revised],context).Length,0);
        Eq(PublishedSchedules.Catalog([version],context with{EffectiveOn="2021-05-01"}).Length,0);
        Eq(PublishedSchedules.Resolve([version,revised],"oe.ng[0]",context).Date,new DateOnly(2021,1,1));
        Eq(PublishedSchedules.Resolve([version,revised],"oe.ng[0]",context with{KnownThrough=2}).Date,new DateOnly(2021,1,2));
        Reject("date_index",()=>PublishedSchedules.Resolve([version],"oe.ng[0]",context with{EffectiveOn="2020-12-15"}));
        Reject("schedule_context",()=>PublishedSchedules.Resolve([version],"oe.ng[0]",new(null,1)));
        Eq(PublishedSchedules.Resolve([version],"oe.ng",context).Date,new DateOnly(2021,1,1));
        Reject("schedule_engine",()=>PublishedSchedules.Resolve([version with{EngineVersion="unsupported"}],"oe.ng",context));
        var uploaded=version with {Definition=new("Uploaded","oe.ng",null,null,null,null,null,null,definition.Calendar,["2021-01-04","2021-01-19","2021-02-03"]),Selection="on_or_after"};
        Reject("schedule_definition",()=>DateWorkspace.Compile(definition with{Dates=["2021-01-04"]}));
        var noCalendar=uploaded with{EngineVersion="date_schedule_v3",Definition=uploaded.Definition with{Calendar=null}};
        Eq(PublishedSchedules.Resolve([noCalendar],"oe.ng[0]",context).Date,new DateOnly(2021,1,19));
        Reject("calendar_reference",()=>PublishedSchedules.Resolve([noCalendar],"oe.ng[0] + 1bd",context));
        var unavailable=noCalendar with{Calendar=new(Guid.NewGuid(),Guid.NewGuid(),"missing")};
        Reject("calendar_reference",()=>PublishedSchedules.Resolve([unavailable],"oe.ng[0]",context));
        var calendarKey=new CalendarReference(Guid.NewGuid(),Guid.NewGuid(),"long_closure");
        var longClosure=new ReferenceCalendar(calendarKey,new(2021,1,1),new(2021,4,1),127,[(new DateOnly(2021,3,1),true)]);
        var repeated=version with{EngineVersion="date_schedule_v2",Selection="on_or_after",Calendar=calendarKey,ResolvedCalendar=longClosure,
            Definition=definition with{FirstMonth="2021-01",LastMonth="2021-02",Day=31,ShortMonth="clamp",Adjustment="following",Calendar=null}};
        Reject("schedule_definition",()=>PublishedSchedules.Resolve([repeated],"oe.ng[0]",context));
        var resolver=new PublishedScheduleResolver([uploaded],context);
        Eq(resolver.Resolve("oe.ng[0]").Date,new DateOnly(2021,1,19));
        Eq(resolver.Resolve("oe.ng[1]").Date,new DateOnly(2021,2,3));
        for(var i=0;i<10000;i++)Eq(resolver.Resolve("oe.ng[0]").Date,new DateOnly(2021,1,19));
        Eq(resolver.CompiledScheduleCount,1);
        Eq(resolver.ParsedExpressionCount,2);
        var selected=OpenEtrm.MarketData.CustomCurves.RecipeEngine.ScheduleDates([..DateWorkspace.Compile(uploaded.Definition).Dates],"on_or_after",new(2021,1,15),0,2,out var origin);
        Eq(origin,1);Eq(selected[0],new DateOnly(2021,1,19));Eq(selected[1],new DateOnly(2021,2,3));
        Eq(PublishedSchedules.Resolve([uploaded],"oe.ng[0]",context with{EffectiveOn="2021-01-19"}).Date,new DateOnly(2021,1,19));
        Reject("date_index",()=>PublishedSchedules.Resolve([uploaded],"oe.ng[0]",context with{EffectiveOn="2021-03-01"}));
        Console.WriteLine("Published schedule resolution and repeated lookup checks passed.");
        DateLists();
    }

    // Date-list mappings: a label and one date expression per row, resolved against the context date.
    static void DateLists()
    {
        void Check(string name,Action work){work();Console.WriteLine($"PASS {name}");}
        void Reject(string code,Action work,string? mentions=null)
        {
            try{work();}
            catch(TemporalException e)when(e.Code==code){if(mentions is not null&&!mentions.Split(',').All(e.Message.Contains))throw new Exception($"{code} must name {mentions}: {e.Message}");return;}
            throw new Exception($"Expected {code}");
        }
        var calendar=new ReferenceCalendar(new(Guid.Empty,Guid.Empty,"synthetic_business"),new(2026,1,1),new(2027,1,1),96,[]);
        var days=new ScheduleVersion(Guid.NewGuid(),1,1,"published",new("Synthetic days","oe.days",null,null,null,null,null,null,null,Daily:new("calendar","2026-01-01","2030-12-31")),
            "on_or_after",new(2020,1,1),new(2040,1,1),1,new DateTimeOffset(2020,1,1,0,0,0,TimeSpan.Zero),"date_schedule_v4",Calendar:calendar.Reference,ResolvedCalendar:calendar);
        var context=new ScheduleContext("2026-11-03",1);
        BoundDateList Compile(params (string Label,string Date)[] rows)=>
            ContractDateStore.CompileDateList(new("usd_tenors","Synthetic USD tenors",context,null,[..rows.Select(r=>new DateListRowDraft(r.Label,r.Date))]),[days],context);
        Check("a date list resolves each label against the context",()=>{
            var list=Compile(("1D","oe.days + 1bd"),("4Y","oe.days[1461]"));
            if(list.Dates is not [{Position:1,Label:"1D"} first,{Position:2,Label:"4Y"} last]||first.Date!=new DateOnly(2026,11,4)||last.Date!=new DateOnly(2030,11,3)||
               first.Reference.Symbol!="oe.days"||last.Reference.Expression!="oe.days[1461]")throw new Exception("Wrong resolved date list");
        });
        Check("date-list dates must strictly increase",()=>Reject("date_list_order",()=>Compile(("A","oe.days[2]"),("B","oe.days[1]")),"A,B"));
        Check("date-list labels are unique and at most 120 rows",()=>{
            Reject("contract_dates",()=>Compile(("A","oe.days[1]"),("A","oe.days[2]")));
            Reject("contract_dates",()=>Compile([..Enumerable.Range(1,121).Select(i=>($"D{i}",$"oe.days[{i}]"))]));
            Reject("contract_dates",()=>Compile(("",  "oe.days[1]")));
        });
        Check("a literal date row is retained as a literal",()=>{
            if(Compile(("Fixed","2027-01-15")).Dates.Single() is not {Date:var date,Reference.Symbol:"literal"}||date!=new DateOnly(2027,1,15))throw new Exception("Literal row changed");
        });
        Check("a request names contracts or dates, not both",()=>{
            Reject("contract_dates",()=>ContractDateStore.CompileDateList(new("usd_tenors","Tenors",context,[new("M1","oe.days[1]","oe.days[2]","oe.days[1]","15:30")],[new("1D","oe.days[1]")]),[days],context));
            Reject("contract_dates",()=>ContractDateStore.Compile(new("usd_tenors","Tenors",context,null,[new("1D","oe.days[1]")]),[days],context));
        });
    }
}
