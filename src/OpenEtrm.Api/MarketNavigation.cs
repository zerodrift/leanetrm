using System.Collections.Immutable;
using Npgsql;
using OpenEtrm.MarketData;

namespace OpenEtrm.Api;

public sealed record NavigationCategory(string Code,string Name);
// A commodity curve names its commodity. An interest-rate curve has none; its market type identifies it.
public sealed record NavigationCurve(string Code,string Name,string MarketType,NavigationCategory? Category);
public sealed record NavigationFrame(Guid Id,string Name,DateTimeOffset At,Guid? OriginCloseId=null);
public sealed record NavigationRead(DefinitionRevision DefinitionRevision,ReferenceRevision ReferenceRevision,
    ImmutableArray<NavigationCurve> Curves,ImmutableArray<CurveName> CurveNames,ReferenceDisplayData? ReferenceDisplay=null);
public sealed record MarketNavigation(string Profile,ScheduleContext? Context,DefinitionRevision DefinitionRevision,
    ReferenceRevision ReferenceRevision,string FuturesConvention,PreviewExample? Example,ImmutableArray<Guid> InitialFrameIds,
    ImmutableArray<NavigationFrame> Frames,ImmutableArray<CurveName> CurveNames,ImmutableArray<NavigationCurve> Curves,ReferenceDisplayData? ReferenceDisplay=null)
{
    public string Kind=>"market_navigation";
    public FrameCatalogInfo? FrameCatalog{get;init;}
    public object? Live{get;init;}

    // Interval curves are listed by the interval-curve screen, not here.
    internal static async Task<ImmutableArray<NavigationCurve>> ReadCurves(NpgsqlConnection connection,PreviewManifest manifest,CancellationToken ct)
    {
        var curves=ImmutableArray.CreateBuilder<NavigationCurve>();
        await using(var command=ScheduleStore.Command(connection,"""
            WITH members AS(
                SELECT m.curve_code,count(*) total,count(k.id) matched,
                    count(*) FILTER(WHERE k.kind='future') futures,count(*) FILTER(WHERE k.kind='forward') forwards,count(i.contract_id) intervals
                FROM market_data.curve_members m LEFT JOIN market_data.contracts k
                    ON(k.catalog_id,k.revision_id,k.id)=(m.catalog_id,m.revision_id,m.contract_id)
                LEFT JOIN market_data.contract_intervals i ON(i.catalog_id,i.revision_id,i.contract_id)=(m.catalog_id,m.revision_id,m.contract_id)
                WHERE m.catalog_id=$1 AND m.revision_id=$2 GROUP BY m.curve_code)
            SELECT c.code,c.name,c.commodity_code,r.name,coalesce(m.total,0),coalesce(m.matched,0),
                coalesce(m.futures,0),coalesce(m.forwards,0)
            FROM market_data.curves c LEFT JOIN members m ON m.curve_code=c.code
                LEFT JOIN ref_data.commodities r ON(r.catalog_id,r.revision_id,r.code)=($3,$4,c.commodity_code)
            WHERE c.catalog_id=$1 AND c.revision_id=$2 AND coalesce(m.intervals,0)=0 ORDER BY c.code
            """,manifest.CatalogId,manifest.RevisionId,manifest.ReferenceCatalogId,manifest.ReferenceRevisionId))
        await using(var reader=await command.ExecuteReaderAsync(ct))
        {
            while(await reader.ReadAsync(ct))
            {
                var total=reader.GetInt64(4);var futures=reader.GetInt64(6);var forwards=reader.GetInt64(7);
                if(reader.IsDBNull(3)||total==0||reader.GetInt64(5)!=total||futures+forwards!=total)
                    throw Invalid();
                var kind=futures==total?"futures":forwards==total?"forwards":"mixed";
                curves.Add(new(reader.GetString(0),reader.GetString(1),kind,new(reader.GetString(2),reader.GetString(3))));
            }
        }
        await using(var command=ScheduleStore.Command(connection,"SELECT code,name FROM market_data.rate_curves WHERE catalog_id=$1 AND revision_id=$2 ORDER BY code",manifest.CatalogId,manifest.RevisionId))
        await using(var reader=await command.ExecuteReaderAsync(ct))
            while(await reader.ReadAsync(ct))curves.Add(new(reader.GetString(0),reader.GetString(1),"rates",null));
        if(curves.Any(c=>string.IsNullOrWhiteSpace(c.Name)||c.Category is {} category&&string.IsNullOrWhiteSpace(category.Name))||curves.Select(c=>c.Code).Distinct().Count()!=curves.Count)
            throw Invalid();
        return curves.ToImmutable();
    }

    static MarketDataException Invalid()=>new("market_navigation","The selected curve directory has missing or conflicting classifications.");
}
