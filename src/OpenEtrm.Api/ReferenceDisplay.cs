using System.Collections.Immutable;
using System.Text.Json;
using Npgsql;
using OpenEtrm.MarketData;
using OpenEtrm.MarketData.Trading;

namespace OpenEtrm.Api;

// Presentation labels for exact references consumed by a response. Equal labels never merge identities.
internal sealed class ReferenceDisplay(ImmutableDictionary<(string Kind,ReferenceKey Key),string> labels)
{
    internal string Label(ReferenceKey key,string kind)=>kind switch
    {ScheduleReferenceKinds.Currency=>Currency(key),ScheduleReferenceKinds.Quantity=>Quantity(key),_=>throw new MarketDataException("reference_display","Select the exact quantity reference kind.")};
    internal string Currency(ReferenceKey key)=>labels[("currency",key)];
    internal string Quantity(ReferenceKey key)=>labels[("quantity",key)];
    internal string Price(PriceUnit unit)=>PreviewData.PriceUnitLabel(Currency(unit.Currency),Quantity(unit.Quantity),unit.Multiplier.ToString());
    internal static async Task<ReferenceDisplay> Read(NpgsqlConnection connection,IEnumerable<PriceUnit> units,CancellationToken ct,
        IEnumerable<ReferenceKey>? currencies=null,IEnumerable<ReferenceKey>? quantities=null)
    {
        var prices=units.ToArray();
        var keys=prices.Select(p=>(Kind:"currency",Key:p.Currency)).Concat(prices.Select(p=>(Kind:"quantity",Key:p.Quantity)))
            .Concat((currencies??[]).Select(k=>(Kind:"currency",Key:k))).Concat((quantities??[]).Select(k=>(Kind:"quantity",Key:k))).Distinct().ToArray();
        if(keys.Length==0)return new(ImmutableDictionary<(string,ReferenceKey),string>.Empty);
        var request=keys.Select(k=>new{kind=k.Kind,catalog=k.Key.Revision.CatalogId,revision=k.Key.Revision.RevisionId,code=k.Key.Code});
        var rows=await PreviewDatabase.Rows(connection,"""
WITH requested AS (SELECT * FROM jsonb_to_recordset($1::jsonb) k(kind text,catalog uuid,revision uuid,code text)),
 labels AS (
 SELECT k.kind,k.catalog,k.revision,k.code,c.display_code label,rc.code||' '||r.revision_key||' '||c.code qualifier
 FROM requested k JOIN ref_data.currencies c ON(c.catalog_id,c.revision_id,c.code)=(k.catalog,k.revision,k.code)
 JOIN ref_data.catalogs rc ON rc.id=c.catalog_id JOIN ref_data.revisions r ON(r.catalog_id,r.id)=(c.catalog_id,c.revision_id) WHERE k.kind='currency'
 UNION ALL
 SELECT k.kind,k.catalog,k.revision,k.code,u.symbol,co.name||' · '||b.code||' · '||rc.code||' '||r.revision_key||' '||q.code
 FROM requested k JOIN ref_data.quantity_specifications q ON(q.catalog_id,q.revision_id,q.code)=(k.catalog,k.revision,k.code)
 JOIN ref_data.units u ON(u.catalog_id,u.revision_id,u.code)=(q.catalog_id,q.revision_id,q.unit_code)
 JOIN ref_data.commodities co ON(co.catalog_id,co.revision_id,co.code)=(q.catalog_id,q.revision_id,q.commodity_code)
 JOIN ref_data.measurement_bases b ON(b.catalog_id,b.revision_id,b.code)=(q.catalog_id,q.revision_id,q.basis_code)
 JOIN ref_data.catalogs rc ON rc.id=q.catalog_id JOIN ref_data.revisions r ON(r.catalog_id,r.id)=(q.catalog_id,q.revision_id) WHERE k.kind='quantity')
SELECT row_to_json(labels)::text FROM labels
""",[JsonSerializer.Serialize(request)],ct);
        if(rows.Length!=keys.Length)throw new MarketDataException("reference_display","An exact currency or quantity reference is unavailable.");
        return new(rows.GroupBy(r=>(r.Text("kind"),r.Text("label"))).SelectMany(g=>g.Select(r=>
            KeyValuePair.Create((r.Text("kind"),new ReferenceKey(new(r.Id("catalog"),r.Id("revision")),r.Text("code"))),
                r.Text("label")+(g.Count()>1?" ("+r.Text("qualifier")+")":"")))).ToImmutableDictionary());
    }
}
