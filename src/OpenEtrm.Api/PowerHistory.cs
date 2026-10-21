using System.Text.Json;
using System.Text.Json.Nodes;
using Npgsql;
using OpenEtrm.MarketData;
using OpenEtrm.MarketData.Power;

namespace OpenEtrm.Api;

public sealed record PowerHistoryRequest(Guid SourceId,string From,string To,string KnownAt);
public sealed record PowerHistoryPeriodRequest(Guid SourceId,string FromDate,string ThroughDate,string KnownAt);
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
    public static async Task<object> Catalog(NpgsqlConnection connection,CancellationToken ct)
    {
        Task<System.Collections.Immutable.ImmutableArray<JsonElement>> Rows(string sql)=>PreviewDatabase.Rows(connection,sql,[],ct);
        return new {sources=(await Sources(connection,null,ct)).Select(s=>s.Display).ToArray(),jobs=await Rows("SELECT row_to_json(j)::text FROM (SELECT id,name,provider,enabled,schedule_enabled,cron,schedule_zone FROM power_data.jobs)j"),attempts=await Rows("SELECT jsonb_build_object('jobId',j.id,'jobName',j.name,'provider',j.provider,'startedAt',a.started_at,'finishedAt',a.finished_at,'state',a.state,'errorCode',a.error_code,'fromDate',a.from_date,'throughDate',a.through_date)::text FROM power_data.attempts a JOIN power_data.jobs j ON j.id=a.job_id ORDER BY a.started_at DESC,a.id LIMIT 50"),knownAt=DateTimeOffset.UtcNow};
    }
    public static async Task<object> Period(NpgsqlConnection connection,PowerHistoryPeriodRequest request,CancellationToken ct)
    {
        var source=(await Sources(connection,request.SourceId,ct)).SingleOrDefault()??throw new PreviewDeniedException();
        if(!DateOnly.TryParseExact(request.FromDate,"yyyy-MM-dd",System.Globalization.CultureInfo.InvariantCulture,System.Globalization.DateTimeStyles.None,out var first)||!DateOnly.TryParseExact(request.ThroughDate,"yyyy-MM-dd",System.Globalization.CultureInfo.InvariantCulture,System.Globalization.DateTimeStyles.None,out var last))throw new MarketDataException("power_period","Supply exact first and last operating dates.");
        var window=PowerImportProtocol.Window(first,last,OpenEtrm.MarketData.Power.PowerClock.FromRule(source.Clock,OpenEtrm.MarketData.Power.TimeZoneDatabase.Installed()),source.Minutes);
        return await Read(connection,new(request.SourceId,window[0].Start.ToString("O"),window[^1].End.ToString("O"),request.KnownAt),source,ct);
    }
    public static async Task<object> Read(NpgsqlConnection connection,PowerHistoryRequest request,CancellationToken ct)
        =>await Read(connection,request,(await Sources(connection,request.SourceId,ct)).SingleOrDefault()??throw new PreviewDeniedException(),ct);
    private static async Task<object> Read(NpgsqlConnection connection,PowerHistoryRequest request,SourceMetadata metadata,CancellationToken ct)
    {
        var history=await ReadPrices(connection,request.SourceId,SchedulePreview.ParseAt(request.From),SchedulePreview.ParseAt(request.To),SchedulePreview.ParseAt(request.KnownAt),ct);
        return new {source=metadata.Display,priceUnit=metadata.Display.GetProperty("priceUnit").GetString(),knownAt=history.KnownAt,contextId=history.ContextId,
            rows=history.Rows.Select(row=>new {row.Start,row.End,row.Price,formattedPrice=row.Price.Value.ToString(),row.KnownAt,revision=row.Revision,row.BatchId,sourceId=request.SourceId}).ToArray()};
    }
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
