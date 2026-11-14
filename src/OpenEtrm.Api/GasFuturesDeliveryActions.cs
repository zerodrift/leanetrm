using System.Collections.Immutable;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Npgsql;
using OpenEtrm.MarketData;
using OpenEtrm.MarketData.Gas;
using OpenEtrm.MarketData.Trading;

namespace OpenEtrm.Api;

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record GasFuturesSupplySelection(Guid LotId,Guid EvidenceId,[property:JsonRequired]Guid? ReservationId,string Quantity);
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record GasFuturesPerformanceSelection(Guid PositionId,string BusinessReference,[property:JsonRequired]int ExpectedVersion,string Quantity,
    [property:JsonRequired]DateTimeOffset PerformedAt,[property:JsonRequired]Guid? TitleEvidenceId,[property:JsonRequired]DateTimeOffset? AvailableAt,
    [property:JsonRequired]ImmutableArray<GasFuturesSupplySelection> Supply);
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record GasFuturesPaymentSelection(Guid PositionId,Guid CashId,string BusinessReference,[property:JsonRequired]int ExpectedVersion,
    [property:JsonRequired]DateTimeOffset PaidAt,string Amount,string Evidence);

internal sealed class GasFuturesDeliveryActions(PreviewDatabase database):ILifecycleActionHandler
{
    internal const string Handler="gas_futures_delivery";
    public string Kind=>Handler;
    public ImmutableArray<string> Permissions=>["ReadMarketData","ReadTrades","ReadGasOwnedHoldings","RecordGasOwnedHoldings"];
    private static (string Action,JsonElement Body) Inputs(LifecycleActionRequest request)
    {
        LifecycleNative.Require(request,Handler);
        if(request.Automatic)throw Error("Physical title and recorded principal payments require observed evidence and manual review.");
        try
        {
            object body=request.Action switch{
                "gas.futures.delivery"=>request.Selection.Deserialize<GasFuturesPerformanceSelection>(GasOwnedOperations.Json)??throw new JsonException(),
                "gas.cash.record_payment"=>request.Selection.Deserialize<GasFuturesPaymentSelection>(GasOwnedOperations.Json)??throw new JsonException(),
                _=>throw Error("Select an exact physical-performance or principal-payment operation.")};
            var selected=JsonSerializer.SerializeToElement(body,body.GetType(),GasOwnedOperations.Json);
            if(selected.Id("positionId")!=request.Target.ScopeId||request.Target.BindingId!=(request.Action=="gas.futures.delivery"?request.Target.ScopeId:selected.Id("cashId")))
                throw Error("The selected obligation or principal cash must match the exact native target.");
            return(request.Action=="gas.futures.delivery"?"performance":"payment",selected);
        }
        catch(JsonException error){throw new MarketDataException("futures_delivery_input","Supply the closed physical inputs, explicit null choices and exact instants.",error);}
    }
    private async Task<JsonElement?> Record(NpgsqlConnection connection,LifecycleActionRequest request,string action,JsonElement body,JsonElement? continuation,bool status,CancellationToken ct)
    {
        await using var command=ScheduleStore.Command(connection,
            "SELECT security.record_gas_futures_delivery($1,$2,$3,$4,$5::jsonb,$6,$7,$8,$9,$10,$11::jsonb)::text",
            database.Manifest.CatalogId,request.OperationId,request.Target.ScopeId,action,body.GetRawText(),request.Context.AsOf,request.Context.ContextId,
            request.PolicyId,request.PolicyRevision,status,continuation is {} plan?plan.GetRawText():DBNull.Value);
        return await command.ExecuteScalarAsync(ct) is string text?JsonSerializer.Deserialize<JsonElement>(text):null;
    }
    public async Task<LifecycleActionReview> Preview(NpgsqlConnection connection,LifecycleActionRequest request,CancellationToken ct)
    {
        var(action,body)=Inputs(request);var admission=await GasOwnedOperations.Admission(connection,database,"futures_delivery_"+action,body,false,ct);
        var context=await LifecycleNative.Source(connection,database,request.Context,ct);
        await using(var save=ScheduleStore.Command(connection,"SAVEPOINT gas_delivery_review"))await save.ExecuteNonQueryAsync(ct);
        GasFuturesDeliveryReview principal=new(null,null);JsonElement outcome;
        try
        {
            if(await Record(connection,request,action,body,null,true,ct) is {} receipt)
            {
                outcome=receipt.GetProperty("outcome");
                principal=new(receipt.GetProperty("continuation").ValueKind==JsonValueKind.Null?null:receipt.GetProperty("continuation"),null);
            }
            else
            {
                await using(var capture=ScheduleStore.Command(connection,"SAVEPOINT gas_delivery_review_source"))await capture.ExecuteNonQueryAsync(ct);
                try{if(action=="performance")principal=await Principal(connection,request,body,context,ct);}
                finally
                {
                    await using var restore=ScheduleStore.Command(connection,"ROLLBACK TO SAVEPOINT gas_delivery_review_source;RELEASE SAVEPOINT gas_delivery_review_source");await restore.ExecuteNonQueryAsync(CancellationToken.None);
                }
                outcome=(await Record(connection,request,action,body,principal.Continuation,false,ct))!.Value;
            }
            await using var complete=ScheduleStore.Command(connection,"SET CONSTRAINTS ALL IMMEDIATE");await complete.ExecuteNonQueryAsync(ct);
        }
        finally
        {
            await using var restore=ScheduleStore.Command(connection,"ROLLBACK TO SAVEPOINT gas_delivery_review;RELEASE SAVEPOINT gas_delivery_review");await restore.ExecuteNonQueryAsync(CancellationToken.None);
        }
        var hash=Hash(request,body,principal.Continuation);
        var offer=new LifecycleActionOffer(request.Target,request.Action,Label(action),request.Target.BindingId,request.Context.AsOf,null,true,null,1,
            body.GetProperty("expectedVersion").GetInt32()){PolicyId=request.PolicyId,PolicyRevision=request.PolicyRevision,Selection=body};
        var details=JsonSerializer.SerializeToElement(new{selection=body,principal=principal.Details,willChange=outcome.GetProperty("changed").GetBoolean(),
            acknowledgedRevision=outcome.GetProperty("acknowledgedRevision").GetInt32(),currentRevision=outcome.GetProperty("currentRevision").GetInt32()},GasOwnedOperations.Json);
        await admission.RequireCommit(connection,ct);return new(offer,hash,details,database.Manifest.CatalogId,admission.Until);
    }
    public async Task<LifecycleActionWrite> Execute(NpgsqlConnection connection,LifecycleActionRequest request,CancellationToken ct)
    {
        var(action,body)=Inputs(request);var admission=await GasOwnedOperations.Admission(connection,database,"futures_delivery_"+action,body,false,ct);
        if(await Record(connection,request,action,body,null,true,ct) is {} receipt)return new(receipt.GetProperty("outcome"),admission);
        var context=await LifecycleNative.Source(connection,database,request.Context,ct);
        GasFuturesDeliveryReview principal=new(null,null);
        await using(var save=ScheduleStore.Command(connection,"SAVEPOINT gas_delivery_source"))await save.ExecuteNonQueryAsync(ct);
        try{if(action=="performance")principal=await Principal(connection,request,body,context,ct);}
        finally
        {
            await using var restore=ScheduleStore.Command(connection,"ROLLBACK TO SAVEPOINT gas_delivery_source;RELEASE SAVEPOINT gas_delivery_source");await restore.ExecuteNonQueryAsync(CancellationToken.None);
        }
        if(Hash(request,body,principal.Continuation)!=request.PreviewHash)throw new MarketDataException("futures_delivery_stale_review","The physical source, principal prefix or reviewed inputs changed. Review them again.");
        return new((await Record(connection,request,action,body,principal.Continuation,false,ct))!.Value,admission);
    }
    private async Task<GasFuturesDeliveryReview> Principal(NpgsqlConnection connection,LifecycleActionRequest request,JsonElement body,ScheduleContext context,CancellationToken ct)
    {
        await PinnedContext.BindExact(connection,database.Manifest.CatalogId,context,ct);
        await TradingContext.Bind(connection,database.Manifest.CatalogId,context,request.Context.AsOf,ct);
        var source=await LifecycleNative.Read(connection,"SELECT settlement_data.read_futures_delivery_position($1,$2,$3,$4)::text",ct,
            database.Manifest.CatalogId,request.Target.ScopeId,context.ContextId!.Value,request.Context.AsOf);
        var profile=FuturesPhysicalDeliveryStore.Materialize(source);var position=source.GetProperty("position");var link=source.GetProperty("link");var final=source.GetProperty("final");
        database.RegisterCommitAdmission(connection,new FuturesPhysicalReadAdmission(database.Manifest.CatalogId,profile.Id,profile.Revision,source.Instant("authorityUntil")));
        var original=new Quantity(position.Number("quantity"),profile.Terms.Quantity);
        var frame=new InputFrame(request.Context.SnapshotId??Guid.NewGuid(),"Physical delivery review",request.Context.AsOf,request.Context.BusinessDate,[],
            ImmutableDictionary<string,ImmutableArray<Guid>>.Empty,request.Context.ContextId);
        var retained=await GasFuturesDeliveryStore.ReadPrincipal(connection,database,frame,new(link.Id("trade_id"),link.Id("binding_id")),profile.Configuration,original,ct);
        var amount=ExactDecimal.Parse(body.Text("quantity"));
        var performance=new GasDeliveryPrincipalPerformance(request.OperationId,body.Text("businessReference"),body.Instant("performedAt"),
            new(original.Amount.CompareTo(default)>0?amount:-amount,original.Specification));
        if(retained.Performances.Any(p=>p.BusinessReference==performance.BusinessReference))
            throw Error("This physical business reference already has an accepted performance. Retry its recorded operation or use the explicit correction contract.");
        var price=ExactUnitPrice.Parse(final.Text("final_price"),new(profile.Terms.Currency,profile.Terms.Quantity,profile.QuoteMultiplier));
        return GasFuturesDeliveryStore.Plan(retained with{Performances=retained.Performances.Add(performance)},price,link.Id("final_occurrence_id"),final.Id("source_observation_id"));
    }
    private static string Hash(LifecycleActionRequest request,JsonElement body,JsonElement? continuation)=>Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(
        JsonSerializer.Serialize(new{implementation=GasFuturesDeliveryStore.Version,request.OperationId,request.Context,request.Target,request.Action,
            request.PolicyId,request.PolicyRevision,selection=body,continuation},GasOwnedOperations.Json))));
    public async Task<ImmutableArray<LifecycleActionOffer>> Discover(NpgsqlConnection connection,LifecycleReadContext context,LifecycleTarget? target,LifecycleDiscoveryAccess access,CancellationToken ct)
    {
        if(!access.Allows("ReadGasOwnedHoldings"))
        {
            if(target is not null)throw new PreviewDeniedException();
            return [];
        }
        await using(var capture=ScheduleStore.Command(connection,"SELECT security.gas_futures_delivery_discovery_authority($1)",database.Manifest.CatalogId))
        {
            var until=new DateTimeOffset((DateTime)(await capture.ExecuteScalarAsync(ct))!);
            database.RegisterCommitAdmission(connection,new GasOwnedOperationReadAdmission(database.Manifest.CatalogId,until));
        }
        // Discovery consumes the shared registry's admitted, exact bound source.
        var state=await LifecycleNative.Read(connection,"SELECT security.read_gas_futures_delivery_actions($1,$2,$3)::text",ct,database.Manifest.CatalogId,context.ContextId,context.AsOf);
        database.RegisterCommitAdmission(connection,new GasOwnedOperationReadAdmission(database.Manifest.CatalogId,state.Instant("authorityUntil")));
        var offers=ImmutableArray.CreateBuilder<LifecycleActionOffer>();
        foreach(var row in state.GetProperty("deliveryRemainders").EnumerateArray())Add(row,"performance",row.Id("id"),row.Id("id"),row.Text("remaining_quantity"));
        foreach(var row in state.GetProperty("deliveryCash").EnumerateArray())Add(row,"payment",row.Id("position_id"),row.Id("id"),row.Text("amount"));
        return offers.ToImmutable();
        void Add(JsonElement row,string action,Guid owner,Guid binding,string amount)
        {
            if(target is not null&&(target.ScopeId!=owner||target.BindingId!=binding))return;
            var account=state.GetProperty("accounts").EnumerateArray().Single(a=>a.Id("id")==row.Id("account_id"));
            var code=action=="performance"?"gas.futures.delivery":"gas.cash.record_payment";
            var policies=state.GetProperty("policies").EnumerateArray().Where(p=>p.Id("account_id")==account.Id("id")&&p.Text("action")==code).OrderByDescending(p=>p.GetProperty("revision").GetInt32()).ToArray();
            var policy=policies.FirstOrDefault();var reason=policies.Length==0?"lifecycle_configuration_required":context.AsOf<policy.Instant("from_at")||context.AsOf>=policy.Instant("until_at")?"lifecycle_action_window":null;
            var revision=action=="performance"?state.GetProperty("principalVersions").EnumerateArray().Where(p=>p.Id("position_id")==owner).Select(p=>p.GetProperty("revision").GetInt32()).DefaultIfEmpty().Single():0;
            offers.Add(new(new(Kind,owner,binding),code,Label(action),binding,context.AsOf,null,reason is null,reason,1,revision){
                Subject=new(account.Text("code"),"Physical futures delivery",account.Id("book_id"),account.Text("book_name")),
                PolicyId=policies.Length==0?null:policy.Id("policy_id"),PolicyRevision=policies.Length==0?null:policy.GetProperty("policy_revision").GetInt32(),AvailableQuantity=amount,
                RequiredInputs=action=="performance"?["positionId","businessReference","expectedVersion","quantity","performedAt","titleEvidenceId","availableAt","supply"]:["positionId","cashId","businessReference","expectedVersion","paidAt","amount","evidence"]});
        }
    }
    private static string Label(string action)=>action=="performance"?"Record observed futures delivery":"Record delivery principal payment";
    private static MarketDataException Error(string message)=>new("futures_delivery_input",message);
}
