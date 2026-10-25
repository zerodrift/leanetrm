using System.Globalization;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using Npgsql;
using OpenEtrm.MarketData;
using OpenEtrm.MarketData.Power;

namespace OpenEtrm.Api;

// Every member is required: a missing member is a malformed request, never a default.
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record IntervalSeriesDefineRequest([property:JsonRequired]Guid RequestId,[property:JsonRequired]string Code,[property:JsonRequired]string Name,
    [property:JsonRequired]string Kind,[property:JsonRequired]string Specification,[property:JsonRequired]string Clock,[property:JsonRequired]int IntervalMinutes);
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record IntervalSeriesSegmentInput([property:JsonRequired]string Start,[property:JsonRequired]string?[] Values);
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record IntervalSeriesReviseRequest([property:JsonRequired]Guid RequestId,[property:JsonRequired]Guid SeriesId,[property:JsonRequired]int ExpectedRevision,
    [property:JsonRequired]string Reason,[property:JsonRequired]IntervalSeriesSegmentInput[] Segments);
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record IntervalSeriesLatestRequest([property:JsonRequired]Guid SeriesId,[property:JsonRequired]string KnownAt);
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record IntervalSeriesReadRequest([property:JsonRequired]Guid SeriesId,[property:JsonRequired]string From,[property:JsonRequired]string To,[property:JsonRequired]string KnownAt);
public sealed record IntervalSeriesCommandReceipt(Guid RequestId,Guid SeriesId,int Revision,int Intervals);

// Audited interval series through the definer operations of migration 0064. This store expands
// imports onto the series' pinned clock grid; reads never load the clock, so they stay available
// after a tzdata update. Storage rechecks authority, bounds and the head, refuses a read window that
// cuts through a stored interval, replays a retried request and certifies each revision's knowledge
// time at its commit.
public sealed partial class IntervalSeriesStore(PreviewDatabase database)
{
    // Zero-offset instants only, Z or +00:00, to the microsecond; reads render +00:00, so clients can send them back.
    [GeneratedRegex(@"\A[0-9]{4}-[0-9]{2}-[0-9]{2}T[0-9]{2}:[0-9]{2}:[0-9]{2}(\.[0-9]{1,6})?(Z|\+00:00)\z",RegexOptions.CultureInvariant)]
    private static partial Regex InstantSyntax();
    private static readonly string[] InstantFormats=["yyyy-MM-dd'T'HH:mm:ss.FFFFFF'Z'","yyyy-MM-dd'T'HH:mm:ss.FFFFFFzzz"];

    internal static DateTimeOffset ParseInstant(string? value)=>
        value is not null&&InstantSyntax().IsMatch(value)&&
        DateTimeOffset.TryParseExact(value,InstantFormats,CultureInfo.InvariantCulture,DateTimeStyles.AssumeUniversal,out var at)?at:
        throw new MarketDataException("interval_series_instant","Write each instant in UTC with Z or +00:00, for example 2027-01-01T05:00:00Z.");

    // One sentence for each storage refusal; Program.cs gives each code its HTTP status.
    public static string Refusal(string code)=>code switch
    {
        "interval_series_conflict"=>"This request identity already saved a different command. Start a new request.",
        "interval_series_revision_conflict"=>"Another revision was saved first. Reload the series before saving.",
        "interval_series_values"=>"Each interval needs the series' duration, a canonical decimal within the specification's bounds and its own slot among the stored intervals.",
        "interval_series_window"=>"Choose a From before To spanning at most 35,136 intervals and 100,000 changes, known no later than now.",
        "interval_series_grid"=>"Choose a From and To that do not fall inside a stored interval.",
        "interval_series_code"=>"Choose an unused code that starts with a lowercase letter and uses lowercase letters, digits, '_', '.' or '-'.",
        "interval_series_definition"=>"Choose a kind, a specification of that kind and a clock from the workspace's sealed reference data.",
        _=>throw new ArgumentOutOfRangeException(nameof(code))
    };

    public async Task<object> Catalog(NpgsqlConnection connection,CancellationToken ct)
    {
        // Choices come from the workspace's reference revision, the pin every definition records. Known at
        // is the database's clock, the clock every read compares its cutoff with.
        await using var command=ScheduleStore.Command(connection,"""
            SELECT jsonb_build_object(
             'knownAt',to_char(clock_timestamp() AT TIME ZONE 'UTC','YYYY-MM-DD"T"HH24:MI:SS.US"Z"'),
             'series',(SELECT coalesce(jsonb_agg(jsonb_build_object('id',s.id,'code',s.code,'name',s.name,'kind',s.kind,
               'specification',coalesce(s.quote_code,s.value_code,s.quantity_code),'clock',s.clock_code,'intervalMinutes',s.interval_minutes,
               'head',(SELECT coalesce(max(r.revision),0) FROM market_data.interval_series_revisions r WHERE(r.catalog_id,r.series_id)=(s.catalog_id,s.id)))
              ORDER BY s.code),'[]') FROM market_data.interval_series s WHERE s.catalog_id=$1),
             'choices',jsonb_build_object(
              'clocks',(SELECT coalesce(jsonb_agg(k.code ORDER BY k.code),'[]') FROM ref_data.clock_rules k WHERE(k.catalog_id,k.revision_id)=($2,$3)),
              'specifications',jsonb_build_object(
               'additive_price_shape',(SELECT coalesce(jsonb_agg(q.code ORDER BY q.code),'[]') FROM ref_data.quote_specifications q WHERE(q.catalog_id,q.revision_id)=($2,$3)),
               'multiplicative_price_shape',(SELECT coalesce(jsonb_agg(v.code ORDER BY v.code),'[]') FROM ref_data.value_specifications v
                WHERE(v.catalog_id,v.revision_id,v.kind)=($2,$3,'dimensionless')),
               'capacity_factor_forecast',(SELECT coalesce(jsonb_agg(v.code ORDER BY v.code),'[]') FROM ref_data.value_specifications v
                WHERE(v.catalog_id,v.revision_id,v.kind)=($2,$3,'dimensionless')),
               'metered_energy',(SELECT coalesce(jsonb_agg(q.code ORDER BY q.code),'[]') FROM ref_data.quantity_specifications q
                WHERE(q.catalog_id,q.revision_id,q.dimension_code)=($2,$3,'energy')))))::text
            """,database.Manifest.CatalogId,database.Manifest.ReferenceCatalogId,database.Manifest.ReferenceRevisionId);
        using var listed=JsonDocument.Parse((string)(await command.ExecuteScalarAsync(ct))!);
        var offered=listed.RootElement.GetProperty("choices");
        return new{knownAt=listed.RootElement.GetProperty("knownAt").GetString(),
            series=listed.RootElement.GetProperty("series").Clone(),
            choices=new{clocks=offered.GetProperty("clocks").Clone(),specifications=offered.GetProperty("specifications").Clone(),resolutions=IntervalSeriesGrid.Resolutions},
            canDefine=await PreviewDatabase.HasPermission(connection,"WriteMarketDefinitions",ct),
            canRevise=await PreviewDatabase.HasPermission(connection,"ImportObservations",ct)};
    }

    public async Task<IntervalSeriesCommandReceipt> Define(NpgsqlConnection connection,IntervalSeriesDefineRequest request,CancellationToken ct)
    {
        RequireRequest(request.RequestId);
        if(!IntervalSeriesGrid.Resolutions.Contains(request.IntervalMinutes))
            throw new MarketDataException("interval_series_resolution","Choose a 5, 15, 30 or 60 minute resolution.");
        if(string.IsNullOrWhiteSpace(request.Name)||request.Name!=request.Name.Trim()||request.Name.Length>80)
            throw new MarketDataException("interval_series_definition","Name the series in 1 to 80 characters without leading or trailing spaces.");
        // Storage reports an absent or unusable code, kind, specification or clock with its own code.
        var manifest=database.Manifest;
        await using var command=ScheduleStore.Command(connection,"SELECT market_data.define_interval_series($1,$2,$3,$4,$5,$6::text,$7,$8::text,$9::text,$10::text,$11)::text",
            request.RequestId,Digest(request),manifest.CatalogId,manifest.ReferenceCatalogId,manifest.ReferenceRevisionId,
            Text(request.Code),request.Name,Text(request.Kind),Text(request.Specification),Text(request.Clock),request.IntervalMinutes);
        return Receipt(await command.ExecuteScalarAsync(ct));
    }

    public async Task<IntervalSeriesCommandReceipt> Revise(NpgsqlConnection connection,IntervalSeriesReviseRequest request,CancellationToken ct)
    {
        RequireRequest(request.RequestId);
        // Every character counts, padding included: the reason is copied into each change-log row.
        if(string.IsNullOrWhiteSpace(request.Reason)||request.Reason.Length>500)
            throw new MarketDataException("interval_series_reason","Give a reason of 1 to 500 characters.");
        var (clock,minutes)=await Grid(connection,request.SeriesId,ct);
        // The grid refuses a missing segment or value list; nothing is dropped here.
        var segments=(request.Segments??[]).Select(s=>s is null?null!:new IntervalSeriesSegment(ParseInstant(s.Start),[..s.Values??[]])).ToArray();
        var intervals=IntervalSeriesGrid.Expand(clock,minutes,segments);
        await using var command=ScheduleStore.Command(connection,"SELECT market_data.append_interval_series_revision($1,$2,$3,$4,$5,$6,$7,$8)::text",
            request.SeriesId,request.RequestId,Digest(request),request.ExpectedRevision,request.Reason,
            intervals.Select(i=>i.Start).ToArray(),intervals.Select(i=>i.End).ToArray(),intervals.Select(i=>i.Value?.ToString()).ToArray());
        return Receipt(await command.ExecuteScalarAsync(ct));
    }

    // A trailing 24-hour browsing window ends at the last non-withdrawn interval.
    // RLS and the exact requested cutoff apply before choosing that endpoint.
    public async Task<object> Latest(NpgsqlConnection connection,IntervalSeriesLatestRequest request,CancellationToken ct)
    {
        var cutoff=ParseInstant(request.KnownAt);
        await RequireReadableSeries(connection,request.SeriesId,ct);
        _=await new KnowledgeStore(database.Manifest.CatalogId).Capture(connection,
            new(cutoff.UtcDateTime.ToString("yyyy-MM-dd"),request.KnownAt,"at"),ct);
        await using var command=ScheduleStore.Command(connection,"""
            SELECT max(ends_at) FROM (
              SELECT DISTINCT ON(v.starts_at) v.ends_at,v.value
              FROM market_data.interval_series_values v
              JOIN market_data.interval_series_revisions r USING(catalog_id,series_id,revision)
              JOIN market_data.interval_series_knowledge_receipts k
                ON(k.catalog_id,k.publication_order,k.publication_xid)=(r.catalog_id,r.publication_order,r.publication_xid)
              WHERE v.series_id=$1 AND v.catalog_id=$3 AND k.known_from<=$2
              ORDER BY v.starts_at,v.revision DESC
            ) latest WHERE value IS NOT NULL
            """,request.SeriesId,cutoff,database.Manifest.CatalogId);
        var result=await command.ExecuteScalarAsync(ct);
        if(result is null or DBNull)
        {
            await RequireReadableSeries(connection,request.SeriesId,ct);
            return new{window=(object?)null};
        }
        var end=(DateTime)result;
        var to=new DateTimeOffset(DateTime.SpecifyKind(end,DateTimeKind.Utc));
        var from=to.AddDays(-1);
        // Use the ordinary read to validate cutoff, grid, certification and authority even for browsing.
        _=await Values(connection,request.SeriesId,from,to,cutoff,ct);
        return new{window=(object)new{from=from.ToString("yyyy-MM-ddTHH:mm:ss.ffffffZ"),to=to.ToString("yyyy-MM-ddTHH:mm:ss.ffffffZ")}};
    }

    private async Task RequireReadableSeries(NpgsqlConnection connection,Guid series,CancellationToken ct)
    {
        await using var command=ScheduleStore.Command(connection,"""
            SELECT security.reference_access(reference_catalog_id,'ReadMarketData')
            FROM market_data.interval_series WHERE id=$1 AND catalog_id=$2
            """,series,database.Manifest.CatalogId);
        if(await command.ExecuteScalarAsync(ct) is not true)throw new PreviewDeniedException();
    }

    public Task<JsonElement> Values(NpgsqlConnection connection,IntervalSeriesReadRequest request,CancellationToken ct)=>
        Values(connection,request.SeriesId,ParseInstant(request.From),ParseInstant(request.To),ParseInstant(request.KnownAt),ct);

    // Each interval's value in the latest revision known at the cutoff.
    internal static Task<JsonElement> Values(NpgsqlConnection connection,Guid series,DateTimeOffset from,DateTimeOffset to,DateTimeOffset knownAt,CancellationToken ct)=>
        Read(connection,"SELECT market_data.read_interval_series($1,$2,$3,$4)::text",series,from,to,knownAt,ct);

    public Task<JsonElement> Changes(NpgsqlConnection connection,IntervalSeriesReadRequest request,CancellationToken ct)=>
        Read(connection,"SELECT market_data.interval_series_changes($1,$2,$3,$4)::text",request.SeriesId,
            ParseInstant(request.From),ParseInstant(request.To),ParseInstant(request.KnownAt),ct);

    // Storage bounds the window's length and cutoff and refuses an end inside a stored interval.
    private static async Task<JsonElement> Read(NpgsqlConnection connection,string sql,Guid series,DateTimeOffset from,DateTimeOffset to,
        DateTimeOffset knownAt,CancellationToken ct)
    {
        await using var command=ScheduleStore.Command(connection,sql,series,from,to,knownAt);
        using var document=JsonDocument.Parse((string)(await command.ExecuteScalarAsync(ct))!);
        return document.RootElement.Clone();
    }

    // The series' pinned clock and resolution as row-level security shows them; an invisible series is refused.
    private static async Task<(PowerClock Clock,int Minutes)> Grid(NpgsqlConnection connection,Guid series,CancellationToken ct)
    {
        await using var command=ScheduleStore.Command(connection,"""
            SELECT row_to_json(k)::text,s.interval_minutes FROM market_data.interval_series s
            JOIN ref_data.clock_rules k ON(k.catalog_id,k.revision_id,k.code)=(s.reference_catalog_id,s.reference_revision_id,s.clock_code)
            WHERE s.id=$1
            """,series);
        await using var reader=await command.ExecuteReaderAsync(ct);
        if(!await reader.ReadAsync(ct))throw new PreviewDeniedException();
        var rule=ReferenceClockJson.Read(JsonSerializer.Deserialize<JsonElement>(reader.GetString(0)));
        return (PowerClock.FromRule(rule,TimeZoneDatabase.Installed()),reader.GetInt32(1));
    }

    private static void RequireRequest(Guid request)
    {
        if(request==Guid.Empty)throw new MarketDataException("interval_series_request","Give each new command its own request identity.");
    }

    private static object Text(string? value)=>(object?)value??DBNull.Value;

    private static string Digest<T>(T request)=>Convert.ToHexStringLower(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(request,JsonSerializerOptions.Web)));

    private static IntervalSeriesCommandReceipt Receipt(object? response)=>
        JsonSerializer.Deserialize<IntervalSeriesCommandReceipt>((string)response!,JsonSerializerOptions.Web)!;
}
