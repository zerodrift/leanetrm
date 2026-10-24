using System.Collections.Immutable;
using Npgsql;
using OpenEtrm.MarketData;

namespace OpenEtrm.Api;

// A narrow read still requires the exact authorized sealed bundle. This is a
// request-local guard, not a reusable authorization proof or market-data cache.
internal static class MarketBundle
{
    internal sealed record Pins(DefinitionRevision Definitions,ReferenceRevision References);

    internal static async Task<Pins> Require(NpgsqlConnection connection,PreviewManifest manifest,CancellationToken ct)
    {
        var rows=await PreviewDatabase.Rows(connection,"""
            SELECT row_to_json(r)::text FROM(SELECT catalog_id,id,state,reference_catalog_id,reference_revision_id
            FROM market_data.revisions WHERE catalog_id=$1 AND id=$2)r
            """,[manifest.CatalogId,manifest.RevisionId],ct);
        if(rows.Length!=1||rows[0].Text("state")!="sealed")throw new PreviewDeniedException();
        PreviewData.RequireBundlePin(manifest,rows[0]);
        await RequireReferenceVisibility(connection,manifest,ct);
        return new(new(manifest.CatalogId,manifest.RevisionId),new(manifest.ReferenceCatalogId,manifest.ReferenceRevisionId));
    }

    internal static async Task RequireReferenceVisibility(NpgsqlConnection connection,PreviewManifest manifest,CancellationToken ct)
    {
        await using var command=ScheduleStore.Command(connection,"SELECT security.reference_version_visible($1,$2)",manifest.ReferenceCatalogId,manifest.ReferenceRevisionId);
        if(!Equals(await command.ExecuteScalarAsync(ct),true))throw Missing();
    }

    internal static async Task RequireObservationPins(NpgsqlConnection connection,PreviewManifest manifest,ImmutableArray<Guid> pins,CancellationToken ct)
    {
        var expected=pins.ToHashSet();
        await using var command=ScheduleStore.Command(connection,"SELECT id FROM market_data.observations WHERE catalog_id=$1 AND definition_revision_id=$2 AND id=ANY($3::uuid[])",manifest.CatalogId,manifest.RevisionId,expected.ToArray());
        await using var reader=await command.ExecuteReaderAsync(ct);
        while(await reader.ReadAsync(ct))expected.Remove(reader.GetGuid(0));
        if(expected.Count!=0)throw Missing();
    }

    private static MarketDataException Missing()=>new("ambiguous_reference","A required exact reference is missing or ambiguous.");
}
