using System.Collections.Immutable;
using System.Text.RegularExpressions;
using Npgsql;
using OpenEtrm.MarketData;
using OpenEtrm.MarketData.Power;

namespace OpenEtrm.Api;

public sealed record ClockReference(Guid CatalogId,Guid RevisionId,string Code);
public sealed record ClockChoice(ClockReference Reference,string Kind,int? FixedOffsetSeconds,string? IanaZone,string? TzdbVersion)
{
    public ClockRule Rule => new(Reference.Code,Kind,FixedOffsetSeconds,IanaZone,TzdbVersion);
}
public sealed record TouProductVersion(Guid Id,string Code,int Version,int Generation,string State,string Name,ClockReference Clock,
    int PeakStartHour,int PeakEndHour,bool PeakSaturday,int? PublicationOrder,DateTimeOffset? PublishedAt)
{
    [System.Text.Json.Serialization.JsonIgnore] public TouPeak Peak=>new(PeakStartHour,PeakEndHour,PeakSaturday);
}
public sealed record TouProductSaveRequest(Guid? Id,int? Version,int? Generation,string? ExpectedState,string? Code,string? Name,
    ClockReference? Clock,int? PeakStartHour,int? PeakEndHour,bool? PeakSaturday);
public sealed record TouProductPublishRequest(Guid Id,int Version,int Generation);

// Authored TOU products: drafts and immutable published versions bound to exact
// clock rules of this workspace's pinned reference revision.
public sealed class TouProductStore(Guid catalog,Guid referenceCatalog,Guid referenceRevision)
{
    public async Task<ImmutableArray<TouProductVersion>> Read(NpgsqlConnection connection,CancellationToken ct)
    {
        await using var command=ScheduleStore.Command(connection,"SELECT v.product_id,p.code,v.version,v.generation,v.state,v.name,v.clock_catalog_id,v.clock_revision_id,v.clock_code,v.peak_start_hour,v.peak_end_hour,v.peak_saturday,v.publication_order,v.published_at FROM market_data.tou_product_versions v JOIN market_data.tou_products p ON (p.catalog_id,p.id)=(v.catalog_id,v.product_id) WHERE v.catalog_id=$1 ORDER BY p.code,v.version",catalog);
        await using var reader=await command.ExecuteReaderAsync(ct);var result=ImmutableArray.CreateBuilder<TouProductVersion>();
        while(await reader.ReadAsync(ct))result.Add(new(reader.GetGuid(0),reader.GetString(1),reader.GetInt32(2),reader.GetInt32(3),reader.GetString(4),reader.GetString(5),
            new(reader.GetGuid(6),reader.GetGuid(7),reader.GetString(8)),reader.GetInt16(9),reader.GetInt16(10),reader.GetBoolean(11),
            reader.IsDBNull(12)?null:reader.GetInt32(12),reader.IsDBNull(13)?null:reader.GetFieldValue<DateTimeOffset>(13)));
        return result.ToImmutable();
    }
    // The product definitions in force: each product's latest published version.
    public static ImmutableArray<TouProductVersion> Published(IEnumerable<TouProductVersion> versions) =>
        [..versions.Where(v=>v.State=="published").GroupBy(v=>v.Id).Select(g=>g.MaxBy(v=>v.Version)!).OrderBy(v=>v.Name,StringComparer.Ordinal)];
    public async Task<ImmutableArray<ClockChoice>> Clocks(NpgsqlConnection connection,CancellationToken ct)
    {
        await using var command=ScheduleStore.Command(connection,"SELECT code,kind,fixed_offset_seconds,iana_zone,tzdb_version FROM ref_data.clock_rules WHERE catalog_id=$1 AND revision_id=$2 ORDER BY code",referenceCatalog,referenceRevision);
        await using var reader=await command.ExecuteReaderAsync(ct);var result=ImmutableArray.CreateBuilder<ClockChoice>();
        while(await reader.ReadAsync(ct))result.Add(new(new(referenceCatalog,referenceRevision,reader.GetString(0)),reader.GetString(1),
            reader.IsDBNull(2)?null:reader.GetInt32(2),reader.IsDBNull(3)?null:reader.GetString(3),reader.IsDBNull(4)?null:reader.GetString(4)));
        return result.ToImmutable();
    }
    public async Task<ClockChoice> Clock(NpgsqlConnection connection,ClockReference? reference,CancellationToken ct)
    {
        if(reference is null || reference.CatalogId!=referenceCatalog || reference.RevisionId!=referenceRevision)
            throw new MarketDataException("clock_reference","Choose a market clock from this workspace's reference data.");
        return (await Clocks(connection,ct)).SingleOrDefault(c=>c.Reference==reference)??
            throw new MarketDataException("clock_reference","The chosen market clock is unavailable.");
    }
    public async Task<TouProductVersion> Save(NpgsqlConnection connection,TouProductSaveRequest body,CancellationToken ct)
    {
        if(body.Code is null || !Regex.IsMatch(body.Code,@"\A[a-z][a-z0-9_]{0,31}\z",RegexOptions.CultureInvariant) || string.IsNullOrWhiteSpace(body.Name) ||
            body.Name!=body.Name.Trim() || body.Name.Length>80 || body.PeakStartHour is not (>=0 and <=23) || body.PeakEndHour is not (>=1 and <=24) ||
            body.PeakStartHour>=body.PeakEndHour || body.PeakSaturday is null)
            throw new MarketDataException("tou_definition","Supply a lowercase code, a name, a market clock and an ordered peak window within the day.");
        var clock=await Clock(connection,body.Clock,ct);
        await using(var gate=ScheduleStore.Command(connection,"SELECT pg_advisory_xact_lock(730849105)"))await gate.ExecuteNonQueryAsync(ct);
        var all=await Read(connection,ct);Guid id;int version,generation;
        if(body.Id is null)
        {
            if(body.Version is not null || body.Generation is not null || body.ExpectedState is not null)
                throw new MarketDataException("tou_definition","Create a new product without an existing version.");
            if(all.Any(v=>v.Code==body.Code))throw new MarketDataException("tou_conflict","A TOU product already uses this code. Open it to edit.");
            if(all.Select(v=>v.Id).Distinct().Count()>=64)throw new MarketDataException("preview_limit","This preview holds at most 64 TOU products.");
            id=Guid.NewGuid();version=1;generation=1;
            await using var insert=ScheduleStore.Command(connection,"INSERT INTO market_data.tou_products VALUES($1,$2,$3)",catalog,id,body.Code);
            await insert.ExecuteNonQueryAsync(ct);
        }
        else
        {
            id=body.Id.Value;var old=all.SingleOrDefault(v=>v.Id==id&&v.Version==body.Version);
            if(old is null || old.State!=body.ExpectedState || old.Generation!=body.Generation || old.Code!=body.Code)
                throw new MarketDataException("tou_conflict","This product changed or its code differs. Reload before editing.");
            if(old.State=="published")
            {
                if(all.Any(v=>v.Id==id&&v.State=="draft"))throw new MarketDataException("tou_conflict","This product already has a draft. Open that draft.");
                version=all.Where(v=>v.Id==id).Max(v=>v.Version)+1;generation=1;
            }
            else {version=old.Version;generation=old.Generation+1;}
        }
        if(version>64)throw new MarketDataException("preview_limit","A TOU product can have at most 64 versions in this preview.");
        var values=new object[]{body.Name,clock.Reference.CatalogId,clock.Reference.RevisionId,clock.Reference.Code,(short)body.PeakStartHour!.Value,(short)body.PeakEndHour!.Value,body.PeakSaturday!.Value};
        if(generation>1)
        {
            await using var update=ScheduleStore.Command(connection,"UPDATE market_data.tou_product_versions SET generation=$8,name=$1,clock_catalog_id=$2,clock_revision_id=$3,clock_code=$4,peak_start_hour=$5,peak_end_hour=$6,peak_saturday=$7 WHERE catalog_id=$9 AND product_id=$10 AND version=$11 AND generation=$12 AND state='draft'",
                [..values,generation,catalog,id,version,body.Generation!.Value]);
            if(await update.ExecuteNonQueryAsync(ct)!=1)throw new MarketDataException("tou_conflict","The draft changed. Reload it.");
        }
        else
        {
            await using var insert=ScheduleStore.Command(connection,"INSERT INTO market_data.tou_product_versions(name,clock_catalog_id,clock_revision_id,clock_code,peak_start_hour,peak_end_hour,peak_saturday,catalog_id,product_id,version,generation,state) VALUES($1,$2,$3,$4,$5,$6,$7,$8,$9,$10,$11,'draft')",
                [..values,catalog,id,version,generation]);
            await insert.ExecuteNonQueryAsync(ct);
        }
        return (await Read(connection,ct)).Single(v=>v.Id==id&&v.Version==version);
    }
    public async Task<TouProductVersion> Publish(NpgsqlConnection connection,TouProductPublishRequest body,CancellationToken ct)
    {
        await using var command=ScheduleStore.Command(connection,"SELECT security.publish_tou_product($1,$2,$3,$4)",catalog,body.Id,body.Version,body.Generation);
        await command.ExecuteScalarAsync(ct);
        return (await Read(connection,ct)).Single(v=>v.Id==body.Id&&v.Version==body.Version);
    }
}
