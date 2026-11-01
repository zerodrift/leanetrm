using System.Collections.Immutable;
using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;
using OpenEtrm.Shared.Temporal;

namespace OpenEtrm.Api;

public sealed record CalendarDraft(string? Name,string? From,string? To,ImmutableList<int>? WeekendDays,ImmutableList<string>? ClosedDates);
// An exact reference calendar version and its retained days. weekend_mask bit 0 is
// Monday through bit 6 Sunday (ISO 8601); a day row closes (false) or opens (true) a date.
public sealed record CalendarReference(Guid CatalogId,Guid RevisionId,string Code);
public sealed record ReferenceCalendar(CalendarReference Reference,DateOnly CoverageFrom,DateOnly CoverageTo,int WeekendMask,
    ImmutableArray<(DateOnly Date,bool Open)> Days)
{
    public IEnumerable<string> WeekendDays => Enumerable.Range(0,7).Where(bit=>(WeekendMask>>bit&1)==1)
        .Select(bit=>((DayOfWeek)((bit+1)%7)).ToString());
}
public sealed record MonthlyDraft(string? Name,string? Symbol,string? FirstMonth,string? LastMonth,string? Anchor,
    int? Day,string? ShortMonth,string? Adjustment,CalendarDraft? Calendar,[property:System.Text.Json.Serialization.JsonIgnore(Condition=System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)] ImmutableList<string>? Dates=null);
public sealed record MonthlySaved(MonthlyDraft Definition,ImmutableList<MonthlyOccurrence> Dates,[property: System.Text.Json.Serialization.JsonIgnore] DateSet? BusinessDays);
public sealed record DateExpressionRequest(string? Expression,string? At,CalendarDraft? RollingCalendar);
public sealed record DateReceipt(string Expression,DateOnly Date,string Symbol,int? Index,int? BusinessDays,
    CalendarDraft? Calendar,object Source);
public sealed record ContractDatesDraft(string? Label,string? DeliveryStart,string? DeliveryEnd,string? LastTrading,string? CutoffUtc);
public sealed record ContractDates(string Label,DateOnly DeliveryFrom,DateOnly DeliveryTo,DateTimeOffset Expiry,
    DateReceipt DeliveryStart,DateReceipt DeliveryEnd,DateReceipt LastTrading,string CutoffUtc);
public sealed record CurveDatesRequest(string? Code,string? Name,string? TemplateCurve,string? At,
    CalendarDraft? RollingCalendar,ContractDatesDraft[]? Contracts);
public sealed record CurveDatesSaved(string Code,string Name,string TemplateCurve,object Template,object DefinitionPin,
    object ReferencePin,string At,CalendarDraft? RollingCalendar,ImmutableArray<ContractDates> Contracts);

// All accesses hold the session gate and current ReadMarketData authority fence.
// These are private immutable drafts, not durable or published reference revisions.
public sealed class DateWorkspace
{
    private readonly Dictionary<(string Owner,string Symbol),MonthlySaved> schedules=[];
    private readonly Dictionary<(string Owner,string Code),CurveDatesSaved> curves=[];
    public IEnumerable<MonthlySaved> For(string owner)=>schedules.Where(p=>p.Key.Owner==owner).Select(p=>p.Value);
    public IEnumerable<CurveDatesSaved> Curves(string owner)=>curves.Where(p=>p.Key.Owner==owner).Select(p=>p.Value);
    public MonthlySaved Find(string owner,string symbol)=>schedules.TryGetValue((owner,symbol),out var found)?found:
        throw Error("date_reference_missing","Choose a date schedule kept in this persona's preview session.");
    public static DateOnly Date(string? value)
    {
        if(!DateOnly.TryParseExact(value,"yyyy-MM-dd",CultureInfo.InvariantCulture,DateTimeStyles.None,out var date))
            throw Error("calendar_definition","Supply an exact date in year-month-day form.");
        return date;
    }
    public static DateSet Calendar(CalendarDraft? draft)
    {
        if(draft is null || string.IsNullOrWhiteSpace(draft.Name) || draft.Name.Length>80 || draft.WeekendDays is null || draft.ClosedDates is null ||
            draft.WeekendDays.Count>7 || draft.WeekendDays.Any(d=>d<0||d>6) || draft.WeekendDays.Distinct().Count()!=draft.WeekendDays.Count || draft.ClosedDates.Count>3660)
            throw Error("calendar_definition","Name the calendar and explicitly supply weekend days, closures and known coverage.");
        var from=Date(draft.From);var to=Date(draft.To);var count=to.DayNumber-from.DayNumber;
        if(count<1||count>3660)throw Error("calendar_definition","Calendar coverage must be nonempty and at most 3660 days.");
        var closed=DateSet.Create(from,to,draft.ClosedDates.Select(Date));
        return DateSet.Create(from,to,Enumerable.Range(0,count).Select(from.AddDays)
            .Where(d=>!draft.WeekendDays.Contains((int)d.DayOfWeek)&&!closed.Contains(d)));
    }
    public static DateSet Calendar(ReferenceCalendar calendar)
    {
        if(calendar.WeekendMask is <0 or >127 || calendar.Days.Select(d=>d.Date).Distinct().Count()!=calendar.Days.Length)
            throw Error("calendar_definition","The reference calendar has an invalid weekend mask or repeated days.");
        var count=calendar.CoverageTo.DayNumber-calendar.CoverageFrom.DayNumber;
        if(count<1||count>3660)throw Error("calendar_definition","Calendar coverage must be nonempty and at most 3660 days.");
        var overrides=DateSet.Create(calendar.CoverageFrom,calendar.CoverageTo,calendar.Days.Select(d=>d.Date));
        var open=calendar.Days.Where(d=>d.Open).Select(d=>d.Date).ToHashSet();
        bool Weekend(DateOnly day)=>(calendar.WeekendMask>>(((int)day.DayOfWeek+6)%7)&1)==1;
        return DateSet.Create(calendar.CoverageFrom,calendar.CoverageTo,Enumerable.Range(0,count).Select(calendar.CoverageFrom.AddDays)
            .Where(day=>overrides.Contains(day)?open.Contains(day):!Weekend(day)));
    }
    // The supported schedule rules: one table drives both validation and the editor's choices.
    private static readonly (string Code,string Name,MonthlyAnchor Value)[] AnchorRules=[("day","Day of month",MonthlyAnchor.DayOfMonth),("month_end","Month end",MonthlyAnchor.MonthEnd)];
    private static readonly (string Code,string Name,ShortMonth Value)[] ShortMonthRules=[("reject","Reject the schedule",ShortMonth.Reject),
        ("clamp","Use that month’s last day",ShortMonth.Clamp),("not_applicable","Not applicable · month-end anchor",ShortMonth.NotApplicable)];
    private static readonly (string Code,string Name,DateRoll Value)[] AdjustmentRules=[("unadjusted","Unadjusted",DateRoll.Unadjusted),("following","Following",DateRoll.Following),
        ("preceding","Preceding",DateRoll.Preceding),("modified_following","Modified following",DateRoll.ModifiedFollowing),("modified_preceding","Modified preceding",DateRoll.ModifiedPreceding)];
    public static readonly (string Code,string Name)[] SelectionRules=[("on_or_after","Next date · [0] = first date on or after context"),("context_month","Context month · [0] = this month"),("absolute_index","Fixed occurrences · [0] = first date")];
    public static object Choices => new {
        anchors=AnchorRules.Select(r=>new {code=r.Code,name=r.Name}),shortMonths=ShortMonthRules.Select(r=>new {code=r.Code,name=r.Name}),
        adjustments=AdjustmentRules.Select(r=>new {code=r.Code,name=r.Name}),selections=SelectionRules.Select(r=>new {code=r.Code,name=r.Name})};
    private static T Rule<T>(IEnumerable<(string Code,string Name,T Value)> rules,string? code,string error,string message)
    {
        foreach(var rule in rules)if(rule.Code==code)return rule.Value;
        throw Error(error,message);
    }
    public static MonthlySaved Compile(MonthlyDraft request)=>Compile(request,null);
    // Exactly one calendar source: an inline draft (engine v1) or an exact reference calendar (engine v2).
    public static MonthlySaved Compile(MonthlyDraft request,ReferenceCalendar? reference)
    {
        if(reference is not null && request.Calendar is not null)
            throw Error("calendar_binding","Choose a reference calendar or an inline calendar, not both.");
        if(string.IsNullOrWhiteSpace(request.Name)||request.Name.Length>80 || request.Symbol is null ||
            !Regex.IsMatch(request.Symbol,@"\Aoe\.[a-z][a-z0-9_]{0,31}\z",RegexOptions.CultureInvariant))
            throw Error("monthly_definition","Name the schedule and give it a lowercase oe.shortcut.");
        if(request.Dates is {} explicitDates)
        {
            if(!string.IsNullOrEmpty(request.FirstMonth)||!string.IsNullOrEmpty(request.LastMonth)||!string.IsNullOrEmpty(request.Anchor)||request.Day is not null||!string.IsNullOrEmpty(request.ShortMonth)||!string.IsNullOrEmpty(request.Adjustment))
                throw Error("schedule_definition","Choose a date list or generation rules, not both.");
            if(explicitDates.Count is <1 or >2000)throw Error("schedule_definition","Supply 1–2,000 dates.");
            var dates=explicitDates.Select(Date).ToArray();
            if(!dates.SequenceEqual(dates.Distinct().Order()))throw Error("schedule_definition","Saved dates must be unique and ordered.");
            var businessDays=reference is null?(request.Calendar is null?null:Calendar(request.Calendar)):Calendar(reference);
            return new(request,dates.Select(d=>new MonthlyOccurrence(new(d.Year,d.Month,1),d,d)).ToImmutableList(),businessDays);
        }
        DateOnly Month(string? text)=>text?.Length==7?Date(text+"-01"):throw Error("monthly_definition","Choose explicit first and last months.");
        var anchor=Rule(AnchorRules,request.Anchor,"monthly_definition","Choose a monthly anchor.");
        var shortMonth=Rule(ShortMonthRules,request.ShortMonth,"monthly_definition","Choose a short-month policy.");
        var adjustment=Rule(AdjustmentRules,request.Adjustment,"roll_convention","Choose a business-day adjustment.");
        var calendar=reference is null?Calendar(request.Calendar):Calendar(reference);
        return new(request,MonthlyDates.Generate(Month(request.FirstMonth),Month(request.LastMonth),anchor,request.Day,shortMonth,adjustment,calendar).ToImmutableList(),calendar);
    }
    public MonthlySaved Keep(string owner,MonthlySaved value)
    {
        var key=(owner,value.Definition.Symbol!);
        if(schedules.TryGetValue(key,out var old))
        {
            if(JsonSerializer.Serialize(old.Definition)!=JsonSerializer.Serialize(value.Definition))throw Error("date_definition_conflict","This shortcut has different terms. Choose a new name.");
            return old;
        }
        if(For(owner).Count()>=32)throw Error("preview_limit","This persona already has 32 date schedules.");
        var shared=For(owner).FirstOrDefault(s=>s.Dates.SequenceEqual(value.Dates));
        var kept=shared is null?value:value with{Dates=shared.Dates};schedules.Add(key,kept);return kept;
    }
    public DateReceipt Resolve(string owner,string? expression,DateTimeOffset? at,Func<string,DateReceipt>? resolveContract,CalendarDraft? rollingCalendar=null)
    {
        var reference=DateReference.Parse(expression);
        DateOnly date;object source;CalendarDraft? calendar;
        if(schedules.TryGetValue((owner,reference.Symbol),out var schedule))
        {
            if(reference.Index is not int index || index>=schedule.Dates.Count)throw Error("date_index","Select a zero-based occurrence within the schedule.");
            date=schedule.Dates[index].Date;source=schedule.Definition;calendar=schedule.Definition.Calendar;
            if(reference.BusinessDays is int count)date=DateRolling.Advance(date,schedule.BusinessDays??throw Error("calendar_reference","Business-day offsets require an explicit calendar."),count);
        }
        else
        {
            if(reference.Index is not null)throw Error("date_reference_missing","No such date schedule is kept in this persona's session.");
            if(at is null || resolveContract is null)throw Error("date_reference_missing","Choose a kept date reference and an explicit context.");
            var resolved=resolveContract(reference.Symbol);date=resolved.Date;source=resolved.Source;calendar=rollingCalendar;
            if(reference.BusinessDays is int count)date=DateRolling.Advance(date,Calendar(calendar),count);
            else if(calendar is not null)_=Calendar(calendar);
        }
        return new(expression!,date,reference.Symbol,reference.Index,reference.BusinessDays,calendar,source);
    }
    public static ContractDates Contract(ContractDatesDraft draft,Func<string,DateReceipt> resolve)=>Contract(draft,(_,expression)=>resolve(expression));
    public static ContractDates Contract(ContractDatesDraft draft,Func<string,string,DateReceipt> resolve)
    {
        if(string.IsNullOrWhiteSpace(draft.Label)||draft.Label.Length>80 ||
            !TimeOnly.TryParseExact(draft.CutoffUtc,"HH:mm",CultureInfo.InvariantCulture,DateTimeStyles.None,out var cutoff))
            throw Error("contract_dates","Name the contract and select an explicit UTC last-trading time (hours and minutes).");
        var start=resolve("start",draft.DeliveryStart!);var end=resolve("end",draft.DeliveryEnd!);var last=resolve("last_trading",draft.LastTrading!);
        if(start.Date>=end.Date)throw Error("contract_dates","Delivery end must follow start.");
        return new(draft.Label,start.Date,end.Date,new DateTimeOffset(last.Date.ToDateTime(cutoff,DateTimeKind.Utc)),start,end,last,draft.CutoffUtc!);
    }
    public CurveDatesSaved KeepCurve(string owner,CurveDatesSaved value)
    {
        var key=(owner,value.Code);
        if(curves.TryGetValue(key,out var old))
        {
            if(JsonSerializer.Serialize(old)!=JsonSerializer.Serialize(value))throw Error("date_definition_conflict","This draft curve already has different terms. Choose a new code.");
            return old;
        }
        if(Curves(owner).Count()>=16)throw Error("preview_limit","This persona already has 16 draft curves.");
        curves.Add(key,value);return value;
    }
    private static TemporalException Error(string code,string message)=>new(code,message);
}
