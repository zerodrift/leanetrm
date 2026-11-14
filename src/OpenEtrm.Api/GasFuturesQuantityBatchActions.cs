using System.Collections.Immutable;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Npgsql;
using OpenEtrm.MarketData;
using OpenEtrm.MarketData.Gas;
using OpenEtrm.MarketData.Trading;

namespace OpenEtrm.Api;

internal sealed class GasFuturesQuantityBatchActions(PreviewDatabase database):ILifecycleActionHandler
{
    internal const string Handler="gas_futures_reconciliation_batch";
    internal const string Version="gas_futures_quantity_batch_v1";
    private const string SourceVersion="gas_futures_quantity_batch_source_v1";
    private const string ContinuationVersion="gas_futures_quantity_batch_continuation_v1";
    public string Kind=>Handler;
    public ImmutableArray<string> Permissions=>["ReadMarketData","ReadTrades","ReadGasOwnedHoldings","RecordGasOwnedHoldings"];

    private static JsonElement Inputs(LifecycleActionRequest request)
    {
        LifecycleNative.Require(request,Handler);
        if(request.Automatic||request.Action!=GasFuturesReconciliationActions.Action||request.Target.ScopeId!=request.Target.BindingId)
            throw Error("Select one exact native account and its saved manual correction policy.");
        try
        {
            var selected=request.Selection.Deserialize<GasFuturesQuantityBatchSelection>(GasOwnedOperations.Json)??throw new JsonException();
            if(selected.Corrections.IsDefaultOrEmpty||selected.Corrections.Length>16384||
                selected.Corrections.Any(s=>s is null||s.PositionId==Guid.Empty||s.PerformanceId==Guid.Empty||s.Supply.IsDefault||
                    s.SourceAllocation is not(GasSourceAllocation.Preserve or GasSourceAllocation.Withdraw))||
                selected.Corrections.Select(s=>s.PerformanceId).Distinct().Count()!=selected.Corrections.Length)
                throw Error("Select every affected performance and its exact financial and stock-use revisions.");
            return JsonSerializer.SerializeToElement(selected,GasOwnedOperations.Json);
        }
        catch(JsonException error){throw new MarketDataException("gas_quantity_input","Supply the closed array of complete physical quantity corrections.",error);}
    }

    private async Task<JsonElement?> Record(NpgsqlConnection connection,LifecycleActionRequest request,JsonElement body,JsonElement? continuation,bool receipt,CancellationToken ct)
    {
        await using var command=ScheduleStore.Command(connection,
            "SELECT security.reconcile_gas_futures_delivery_batch($1,$2,$3,$4::jsonb,$5,$6,$7,$8,$9,$10::jsonb)::text",
            database.Manifest.CatalogId,request.OperationId,request.Target.ScopeId,body.GetRawText(),request.Context.AsOf,request.Context.ContextId,
            request.PolicyId,request.PolicyRevision,receipt,continuation is {} value?value.GetRawText():DBNull.Value);
        return await command.ExecuteScalarAsync(ct) is string text?JsonSerializer.Deserialize<JsonElement>(text):null;
    }

    private async Task<GasFuturesDeliveryReview> Plan(NpgsqlConnection connection,LifecycleActionRequest request,JsonElement body,ScheduleContext context,CancellationToken ct)
    {
        await using(var save=ScheduleStore.Command(connection,"SAVEPOINT gas_quantity_batch_source"))await save.ExecuteNonQueryAsync(ct);
        try
        {
            await PinnedContext.BindExact(connection,database.Manifest.CatalogId,context,ct);
            await TradingContext.Bind(connection,database.Manifest.CatalogId,context,request.Context.AsOf,ct);
            var source=await LifecycleNative.Read(connection,"SELECT security.read_gas_futures_quantity_batch($1,$2,$3::jsonb,$4,$5)::text",ct,
                database.Manifest.CatalogId,request.Target.ScopeId,body.GetRawText(),context.ContextId!.Value,request.Context.AsOf);
            database.RegisterCommitAdmission(connection,new GasOwnedOperationReadAdmission(database.Manifest.CatalogId,source.Instant("authorityUntil")));
            if(source.Text("implementation")!=SourceVersion||source.Id("accountId")!=request.Target.ScopeId||source.Id("contextId")!=context.ContextId||source.Instant("asOf")!=request.Context.AsOf)
                throw Error("Retain the exact account, captured context and complete native batch source.");
            var rows=source.GetProperty("corrections").EnumerateArray().ToImmutableArray();
            if(rows.Length!=body.GetProperty("corrections").GetArrayLength()||rows.Select(r=>r.GetProperty("selection").Id("performanceId")).Distinct().Count()!=rows.Length)
                throw Error("Retain each distinct selected performance in the native batch source.");
            foreach(var row in rows)
            {
                var selected=row.GetProperty("selection");var native=row.GetProperty("source");var profile=FuturesPhysicalDeliveryStore.Materialize(native);
                database.RegisterCommitAdmission(connection,new FuturesPhysicalReadAdmission(database.Manifest.CatalogId,profile.Id,profile.Revision,native.Instant("authorityUntil")));
                if(profile.AccountId!=request.Target.ScopeId||native.Text("reconciliationProfile")!=GasFuturesReconciliationActions.Version||
                    native.GetProperty("performance").Id("id")!=selected.Id("performanceId")||
                    row.GetProperty("currentRevision").GetInt32()!=selected.GetProperty("expectedRevision").GetInt32())
                    throw new MarketDataException("gas_quantity_revision","The selected financial performance changed. Reload the complete batch source.");
                if(row.GetProperty("acknowledgedRevision") is {ValueKind:JsonValueKind.Number} acknowledged&&acknowledged.GetInt32()!=row.GetProperty("currentRevision").GetInt32())
                    throw new MarketDataException("gas_quantity_batch_history","An older acknowledged snapshot cannot replace current state within a new financial batch.");
            }
            var frame=new InputFrame(request.Context.SnapshotId??Guid.NewGuid(),"Physical quantity batch correction",request.Context.AsOf,request.Context.BusinessDate,[],
                ImmutableDictionary<string,ImmutableArray<Guid>>.Empty,request.Context.ContextId);
            var plans=ImmutableArray.CreateBuilder<GasFuturesDeliveryReview>();
            foreach(var group in rows.GroupBy(row=>row.GetProperty("selection").Id("positionId")).Where(g=>g.Any(row=>row.GetProperty("acknowledgedRevision").ValueKind==JsonValueKind.Null)))
            {
                var native=group.First().GetProperty("source");var profile=FuturesPhysicalDeliveryStore.Materialize(native);
                var position=native.GetProperty("position");var link=native.GetProperty("link");var final=native.GetProperty("final");
                var retained=await GasFuturesDeliveryStore.ReadPrincipal(connection,database,frame,new(link.Id("trade_id"),link.Id("binding_id")),
                    profile.Configuration,new(position.Number("quantity"),profile.Terms.Quantity),ct);
                var selected=group.Select(row=>row.GetProperty("selection").Deserialize<GasFuturesQuantityReconciliationSelection>(GasOwnedOperations.Json)!).ToImmutableArray();
                var price=ExactUnitPrice.Parse(final.Text("final_price"),new(profile.Terms.Currency,profile.Terms.Quantity,profile.QuoteMultiplier));
                plans.Add(GasFuturesDeliveryStore.CorrectMany(retained,selected,price,link.Id("final_occurrence_id"),final.Id("source_observation_id")));
            }
            return new(JsonSerializer.SerializeToElement(new{implementation=ContinuationVersion,positions=plans.Select(p=>p.Continuation)},GasOwnedOperations.Json),
                JsonSerializer.SerializeToElement(plans.Select(p=>p.Details),GasOwnedOperations.Json));
        }
        finally
        {
            await using var restore=ScheduleStore.Command(connection,"ROLLBACK TO SAVEPOINT gas_quantity_batch_source;RELEASE SAVEPOINT gas_quantity_batch_source");
            await restore.ExecuteNonQueryAsync(CancellationToken.None);
        }
    }

    public async Task<LifecycleActionReview> Preview(NpgsqlConnection connection,LifecycleActionRequest request,CancellationToken ct)
    {
        var body=Inputs(request);var admission=await GasOwnedOperations.Admission(connection,database,"futures_quantity_reconciliation_batch",body,false,ct);
        var context=await LifecycleNative.Source(connection,database,request.Context,ct);
        await using(var save=ScheduleStore.Command(connection,"SAVEPOINT gas_quantity_batch_review"))await save.ExecuteNonQueryAsync(ct);
        GasFuturesDeliveryReview principal=new(null,null);JsonElement outcome;bool alreadyRecorded=false;
        try
        {
            if(await Record(connection,request,body,null,true,ct) is {} receipt)
            {
                alreadyRecorded=true;outcome=receipt.GetProperty("outcome");
                principal=new(receipt.GetProperty("continuation").ValueKind==JsonValueKind.Null?null:receipt.GetProperty("continuation"),null);
            }
            else
            {
                principal=await Plan(connection,request,body,context,ct);
                outcome=(await Record(connection,request,body,principal.Continuation,false,ct))!.Value;
            }
            await using var complete=ScheduleStore.Command(connection,"SET CONSTRAINTS ALL IMMEDIATE");await complete.ExecuteNonQueryAsync(ct);
        }
        finally
        {
            await using var restore=ScheduleStore.Command(connection,"ROLLBACK TO SAVEPOINT gas_quantity_batch_review;RELEASE SAVEPOINT gas_quantity_batch_review");
            await restore.ExecuteNonQueryAsync(CancellationToken.None);
        }
        var details=JsonSerializer.SerializeToElement(new{selection=body,principal=principal.Details,sourceAllocations=Allocations(outcome),alreadyRecorded,
            willChange=!alreadyRecorded&&outcome.GetProperty("changed").GetBoolean(),corrections=outcome.GetProperty("corrections")},GasOwnedOperations.Json);
        var offer=new LifecycleActionOffer(request.Target,request.Action,"Correct linked physical quantities",request.Target.BindingId,request.Context.AsOf,null,true,null,0,0)
            {PolicyId=request.PolicyId,PolicyRevision=request.PolicyRevision,Selection=body};
        await admission.RequireCommit(connection,ct);return new(offer,Hash(request,body,principal.Continuation),details,database.Manifest.CatalogId,admission.Until);
    }
    public async Task<LifecycleActionWrite> Execute(NpgsqlConnection connection,LifecycleActionRequest request,CancellationToken ct)
    {
        var body=Inputs(request);var admission=await GasOwnedOperations.Admission(connection,database,"futures_quantity_reconciliation_batch",body,false,ct);
        if(await Record(connection,request,body,null,true,ct) is {} receipt)return new(receipt.GetProperty("outcome"),admission);
        var context=await LifecycleNative.Source(connection,database,request.Context,ct);
        var principal=await Plan(connection,request,body,context,ct);
        if(Hash(request,body,principal.Continuation)!=request.PreviewHash)
            throw new MarketDataException("gas_quantity_stale_review","The complete source, stock allocations or principal prefix changed. Review the full batch again.");
        var outcome=(await Record(connection,request,body,principal.Continuation,false,ct))!.Value;
        _=Allocations(outcome);return new(outcome,admission);
    }
    private static ImmutableArray<JsonElement> Allocations(JsonElement outcome)=>[..outcome.GetProperty("corrections").EnumerateArray()
        .Where(row=>row.GetProperty("changed").GetBoolean()).Select(row=>GasFuturesReconciliationActions.AllocationReview(row)!.Value)];
    private static string Hash(LifecycleActionRequest request,JsonElement body,JsonElement? continuation)=>Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(
        JsonSerializer.Serialize(new{implementation=Version,request.OperationId,request.Context,request.Target,request.Action,request.PolicyId,request.PolicyRevision,selection=body,continuation},GasOwnedOperations.Json))));

    public async Task<ImmutableArray<LifecycleActionOffer>> Discover(NpgsqlConnection connection,LifecycleReadContext context,LifecycleTarget? target,LifecycleDiscoveryAccess access,CancellationToken ct)
    {
        if(!access.Allows("ReadGasOwnedHoldings")){if(target is not null)throw new PreviewDeniedException();return [];}
        await using(var capture=ScheduleStore.Command(connection,"SELECT security.gas_futures_delivery_discovery_authority($1)",database.Manifest.CatalogId))
            database.RegisterCommitAdmission(connection,new GasOwnedOperationReadAdmission(database.Manifest.CatalogId,new DateTimeOffset((DateTime)(await capture.ExecuteScalarAsync(ct))!)));
        var source=await LifecycleNative.Read(connection,"SELECT security.read_gas_futures_reconciliations($1,$2,$3)::text",ct,database.Manifest.CatalogId,context.ContextId,context.AsOf);
        database.RegisterCommitAdmission(connection,new GasOwnedOperationReadAdmission(database.Manifest.CatalogId,source.Instant("authorityUntil")));
        return [..source.GetProperty("corrections").EnumerateArray().Where(row=>target is null||target.ScopeId==row.Id("accountId")&&target.BindingId==row.Id("accountId"))
            .GroupBy(row=>row.Id("accountId")).Select(group=>
            {
                var row=group.First();var policy=row.OptionalId("policyId");var reason=policy is null?"Select a saved correction policy for this account.":null;
                return new LifecycleActionOffer(new(Handler,group.Key,group.Key),GasFuturesReconciliationActions.Action,"Correct linked physical quantities",group.Key,context.AsOf,null,reason is null,reason,0,0){
                    Subject=new(row.Text("account"),"Physical delivery quantities",row.Id("bookId"),row.Text("book")),PolicyId=policy,
                    PolicyRevision=policy is null?null:row.GetProperty("policyRevision").GetInt32(),RequiredInputs=["corrections"]};
            })];
    }
    private static MarketDataException Error(string message)=>new("gas_quantity_input",message);
}
