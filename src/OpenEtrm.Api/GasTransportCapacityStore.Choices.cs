using System.Collections.Immutable;
using System.Text.Json;
using Npgsql;
using OpenEtrm.MarketData;

namespace OpenEtrm.Api;

public sealed partial class GasTransportCapacityStore
{
    // The caller has already captured authority and bound the exact operating
    // source. Existing entitlements retain their own reference bundle; new
    // definitions use the explicitly resolved location authoring scope.
    private async Task<GasTransportCapacityChoices> Choices(NpgsqlConnection c,JsonElement? facts,ScheduleContext context,CancellationToken ct)
    {
        var reference=facts is {} selected?
            new ReferenceRevision(selected.GetProperty("version").Id("reference_catalog_id"),selected.GetProperty("version").Id("reference_revision_id")):
            (await database.ReferenceScope(c,"locations",ct,DateWorkspace.Date(context.EffectiveOn))).Revision;
        var books=await PreviewDatabase.Rows(c,"""
            SELECT jsonb_build_object('id',id,'label',name)::text FROM trading.books
            WHERE catalog_id=$1 ORDER BY name,id LIMIT 5001
            """,[Catalog],ct);
        var entities=await TradeParties.Catalog(c,Catalog,true,ct);
        var counterparties=await TradeParties.Catalog(c,Catalog,false,ct);
        var quantities=await PreviewDatabase.Rows(c,"""
            SELECT jsonb_build_object('code',q.code,'label',q.code||' · '||co.name||' · '||u.symbol)::text
            FROM ref_data.quantity_specifications q
            JOIN ref_data.commodities co ON(co.catalog_id,co.revision_id,co.code)=(q.catalog_id,q.revision_id,q.commodity_code)
            JOIN ref_data.units u ON(u.catalog_id,u.revision_id,u.code)=(q.catalog_id,q.revision_id,q.unit_code)
            WHERE(q.catalog_id,q.revision_id)=($1,$2) AND EXISTS(SELECT 1 FROM ref_data.gas_delivery_conventions g
                WHERE(g.catalog_id,g.revision_id,g.product_code)=(q.catalog_id,q.revision_id,q.commodity_code))
            ORDER BY q.code LIMIT 5001
            """,[reference.CatalogId,reference.RevisionId],ct);
        var products=await PreviewDatabase.Rows(c,"""
            SELECT jsonb_build_object('code',co.code,'label',co.name)::text FROM ref_data.commodities co
            WHERE(co.catalog_id,co.revision_id)=($1,$2) AND EXISTS(SELECT 1 FROM ref_data.gas_delivery_conventions g
                WHERE(g.catalog_id,g.revision_id,g.product_code)=(co.catalog_id,co.revision_id,co.code))
            ORDER BY co.name,co.code LIMIT 5001
            """,[reference.CatalogId,reference.RevisionId],ct);
        var pipelines=await PreviewDatabase.Rows(c,"""
            SELECT jsonb_build_object('code',l.code,'label',l.name)::text FROM ref_data.locations l
            WHERE(l.catalog_id,l.revision_id)=($1,$2) AND EXISTS(SELECT 1 FROM ref_data.gas_delivery_conventions g
                WHERE(g.catalog_id,g.revision_id,g.pipeline_code)=(l.catalog_id,l.revision_id,l.code))
            ORDER BY l.name,l.code LIMIT 5001
            """,[reference.CatalogId,reference.RevisionId],ct);
        // Endpoints are separate choices. Never materialize every possible
        // receipt-by-delivery pair or imply a supported interconnection.
        var endpoints=await PreviewDatabase.Rows(c,"""
            SELECT jsonb_build_object('zone',p.zone_code,'point',p.point_code,'label',p.label)::text FROM(
                SELECT DISTINCT g.zone_code,g.point_code,l.name||' · '||g.zone_code AS label
                FROM ref_data.gas_delivery_conventions g
                JOIN ref_data.locations l ON(l.catalog_id,l.revision_id,l.code)=(g.catalog_id,g.revision_id,g.point_code)
                WHERE(g.catalog_id,g.revision_id)=($1,$2)
            )p ORDER BY p.label,p.zone_code,p.point_code LIMIT 5001
            """,[reference.CatalogId,reference.RevisionId],ct);
        if(new[]{books.Length,entities.Length,counterparties.Length,quantities.Length,products.Length,pipelines.Length,endpoints.Length}.Any(n=>n>5000))
            throw new MarketDataException("gas_transport_capacity_scope","This setup has more than 5,000 reference choices. A qualified searched authoring path is required; no choices were silently omitted.");
        ImmutableArray<GasFormChoice> Codes(ImmutableArray<JsonElement> rows)=>[..rows.Select(row=>new GasFormChoice(row.Text("code"),row.Text("label"),row.Text("code")))];
        return new([new(reference.CatalogId+"@"+reference.RevisionId,"Exact reference "+reference.CatalogId+" / "+reference.RevisionId,
                new{referenceCatalogId=reference.CatalogId,referenceRevisionId=reference.RevisionId})],
            [..books.Select(row=>new GasFormChoice(row.Id("id").ToString(),row.Text("label"),row.Id("id")))],
            [..entities.Select(row=>new GasFormChoice(row.Id+"@"+row.Revision,row.Name+" · revision "+row.Revision,new{legalEntityId=row.Id,legalEntityRevision=row.Revision}))],
            [..counterparties.Select(row=>new GasFormChoice(row.Id+"@"+row.Revision,row.Name+" · revision "+row.Revision,new{counterpartyId=row.Id,counterpartyRevision=row.Revision}))],
            Codes(products),Codes(quantities),Codes(pipelines),
            [..endpoints.Select(row=>new GasFormChoice(row.Text("zone")+":"+row.Text("point"),row.Text("label"),new{receiptZone=row.Text("zone"),receiptPoint=row.Text("point")}))],
            [..endpoints.Select(row=>new GasFormChoice(row.Text("zone")+":"+row.Text("point"),row.Text("label"),new{deliveryZone=row.Text("zone"),deliveryPoint=row.Text("point")}))],
            [..endpoints.Select(row=>new GasFormChoice(row.Text("point"),row.Text("label"),row.Text("point")))]);
    }
}
