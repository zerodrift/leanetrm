using System.Collections.Immutable;
using System.Text.Json;
using System.Text.Json.Serialization;
using Npgsql;
using OpenEtrm.MarketData;

namespace OpenEtrm.Api;

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record LifecycleMarginOccurrence(Guid Id,string At,string DueAt,string Phase);
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record LifecycleMarginPolicyTerms(Guid Id,Guid DefinitionRevisionId,Guid ContractId,string SettlementCurve,
    Guid ReferenceCatalogId,Guid ReferenceRevisionId,string Currency,string FromAt,string UntilAt,string Opening,string Increment,
    string Tie,string Stage,string RoundingScope,string Implementation,ImmutableArray<LifecycleMarginOccurrence> Occurrences,
    [property:JsonRequired]LifecycleDeliveryProfileSelection? Delivery);
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record LifecycleMarginPolicySave(Guid OperationId,[property:JsonRequired]int ExpectedRevision,LifecycleReadContext Context,LifecycleMarginPolicyTerms Policy);
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record LifecycleFuturesAdoption(Guid OperationId,LifecycleReadContext Context,Guid TradeId,Guid BindingId,
    [property:JsonRequired]int ExpectedRevision,Guid PolicyId,int PolicyRevision,string Adoption);

internal static class LifecycleFuturesSettings
{
    internal static async Task<ImmutableArray<JsonElement>> Read(NpgsqlConnection connection,Guid catalog,CancellationToken ct)=>
        await PreviewDatabase.Rows(connection,"""
          SELECT(to_jsonb(v)||jsonb_build_object('sources',(SELECT coalesce(jsonb_agg(jsonb_build_object('curve_code',s.code,'curve_name',s.name,
           'reference_catalog_id',s.reference_catalog_id,'reference_revision_id',s.reference_revision_id,'quote_code',s.quote_code,'source_code',s.direct_source_code,
           'currency_code',q.currency_code) ORDER BY s.code),'[]'::jsonb)
           FROM market_data.curve_members cm JOIN market_data.curves s ON(s.catalog_id,s.revision_id,s.code)=(cm.catalog_id,cm.revision_id,cm.curve_code)
           JOIN ref_data.quote_specifications q ON(q.catalog_id,q.revision_id,q.code)=(s.reference_catalog_id,s.reference_revision_id,s.quote_code)
           WHERE(cm.catalog_id,cm.revision_id,cm.contract_id)=(v.catalog_id,v.definition_revision_id,v.contract_id)
           AND s.rule='direct' AND s.direct_role='daily_settlement' AND(s.reference_catalog_id,s.reference_revision_id,s.quote_code)=(v.reference_catalog_id,v.reference_revision_id,v.quote_code))))::text
          FROM(SELECT r.catalog_id,r.trade_id,r.binding_id,r.revision,r.book_id,b.name book_name,tr.reference_number trade_reference,
           r.lots,r.execution_price,r.execution_at,r.legal_entity_id,r.legal_entity_revision,m.definition_revision_id,m.contract_id,m.curve_code mark_curve,m.reference_catalog_id,m.reference_revision_id,
           c.expiry_at,s.code series_code,p.name product_name,p.settlement_method,p.location_code,q.quantity_code,q.multiplier quote_multiplier,q.currency_code,mv.quote_code,
           a.policy_id adopted_policy_id,a.policy_revision adopted_policy_revision
           FROM(SELECT DISTINCT ON(trade_id)* FROM trading.trade_revisions WHERE catalog_id=$1 ORDER BY trade_id,revision DESC)r
           JOIN trading.futures_revision_markets m ON(m.catalog_id,m.trade_id,m.revision)=(r.catalog_id,r.trade_id,r.revision)
           JOIN trading.books b ON(b.catalog_id,b.id)=(r.catalog_id,r.book_id)
           JOIN trading.trade_references tr ON(tr.catalog_id,tr.trade_id)=(r.catalog_id,r.trade_id)
           JOIN market_data.contracts c ON(c.catalog_id,c.revision_id,c.id)=(m.catalog_id,m.definition_revision_id,m.contract_id)
           JOIN ref_data.futures_series s ON(s.catalog_id,s.revision_id,s.code)=(m.reference_catalog_id,m.reference_revision_id,m.series_code)
           JOIN ref_data.futures_products p ON(p.catalog_id,p.revision_id,p.code)=(s.catalog_id,s.revision_id,s.product_code)
           JOIN market_data.curves mv ON(mv.catalog_id,mv.revision_id,mv.code)=(m.catalog_id,m.definition_revision_id,m.curve_code)
           JOIN ref_data.quote_specifications q ON(q.catalog_id,q.revision_id,q.code)=(mv.reference_catalog_id,mv.reference_revision_id,mv.quote_code)
           LEFT JOIN settlement_data.futures_margin_adoptions a ON(a.catalog_id,a.trade_id,a.binding_id)=(r.catalog_id,r.trade_id,r.binding_id))v
          ORDER BY v.trade_reference,v.binding_id
          """,[catalog],ct);

    internal static async Task<FuturesMarginWrite> Save(NpgsqlConnection connection,PreviewDatabase database,LifecycleReadContext context,JsonElement body,bool policy,Action requireSource,CancellationToken ct)
    {
        var catalog=database.Manifest.CatalogId;var kind=policy?"policy_save":"adopt";
        await using var capture=ScheduleStore.Command(connection,"SELECT least(settlement_data.futures_margin_selected_authority($1,$2,$3::jsonb),settlement_data.futures_margin_preview_authority($1))",catalog,kind,body.GetRawText());
        var until=new DateTimeOffset((DateTime)(await capture.ExecuteScalarAsync(ct))!);
        database.RegisterCommitAdmission(connection,new LifecycleReadAdmission(catalog,until));
        // Receipt replay precedes the mutable source. Its first native result
        // still reauthorizes every retained reference on the owned final fence.
        await using var status=ScheduleStore.Command(connection,"SELECT settlement_data.futures_margin_command_status($1,$2,$3,$4::jsonb)::text",catalog,body.Id("operationId"),kind,body.GetRawText());
        if(await status.ExecuteScalarAsync(ct) is string recorded)
        {
            var replay=new FuturesMarginWrite(catalog,body.Id("operationId"),kind,body.Clone(),until,JsonSerializer.Deserialize<JsonElement>(recorded));
            database.RegisterCommitAdmission(connection,replay);return replay;
        }
        requireSource();
        await LifecycleNative.Source(connection,database,context,ct);
        var result=policy?await FuturesMarginCommands.SavePolicy(connection,catalog,body,ct):await FuturesMarginCommands.Adopt(connection,catalog,body,ct);
        var pending=result with{AuthorityUntil=until<result.AuthorityUntil?until:result.AuthorityUntil};
        database.RegisterCommitAdmission(connection,pending);return pending;
    }
}
