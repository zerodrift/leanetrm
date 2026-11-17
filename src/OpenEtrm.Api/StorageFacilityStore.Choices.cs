using System.Collections.Immutable;
using System.Text.Json;
using Npgsql;
using OpenEtrm.MarketData;

namespace OpenEtrm.Api;

public sealed partial class StorageFacilityStore
{
    private async Task<StorageFacilityChoices> Choices(NpgsqlConnection c,JsonElement? facts,ScheduleContext context,string sourceSearch,CancellationToken ct)
    {
        var reference=facts is {} selected?new ReferenceRevision(selected.GetProperty("version").Id("reference_catalog_id"),selected.GetProperty("version").Id("reference_revision_id")):
            (await database.ReferenceScope(c,"locations",ct,DateWorkspace.Date(context.EffectiveOn))).Revision;
        var facilities=await PreviewDatabase.Rows(c,"""
            SELECT jsonb_build_object('code',l.code,'label',l.name||' · '||co.name,'commodity',co.code)::text
            FROM ref_data.commodity_locations m JOIN ref_data.locations l ON(l.catalog_id,l.revision_id,l.code)=(m.catalog_id,m.revision_id,m.location_code)
            JOIN ref_data.commodities co ON(co.catalog_id,co.revision_id,co.code)=(m.catalog_id,m.revision_id,m.commodity_code)
            WHERE(m.catalog_id,m.revision_id)=($1,$2) AND EXISTS(SELECT 1 FROM ref_data.gas_delivery_conventions g
                WHERE(g.catalog_id,g.revision_id,g.product_code)=(m.catalog_id,m.revision_id,m.commodity_code))
            ORDER BY l.name,co.name,l.code,co.code LIMIT 5001
            """,[reference.CatalogId,reference.RevisionId],ct);
        var quantities=await PreviewDatabase.Rows(c,"""
            SELECT jsonb_build_object('code',q.code,'label',q.code||' · '||co.name||' · '||u.symbol)::text
            FROM ref_data.quantity_specifications q JOIN ref_data.commodities co ON(co.catalog_id,co.revision_id,co.code)=(q.catalog_id,q.revision_id,q.commodity_code)
            JOIN ref_data.units u ON(u.catalog_id,u.revision_id,u.code)=(q.catalog_id,q.revision_id,q.unit_code)
            WHERE(q.catalog_id,q.revision_id)=($1,$2) AND EXISTS(SELECT 1 FROM ref_data.gas_delivery_conventions g
                WHERE(g.catalog_id,g.revision_id,g.product_code)=(q.catalog_id,q.revision_id,q.commodity_code)) ORDER BY q.code LIMIT 5001
            """,[reference.CatalogId,reference.RevisionId],ct);
        var clocks=await PreviewDatabase.Rows(c,"""
            SELECT jsonb_build_object('code',code,'label',code||' · '||coalesce(iana_zone,'offset seconds '||fixed_offset_seconds::text))::text
            FROM ref_data.clock_rules WHERE(catalog_id,revision_id)=($1,$2) ORDER BY code LIMIT 5001
            """,[reference.CatalogId,reference.RevisionId],ct);
        var connections=await PreviewDatabase.Rows(c,"""
            SELECT jsonb_build_object('pipeline',g.pipeline_code,'zone',g.zone_code,'point',g.point_code,'product',g.product_code,
                'label',g.pipeline_code||' / '||g.zone_code||' / '||l.name||' · '||g.product_code||' · '||g.clock_code)::text
            FROM ref_data.gas_delivery_conventions g JOIN ref_data.locations l ON(l.catalog_id,l.revision_id,l.code)=(g.catalog_id,g.revision_id,g.point_code)
            WHERE(g.catalog_id,g.revision_id)=($1,$2) ORDER BY g.pipeline_code,g.zone_code,g.point_code,g.product_code LIMIT 5001
            """,[reference.CatalogId,reference.RevisionId],ct);
        if(new[]{facilities.Length,quantities.Length,clocks.Length,connections.Length}.Any(n=>n>5000))
            throw new MarketDataException("storage_facility_scope","This setup has more than 5,000 reference choices. A qualified searched authoring path is required; no choices were silently omitted.");
        var sources=facts is {} asset?await Native(c,"SELECT security.storage_facility_flow_choices($1,$2,$3)::text",ct,Catalog,asset.GetProperty("facility").Id("id"),sourceSearch):Json(Array.Empty<object>());
        return new([..facilities.Select(row=>new GasFormChoice(row.Text("code")+":"+row.Text("commodity"),row.Text("label"),new{
                referenceCatalogId=reference.CatalogId,referenceRevisionId=reference.RevisionId,facilityCode=row.Text("code"),commodity=row.Text("commodity")}))],
            [..quantities.Select(row=>new GasFormChoice(row.Text("code"),row.Text("label"),row.Text("code")))],
            [..clocks.Select(row=>new GasFormChoice(row.Text("code"),row.Text("label"),row.Text("code")))],
            [..connections.Select(row=>new GasFormChoice(string.Join(":",new[]{row.Text("pipeline"),row.Text("zone"),row.Text("point"),row.Text("product")}),row.Text("label"),
                new{pipeline=row.Text("pipeline"),zone=row.Text("zone"),point=row.Text("point"),product=row.Text("product")}))],
            [..sources.EnumerateArray().Take(200).Select(row=>new GasFormChoice(row.Id("sourceRevisionId").ToString(),row.Text("label"),new{sourceRevisionId=row.Id("sourceRevisionId")}))]){SourcesTruncated=sources.GetArrayLength()>200};
    }
}
