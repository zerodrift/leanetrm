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
    }
}
