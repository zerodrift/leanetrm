using System.Text.Json;
using System.Text.RegularExpressions;

namespace OpenEtrm.Api;

// One explicitly installed demo dataset. It shares the authenticated connection pools,
// while every operation keeps its own catalog, exact references, page session and pins.
public static class SyntheticDataset
{
    public const string ManifestName="synthetic-manifest.json";
    public const int DurableCloseLimit=8;
    public static PreviewDatabase Open(string root,PreviewDatabase primary)
    {
        var bytes=File.ReadAllBytes(Path.Combine(root,ManifestName));
        using var document=JsonDocument.Parse(bytes);
        if(!document.RootElement.TryGetProperty("syntheticProfileSha256",out var identity)||
            identity.ValueKind!=JsonValueKind.String||!Regex.IsMatch(identity.GetString()!,"^[a-f0-9]{64}$"))
            throw new InvalidOperationException("The synthetic dataset requires its immutable generation profile identity.");
        var selected=JsonSerializer.Deserialize<PreviewManifest>(bytes,new JsonSerializerOptions{PropertyNameCaseInsensitive=true})
            ??throw new InvalidOperationException("The synthetic dataset manifest is required.");
        Validate(primary.Manifest,selected);
        return new PreviewDatabase(selected,primary);
    }

    public static void Validate(PreviewManifest primary,PreviewManifest selected)
    {
        if(selected.Mode!=primary.Mode||selected.IdentityIssuer!=primary.IdentityIssuer||
            selected.TenantId!=primary.TenantId||selected.WorkspaceId!=primary.WorkspaceId||
            selected.CatalogId==Guid.Empty||selected.CatalogId==primary.CatalogId||
            selected.ReferenceCatalogId==Guid.Empty||selected.ReferenceCatalogId==primary.ReferenceCatalogId||
            selected.RevisionId==Guid.Empty||selected.ReferenceRevisionId==Guid.Empty||
            selected.FuturesConvention!="contract_lot_execution_pnl_v2"||
            selected.Frames is not{Length:>0 and <=128}||
            selected.Frames.Any(f=>f is null||f.ObservationIds is not{Length:>0}||f.ObservationIds.Contains(Guid.Empty)||
                f.ObservationIds.Distinct().Count()!=f.ObservationIds.Length||f.CurveContracts is not{Count:>0}||
                f.CurveContracts.Any(p=>string.IsNullOrWhiteSpace(p.Key)||p.Value is not{Length:>0}||p.Value.Contains(Guid.Empty)||p.Value.Distinct().Count()!=p.Value.Length))||
            selected.Frames.Select(f=>f.At).Distinct().Count()!=selected.Frames.Length||
            selected.SwapMarkets is{Length:>0}||selected.GasMarket is not null||selected.CrudeMarket is not null||selected.LngMarket is not null||
            selected.UnitCatalog is not null||selected.ReferenceCatalog is not null||selected.LngReferenceCatalog is not null)
            throw new InvalidOperationException("The synthetic dataset needs independent catalogs and complete pins in the same authenticated workspace.");
    }

    public static PathString ApiPath(PathString path)=>
        path.StartsWithSegments("/examples/exchange/api",out var exchange)?new("/api"+exchange):
        path.StartsWithSegments("/examples/synthetic/api",out var synthetic)?new("/api"+synthetic):path;
}
