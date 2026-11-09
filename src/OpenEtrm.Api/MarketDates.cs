using System.Collections.Immutable;
using System.Globalization;
using System.Text.Json.Serialization;
using Npgsql;
using OpenEtrm.MarketData;
using OpenEtrm.MarketData.CustomCurves;
using OpenEtrm.Shared.Temporal;

namespace OpenEtrm.Api;

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record MarketDatesRequest([property:JsonRequired] string? Curve,[property:JsonRequired] string TimeZone,[property:JsonRequired] string Session,
    RecipeDefinition? Definition=null,int? MaximumDates=null,Guid? MarketRevisionId=null);
public sealed record MarketDateFrame(Guid Id,DateTimeOffset At,string Name,string BusinessDate);
// A session business day: its one close in force, and how many recorded closes it replaced (D334).
public sealed record MarketDateChoice(string Date,ImmutableArray<MarketDateFrame> Frames,int Replaced);
public sealed record UnavailableMarketDate(string Date,string Reason);
public sealed record MarketDatesResponse(ScheduleContext? Context,string? Curve,string TimeZone,string SelectionPolicy,
    ImmutableArray<MarketDateChoice> Dates,ImmutableArray<UnavailableMarketDate> Unavailable,string? MinDate,string? MaxDate)
{
    public FrameCatalogInfo? FrameCatalog{get;init;}
    [JsonIgnore(Condition=JsonIgnoreCondition.WhenWritingNull)] public Guid? MarketRevisionId{get;init;}
    public bool HasMoreCandidates{get;init;}
}
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record LastClosesRequest([property:JsonRequired] string Session);
// The newest close in force of the session that can price a listed curve. A curve with none is left out.
public sealed record CurveLastClose(string Curve,Guid Id,string BusinessDate,DateTimeOffset At);
public sealed record LastClosesResponse(ScheduleContext? Context,string Session,ImmutableArray<CurveLastClose> Curves);
public delegate Task<IResult> MarketDatesOperation(HttpContext context,
    Func<NpgsqlConnection,PinnedContext,ImmutableArray<InputFrame>,FrameCatalogInfo,CancellationToken,Task<IResult>> work);

// Calendar availability is a business read. It qualifies exact retained snapshot
// choices without filling prices, selecting a close, or widening knowledge.
public static class MarketDates
{
    public const string SelectionPolicy="exact_snapshot_no_close_substitution_v1";
    public static void MapMarketDates(this RouteGroupBuilder api,PreviewDatabase database,MarketDatesOperation operation)
    {
        api.MapPost("/market/dates",(HttpContext context,MarketDatesRequest body)=>operation(context,async(connection,pin,frames,catalog,ct)=>
            Results.Ok((await Read(database,connection,pin,frames,body,ct)) with{FrameCatalog=catalog})));
        api.MapPost("/market/last-closes",(HttpContext context,LastClosesRequest body)=>operation(context,async(connection,pin,frames,_,ct)=>
            Results.Ok(await LastCloses(database,connection,pin,frames,body,ct))));
    }
    public static async Task<MarketDatesResponse> Read(PreviewDatabase database,NpgsqlConnection connection,PinnedContext pin,
        ImmutableArray<InputFrame> frames,MarketDatesRequest request,CancellationToken ct)
    {
        if(request.Curve is not null&&(string.IsNullOrWhiteSpace(request.Curve)||request.Definition is not null))
            throw new MarketDataException("market_dates_input","Choose one exact curve or one custom definition, or explicitly request workspace dates.");
        TimeZoneInfo zone;
        try{zone=TimeZoneInfo.FindSystemTimeZoneById(request.TimeZone??"");}
        catch(Exception error) when(error is TimeZoneNotFoundException or InvalidTimeZoneException or ArgumentException)
        {throw new TemporalException("time_zone","Choose an available display time zone explicitly.");}
        frames=await BindSessionCloses(database,connection,pin,frames,request.Session,ct);
        if(request.MaximumDates is <1 or >128)throw new MarketDataException("market_dates_input","Request from one to 128 dates explicitly.");
        var revision=CurveAuthoringMarket.Revision(database,request.MarketRevisionId);
        var sourceUnavailable=new List<UnavailableMarketDate>();
        if(revision!=database.Manifest.RevisionId)
        {
            var scoped=ImmutableArray.CreateBuilder<InputFrame>();
            foreach(var frame in frames)
            {
                try{scoped.Add(CurveAuthoringMarket.Frame(database,frame,revision));}
                catch(MarketDataException error)when(error.Code=="curve_market")
                {sourceUnavailable.Add(new(Day(frame).ToString("yyyy-MM-dd",CultureInfo.InvariantCulture),error.Code));}
            }
            frames=scoped.ToImmutable();
        }
        var data=await database.Read(connection,ct,selectedRevision:revision,observationIds:[],includeCalendarDays:false);
        var metadata=data;
        var curveCodes=data.Definitions["curves"].Select(row=>row.Text("code")).ToHashSet(StringComparer.Ordinal);
        var rateCodes=data.Definitions["rate_curves"].Select(row=>row.Text("code")).ToHashSet(StringComparer.Ordinal);
        if(request.Curve is {} curve&&!curveCodes.Contains(curve)&&!rateCodes.Contains(curve))
            throw new MarketDataException("missing_curve","Choose an exact visible curve from the selected definition bundle.");
        Func<InputFrame,CancellationToken,Task> qualify;
        // Rate curves' pinned date lists load once, before frames stream: each close's tenors then resolve in memory (D359).
        var rateSources=new Dictionary<string,RateTenorSource>(StringComparer.Ordinal);
        if(request.Definition is null)await LoadRateSources(connection,data,frames,rateCodes.Where(code=>request.Curve is null||request.Curve==code),rateSources,ct);
        if(request.Definition is {} definition)
        {
            CustomCurves.Validate(definition);
            var references=await new CustomCurvesStore(database).References(connection,definition,data,ct);
            var period=data.Definitions["revision_periods"].Single();
            var from=DateWorkspace.Date(period.Text("effective_from"));var to=DateWorkspace.Date(period.Text("effective_to"));
            var contexts=new Dictionary<DateOnly,ScheduleContext>();
            var planningErrors=new Dictionary<Guid,Exception>();
            var retainedSamples=false;
            var exactSeries=new HashSet<Guid>();var publicationObservations=new HashSet<Guid>();
            var exactSamples=new Dictionary<Guid,PreviewData>();
            // Exact immutable reference calendars stay inside this authorized
            // operation; each candidate still binds and checks its own context.
            var calendarReads=new Dictionary<CalendarReference,ReferenceCalendar>();
            var sampledHistory=data;
            ScheduleContext? knowledge=pin.Context;
            if(pin.Context is null)
            {
                // One certified cutoff and immutable history for the entire working
                // calendar. Effective dates still get separate exact contexts below.
                var anchor=frames.Where(frame=>Day(frame)>=from&&Day(frame)<to).OrderByDescending(frame=>frame.At).FirstOrDefault();
                if(anchor is not null)
                {
                    knowledge=(await new KnowledgeStore(database.Manifest.CatalogId).Capture(connection,
                        new(Day(anchor).ToString("yyyy-MM-dd",CultureInfo.InvariantCulture),null,"current"),ct)).Context;
                    contexts.Add(Day(anchor),knowledge);
                    (data,references)=await AtContext(knowledge,async()=>
                    {
                        var captured=await database.Read(connection,ct,selectedRevision:revision,observationIds:[],includeCalendarDays:false);
                        return(captured,await new CustomCurvesStore(database).References(connection,definition,captured,ct));
                    });
                }
            }
            async Task<ScheduleContext> Context(DateOnly day)
            {
                if(contexts.TryGetValue(day,out var selected))return selected;
                selected=pin.Context??(await new KnowledgeStore(database.Manifest.CatalogId).Capture(connection,
                    new(day.ToString("yyyy-MM-dd",CultureInfo.InvariantCulture),knowledge!.KnownAt,"at"),ct)).Context;
                contexts.Add(day,selected);return selected;
            }
            if(knowledge is not null&&request.MaximumDates is null)
            {
                var windows=new HashSet<ObservationWindow>();
                var bounded=true;
                foreach(var frame in frames)
                {
                    var day=Day(frame);
                    if(day<from||day>=to||pin.Context is {} retained&&day!=DateWorkspace.Date(retained.EffectiveOn))continue;
                    var selected=await Context(day);
                    try
                    {
                        var demand=await AtContext(selected,()=>CustomCurves.ObservationDemand(connection,database,definition,data,
                            frame with{ContextId=selected.ContextId},references.Sources,references.Calendars,references.Schedules,selected,ct,calendarReads));
                        windows.UnionWith(demand.Windows);
                        exactSeries.UnionWith(demand.ExactSeries);
                        publicationObservations.UnionWith(demand.ExactObservations);
                        if(windows.Count>PreviewDatabase.MaximumObservationWindows||publicationObservations.Count>PreviewDatabase.MaximumObservationWindows){bounded=false;break;}
                    }
                    catch(MarketDataException error){planningErrors.Add(frame.Id,error);}
                    catch(TemporalException error){planningErrors.Add(frame.Id,error);}
                }
                if(bounded)
                {
                    sampledHistory=windows.Count==0&&publicationObservations.Count==0?data:await AtContext(knowledge,()=>database.ReadObservationWindows(connection,data,[..windows],ct,[..publicationObservations]));
                    if(exactSeries.Count>0)await AtContext(knowledge,async()=>
                    {
                        var index=0;
                        await foreach(var selected in database.ReadFrameObservations(connection,data,frames,ObservationReadScope.ExactPins,ct,[..exactSeries]))
                            exactSamples.Add(frames[index++].Id,selected);
                        return true;
                    });
                    retainedSamples=true;
                }
            }
            qualify=async(frame,cancellation)=>
            {
                if(planningErrors.TryGetValue(frame.Id,out var error))throw error;
                if(pin.Context is {} retained)
                {
                    _=await Evaluate(frame,retained,cancellation);return;
                }
                var day=Day(frame);
                if(knowledge is null||day<from||day>=to)
                    throw new MarketDataException("custom_curve_definition","The snapshot must lie within its exact source definition's effective period.");
                var selected=await Context(day);
                _=await AtContext(selected,()=>Evaluate(frame with{ContextId=selected.ContextId},selected,cancellation));
            };
            async Task<RecipeResult> Evaluate(InputFrame frame,ScheduleContext selected,CancellationToken cancellation)
            {
                var samples=data;
                InputFrame? sourceFrame=null;
                if(retainedSamples)
                {
                    samples=sampledHistory;
                    if(exactSamples.TryGetValue(frame.Id,out var current))
                    {
                        sourceFrame=current.HistoryFrame(frame);
                        samples=sampledHistory.WithObservations([..sampledHistory.Observations.Concat(current.Observations).DistinctBy(row=>row.Id("id"))]);
                    }
                }
                return await CustomCurves.EvaluateDefinition(connection,database,definition,samples,frame,
                    references.Sources,references.Calendars,references.Schedules,selected,cancellation,retainedObservations:retainedSamples,sourceFrame:sourceFrame,calendarReads:calendarReads);
            }
        }
        else qualify=(frame,cancellation)=>
        {
            cancellation.ThrowIfCancellationRequested();
            if(request.Curve is {} selected){QualifyCurve(data,frame,selected,rateSources);return Task.CompletedTask;}
            foreach(var code in frame.CurveContracts.Keys.Where(curveCodes.Contains).Concat(rateCodes).Order(StringComparer.Ordinal))
            {
                cancellation.ThrowIfCancellationRequested();
                try{QualifyCurve(data,frame,code,rateSources);return Task.CompletedTask;}
                catch(MarketDataException){/* One unsupported curve does not qualify or hide another curve's explicit data. */}
            }
            throw new MarketDataException("market_data_unavailable","No complete curve can be evaluated from this exact snapshot.");
        };
        var available=new List<(string Date,MarketDateFrame Frame)>();
        var unavailable=new List<UnavailableMarketDate>(sourceUnavailable);
        var processed=0;
        async Task Qualify(InputFrame frame)
        {
            ct.ThrowIfCancellationRequested();
            var businessDate=Day(frame).ToString("yyyy-MM-dd",CultureInfo.InvariantCulture);
            var date=businessDate;
            if(pin.Context is {} context&&businessDate!=context.EffectiveOn)
            {unavailable.Add(new(date,"market_date"));return;}
            try
            {
                await qualify(frame,ct);
                available.Add((date,new(frame.Id,frame.At,frame.Name,businessDate)));
            }
            catch(MarketDataException error){unavailable.Add(new(date,error.Code));}
            catch(TemporalException error){unavailable.Add(new(date,error.Code));}
        }
        foreach(var batch in CandidateBatches(frames,zone,request.MaximumDates is not null))
        {
            if(request.Definition is null)
            {
                var index=0;
                await foreach(var selected in database.ReadFrameObservations(connection,metadata,batch,ObservationReadScope.ExactPins,ct))
                {
                    data=selected;await Qualify(batch[index++]);processed++;
                }
            }
            else foreach(var frame in batch){await Qualify(frame);processed++;}
            if(request.MaximumDates is {} maximum&&available.Select(item=>item.Date).Distinct().Count()>=maximum)break;
        }
        var replaced=await ReplacedCounts(connection,database.Manifest.CatalogId,request.Session,ct);
        // A day whose close in force this reader cannot see is unavailable, never answered with the close it replaced.
        unavailable.AddRange((await HiddenDays(connection,database.Manifest.CatalogId,request.Session,ct)).Select(day=>new UnavailableMarketDate(day,"close_not_visible")));
        var dates=available.GroupBy(row=>row.Date,StringComparer.Ordinal).OrderBy(group=>group.Key,StringComparer.Ordinal)
            .Select(group=>new MarketDateChoice(group.Key,[..group.Select(row=>row.Frame).OrderBy(frame=>frame.At).ThenBy(frame=>frame.Id)],
                replaced.GetValueOrDefault(group.Key))).ToImmutableArray();
        var validDates=dates.Select(day=>day.Date).ToHashSet(StringComparer.Ordinal);
        await RequireAccess(database,connection,ct,metadata);
        return new(pin.Context,request.Curve,zone.Id,SelectionPolicy,dates,
            [..unavailable.Where(day=>!validDates.Contains(day.Date)).Distinct().OrderBy(day=>day.Date,StringComparer.Ordinal).ThenBy(day=>day.Reason,StringComparer.Ordinal)],
            dates.IsEmpty?null:dates[0].Date,dates.IsEmpty?null:dates[^1].Date){HasMoreCandidates=processed<frames.Length,MarketRevisionId=request.MarketRevisionId};

        async Task<T> AtContext<T>(ScheduleContext context,Func<Task<T>> read)
        {
            await using(var save=ScheduleStore.Command(connection,"SAVEPOINT market_dates_read"))await save.ExecuteNonQueryAsync(ct);
            try
            {
                await PinnedContext.BindExact(connection,database.Manifest.CatalogId,context,ct);
                await RequireAccess(database,connection,ct,metadata);
                return await read();
            }
            finally
            {
                await using var restore=ScheduleStore.Command(connection,"ROLLBACK TO SAVEPOINT market_dates_read;RELEASE SAVEPOINT market_dates_read");
                await restore.ExecuteNonQueryAsync(CancellationToken.None);
            }
        }
    }
    // Walks the session's closes newest first and stops once every listed curve has one, so a read of current curves touches one close.
    public static async Task<LastClosesResponse> LastCloses(PreviewDatabase database,NpgsqlConnection connection,PinnedContext pin,
        ImmutableArray<InputFrame> frames,LastClosesRequest request,CancellationToken ct)
    {
        frames=await BindSessionCloses(database,connection,pin,frames,request.Session,ct);
        var metadata=await database.Read(connection,ct,observationIds:[],includeCalendarDays:false);
        var pending=(await MarketNavigation.ReadCurves(connection,database.Manifest,ct)).Select(curve=>curve.Code).ToHashSet(StringComparer.Ordinal);
        var rateSources=new Dictionary<string,RateTenorSource>(StringComparer.Ordinal);
        await LoadRateSources(connection,metadata,frames,pending.Where(metadata.IsRateCurve),rateSources,ct);
        var found=ImmutableArray.CreateBuilder<CurveLastClose>();
        // A historical view reads only its own business day, as its calendar does.
        var candidates=pin.Context is {} context?frames.Where(frame=>Day(frame).ToString("yyyy-MM-dd",CultureInfo.InvariantCulture)==context.EffectiveOn).ToImmutableArray():frames;
        foreach(var batch in CandidateBatches(candidates,TimeZoneInfo.Utc,limited:true))
        {
            var index=0;
            await foreach(var selected in database.ReadFrameObservations(connection,metadata,batch,ObservationReadScope.ExactPins,ct))
            {
                var frame=batch[index++];
                foreach(var code in pending.Order(StringComparer.Ordinal).ToArray())
                {
                    ct.ThrowIfCancellationRequested();
                    try{QualifyCurve(selected,frame,code,rateSources);}
                    catch(Exception error) when(error is MarketDataException or TemporalException){continue;}
                    found.Add(new(code,frame.Id,Day(frame).ToString("yyyy-MM-dd",CultureInfo.InvariantCulture),frame.At));
                    pending.Remove(code);
                }
            }
            if(pending.Count==0)break;
        }
        await RequireAccess(database,connection,ct);
        return new(pin.Context,request.Session,[..found.OrderBy(curve=>curve.Curve,StringComparer.Ordinal)]);
    }
    // Only recorded closes of the named session are closes; a day lists the close in force alone (D334).
    static async Task<ImmutableArray<InputFrame>> BindSessionCloses(PreviewDatabase database,NpgsqlConnection connection,PinnedContext pin,
        ImmutableArray<InputFrame> frames,string session,CancellationToken ct)
    {
        if(frames.IsDefault||frames.Length>128||frames.Select(frame=>frame.Id).Distinct().Count()!=frames.Length)
            throw new MarketDataException("market_dates_input","Supply at most 128 distinct retained snapshot choices.");
        if(string.IsNullOrWhiteSpace(session))throw new MarketDataException("market_dates_input","Choose a close session explicitly.");
        ImmutableArray<InputFrame> closes=[..frames.Where(frame=>frame.Close?.Session==session&&frame.Close.ReplacedBy is null)];
        foreach(var frame in closes)_=pin.Require(frame);
        await pin.Bind(connection,database.Manifest.CatalogId,ct);
        await SelectedClose.BindSession(connection,database.Manifest.CatalogId,session,ct);
        await RequireAccess(database,connection,ct);
        return closes;
    }
    // Rate curves' pinned date lists load once, before frames stream. A curve whose date list cannot load here is unavailable on every
    // date; it does not fail the others.
    static async Task LoadRateSources(NpgsqlConnection connection,PreviewData data,ImmutableArray<InputFrame> frames,IEnumerable<string> codes,
        Dictionary<string,RateTenorSource> sources,CancellationToken ct)
    {
        if(frames.Length==0)return;
        foreach(var code in codes.Order(StringComparer.Ordinal))
        {
            try{sources[code]=await RateCurveResolver.Load(connection,data,frames[0],code,ct);}
            catch(Exception error) when(error is MarketDataException or TemporalException){}
        }
    }
    static DateOnly Day(InputFrame frame)=>frame.Close!.BusinessDate;
    internal static IEnumerable<ImmutableArray<InputFrame>> CandidateBatches(ImmutableArray<InputFrame> frames,TimeZoneInfo zone,bool limited)
    {
        if(!limited){yield return [..frames.OrderBy(frame=>frame.At).ThenBy(frame=>frame.Id)];yield break;}
        foreach(var day in frames.GroupBy(frame=>frame.Close?.BusinessDate??DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(frame.At,zone).DateTime)).OrderByDescending(group=>group.Key))
            yield return [..day.OrderByDescending(frame=>frame.At).ThenBy(frame=>frame.Id)];
    }
    // How many visible recorded closes each business day of the session has beyond its close in force.
    static async Task<Dictionary<string,int>> ReplacedCounts(NpgsqlConnection connection,Guid catalog,string session,CancellationToken ct)
    {
        var rows=await PreviewDatabase.Rows(connection,"SELECT json_build_object('day',business_date,'replaced',count(*)-1)::text FROM market_data.market_closes "+
            "WHERE catalog_id=$1 AND session_code=$2 GROUP BY business_date",[catalog,session],ct);
        return rows.ToDictionary(row=>row.Text("day"),row=>int.Parse(row.Text("replaced"),CultureInfo.InvariantCulture),StringComparer.Ordinal);
    }
    static async Task<string[]> HiddenDays(NpgsqlConnection connection,Guid catalog,string session,CancellationToken ct)
    {
        await using var command=ScheduleStore.Command(connection,"SELECT array(SELECT to_char(d,'YYYY-MM-DD') FROM unnest(security.close_session_hidden_days($1,$2))d)",catalog,session);
        return (string[])(await command.ExecuteScalarAsync(ct))!;
    }
    static async Task RequireAccess(PreviewDatabase database,NpgsqlConnection connection,CancellationToken ct,PreviewData? selected=null)
    {
        var manifest=database.Manifest;
        await using var command=ScheduleStore.Command(connection,"SELECT security.market_fact_visible($1,$2,NULL) AND security.reference_version_visible($3,$4)",
            selected?.DefinitionRevision.CatalogId??manifest.CatalogId,selected?.DefinitionRevision.RevisionId??manifest.RevisionId,
            selected?.ReferenceRevision.CatalogId??manifest.ReferenceCatalogId,selected?.ReferenceRevision.RevisionId??manifest.ReferenceRevisionId);
        if(!Equals(await command.ExecuteScalarAsync(ct),true))throw new PreviewDeniedException();
    }
    static void QualifyCurve(PreviewData data,InputFrame frame,string code,IReadOnlyDictionary<string,RateTenorSource> rateSources)
    {
        if(data.IsRateCurve(code))
        {
            if(data.BuildRates(frame,RateCurveResolver.At(data,frame,code,rateSources.GetValueOrDefault(code)??
                throw new MarketDataException("rate_definition","This rate curve's date list is unavailable in this view."))).Values.IsEmpty)throw new MarketDataException("market_data_unavailable","This exact snapshot has no rate points.");
        }
        else if(data.Build(frame,code).Values.IsEmpty)throw new MarketDataException("market_data_unavailable","This exact snapshot has no live curve points.");
    }
}
