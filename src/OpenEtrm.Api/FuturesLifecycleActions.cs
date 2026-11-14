using System.Collections.Immutable;
using System.Text.Json;
using System.Text.Json.Serialization;
using Npgsql;
using OpenEtrm.MarketData;

namespace OpenEtrm.Api;

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
internal sealed record FuturesMarginActionSelection(int ExpectedRevision,int ExpectedMarginRevision,Guid ThroughOccurrence,
    ImmutableArray<FuturesMarginCorrectionDue> CorrectionDue);

internal sealed class FuturesLifecycleActions(PreviewDatabase database,Action<LifecycleReadContext> requireFrame):ILifecycleActionHandler
{
    public string Kind=>"futures";
    public ImmutableArray<string> Permissions=>["ReadMarketData","ReadTrades","AmendTrade"];
    private Guid Catalog=>database.Manifest.CatalogId;
    private static FuturesMarginActionSelection Selection(LifecycleActionRequest request)
    {
        LifecycleNative.Require(request,"futures");
        FuturesMarginActionSelection value;
        try{value=request.Selection.Deserialize<FuturesMarginActionSelection>(LifecycleNative.Json)??throw new JsonException();}
        catch(JsonException error){throw new MarketDataException("lifecycle_input","Select exact futures margin versions, occurrence and correction dues.",error);}
        if(request.Action!="futures.margin"||value.ExpectedRevision<1||value.ExpectedMarginRevision<1||value.ThroughOccurrence==Guid.Empty||value.CorrectionDue.IsDefault)
            throw new MarketDataException("lifecycle_input","Supply the complete explicit futures margin selection.");
        return value;
    }
    private static JsonElement NativeBody(LifecycleActionRequest request,FuturesMarginActionSelection s)=>JsonSerializer.SerializeToElement(new{
        request.OperationId,tradeId=request.Target.ScopeId,bindingId=request.Target.BindingId,s.ExpectedRevision,s.ExpectedMarginRevision,
        request.Context.ContextId,asOf=LifecycleNative.Instant(request.Context.AsOf),s.ThroughOccurrence,implementation=FuturesMarginCoordinator.Version,s.CorrectionDue,request.PreviewHash},LifecycleNative.Json);
    private Task<JsonElement> CheckPolicy(NpgsqlConnection connection,LifecycleActionRequest request,CancellationToken ct)=>LifecycleNative.Read(connection,
        "SELECT lifecycle.check_action_policy($1,$2,$3,$4,$5,$6)::text",ct,Catalog,request.PolicyId,request.PolicyRevision,request.Action,request.Context.AsOf,request.Automatic);
    public async Task<LifecycleActionReview> Preview(NpgsqlConnection connection,LifecycleActionRequest request,CancellationToken ct)
    {
        var s=Selection(request);var until=await LifecycleNative.ActionAuthority(connection,database,request.Action,LifecycleNative.Body(request,s),ct);
        await LifecycleNative.Source(connection,database,request.Context,ct);
        await CheckPolicy(connection,request,ct);
        var prepared=await FuturesMarginCoordinator.Preview(connection,database,NativeBody(request,s),ct);
        var result=new LifecycleActionReview(new(request.Target,request.Action,"Determine futures margin",s.ThroughOccurrence,request.Context.AsOf,null,true,null,
            prepared.TradeRevision,prepared.MarginRevision),prepared.Hash,prepared.Details(),Catalog,until);
        await result.RequireCommit(connection,ct);return result;
    }
    public async Task<LifecycleActionWrite> Execute(NpgsqlConnection connection,LifecycleActionRequest request,CancellationToken ct)
    {
        var s=Selection(request);var body=LifecycleNative.Body(request,s);var until=await LifecycleNative.ActionAuthority(connection,database,request.Action,body,ct);
        await using(var status=ScheduleStore.Command(connection,"SELECT lifecycle.command_status($1,$2,$3,$4::jsonb,'AmendTrade',$5)::text",Catalog,request.OperationId,request.Action,body.GetRawText(),until))
            if(await status.ExecuteScalarAsync(ct) is string previous)return new(JsonSerializer.Deserialize<JsonElement>(previous),Admission(request,body,until));
        requireFrame(request.Context);
        await LifecycleNative.Source(connection,database,request.Context,ct);
        await CheckPolicy(connection,request,ct);
        var accepted=await FuturesMarginCoordinator.Determine(connection,database,NativeBody(request,s),ct);
        var value=await LifecycleNative.Read(connection,"SELECT lifecycle.record_native_action($1,$2::jsonb,$3::jsonb,$4)::text",ct,Catalog,body.GetRawText(),accepted.Outcome.GetRawText(),until);
        return new(value,new LifecycleCombinedAdmission(accepted,Admission(request,body,until)));
    }
    private LifecycleNativeAdmission Admission(LifecycleActionRequest request,JsonElement body,DateTimeOffset until)=>new(Catalog,request.OperationId,request.Action,body,"AmendTrade",until);
    public async Task<ImmutableArray<LifecycleActionOffer>> Discover(NpgsqlConnection connection,LifecycleReadContext context,LifecycleTarget? target,LifecycleDiscoveryAccess access,CancellationToken ct)
    {
        var rows=await PreviewDatabase.Rows(connection,"""
         SELECT row_to_json(v)::text FROM(SELECT a.trade_id,a.binding_id,a.policy_id,a.policy_revision,o.id,o.at,r.revision,r.book_id,b.name book_name,
         tr.reference_number trade_reference,fp.name product_name,
         (settlement_data.read_futures_margin_state(a.catalog_id,a.trade_id,a.binding_id,$4,$2)->>'version')::integer AS margin_revision
         FROM settlement_data.futures_margin_adoptions a JOIN settlement_data.futures_margin_policy_occurrences o
         ON(o.catalog_id,o.policy_id,o.policy_revision)=(a.catalog_id,a.policy_id,a.policy_revision)
         JOIN LATERAL(SELECT h.revision,h.book_id FROM trading.trade_revisions h WHERE(h.catalog_id,h.trade_id)=(a.catalog_id,a.trade_id) ORDER BY h.revision DESC LIMIT 1)r ON true
         JOIN trading.books b ON(b.catalog_id,b.id)=(a.catalog_id,r.book_id)
         JOIN trading.trade_references tr ON(tr.catalog_id,tr.trade_id)=(a.catalog_id,a.trade_id)
         JOIN settlement_data.futures_margin_policies p ON(p.catalog_id,p.id,p.revision)=(a.catalog_id,a.policy_id,a.policy_revision)
         JOIN market_data.contracts c ON(c.catalog_id,c.revision_id,c.id)=(p.catalog_id,p.definition_revision_id,p.contract_id)
         JOIN ref_data.futures_series s ON(s.catalog_id,s.revision_id,s.code)=(c.reference_catalog_id,c.reference_revision_id,c.futures_series_code)
         JOIN ref_data.futures_products fp ON(fp.catalog_id,fp.revision_id,fp.code)=(s.catalog_id,s.revision_id,s.product_code)
         WHERE a.catalog_id=$1 AND o.at<=$2 AND($3::uuid IS NULL OR a.trade_id=$3) AND($5 OR p.delivery_profile_id IS NULL) ORDER BY a.trade_id,o.at,o.phase)v
         """,[Catalog,context.AsOf,target is null?DBNull.Value:target.ScopeId,context.ContextId,access.Allows("ReadGasOwnedHoldings")],ct);
        return [..rows.Where(r=>target is null||target.BindingId==r.Id("binding_id")).Select(r=>new LifecycleActionOffer(new(Kind,r.Id("trade_id"),r.Id("binding_id")),
            "futures.margin","Review scheduled margin",r.Id("id"),r.Instant("at"),null,false,"lifecycle_policy_required",int.Parse(r.Text("revision"),System.Globalization.CultureInfo.InvariantCulture),
            int.Parse(r.Text("margin_revision"),System.Globalization.CultureInfo.InvariantCulture))
        {
            ScheduleSettings=new(int.Parse(r.Text("revision"),System.Globalization.CultureInfo.InvariantCulture),null,null,null,r.Id("policy_id"),int.Parse(r.Text("policy_revision"),System.Globalization.CultureInfo.InvariantCulture)),
            Subject=new(TradingReference.Trade(r),r.Text("product_name"),r.Id("book_id"),r.Text("book_name")),
            Selection=JsonSerializer.SerializeToElement(new FuturesMarginActionSelection(int.Parse(r.Text("revision"),System.Globalization.CultureInfo.InvariantCulture),
                int.Parse(r.Text("margin_revision"),System.Globalization.CultureInfo.InvariantCulture),r.Id("id"),[]),LifecycleNative.Json),RequiredInputs=["policyId","correctionDue"]
        })];
    }
}
