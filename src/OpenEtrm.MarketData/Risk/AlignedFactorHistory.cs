using System.Collections.Immutable;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using OpenEtrm.MarketData.Power;

namespace OpenEtrm.MarketData.Risk;

public sealed record RiskHistoryMarket(DefinitionRevision Definitions,Guid SnapshotId,string GraphManifest);
public sealed record RiskHistoryForecast(Guid ContextId,Guid CatalogId,DateTimeOffset ValuationAt,DateTimeOffset MarketCutoff,
    DateTimeOffset KnownAt,int MarketKnownThrough,ImmutableArray<RiskHistoryMarket> Markets);
public sealed record RiskHistoryFactor(QuoteFactor Factor,PublicationState State);
public sealed record RiskHistoryCalendar(ReferenceKey Reference,DateOnly From,DateOnly To,ImmutableArray<DateOnly> BusinessDates);
public sealed record RiskHistoryResolvedClose(DateOnly Date,DateTimeOffset Instant,int OffsetSeconds);
public sealed record RiskHistoryClock(ReferenceKey Reference,string Rules,string Fingerprint,string CloseTime,string AmbiguousTime,string InvalidTime,
    string Resolution,ImmutableArray<RiskHistoryResolvedClose> ResolvedCloses,
    [property:System.Text.Json.Serialization.JsonIgnore(Condition=System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingDefault)]ImmutableArray<RiskHistoryDayWindow> DayWindows=default)
{
    public const string Captured="captured_calendar_session_closes_v1",DayEnds="captured_calendar_day_windows_v1",EndOfDay="end_of_day";
}
[System.Text.Json.Serialization.JsonUnmappedMemberHandling(System.Text.Json.Serialization.JsonUnmappedMemberHandling.Disallow)]
public sealed record RiskHistoryPolicy(string Convention,string NumericalPolicy,string Basis,string Revisions,string Alignment,string Transform,string Missing,string Regimes,int MinimumSamples,
    [property:System.Text.Json.Serialization.JsonIgnore(Condition=System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingDefault)]ImmutableArray<string> PriceTypes=default)
{
    public const string Version1="dated_factor_common_session_changes_v1",Raw="fixed_current_dated_input_mapping_v1",
        Heads="unique_visible_correction_head_v1",ExactCloses="exact_session_close_v1",Absolute="native_absolute_change_v1",
        Complete="common_complete_adjacent_sessions_v1",Fixed="exact_source_role_state_unit_contract_v1",DayEnd="daily_latest_visible_session_end_v1",PriceOrder="ordered_price_types_fixed_series_source_unit_contract_v1";
}
// Knowledge is certified publication evidence, separate from observed or received time.
public sealed record RiskHistoryObservation(MarketObservation Observation,ReferenceKey Quote,PriceUnit Unit,ContractKey Contract,
    ObservationKey? Revises,int? KnowledgeOrder,DateTimeOffset? KnownAt);
public sealed record RiskHistoryColumn(string Id,RiskHistoryFactor Mapping);
public sealed record RiskHistorySession(DateOnly Date,DateTimeOffset Close);
public sealed record RiskHistoryMark(ObservationKey Observation,ExactDecimal Value,int KnowledgeOrder,DateTimeOffset KnownAt,
    ImmutableArray<ObservationKey> Lineage);
public sealed record RiskHistoryEndpoint(DateTimeOffset At,ImmutableArray<RiskHistoryMark?> Marks);
public sealed record RiskHistoryIssue(string FactorId,string Endpoint,string Reason);
public sealed record RiskHistoryChange(string Id,RiskHistoryEndpoint From,RiskHistoryEndpoint To,ImmutableArray<ExactDecimal> Changes);
public sealed record RiskHistoryRejected(string Id,RiskHistoryEndpoint From,RiskHistoryEndpoint To,ImmutableArray<RiskHistoryIssue> Issues);
public sealed record AlignedFactorHistory(string Id,RiskHistoryForecast Forecast,RiskHistoryPolicy Policy,RiskHistoryCalendar Calendar,
    RiskHistoryClock Clock,ImmutableArray<RiskHistoryColumn> Columns,ImmutableArray<RiskHistorySession> Sessions,
    ImmutableArray<RiskHistoryObservation> Evidence,ImmutableArray<RiskHistoryChange> Samples,ImmutableArray<RiskHistoryRejected> Rejected,bool Available,string? UnavailableReason,
    [property:System.Text.Json.Serialization.JsonIgnore(Condition=System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingDefault)]ImmutableArray<RiskHistoryDayPrices> DayPrices=default);

// Admission precedes loading observation history. The adapter consumes these
// exact columns and sessions for its bounded authorized read.
public sealed class PreparedFactorHistory
{
    public RiskHistoryForecast Forecast{get;}
    public RiskHistoryPolicy Policy{get;}
    public RiskHistoryCalendar Calendar{get;}
    public RiskHistoryClock Clock{get;}
    public ImmutableArray<RiskHistoryColumn> Columns{get;}
    public ImmutableArray<RiskHistorySession> Sessions{get;}
    internal PreparedFactorHistory(RiskHistoryForecast forecast,RiskHistoryPolicy policy,RiskHistoryCalendar calendar,RiskHistoryClock clock,
        ImmutableArray<RiskHistoryColumn> columns,ImmutableArray<RiskHistorySession> sessions)
    {Forecast=forecast;Policy=policy;Calendar=calendar;Clock=clock;Columns=columns;Sessions=sessions;}
}

public static partial class FactorHistory
{
    public const int MaximumFactors=1024,MaximumSessions=2049,MaximumCells=250000,MaximumObservations=500000,MaximumCalendarDays=3660;
    // The authorized adapter resolves a reference clock once. Retained replay uses
    // these resolved instants, not the machine's current timezone database.
    public static RiskHistoryClock CaptureClock(RiskHistoryCalendar calendar,ReferenceKey reference,PowerClock clock,string closeTime,string ambiguousTime,string invalidTime)
    {
        RequireCalendar(calendar);Money.RequireKey(reference);
        if(closeTime==RiskHistoryClock.EndOfDay)return CaptureDayClock(calendar,reference,clock,ambiguousTime,invalidTime);
        if(reference.Revision!=calendar.Reference.Revision||clock is null||ambiguousTime!="reject"||invalidTime!="reject"||
            !TimeOnly.TryParseExact(closeTime,"HH:mm:ss",CultureInfo.InvariantCulture,DateTimeStyles.None,out var time))
            throw Error("risk_history_calendar","Choose the exact calendar clock, explicit close time and reject policies for ambiguous/nonexistent closes.");
        return new(reference,clock.Rules,clock.Fingerprint,closeTime,ambiguousTime,invalidTime,RiskHistoryClock.Captured,
            [..calendar.BusinessDates.Order().Select(d=>{var at=clock.AtLocal(d,time);return new RiskHistoryResolvedClose(d,at.ToUniversalTime(),checked((int)at.Offset.TotalSeconds));})]);
    }
    public static AlignedFactorHistory Replay(AlignedFactorHistory manifest,CancellationToken cancellation=default)
    {
        cancellation.ThrowIfCancellationRequested();
        if(manifest is null||!Hash(manifest.Id)||manifest.Columns.IsDefault||manifest.Samples.IsDefault||manifest.Rejected.IsDefault)
            throw Error("risk_history_manifest","Supply a complete retained history manifest.");
        var replay=Align(manifest.Forecast,manifest.Policy,manifest.Calendar,manifest.Clock,[..manifest.Columns.Select(c=>c.Mapping)],manifest.Evidence,cancellation);
        if(replay.Id!=manifest.Id||Digest(manifest)!=manifest.Id)throw Error("risk_history_manifest","The retained history evidence, mapping or values do not match its identity.");
        return replay;
    }
    public static AlignedFactorHistory Align(RiskHistoryForecast forecast,RiskHistoryPolicy policy,RiskHistoryCalendar calendar,RiskHistoryClock clock,
        ImmutableArray<RiskHistoryFactor> factors,ImmutableArray<RiskHistoryObservation> observations,CancellationToken cancellation=default)
        =>Align(Prepare(forecast,policy,calendar,clock,factors,cancellation),observations,cancellation);
    public static PreparedFactorHistory Prepare(RiskHistoryForecast forecast,RiskHistoryPolicy policy,RiskHistoryCalendar calendar,RiskHistoryClock clock,
        ImmutableArray<RiskHistoryFactor> factors,CancellationToken cancellation=default)
    {
        cancellation.ThrowIfCancellationRequested();RequireForecast(forecast);ValidatePolicy(policy);
        var columns=Columns(forecast,factors);var sessions=policy.Alignment==RiskHistoryPolicy.DayEnd?DaySessions(calendar,clock,forecast):Sessions(calendar,clock,forecast);
        if((long)columns.Length*sessions.Length>MaximumCells)
            throw Error("risk_history_capacity","Reduce the history window or scope: at most 250000 factor/session cells are admitted.");
        return new(forecast with{Markets=[..forecast.Markets.OrderBy(m=>m.Definitions.RevisionId)]},policy,
            calendar with{BusinessDates=[..calendar.BusinessDates.Order()]},clock with{ResolvedCloses=[..clock.ResolvedCloses.OrderBy(c=>c.Date)]},columns,sessions);
    }
    public static AlignedFactorHistory Align(PreparedFactorHistory prepared,ImmutableArray<RiskHistoryObservation> observations,CancellationToken cancellation=default)
    {
        cancellation.ThrowIfCancellationRequested();ArgumentNullException.ThrowIfNull(prepared);
        var forecast=prepared.Forecast;var policy=prepared.Policy;var calendar=prepared.Calendar;var clock=prepared.Clock;
        var columns=prepared.Columns;var sessions=prepared.Sessions;
        if(observations.IsDefault||observations.Length>MaximumObservations)
            throw Error("risk_history_capacity","Reduce the history window or scope: at most 250000 factor/session cells and 500000 observation versions are admitted.");
        var visible=new Dictionary<ObservationKey,RiskHistoryObservation>();var all=new HashSet<ObservationKey>();
        var series=columns.Select(c=>c.Mapping.Factor.Input.Series).ToHashSet();
        foreach(var row in observations)
        {
            cancellation.ThrowIfCancellationRequested();
            if(row?.Observation is not {} o||o.Key.Id==Guid.Empty||o.Key.CatalogId!=forecast.CatalogId||o.Series.Revision.CatalogId!=forecast.CatalogId||
                !series.Contains(o.Series)||!all.Add(o.Key)||!Utc(o.ObservedAt)||!Enum.IsDefined(o.Role)||!Enum.IsDefined(o.State)||row.Unit is null||
                row.KnowledgeOrder is <=0||row.KnownAt is {} known&&!Utc(known)||(row.KnowledgeOrder is null)!=(row.KnownAt is null))
                throw Error("risk_history_observation","Supply distinct scoped observations and their exact certified knowledge evidence.");
            if(row.KnowledgeOrder is {} order&&row.KnownAt is {} at&&order<=forecast.MarketKnownThrough&&at<=forecast.KnownAt&&o.ObservedAt<=forecast.MarketCutoff)
                visible.Add(o.Key,row);
        }
        var superseded=new HashSet<ObservationKey>();
        foreach(var row in visible.Values)
        {
            cancellation.ThrowIfCancellationRequested();
            if(row.Revises is not {} predecessor)continue;
            if(!visible.TryGetValue(predecessor,out var parent)||parent.KnowledgeOrder>=row.KnowledgeOrder||parent.KnownAt>row.KnownAt||
                parent.Observation.Series!=row.Observation.Series||parent.Observation.Source!=row.Observation.Source||
                parent.Observation.Role!=row.Observation.Role||parent.Observation.ObservedAt!=row.Observation.ObservedAt)
                throw Error("risk_history_lineage","Visible corrections require a complete, earlier certified ancestry for the same exact observation fact.");
            superseded.Add(predecessor);
        }
        foreach(var column in columns)
        {
            var factor=column.Mapping.Factor;
            if(!visible.TryGetValue(factor.Observation,out var anchor)||!Mapping(column.Mapping,anchor)||anchor.Observation.State!=column.Mapping.State||
                anchor.Observation.Value!=factor.BaseValue)
                throw Error("risk_history_factor","Each current factor requires its exact visible pinned base observation and coherent source mapping.");
        }
        var byClose=visible.Values.GroupBy(r=>(r.Observation.Series,r.Observation.Source,r.Observation.Role,r.Observation.ObservedAt))
            .ToDictionary(g=>g.Key,g=>g.Where(r=>!superseded.Contains(r.Observation.Key)).ToArray());
        var anyClose=visible.Values.Select(r=>(r.Observation.Series,r.Observation.ObservedAt)).ToHashSet();
        var timelines=visible.Values.GroupBy(r=>(r.Observation.Series,r.Observation.Source,r.Observation.Role))
            .ToDictionary(g=>g.Key,g=>g.Select(r=>r.Observation.ObservedAt).Distinct().Order().ToArray());
        RiskHistoryMark? Mark(RiskHistoryColumn column,RiskHistorySession session,out string? reason)
        {
            var f=column.Mapping.Factor;reason=null;
            if(!byClose.TryGetValue((f.Input.Series,f.Source,f.Role,session.Close),out var heads))
            {
                reason="missing_mark";
                if(anyClose.Contains((f.Input.Series,session.Close)))reason="source_role";
                else if(timelines.TryGetValue((f.Input.Series,f.Source,f.Role),out var times))
                {
                    var next=~Array.BinarySearch(times,session.Close);
                    // Diagnostic dates are UTC; selection always requires exact captured close equality.
                    bool SameDate(int n)=>n>=0&&n<times.Length&&times[n].UtcDateTime.Date==session.Close.UtcDateTime.Date;
                    reason=SameDate(next)||SameDate(next-1)?"asynchronous_close":next>0?"stale_mark":"missing_mark";
                }
                return null;
            }
            if(heads.Length!=1){reason="ambiguous_revision";return null;}
            var row=heads[0];
            // Resolve the visible head before state eligibility; never revive a superseded final mark.
            if(row.Observation.State!=column.Mapping.State){reason="publication_state";return null;}
            var lineage=ImmutableArray.CreateBuilder<ObservationKey>();
            for(var current=row;;current=visible[current.Revises!.Value])
            {
                cancellation.ThrowIfCancellationRequested();
                if(!Mapping(column.Mapping,current)){reason="mapping_regime";return null;}
                lineage.Add(current.Observation.Key);if(current.Revises is null)break;
            }
            lineage.Reverse();
            return new(row.Observation.Key,row.Observation.Value,row.KnowledgeOrder!.Value,row.KnownAt!.Value,lineage.ToImmutable());
        }
        var dayRows=policy.Alignment==RiskHistoryPolicy.DayEnd?visible.Values.GroupBy(r=>(r.Observation.Series,r.Observation.Source)).ToDictionary(g=>g.Key,g=>g.OrderBy(r=>r.Observation.ObservedAt).ToArray()):new Dictionary<(SeriesKey,ReferenceKey),RiskHistoryObservation[]>();
        var dayPrices=ImmutableArray.CreateBuilder<RiskHistoryDayPrices>();
        var endpoints=ImmutableArray.CreateBuilder<RiskHistoryEndpoint>();var issuesBySession=new List<ImmutableArray<(string Factor,string Reason)>>();
        foreach(var session in sessions)
        {
            var dayWindow=policy.Alignment==RiskHistoryPolicy.DayEnd?clock.DayWindows.Single(w=>w.Date==session.Date):null;
            var choices=ImmutableArray.CreateBuilder<RiskHistoryPriceChoice>();
            var marks=ImmutableArray.CreateBuilder<RiskHistoryMark?>();var issues=ImmutableArray.CreateBuilder<(string,string)>();
            foreach(var column in columns)
            {
                cancellation.ThrowIfCancellationRequested();string? reason;
                if(policy.Alignment==RiskHistoryPolicy.DayEnd){marks.Add(MarkDay(column,dayWindow!,policy,visible,dayRows,superseded,out reason,out var choice,cancellation));choices.Add(choice);}
                else marks.Add(Mark(column,session,out reason));
                if(reason is not null)issues.Add((column.Id,reason));
            }
            if(policy.Alignment==RiskHistoryPolicy.DayEnd)dayPrices.Add(new(session.Close,choices.ToImmutable()));
            endpoints.Add(new(session.Close,marks.ToImmutable()));issuesBySession.Add(issues.ToImmutable());
        }
        var samples=ImmutableArray.CreateBuilder<RiskHistoryChange>();var rejected=ImmutableArray.CreateBuilder<RiskHistoryRejected>();
        // Pair the original schedule, never a filtered list of available endpoints.
        for(var n=1;n<sessions.Length;n++)
        {
            cancellation.ThrowIfCancellationRequested();var from=endpoints[n-1];var to=endpoints[n];var id=sessions[n-1].Date.ToString("yyyy-MM-dd",CultureInfo.InvariantCulture)+"/"+sessions[n].Date.ToString("yyyy-MM-dd",CultureInfo.InvariantCulture);
            var issues=issuesBySession[n-1].Select(i=>new RiskHistoryIssue(i.Factor,"from",i.Reason))
                .Concat(issuesBySession[n].Select(i=>new RiskHistoryIssue(i.Factor,"to",i.Reason))).ToImmutableArray();
            if(!issues.IsEmpty){rejected.Add(new(id,from,to,issues));continue;}
            samples.Add(new(id,from,to,[..columns.Select((_,i)=>to.Marks[i]!.Value-from.Marks[i]!.Value)]));
        }
        var evidence=visible.Values.OrderBy(r=>r.Observation.Series.Revision.RevisionId).ThenBy(r=>r.Observation.Series.Id)
            .ThenBy(r=>r.Observation.ObservedAt).ThenBy(r=>r.KnowledgeOrder).ThenBy(r=>r.Observation.Key.Id).ToImmutableArray();
        var available=samples.Count>=policy.MinimumSamples;
        var result=new AlignedFactorHistory("",forecast,policy,calendar,clock,columns,sessions,evidence,samples.ToImmutable(),rejected.ToImmutable(),available,
            available?null:"insufficient_common_history",policy.Alignment==RiskHistoryPolicy.DayEnd?dayPrices.ToImmutable():default);
        cancellation.ThrowIfCancellationRequested();return result with{Id=Digest(result)};
    }
    private static bool Utc(DateTimeOffset at)=>at.Offset==TimeSpan.Zero&&at.Year<9999;
    private static MarketDataException Error(string code,string text)=>new(code,text);
    private static void RequireForecast(RiskHistoryForecast forecast)
    {
        if(forecast is null||forecast.ContextId==Guid.Empty||forecast.CatalogId==Guid.Empty||!Utc(forecast.ValuationAt)||!Utc(forecast.MarketCutoff)||
            !Utc(forecast.KnownAt)||forecast.MarketCutoff>forecast.ValuationAt||forecast.MarketKnownThrough<0||forecast.Markets.IsDefaultOrEmpty||
            forecast.Markets.Any(m=>m is null||m.Definitions.CatalogId!=forecast.CatalogId||m.Definitions.RevisionId==Guid.Empty||m.SnapshotId==Guid.Empty||!Hash(m.GraphManifest))||
            forecast.Markets.Select(m=>m.Definitions).Distinct().Count()!=forecast.Markets.Length)
            throw Error("risk_history_context","Supply exact forecast snapshots, graph manifests, valuation/market cutoffs and certified knowledge coordinates.");
    }
    public static void ValidatePolicy(RiskHistoryPolicy p)
    {
        if(p is null||p.Convention!=RiskHistoryPolicy.Version1||p.NumericalPolicy!=CurveModel.NumericalPolicy||p.Basis!=RiskHistoryPolicy.Raw||p.Revisions!=RiskHistoryPolicy.Heads||
            p.Alignment is not (RiskHistoryPolicy.ExactCloses or RiskHistoryPolicy.DayEnd)||p.Transform!=RiskHistoryPolicy.Absolute||p.Missing!=RiskHistoryPolicy.Complete||
            p.MinimumSamples<2||p.MinimumSamples>=MaximumSessions||
            (p.Alignment==RiskHistoryPolicy.ExactCloses?(p.Regimes!=RiskHistoryPolicy.Fixed||!p.PriceTypes.IsDefault):
             (p.Regimes!=RiskHistoryPolicy.PriceOrder||p.PriceTypes.IsDefaultOrEmpty||p.PriceTypes.Length>PriceTypes.Length||p.PriceTypes.Distinct().Count()!=p.PriceTypes.Length||p.PriceTypes.Any(t=>!PriceTypes.Any(v=>v.Code==t)))))
            throw Error("risk_history_policy","Choose a supported history policy and a minimum of 2–2048 complete daily changes. End-of-day history also needs an ordered list of allowed price types.");
    }
    private static ImmutableArray<RiskHistoryColumn> Columns(RiskHistoryForecast forecast,ImmutableArray<RiskHistoryFactor> factors)
    {
        if(factors.IsDefaultOrEmpty||factors.Length>100000||factors.Any(x=>x?.Factor is not {} f||f.Unit is null||f.Input.Series.Id==Guid.Empty||
            !Enum.IsDefined(f.Input.Use)||!Enum.IsDefined(f.Role)||!Enum.IsDefined(x.State)||f.Contract.Id==Guid.Empty||f.Contract.Revision!=f.Input.Series.Revision||
            f.Observation.Id==Guid.Empty||f.Observation.CatalogId!=forecast.CatalogId||!forecast.Markets.Any(m=>m.Definitions==f.Input.Series.Revision)||f.Curves.IsDefaultOrEmpty))
            throw Error("risk_history_factor","Supply the complete held dated-factor mapping with exact units, source, role, state and base observation.");
        var unique=new Dictionary<InputKey,RiskHistoryFactor>();
        foreach(var mapping in factors)
        {
            var f=mapping.Factor;Money.RequireKey(f.Quote);Money.RequireKey(f.Source);
            if(f.Input.Use==InputUse.FrozenFact)continue;
            if(unique.TryGetValue(f.Input,out var previous))
            {
                var p=previous.Factor;
                if(p.Quote!=f.Quote||p.Unit!=f.Unit||p.Contract!=f.Contract||p.Observation!=f.Observation||p.Source!=f.Source||p.Role!=f.Role||
                    p.BaseValue!=f.BaseValue||p.TradingTick!=f.TradingTick||previous.State!=mapping.State)
                    throw Error("risk_history_factor","One shared input cannot have conflicting source, unit, dated contract or base-price mappings.");
                unique[f.Input]=previous with{Factor=p with{Curves=[..p.Curves.Concat(f.Curves).Distinct().OrderBy(c=>c.Revision.CatalogId).ThenBy(c=>c.Revision.RevisionId).ThenBy(c=>c.Code,StringComparer.Ordinal)]}};
            }
            else unique.Add(f.Input,mapping with{Factor=f with{Curves=[..f.Curves.Distinct().OrderBy(c=>c.Revision.CatalogId).ThenBy(c=>c.Revision.RevisionId).ThenBy(c=>c.Code,StringComparer.Ordinal)]}});
        }
        if(unique.Count==0)throw Error("risk_history_factor","This scope has no live market factors to calibrate.");
        if(unique.Count>MaximumFactors)throw Error("risk_history_capacity","At most 1024 distinct held factors are admitted.");
        return [..unique.Values.OrderBy(x=>x.Factor.Input.Series.Revision.RevisionId).ThenBy(x=>x.Factor.Input.Series.Id)
            .Select(x=>new RiskHistoryColumn(Sha(new{x.Factor.Input,x.Factor.Contract,x.Factor.Quote,unit=Unit(x.Factor.Unit),x.Factor.Source,x.Factor.Role,x.State}),x))];
    }
    private static bool Mapping(RiskHistoryFactor mapping,RiskHistoryObservation row)=>mapping.Factor.Input.Series==row.Observation.Series&&
        mapping.Factor.Source==row.Observation.Source&&mapping.Factor.Role==row.Observation.Role&&mapping.Factor.Quote==row.Quote&&mapping.Factor.Unit==row.Unit&&mapping.Factor.Contract==row.Contract;
    private static void RequireCalendar(RiskHistoryCalendar calendar)
    {
        if(calendar is null||calendar.From>=calendar.To||calendar.To.DayNumber-calendar.From.DayNumber>MaximumCalendarDays||
            calendar.BusinessDates.IsDefault||calendar.BusinessDates.Length<2||calendar.BusinessDates.Length>MaximumSessions||
            calendar.BusinessDates.Any(d=>d<calendar.From||d>=calendar.To)||calendar.BusinessDates.Distinct().Count()!=calendar.BusinessDates.Length)
            throw Error("risk_history_calendar","Supply two to 2049 distinct business sessions in at most 3660 days of covered history.");
        Money.RequireKey(calendar.Reference);
    }
    private static ImmutableArray<RiskHistorySession> Sessions(RiskHistoryCalendar calendar,RiskHistoryClock clock,RiskHistoryForecast forecast)
    {
        RequireCalendar(calendar);
        if(clock is null||calendar.Reference.Revision!=clock.Reference.Revision||clock.AmbiguousTime!="reject"||clock.InvalidTime!="reject"||
            clock.Resolution!=RiskHistoryClock.Captured||!clock.DayWindows.IsDefault||string.IsNullOrWhiteSpace(clock.Rules)||clock.Rules.Length>65536||!Hash(clock.Fingerprint)||
            clock.Fingerprint!=Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(clock.Rules)))||clock.ResolvedCloses.IsDefault||
            clock.ResolvedCloses.Length!=calendar.BusinessDates.Length||!clock.ResolvedCloses.Select(c=>c.Date).Order().SequenceEqual(calendar.BusinessDates.Order())||
            !TimeOnly.TryParseExact(clock.CloseTime,"HH:mm:ss",CultureInfo.InvariantCulture,DateTimeStyles.None,out var time))
            throw Error("risk_history_calendar","Supply the captured calendar-close convention, complete UTC/offset schedule and pinned clock evidence.");
        Money.RequireKey(clock.Reference);
        foreach(var close in clock.ResolvedCloses)
            if(!Utc(close.Instant)||close.OffsetSeconds%60!=0||close.OffsetSeconds is < -50400 or >50400||
                new DateTimeOffset(close.Date.ToDateTime(time,DateTimeKind.Unspecified),TimeSpan.FromSeconds(close.OffsetSeconds)).ToUniversalTime()!=close.Instant)
                throw Error("risk_history_calendar","Each retained close must agree with its exact business date, local time and captured offset.");
        var sessions=clock.ResolvedCloses.OrderBy(c=>c.Date).Select(c=>new RiskHistorySession(c.Date,c.Instant)).ToImmutableArray();
        if(sessions.Any(s=>s.Close>forecast.MarketCutoff||s.Close>forecast.ValuationAt)||sessions.Zip(sessions.Skip(1)).Any(p=>p.First.Close>=p.Second.Close))
            throw Error("risk_history_context","Historical closes must be ordered and no later than both forecast market and valuation cutoffs.");
        return sessions;
    }
    private static bool Hash(string? value)=>value?.Length==64&&value.All(c=>c is >= '0' and <= '9' or >= 'a' and <= 'f');
    private static string Sha(object value)=>Convert.ToHexStringLower(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(value)));
    private static object Unit(PriceUnit u)=>new{u.Currency,u.Quantity,multiplier=u.Multiplier.ToString()};
    private static object Endpoint(RiskHistoryEndpoint p)=>new{p.At,marks=p.Marks.Select(m=>m is null?null:new{m.Observation,value=m.Value.ToString(),m.KnowledgeOrder,m.KnownAt,m.Lineage})};
    private static string Digest(AlignedFactorHistory h)
    {
        var legacy=new{h.Forecast,h.Policy,h.Calendar,h.Clock,
        columns=h.Columns.Select(c=>new{c.Id,factor=new{c.Mapping.Factor.Input,c.Mapping.Factor.Quote,unit=Unit(c.Mapping.Factor.Unit),c.Mapping.Factor.Contract,
            c.Mapping.Factor.Observation,c.Mapping.Factor.Source,c.Mapping.Factor.Role,baseValue=c.Mapping.Factor.BaseValue.ToString(),c.Mapping.Factor.Curves,
            tradingTick=c.Mapping.Factor.TradingTick.ToString()},c.Mapping.State}),h.Sessions,
        evidence=h.Evidence.Select(r=>new{r.Observation.Key,r.Observation.Series,r.Observation.Source,r.Observation.Role,r.Observation.State,r.Observation.ObservedAt,
            value=r.Observation.Value.ToString(),r.Quote,unit=Unit(r.Unit),r.Contract,r.Revises,r.KnowledgeOrder,r.KnownAt}),
        samples=h.Samples.Select(s=>new{s.Id,from=Endpoint(s.From),to=Endpoint(s.To),changes=s.Changes.Select(v=>v.ToString())}),
        rejected=h.Rejected.Select(s=>new{s.Id,from=Endpoint(s.From),to=Endpoint(s.To),s.Issues}),h.Available,h.UnavailableReason};
        return h.DayPrices.IsDefault?Sha(legacy):Sha(new{history=legacy,h.DayPrices});
    }
}
