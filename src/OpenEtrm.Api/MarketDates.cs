using System.Collections.Immutable;
using System.Globalization;
using System.Text.Json.Serialization;
using Npgsql;
using OpenEtrm.MarketData;
using OpenEtrm.MarketData.CustomCurves;
using OpenEtrm.Shared.Temporal;

namespace OpenEtrm.Api;

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record MarketDatesRequest([property:JsonRequired] string? Curve,[property:JsonRequired] string TimeZone,RecipeDefinition? Definition=null);
public sealed record MarketDateFrame(Guid Id,DateTimeOffset At,string Name,string BusinessDateUTC);
public sealed record MarketDateChoice(string Date,ImmutableArray<MarketDateFrame> Frames);
public sealed record UnavailableMarketDate(string Date,string Reason);
public sealed record MarketDatesResponse(ScheduleContext? Context,string? Curve,string TimeZone,string SelectionPolicy,
    ImmutableArray<MarketDateChoice> Dates,ImmutableArray<UnavailableMarketDate> Unavailable,string? MinDate,string? MaxDate)
{
    public FrameCatalogInfo? FrameCatalog{get;init;}
}
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
        if(frames.IsDefault||frames.Length>128||frames.Select(frame=>frame.Id).Distinct().Count()!=frames.Length)
            throw new MarketDataException("market_dates_input","Supply at most 128 distinct retained snapshot choices.");
        foreach(var frame in frames)_=pin.Require(frame);
        await pin.Bind(connection,database.Manifest.CatalogId,ct);
        await RequireAccess(database,connection,ct);
        // Built-in curves consume every offered frame's exact snapshot pins.
        // Custom recipes retain their broader historical correction policy.
        var data=request.Definition is null&&!frames.IsEmpty
            ?await database.Read(connection,ct,observationIds:[..frames.SelectMany(frame=>frame.ObservationIds).Distinct()],
                includeCalendarDays:false,observationScope:ObservationReadScope.ExactPins)
            :await database.Read(connection,ct,includeCalendarDays:false);
        var curveCodes=data.Definitions["curves"].Select(row=>row.Text("code")).ToHashSet(StringComparer.Ordinal);
        var rateCodes=data.Definitions["rate_curves"].Select(row=>row.Text("code")).ToHashSet(StringComparer.Ordinal);
        if(request.Curve is {} curve&&!curveCodes.Contains(curve)&&!rateCodes.Contains(curve))
            throw new MarketDataException("missing_curve","Choose an exact visible curve from the selected definition bundle.");
        Func<InputFrame,CancellationToken,Task> qualify;
        if(request.Definition is {} definition)
        {
            CustomCurves.Validate(definition);
            var references=await new CustomCurvesStore(database).References(connection,definition,data,ct);
            qualify=async(frame,cancellation)=>
            {
                _=await CustomCurves.EvaluateDefinition(connection,database,definition,data,frame,
                    references.Sources,references.Calendars,references.Schedules,pin.Context,cancellation);
            };
        }
        else qualify=(frame,cancellation)=>
        {
            cancellation.ThrowIfCancellationRequested();
            if(request.Curve is {} selected){QualifyCurve(data,frame,selected);return Task.CompletedTask;}
            foreach(var code in frame.CurveContracts.Keys.Where(curveCodes.Contains).Concat(rateCodes).Order(StringComparer.Ordinal))
            {
                cancellation.ThrowIfCancellationRequested();
                try{QualifyCurve(data,frame,code);return Task.CompletedTask;}
                catch(MarketDataException){/* One unsupported curve does not qualify or hide another curve's explicit data. */}
            }
            throw new MarketDataException("market_data_unavailable","No complete curve can be evaluated from this exact snapshot.");
        };
        var available=new List<(string Date,MarketDateFrame Frame)>();
        var unavailable=new List<UnavailableMarketDate>();
        foreach(var frame in frames.OrderBy(frame=>frame.At).ThenBy(frame=>frame.Id))
        {
            ct.ThrowIfCancellationRequested();
            var date=DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(frame.At,zone).DateTime).ToString("yyyy-MM-dd",CultureInfo.InvariantCulture);
            var businessDate=DateOnly.FromDateTime(frame.At.UtcDateTime).ToString("yyyy-MM-dd",CultureInfo.InvariantCulture);
            if(pin.Context is {} context&&businessDate!=context.EffectiveOn)
            {unavailable.Add(new(date,"market_date"));continue;}
            try
            {
                await qualify(frame,ct);
                available.Add((date,new(frame.Id,frame.At,frame.Name,businessDate)));
            }
            catch(MarketDataException error){unavailable.Add(new(date,error.Code));}
            catch(TemporalException error){unavailable.Add(new(date,error.Code));}
        }
        var dates=available.GroupBy(row=>row.Date,StringComparer.Ordinal).OrderBy(group=>group.Key,StringComparer.Ordinal)
            .Select(group=>new MarketDateChoice(group.Key,[..group.Select(row=>row.Frame)])).ToImmutableArray();
        var validDates=dates.Select(day=>day.Date).ToHashSet(StringComparer.Ordinal);
        await RequireAccess(database,connection,ct);
        return new(pin.Context,request.Curve,zone.Id,SelectionPolicy,dates,
            [..unavailable.Where(day=>!validDates.Contains(day.Date)).Distinct().OrderBy(day=>day.Date,StringComparer.Ordinal).ThenBy(day=>day.Reason,StringComparer.Ordinal)],
            dates.IsEmpty?null:dates[0].Date,dates.IsEmpty?null:dates[^1].Date);
    }
    static async Task RequireAccess(PreviewDatabase database,NpgsqlConnection connection,CancellationToken ct)
    {
        var manifest=database.Manifest;
        await using var command=ScheduleStore.Command(connection,"SELECT security.market_fact_visible($1,$2,NULL) AND security.reference_version_visible($3,$4)",
            manifest.CatalogId,manifest.RevisionId,manifest.ReferenceCatalogId,manifest.ReferenceRevisionId);
        if(!Equals(await command.ExecuteScalarAsync(ct),true))throw new PreviewDeniedException();
    }
    static void QualifyCurve(PreviewData data,InputFrame frame,string code)
    {
        if(data.IsRateCurve(code))
        {
            if(data.BuildRates(frame,code).Values.IsEmpty)throw new MarketDataException("market_data_unavailable","This exact snapshot has no rate points.");
        }
        else if(data.Build(frame,code).Values.IsEmpty)throw new MarketDataException("market_data_unavailable","This exact snapshot has no live curve points.");
    }
}
