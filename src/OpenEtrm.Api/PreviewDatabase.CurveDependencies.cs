using System.Collections.Immutable;
using System.Text.Json;
using Npgsql;
using OpenEtrm.Api.Presentation;

namespace OpenEtrm.Api;

public sealed partial class PreviewDatabase
{
    private static async Task<Dictionary<string,ImmutableArray<JsonElement>>> ReadDefinitions(
        NpgsqlConnection connection,PreviewManifest manifest,CancellationToken ct)
    {
        var definitions=new Dictionary<string,ImmutableArray<JsonElement>>();
        foreach(var (table,key) in DefinitionTables)
        {
            var revisionColumn=table=="revisions"?"id":"revision_id";
            definitions[table]=await Rows(connection,
                $"SELECT row_to_json(t)::text FROM (SELECT * FROM market_data.{table} WHERE catalog_id=$1 AND {revisionColumn}=$2 ORDER BY {key}) t",
                [manifest.CatalogId,manifest.RevisionId],ct);
        }
        return definitions;
    }

    private static async Task<Dictionary<string,ImmutableArray<JsonElement>>> ReadCurveDefinitions(
        NpgsqlConnection connection,PreviewManifest manifest,string curve,Guid[] pinnedSeries,CancellationToken ct)
    {
        // Retain every member of each reached curve so normal model validation
        // still checks complete mappings, series, units and expiry rules. The
        // frame's other exact pins also keep their series contracts for the
        // existing full-snapshot validation and identity.
        const string closure="""
            WITH RECURSIVE curve_codes(code) AS (
                SELECT $3::text
                UNION
                SELECT i.source_curve_code::text
                FROM market_data.curve_inputs i JOIN curve_codes c ON c.code=i.curve_code
                WHERE i.catalog_id=$1 AND i.revision_id=$2 AND i.source_curve_code IS NOT NULL
            ), members AS MATERIALIZED (
                SELECT m.* FROM curve_codes c
                JOIN market_data.curve_members m ON m.curve_code=c.code
                WHERE m.catalog_id=$1 AND m.revision_id=$2
            ), pinned_series AS MATERIALIZED (
                SELECT id,contract_id FROM market_data.series
                WHERE catalog_id=$1 AND revision_id=$2 AND id=ANY($4::uuid[])
            ), initial_contract_ids AS (
                SELECT contract_id FROM members
                UNION SELECT contract_id FROM pinned_series WHERE contract_id IS NOT NULL
            ), contract_ids(contract_id) AS (
                SELECT contract_id FROM initial_contract_ids
                UNION
                SELECT benchmark.id FROM contract_ids selected
                JOIN market_data.contracts c ON c.catalog_id=$1 AND c.revision_id=$2 AND c.id=selected.contract_id
                JOIN ref_data.futures_basis_settlements basis
                  ON (basis.catalog_id,basis.revision_id,basis.series_code)=
                     (c.reference_catalog_id,c.reference_revision_id,c.futures_series_code)
                JOIN market_data.contracts benchmark
                  ON (benchmark.catalog_id,benchmark.revision_id,benchmark.futures_series_code)=
                     (c.catalog_id,c.revision_id,basis.benchmark_series_code)
            ), inputs AS MATERIALIZED (
                SELECT i.* FROM curve_codes c
                JOIN market_data.curve_inputs i ON i.curve_code=c.code
                WHERE i.catalog_id=$1 AND i.revision_id=$2
            ), rate_tenors AS MATERIALIZED (
                SELECT * FROM market_data.rate_curve_tenors
                WHERE catalog_id=$1 AND revision_id=$2 AND curve_code=$3
            ), series_ids AS MATERIALIZED (
                SELECT DISTINCT source_series_id AS id FROM inputs WHERE source_series_id IS NOT NULL
                UNION SELECT series_id FROM rate_tenors
                UNION SELECT id FROM pinned_series
            )
            """;
        const string pin="catalog_id=$1 AND revision_id=$2";
        var queries=new Dictionary<string,string>
        {
            ["revisions"]="SELECT * FROM market_data.revisions WHERE catalog_id=$1 AND id=$2",
            ["revision_periods"]=$"SELECT * FROM market_data.revision_periods WHERE {pin}",
            ["curves"]=$"SELECT * FROM market_data.curves WHERE {pin} AND code IN (SELECT code FROM curve_codes)",
            ["curve_members"]="SELECT * FROM members",
            ["curve_inputs"]="SELECT * FROM inputs",
            ["constituent_mappings"]=$"SELECT * FROM market_data.constituent_mappings WHERE {pin} AND curve_code IN (SELECT code FROM curve_codes)",
            ["curve_expiry_rules"]=$"SELECT * FROM market_data.curve_expiry_rules WHERE {pin} AND curve_code IN (SELECT code FROM curve_codes)",
            ["contracts"]=$"SELECT * FROM market_data.contracts WHERE {pin} AND id IN (SELECT contract_id FROM contract_ids)",
            ["contract_intervals"]=$"SELECT * FROM market_data.contract_intervals WHERE {pin} AND contract_id IN (SELECT contract_id FROM contract_ids)",
            ["series"]=$"SELECT * FROM market_data.series WHERE {pin} AND (contract_id IN (SELECT contract_id FROM contract_ids) OR id IN (SELECT id FROM series_ids))",
            ["rate_curves"]=$"SELECT * FROM market_data.rate_curves WHERE {pin} AND code=$3",
            ["rate_curve_tenors"]="SELECT * FROM rate_tenors"
        };
        var statements=DefinitionTables.Where(t=>queries.ContainsKey(t.Table)).Select(t=>(t.Table,t.Key,queries[t.Table]));
        var definitions=await ReadDefinitionGroups(connection,closure,statements,[manifest.CatalogId,manifest.RevisionId,curve,pinnedSeries],ct);
        foreach(var (table,_) in DefinitionTables)definitions.TryAdd(table,[]);
        return definitions;
    }

    // Each branch selects native rows under the bound caller and existing RLS.
    // Return rows separately so grouping never allocates an aggregate JSON value.
    private static async Task<Dictionary<string,ImmutableArray<JsonElement>>> ReadDefinitionGroups(
        NpgsqlConnection connection,string prefix,IEnumerable<(string Table,string Key,string Query)> groups,
        object[] parameters,CancellationToken ct)
    {
        var selected=groups.ToArray();
        var rows=selected.ToDictionary(g=>g.Table,_=>ImmutableArray.CreateBuilder<JsonElement>());
        var sql=prefix+" "+string.Join(" UNION ALL ",selected.Select(g=>
            $"(SELECT '{g.Table}' AS group_name,row_to_json(t)::text FROM ({g.Query} ORDER BY {g.Key}) t)"));
        await using var command=ScheduleStore.Command(connection,sql,parameters);
        await using var reader=await command.ExecuteReaderAsync(ct);
        while(await reader.ReadAsync(ct))
            rows[reader.GetString(0)].Add(ReferenceJson.PreserveNumericLexemes(reader.GetString(1)));
        return rows.ToDictionary(g=>g.Key,g=>g.Value.ToImmutable());
    }
}
