using System.Globalization;
using OpenEtrm.Shared.Temporal;

namespace OpenEtrm.Api;

// One authorized, immutable context per evaluation. No process-wide cache of
// visibility decisions: callers load authorized versions before constructing it.
public sealed class PublishedScheduleResolver
{
    readonly ScheduleContext context;
    readonly DateOnly day;
    readonly Dictionary<string,ScheduleVersion> active;
    readonly Dictionary<(Guid,int),ScheduleVersion> exact;
    readonly Dictionary<(Guid,int,int),(MonthlySaved Schedule,int Origin)> compiled=[];
    readonly Dictionary<string,DateReference> expressions=new(StringComparer.Ordinal);
    readonly Dictionary<(string,Guid?,int?),DateReceipt> receipts=[];
    public int CompiledScheduleCount=>compiled.Count;
    public int ParsedExpressionCount=>expressions.Count;

    public PublishedScheduleResolver(IEnumerable<ScheduleVersion> versions,ScheduleContext? context)
    {
        day=PublishedSchedules.ContextDate(context);this.context=context!;
        var eligible=versions.Where(v=>v.State=="published"&&v.PublicationOrder<=context!.KnownThrough&&v.EffectiveFrom<=day&&day<v.EffectiveTo)
            .OrderByDescending(v=>v.PublicationOrder).ToArray();
        active=eligible.GroupBy(v=>v.Definition.Symbol!).ToDictionary(g=>g.Key,g=>g.First(),StringComparer.Ordinal);
        exact=eligible.ToDictionary(v=>(v.Id,v.Version));
    }
    public DateReceipt Resolve(string? expression,Guid? scheduleId=null,int? version=null)
    {
        if(expression is null)throw Error("date_expression","Supply a date expression.");
        var key=(expression,scheduleId,version);
        if(receipts.TryGetValue(key,out var cached))return cached;
        if(expression.Length==10&&DateOnly.TryParseExact(expression,"yyyy-MM-dd",CultureInfo.InvariantCulture,DateTimeStyles.None,out var literal))
            return receipts[key]=new(expression,literal,"literal",null,null,null,new{kind="literal",context});
        if(!expressions.TryGetValue(expression,out var reference))expressions[expression]=reference=DateReference.Parse(expression);
        var selected=scheduleId is {} id&&version is {} v?exact.GetValueOrDefault((id,v)):active.GetValueOrDefault(reference.Symbol);
        if(selected is null||selected.Definition.Symbol!=reference.Symbol)throw Error("schedule_unpublished","No published schedule covers this context and publication view.");
        var pin=(selected.Id,selected.Version,selected.Generation);
        if(!compiled.TryGetValue(pin,out var prepared))
        {
            var schedule=PublishedSchedules.Compile(selected);
            var origin=DateOccurrences.Origin(schedule.Dates,selected.Selection,day);
            compiled[pin]=prepared=(schedule,origin);
        }
        var offset=reference.Index??(selected.Selection!="absolute_index"?0:throw Error("date_index","Fixed schedules need an explicit zero-based occurrence index."));
        var index=prepared.Origin+offset;
        if(prepared.Origin<0||index<0||index>=prepared.Schedule.Dates.Count)throw Error("date_index","The context-relative occurrence is outside the published schedule.");
        var date=prepared.Schedule.Dates[index].Date;
        if(reference.BusinessDays is int count)date=DateRolling.Advance(date,prepared.Schedule.BusinessDays??throw Error("calendar_reference","Business-day offsets require an explicit calendar."),count);
        return receipts[key]=new(expression,date,reference.Symbol,index,reference.BusinessDays,prepared.Schedule.Definition.Calendar,
            new PublishedDateSource(selected.Id,selected.Version,selected.Generation,selected.PublicationOrder!.Value,
                reference.Symbol,selected.Definition.Name!,selected.Selection,selected.EngineVersion,index,context,selected.Definition,selected.Temporal));
    }
    static TemporalException Error(string code,string message)=>new(code,message);
}
