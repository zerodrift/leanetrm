using System.Collections.Immutable;
using System.Text.Json;
using Npgsql;
using OpenEtrm.MarketData;

namespace OpenEtrm.Api;

// Authoring masters are independent of valuation assignments and market prices.
internal sealed record CrudeDeliveryReferences(Guid CatalogId, Guid RevisionId,
    ImmutableArray<JsonElement> Commodities, ImmutableArray<JsonElement> Grades,
    ImmutableArray<JsonElement> Locations, ImmutableArray<JsonElement> DeliveryPoints,
    ImmutableArray<JsonElement> Quantities, ImmutableArray<JsonElement> Units,
    ImmutableArray<JsonElement> Clocks)
{
    internal static async Task<CrudeDeliveryReferences> Read(NpgsqlConnection connection,
        ReferenceRevision revision, CancellationToken ct)
    {
        await using var authority = ScheduleStore.Command(connection,
            "SELECT security.reference_access($1,'ReadMarketData') AND security.reference_version_visible($1,$2)",
            revision.CatalogId, revision.RevisionId);
        if (!Equals(await authority.ExecuteScalarAsync(ct), true)) throw new PreviewDeniedException();

        async Task<ImmutableArray<JsonElement>> Rows(string table) => await PreviewDatabase.Rows(connection,
            $"SELECT row_to_json(r)::text FROM ref_data.{table} r WHERE(catalog_id,revision_id)=($1,$2) ORDER BY to_jsonb(r)::text",
            [revision.CatalogId, revision.RevisionId], ct);

        return new(revision.CatalogId, revision.RevisionId,
            await Rows("commodities"), await Rows("crude_grades"), await Rows("locations"),
            await Rows("crude_delivery_points"), await Rows("quantity_specifications"),
            await Rows("units"), await Rows("clock_rules"));
    }
}
