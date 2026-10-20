using System.Collections.Immutable;
using OpenEtrm.Shared.Temporal;

namespace OpenEtrm.Api;

public sealed record ScheduleContext(string? EffectiveOn,int? KnownThrough,
    [property:System.Text.Json.Serialization.JsonIgnore(Condition=System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)] string? KnownAt=null,
    [property:System.Text.Json.Serialization.JsonIgnore(Condition=System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)] Guid? ContextId=null);
public sealed record ScheduleVersion(Guid Id,int Version,int Generation,string State,MonthlyDraft Definition,
    string Selection,DateOnly EffectiveFrom,DateOnly EffectiveTo,int? PublicationOrder,DateTimeOffset? PublishedAt,string EngineVersion="date_schedule_v1",BitemporalRange? Temporal=null,
    CalendarReference? Calendar=null,[property:System.Text.Json.Serialization.JsonIgnore] ReferenceCalendar? ResolvedCalendar=null);
public sealed record ScheduleSaveRequest(Guid? Id,int? Version,int? Generation,MonthlyDraft Definition,
    string? Selection,string? EffectiveFrom,string? EffectiveTo,string? ExpectedState=null,CalendarReference? Calendar=null);
public sealed record DateSchedulePreviewRequest(MonthlyDraft? Definition,CalendarReference? Calendar);
public sealed record SchedulePublishRequest(Guid Id,int Version,int Generation);
public sealed record PublishedDateRequest(string? Expression,ScheduleContext? Context);
public sealed record ScheduleCatalogRequest(ScheduleContext? Context);
public sealed record ScheduleReferenceChoice(string Symbol,string Name,int Version,string Selection);
public sealed record PublishedContractRequest(string? Code,string? Name,ScheduleContext? Context,ContractFieldDraft[]? Contracts);
public sealed record PublishedDateSource(Guid ScheduleId,int Version,int Generation,int PublicationOrder,
    string Symbol,string Name,string Selection,string EngineVersion,int Occurrence,ScheduleContext Context,MonthlyDraft Definition,BitemporalRange? Temporal=null);

public static class PublishedSchedules
{
    private static IEnumerable<ScheduleVersion> Eligible(IEnumerable<ScheduleVersion> versions,ScheduleContext? context)
    {
        var day=ContextDate(context);
        return versions.Where(v=>v.State=="published"&&v.PublicationOrder<=context!.KnownThrough&&v.EffectiveFrom<=day&&day<v.EffectiveTo)
            .OrderByDescending(v=>v.PublicationOrder);
    }
    public static ImmutableArray<ScheduleReferenceChoice> Catalog(IEnumerable<ScheduleVersion> versions,ScheduleContext? context)=>
        Eligible(versions,context).GroupBy(v=>v.Definition.Symbol).Select(g=>g.First())
            .Select(v=>new ScheduleReferenceChoice(v.Definition.Symbol!,v.Definition.Name!,v.Version,v.Selection)).OrderBy(v=>v.Symbol,StringComparer.Ordinal).ToImmutableArray();
    public static DateOnly ContextDate(ScheduleContext? context)
    {
        if(context is null || context.EffectiveOn is null || context.KnownThrough is null or <0)
            throw Error("schedule_context","Select an effective date and a captured publication view.");
        try{return DateWorkspace.Date(context.EffectiveOn);}catch(TemporalException){throw Error("schedule_context","Select an exact context date.");}
    }
    // Each engine version has one calendar source; an unknown engine is never reinterpreted.
    public static MonthlySaved Compile(MonthlyDraft definition,string engineVersion,ReferenceCalendar? calendar) => engineVersion switch
    {
        "date_schedule_v1"=>DateWorkspace.Compile(definition),
        "date_schedule_v2"=>DateWorkspace.Compile(definition,calendar??throw Error("calendar_reference","The schedule's reference calendar is unavailable in this view.")),
        _=>throw Error("schedule_engine","This retained schedule engine version is unavailable.")
    };
    public static void Validate(ScheduleSaveRequest request,string engineVersion,ReferenceCalendar? calendar)
    {
        if(request.Definition is null)throw Error("schedule_definition","Supply an explicit schedule definition.");
        _=Compile(request.Definition,engineVersion,calendar);
        if(!DateWorkspace.SelectionRules.Any(r=>r.Code==request.Selection) || DateWorkspace.Date(request.EffectiveFrom)>=DateWorkspace.Date(request.EffectiveTo))
            throw Error("schedule_definition","Choose an occurrence selection rule and nonempty effective interval.");
    }
    public static DateReceipt Resolve(IEnumerable<ScheduleVersion> versions,string? expression,ScheduleContext? context)
    {
        var day=ContextDate(context);
        if(expression?.Length==10 && DateOnly.TryParseExact(expression,"yyyy-MM-dd",System.Globalization.CultureInfo.InvariantCulture,
            System.Globalization.DateTimeStyles.None,out var literal))return new(expression,literal,"literal",null,null,null,new{kind="literal",context});
        var reference=DateReference.Parse(expression);
        var eligible=Eligible(versions,context).Where(v=>v.Definition.Symbol==reference.Symbol).ToArray();
        if(eligible.Length==0)throw Error("schedule_unpublished","No published schedule covers this context and publication view.");
        var selected=eligible[0];
        var compiled=Compile(selected.Definition,selected.EngineVersion,selected.ResolvedCalendar);
        var offset=reference.Index??(selected.Selection=="context_month"?0:throw Error("date_index","Fixed schedules need an explicit zero-based occurrence index."));
        var origin=selected.Selection switch {
            "absolute_index"=>0,
            "context_month"=>compiled.Dates.FindIndex(d=>d.Month.Year==day.Year&&d.Month.Month==day.Month),
            _=>throw Error("schedule_definition","Unsupported occurrence selection rule.")};
        var index=origin+offset;
        if(origin<0 || index<0 || index>=compiled.Dates.Count)throw Error("date_index","The context-relative occurrence is outside the published schedule.");
        var date=compiled.Dates[index].Date;
        if(reference.BusinessDays is int count)date=DateRolling.Advance(date,compiled.BusinessDays,count);
        return new(expression!,date,reference.Symbol,index,reference.BusinessDays,compiled.Definition.Calendar,
            new PublishedDateSource(selected.Id,selected.Version,selected.Generation,selected.PublicationOrder!.Value,
                reference.Symbol,selected.Definition.Name!,selected.Selection,selected.EngineVersion,index,context!,selected.Definition,selected.Temporal));
    }
    private static TemporalException Error(string code,string message)=>new(code,message);
}

public sealed record ContractFieldDraft(string? Label,string? Start,string? End,string? LastTrading,string? CutoffUtc);
public sealed record DatedContractDefinition(string Label,DateOnly Start,DateOnly End,DateTimeOffset LastTradingAt,
    DateReceipt StartReference,DateReceipt EndReference,DateReceipt LastTradingReference,string CutoffUtc);
public sealed record BoundContractDefinition(string Code,string Name,ScheduleContext Context,ImmutableArray<DatedContractDefinition> Contracts,
    [property:System.Text.Json.Serialization.JsonIgnore(Condition=System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)] Guid? DefinitionId=null,
    [property:System.Text.Json.Serialization.JsonIgnore(Condition=System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)] int? Version=null,
    [property:System.Text.Json.Serialization.JsonIgnore(Condition=System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)] string? EngineVersion=null);
