using System.Text.Json;
using Npgsql;
using OpenEtrm.MarketData;

namespace OpenEtrm.Api;

public sealed record PowerHistoryRequest(Guid SourceId,string From,string To,string KnownAt);
public sealed record PowerHistoryPeriodRequest(Guid SourceId,string FromDate,string ThroughDate,string KnownAt);
public static class PowerHistory
{
    public static async Task<object> Catalog(NpgsqlConnection connection,CancellationToken ct)
    {
        async Task<JsonElement[]> Rows(string sql)
        {
            await using var command=ScheduleStore.Command(connection,sql);await using var reader=await command.ExecuteReaderAsync(ct);var rows=new List<JsonElement>();
            while(await reader.ReadAsync(ct))rows.Add(JsonSerializer.Deserialize<JsonElement>(reader.GetString(0)));return rows.ToArray();
        }
        return new {sources=await Rows("SELECT row_to_json(s)::text FROM power_data.sources s ORDER BY provider,market,name"),jobs=await Rows("SELECT row_to_json(j)::text FROM (SELECT id,name,provider,enabled,schedule_enabled,cron,schedule_zone FROM power_data.jobs)j"),attempts=await Rows("SELECT row_to_json(a)::text FROM (SELECT job_id,started_at,finished_at,state,error_code,from_date,through_date FROM power_data.attempts ORDER BY started_at DESC LIMIT 50)a"),knownAt=DateTimeOffset.UtcNow};
    }
    public static async Task<object> Period(NpgsqlConnection connection,PowerHistoryPeriodRequest request,CancellationToken ct)
    {
        await using var command=ScheduleStore.Command(connection,"SELECT row_to_json(c)::text FROM power_data.sources s JOIN ref_data.clock_rules c ON(c.catalog_id,c.revision_id,c.code)=(s.reference_catalog_id,s.reference_revision_id,s.clock_code) WHERE s.id=$1",request.SourceId);
        var text=await command.ExecuteScalarAsync(ct) as string??throw new PreviewDeniedException();
        using var document=JsonDocument.Parse(text);var c=document.RootElement;
        var rule=new OpenEtrm.MarketData.Power.ClockRule(c.GetProperty("code").GetString()!,c.GetProperty("kind").GetString()!,c.GetProperty("fixed_offset_seconds").ValueKind==JsonValueKind.Null?null:c.GetProperty("fixed_offset_seconds").GetInt32(),c.GetProperty("iana_zone").GetString(),c.GetProperty("tzdb_version").GetString());
        if(!DateOnly.TryParseExact(request.FromDate,"yyyy-MM-dd",System.Globalization.CultureInfo.InvariantCulture,System.Globalization.DateTimeStyles.None,out var first)||!DateOnly.TryParseExact(request.ThroughDate,"yyyy-MM-dd",System.Globalization.CultureInfo.InvariantCulture,System.Globalization.DateTimeStyles.None,out var last))throw new MarketDataException("power_period","Supply exact first and last operating dates.");
        var window=PowerImportProtocol.Window(first,last,OpenEtrm.MarketData.Power.PowerClock.FromRule(rule,OpenEtrm.MarketData.Power.TimeZoneDatabase.Installed()),60);
        return await Read(connection,new(request.SourceId,window[0].Start.ToString("O"),window[^1].End.ToString("O"),request.KnownAt),ct);
    }
    public static async Task<object> Read(NpgsqlConnection connection,PowerHistoryRequest request,CancellationToken ct)
    {
        var from=SchedulePreview.ParseAt(request.From);var to=SchedulePreview.ParseAt(request.To);var cutoff=SchedulePreview.ParseAt(request.KnownAt);
        await using var command=ScheduleStore.Command(connection,"SELECT power_data.read_prices($1,$2,$3,$4)::text",request.SourceId,from,to,cutoff);
        using var document=JsonDocument.Parse((string)(await command.ExecuteScalarAsync(ct))!);var root=document.RootElement;var source=root.GetProperty("source");
        var revision=new ReferenceRevision(source.GetProperty("reference_catalog_id").GetGuid(),source.GetProperty("reference_revision_id").GetGuid());
        var unit=new PriceUnit(new(revision,source.GetProperty("currency_code").GetString()!),new(revision,source.GetProperty("quantity_code").GetString()!),ExactDecimal.Parse("1"));
        return new{source=source.Clone(),knownAt=cutoff,contextId=root.GetProperty("contextId").GetGuid(),rows=root.GetProperty("rows").EnumerateArray().Select(row=>new{start=row.GetProperty("starts_at").GetDateTimeOffset(),end=row.GetProperty("ends_at").GetDateTimeOffset(),price=new UnitPrice(ExactDecimal.Parse(row.GetProperty("price").GetString()!),unit),formattedPrice=ExactDecimal.Parse(row.GetProperty("price").GetString()!).ToString(new AmountFormat(4,CashTieRule.ToEven,true,true)),knownAt=row.GetProperty("known_at").GetDateTimeOffset(),revision=row.GetProperty("provider_revision").Clone()}).ToArray()};
    }
}
