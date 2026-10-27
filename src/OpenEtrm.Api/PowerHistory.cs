using System.Collections.Immutable;
using System.Text.Json;
using System.Text.Json.Nodes;
using Npgsql;
using OpenEtrm.MarketData;
using OpenEtrm.MarketData.Power;
using OpenEtrm.Shared.Analytics;
using OpenEtrm.Shared.Numerics;

namespace OpenEtrm.Api;

public sealed record PowerHistoryRequest(Guid SourceId,string From,string To,string KnownAt);
public sealed record PowerHistoryPeriodRequest(Guid SourceId,string FromDate,string ThroughDate,string KnownAt,ImmutableArray<OverlayRequest>? Overlays=null);
public sealed record DeliverySourceDays(Guid SourceId,string Name,IReadOnlyList<DateOnly> AvailableDates);
public sealed record DeliveryOpening(Guid SourceId,DateOnly FromDate,DateOnly ThroughDate,string Policy);
public sealed record DeliveryPriceExtreme(string Price,DateTimeOffset Start);
public sealed record DeliveryPeriodSummary(int Intervals,int ExpectedIntervals,int MissingIntervals,DeliveryPriceExtreme? Low,DeliveryPriceExtreme? High,string? Average,DateTimeOffset? LatestKnownAt,string Policy);
public static class PowerHistory
{
    internal sealed record SourceMetadata(JsonElement Display,OpenEtrm.MarketData.Power.ClockRule Clock,int Minutes);
    internal sealed record History(DateTimeOffset KnownAt,Guid ContextId,IntervalActual[] Rows);
    internal static async Task<SourceMetadata> Source(NpgsqlConnection connection,Guid id,CancellationToken ct)=>
        (await Sources(connection,id,ct)).SingleOrDefault()??throw new PreviewDeniedException();
    private static async Task<SourceMetadata[]> Sources(NpgsqlConnection connection,Guid? id,CancellationToken ct)
    {
        const string sql="""
            SELECT row_to_json(s)::text,q.commodity_code::text,c.name,l.name,m.display_code,u.symbol,row_to_json(k)::text
            FROM power_data.sources s
            LEFT JOIN ref_data.quantity_specifications q ON(q.catalog_id,q.revision_id,q.code)=(s.reference_catalog_id,s.reference_revision_id,s.quantity_code)
            LEFT JOIN ref_data.commodities c ON(c.catalog_id,c.revision_id,c.code)=(q.catalog_id,q.revision_id,q.commodity_code)
            LEFT JOIN ref_data.locations l ON(l.catalog_id,l.revision_id,l.code)=(s.reference_catalog_id,s.reference_revision_id,s.location_code)
            LEFT JOIN ref_data.currencies m ON(m.catalog_id,m.revision_id,m.code)=(s.reference_catalog_id,s.reference_revision_id,s.currency_code)
            LEFT JOIN ref_data.units u ON(u.catalog_id,u.revision_id,u.code)=(q.catalog_id,q.revision_id,q.unit_code)
            LEFT JOIN ref_data.clock_rules k ON(k.catalog_id,k.revision_id,k.code)=(s.reference_catalog_id,s.reference_revision_id,s.clock_code)
            """;
        await using var command=id is {} sourceId?ScheduleStore.Command(connection,sql+" WHERE s.id=$1",sourceId):ScheduleStore.Command(connection,sql+" ORDER BY s.provider,s.market,s.name,s.id");
        await using var reader=await command.ExecuteReaderAsync(ct);var result=new List<SourceMetadata>();
        while(await reader.ReadAsync(ct))
        {
            // A filtered required reference must not silently become a partial source.
            if(Enumerable.Range(1,6).Any(reader.IsDBNull))throw new PreviewDeniedException();
            var source=JsonSerializer.Deserialize<JsonElement>(reader.GetString(0));
            var clock=JsonSerializer.Deserialize<JsonElement>(reader.GetString(6));
            var rule=ReferenceClockJson.Read(clock);
            string Key(string code)=>$"{source.GetProperty("reference_catalog_id").GetGuid():D}:{source.GetProperty("reference_revision_id").GetGuid():D}:{code}";
            var display=JsonNode.Parse(source.GetRawText())!.AsObject();
            display["commodityKey"]=Key(reader.GetString(1));display["commodityName"]=reader.GetString(2);
            display["locationKey"]=Key(source.GetProperty("location_code").GetString()!);display["locationName"]=reader.GetString(3);
            display["marketName"]=source.GetProperty("market").GetString() switch {"DA"=>"Day-ahead","RT"=>"Real-time",_=>throw new MarketDataException("power_source","The source market has no supported classification.")};
            display["priceUnit"]=reader.GetString(4)+"/"+reader.GetString(5);
            display["marketClock"]=ClockName(rule);
            result.Add(new(JsonSerializer.SerializeToElement(display),rule,source.GetProperty("minutes").GetInt32()));
        }
        return result.ToArray();
    }
    private static string ClockName(OpenEtrm.MarketData.Power.ClockRule rule)
    {
        if(rule.Kind=="iana")return rule.IanaZone+" · tzdb "+rule.TzdbVersion;
        var offset=TimeSpan.FromSeconds(rule.FixedOffsetSeconds??throw new PreviewDeniedException());
        var duration=offset.Duration();
        return "UTC"+(offset<TimeSpan.Zero?"−":"+")+duration.ToString(duration.Seconds==0?@"hh\:mm":@"hh\:mm\:ss",System.Globalization.CultureInfo.InvariantCulture);
    }
    public static Dictionary<string,string> SelectableThrough(DateOnly[] available)
    {
        var dates=available.ToHashSet();var ranges=new Dictionary<string,string>();
        foreach(var first in dates.Order())
        {
            var through=first;
            for(var offset=1;offset<PowerImportProtocol.MaximumOperatingDays&&offset<=DateOnly.MaxValue.DayNumber-first.DayNumber&&dates.Contains(first.AddDays(offset));offset++)through=first.AddDays(offset);
            ranges.Add(first.ToString("yyyy-MM-dd",System.Globalization.CultureInfo.InvariantCulture),through.ToString("yyyy-MM-dd",System.Globalization.CultureInfo.InvariantCulture));
        }
        return ranges;
    }

    public static async Task<object> Catalog(NpgsqlConnection connection,CancellationToken ct)
    {
        Task<ImmutableArray<JsonElement>> Rows(string sql)=>PreviewDatabase.Rows(connection,sql,[],ct);
        await using var clock=ScheduleStore.Command(connection,"SELECT clock_timestamp()");
        var cutoff=(DateTime)(await clock.ExecuteScalarAsync(ct))!;
        await using var availability=ScheduleStore.Command(connection,"SELECT power_data.available_dates($1)::text",cutoff);
        using var available=JsonDocument.Parse((string)(await availability.ExecuteScalarAsync(ct))!);
        var dates=available.RootElement.EnumerateArray().ToDictionary(row=>row.GetProperty("id").GetGuid(),row=>row.GetProperty("availableDates"));
        var sources=new List<JsonObject>();var days=new List<DeliverySourceDays>();
        foreach(var metadata in await Sources(connection,null,ct))
        {
            var id=metadata.Display.GetProperty("id").GetGuid();
            if(!dates.TryGetValue(id,out var retained))continue;
            var selectable=retained.Deserialize<DateOnly[]>()!;
            var source=JsonNode.Parse(metadata.Display.GetRawText())!.AsObject();
            source["availableDates"]=JsonNode.Parse(retained.GetRawText());
            source["availableThrough"]=JsonSerializer.SerializeToNode(SelectableThrough(selectable));
            sources.Add(source);days.Add(new(id,metadata.Display.GetProperty("name").GetString()!,selectable));
        }
        var jobs=await Rows("SELECT row_to_json(j)::text FROM (SELECT id,name,provider,enabled,schedule_enabled,cron,schedule_zone FROM power_data.jobs)j");
        var attempts=await Rows("SELECT jsonb_build_object('jobId',j.id,'jobName',j.name,'provider',j.provider,'startedAt',a.started_at,'finishedAt',a.finished_at,'state',a.state,'errorCode',a.error_code,'fromDate',a.from_date,'throughDate',a.through_date)::text FROM power_data.attempts a JOIN power_data.jobs j ON j.id=a.job_id ORDER BY a.started_at DESC,a.id LIMIT 50");
        // Attempts are newest first, so the first is the newest attempt's outcome.
        var latest=attempts.IsEmpty?null:new {state=attempts[0].GetProperty("state").GetString(),finishedAt=attempts[0].GetProperty("finishedAt"),
            fromDate=attempts[0].GetProperty("fromDate").GetString(),throughDate=attempts[0].GetProperty("throughDate").GetString(),errorCode=attempts[0].GetProperty("errorCode")};
        var collection=new {jobs=jobs.Length,enabledJobs=jobs.Count(job=>job.GetProperty("enabled").GetBoolean()),latestAttempt=latest};
        return new {sources,opening=Opening(days),collection,jobs,attempts,knownAt=cutoff};
    }
    public const string OpeningPolicy="newest_available_delivery_day_v1";
    // newest_available_delivery_day_v1: the newest available operating day of any source; equal days
    // fall to the ordinal source name, then the identity's canonical text. No available day opens nothing.
    public static DeliveryOpening? Opening(IEnumerable<DeliverySourceDays> sources)=>sources.Where(source=>source.AvailableDates.Count>0)
        .Select(source=>(source,newest:source.AvailableDates.Max()))
        .OrderByDescending(item=>item.newest).ThenBy(item=>item.source.Name,StringComparer.Ordinal).ThenBy(item=>item.source.SourceId.ToString("D"),StringComparer.Ordinal)
        .Select(item=>new DeliveryOpening(item.source.SourceId,item.newest,item.newest,OpeningPolicy)).FirstOrDefault();
    // Counts come from the period's operating-day intervals, never from the rows read. Statistics are
    // exact over the published rows: low and high keep the recorded price at the earliest equal
    // instant, and the average weights each price by its interval's duration.
    public static DeliveryPeriodSummary Summarize(IReadOnlyList<PowerInterval> window,IReadOnlyList<IntervalActual> rows)
    {
        var published=rows.Select(row=>row.Start).ToHashSet();
        var missing=window.Count(interval=>!published.Contains(interval.Start));
        var ordered=rows.OrderBy(row=>row.Start).Select(row=>(row,value:Exact(row))).ToArray();
        DeliveryPriceExtreme? Extreme(Func<int,bool> better)
        {
            if(ordered.Length==0)return null;
            var chosen=ordered[0];
            foreach(var item in ordered.Skip(1))if(better(item.value.CompareTo(chosen.value)))chosen=item;
            return new(chosen.row.Price.Value.ToString(),chosen.row.Start);
        }
        var seconds=ordered.Select(item=>(long)(item.row.End-item.row.Start).TotalSeconds).ToArray();
        var average=ordered.Length==0?null:HalfEven18.Report(SignedRatio.Sum(ordered.Select((item,i)=>item.value*new SignedRatio(seconds[i],1)))/seconds.Sum());
        return new(rows.Count,window.Count,missing,Extreme(order=>order<0),Extreme(order=>order>0),average,rows.Count==0?null:rows.Max(row=>row.KnownAt),HalfEven18.Policy);
    }
    public const string NotPublished="not_published_at_cutoff";
    // The series the period is charted from: one point per operating-day interval, at its start, with the price published by the
    // cutoff, or no price and the reason where none was. The browser draws exactly these points (markets-desk.md, Delivery prices).
    public static ImmutableArray<HistoryChartPoint> Chart(IReadOnlyList<PowerInterval> window,IReadOnlyList<IntervalActual> rows)
    {
        var prices=rows.ToDictionary(row=>row.Start,row=>row.Price.Value.ToString());
        return [..window.Select(interval=>prices.TryGetValue(interval.Start,out var price)?new HistoryChartPoint(interval.Start,price,null):new(interval.Start,null,NotPublished))];
    }
    // The overlays' input: exactly the chart's points.
    public static ImmutableArray<OverlayPoint> OverlayPoints(IReadOnlyList<PowerInterval> window,IReadOnlyList<IntervalActual> rows)=>
        [..Chart(window,rows).Select(point=>new OverlayPoint(point.At,point.Value))];
    private static SignedRatio Exact(IntervalActual row)=>SignedRatio.TryParse(row.Price.Value.ToString(),out var value)?value:throw new MarketDataException("numeric_format","A recorded price is not a plain decimal.");
    public static async Task<object> Period(NpgsqlConnection connection,PowerHistoryPeriodRequest request,CancellationToken ct)
    {
        var source=(await Sources(connection,request.SourceId,ct)).SingleOrDefault()??throw new PreviewDeniedException();
        if(!DateOnly.TryParseExact(request.FromDate,"yyyy-MM-dd",System.Globalization.CultureInfo.InvariantCulture,System.Globalization.DateTimeStyles.None,out var first)||!DateOnly.TryParseExact(request.ThroughDate,"yyyy-MM-dd",System.Globalization.CultureInfo.InvariantCulture,System.Globalization.DateTimeStyles.None,out var last))throw new MarketDataException("power_period","Supply exact first and last operating dates.");
        var window=PowerImportProtocol.Window(first,last,OpenEtrm.MarketData.Power.PowerClock.FromRule(source.Clock,OpenEtrm.MarketData.Power.TimeZoneDatabase.Installed()),source.Minutes);
        var history=await ReadPrices(connection,request.SourceId,window[0].Start,window[^1].End,SchedulePreview.ParseAt(request.KnownAt),ct);
        var read=Response(source,history,request.SourceId);
        read["summary"]=Summarize(window,history.Rows);
        read["chart"]=new{points=Chart(window,history.Rows)};
        // The read holds one revision per interval, as known at the cutoff, so overlays see the same series.
        if(request.Overlays is {} overlays)read["overlays"]=Overlays.Compute(OverlayPoints(window,history.Rows),overlays);
        return read;
    }
    public static async Task<object> Read(NpgsqlConnection connection,PowerHistoryRequest request,CancellationToken ct)
    {
        var metadata=(await Sources(connection,request.SourceId,ct)).SingleOrDefault()??throw new PreviewDeniedException();
        return Response(metadata,await ReadPrices(connection,request.SourceId,SchedulePreview.ParseAt(request.From),SchedulePreview.ParseAt(request.To),SchedulePreview.ParseAt(request.KnownAt),ct),request.SourceId);
    }
    private static Dictionary<string,object?> Response(SourceMetadata metadata,History history,Guid sourceId)=>new()
    {
        ["source"]=metadata.Display,["priceUnit"]=metadata.Display.GetProperty("priceUnit").GetString(),["knownAt"]=history.KnownAt,["contextId"]=history.ContextId,
        ["rows"]=history.Rows.Select(row=>new {row.Start,row.End,row.Price,formattedPrice=row.Price.Value.ToString(),row.KnownAt,revision=row.Revision,row.BatchId,sourceId}).ToArray(),
    };
    // One authorized reader for display and settlement. Preserve the immutable
    // batch identity instead of discarding provenance while formatting history.
    internal static async Task<History> ReadPrices(NpgsqlConnection connection,Guid sourceId,DateTimeOffset from,DateTimeOffset to,DateTimeOffset cutoff,CancellationToken ct)
    {
        await using var command=ScheduleStore.Command(connection,"SELECT power_data.read_prices($1,$2,$3,$4)::text",sourceId,from,to,cutoff);
        string payload;
        try { payload=(string)(await command.ExecuteScalarAsync(ct))!; }
        catch(PostgresException e) when(e.SqlState=="P0001" && e.MessageText=="collection_window")
        { throw new MarketDataException("collection_window","Supply an explicit retained price cutoff no later than the database clock and a supported delivery window."); }
        using var document=JsonDocument.Parse(payload);var root=document.RootElement;var source=root.GetProperty("source");
        var revision=new ReferenceRevision(source.GetProperty("reference_catalog_id").GetGuid(),source.GetProperty("reference_revision_id").GetGuid());
        var unit=new PriceUnit(new(revision,source.GetProperty("currency_code").GetString()!),new(revision,source.GetProperty("quantity_code").GetString()!),ExactDecimal.Parse("1"));
        return new(cutoff,root.GetProperty("contextId").GetGuid(),root.GetProperty("rows").EnumerateArray().Select(row=>new IntervalActual(
            row.GetProperty("starts_at").GetDateTimeOffset().ToUniversalTime(),row.GetProperty("ends_at").GetDateTimeOffset().ToUniversalTime(),
            new(ExactDecimal.Parse(row.GetProperty("price").GetString()!),unit),row.GetProperty("known_at").GetDateTimeOffset().ToUniversalTime(),
            row.GetProperty("batch_id").GetGuid(),row.GetProperty("provider_revision").ValueKind==JsonValueKind.Null?null:row.GetProperty("provider_revision").GetInt32())).ToArray());
    }
}
