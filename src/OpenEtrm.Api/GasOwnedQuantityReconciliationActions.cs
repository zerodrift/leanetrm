using System.Collections.Immutable;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Npgsql;
using OpenEtrm.MarketData;
using OpenEtrm.MarketData.Gas;

namespace OpenEtrm.Api;

internal sealed class GasOwnedQuantityReconciliationActions(PreviewDatabase database):ILifecycleActionHandler
{
    internal const string Handler="gas_owned_quantity_reconciliation";
    internal const string Action="gas.owned.reconcile_quantity";
    internal const string Version="gas_owned_quantity_reconciliation_v1";
    public string Kind=>Handler;
    public ImmutableArray<string> Permissions=>["ReadMarketData","ReadTrades","ReadGasOwnedHoldings","RecordGasOwnedHoldings"];

    private static JsonElement Inputs(LifecycleActionRequest request)
    {
        LifecycleNative.Require(request,Handler);
        if(request.Automatic||request.Action!=Action||request.Target.ScopeId!=request.Target.BindingId)
            throw Error("Select one exact gas account and its saved manual correction policy.");
        try
        {
            var selected=request.Selection.Deserialize<GasOwnedQuantityReconciliationSelection>(GasOwnedOperations.Json)??throw new JsonException();
            if(selected.Positions.IsDefault||selected.Consumptions.IsDefault||selected.Positions.Length+selected.Consumptions.Length is <1 or >16384||
                string.IsNullOrWhiteSpace(selected.Evidence)||selected.Evidence.Length>2000||
                selected.Positions.Any(row=>row is null||row.PositionId==Guid.Empty||row.ExpectedRevision<1||row.EvidenceId==Guid.Empty||
                    string.IsNullOrWhiteSpace(row.Quantity)||row.SourceAllocation is not(GasSourceAllocation.Preserve or GasSourceAllocation.Withdraw))||
                selected.Consumptions.Any(row=>row is null||row.ConsumptionId==Guid.Empty||row.ExpectedRevision<1||row.LotId==Guid.Empty||row.EvidenceId==Guid.Empty||row.ReservationId==Guid.Empty||
                    string.IsNullOrWhiteSpace(row.Quantity)||row.SourceAllocation is not(GasSourceAllocation.Preserve or GasSourceAllocation.Withdraw))||
                selected.Positions.Select(row=>row.PositionId).Distinct().Count()!=selected.Positions.Length||
                selected.Consumptions.Select(row=>row.ConsumptionId).Distinct().Count()!=selected.Consumptions.Length)
                throw Error("Name every affected title or resource use, its current financial revision and the explicit source allocation law.");
            return JsonSerializer.SerializeToElement(selected,GasOwnedOperations.Json);
        }
        catch(JsonException error){throw new MarketDataException("gas_quantity_input","Supply the closed ordinary title and resource corrections with explicit retractions and source evidence.",error);}
    }
    private async Task<JsonElement?> Record(NpgsqlConnection connection,LifecycleActionRequest request,JsonElement body,bool receipt,CancellationToken ct)
    {
        await using var command=ScheduleStore.Command(connection,"SELECT security.reconcile_gas_owned_quantities($1,$2,$3,$4::jsonb,$5,$6,$7,$8,$9)::text",
            database.Manifest.CatalogId,request.OperationId,request.Target.ScopeId,body.GetRawText(),request.Context.AsOf,request.Context.ContextId,request.PolicyId,request.PolicyRevision,receipt);
        return await command.ExecuteScalarAsync(ct) is string text?JsonSerializer.Deserialize<JsonElement>(text):null;
    }
    private async Task<JsonElement> Source(NpgsqlConnection connection,LifecycleActionRequest request,JsonElement body,ScheduleContext context,CancellationToken ct)
    {
        await using(var save=ScheduleStore.Command(connection,"SAVEPOINT gas_owned_correction_source"))await save.ExecuteNonQueryAsync(ct);
        try
        {
            await PinnedContext.BindExact(connection,database.Manifest.CatalogId,context,ct);
            await TradingContext.Bind(connection,database.Manifest.CatalogId,context,request.Context.AsOf,ct);
            var source=await LifecycleNative.Read(connection,"SELECT security.read_gas_owned_reconciliation($1,$2,$3::jsonb,$4,$5)::text",ct,
                database.Manifest.CatalogId,request.Target.ScopeId,body.GetRawText(),context.ContextId!.Value,request.Context.AsOf);
            database.RegisterCommitAdmission(connection,new GasOwnedOperationReadAdmission(database.Manifest.CatalogId,source.Instant("authorityUntil")));
            if(source.Text("implementation")!="gas_owned_quantity_reconciliation_source_v1"||source.Text("scope")!="gas_owned_reconciliation_dependency_facts_v1"||
                source.Id("accountId")!=request.Target.ScopeId||source.Id("contextId")!=context.ContextId||source.Instant("asOf")!=request.Context.AsOf)
                throw Error("Retain this exact account, certified source and complete native correction dependency facts.");
            return source;
        }
        finally
        {
            await using var restore=ScheduleStore.Command(connection,"ROLLBACK TO SAVEPOINT gas_owned_correction_source;RELEASE SAVEPOINT gas_owned_correction_source");
            await restore.ExecuteNonQueryAsync(CancellationToken.None);
        }
    }
    public async Task<LifecycleActionReview> Preview(NpgsqlConnection connection,LifecycleActionRequest request,CancellationToken ct)
    {
        var body=Inputs(request);var admission=await GasOwnedOperations.Admission(connection,database,"owned_quantity_reconciliation",body,false,ct);
        var context=await LifecycleNative.Source(connection,database,request.Context,ct);
        var source=await Source(connection,request,body,context,ct);
        await using(var save=ScheduleStore.Command(connection,"SAVEPOINT gas_owned_correction_review"))await save.ExecuteNonQueryAsync(ct);
        JsonElement outcome;bool alreadyRecorded;
        try
        {
            var receipt=await Record(connection,request,body,true,ct);alreadyRecorded=receipt is not null;
            outcome=receipt is {} retained?retained.GetProperty("outcome"):(await Record(connection,request,body,false,ct))!.Value;
            await using var complete=ScheduleStore.Command(connection,"SET CONSTRAINTS ALL IMMEDIATE");await complete.ExecuteNonQueryAsync(ct);
        }
        finally
        {
            await using var restore=ScheduleStore.Command(connection,"ROLLBACK TO SAVEPOINT gas_owned_correction_review;RELEASE SAVEPOINT gas_owned_correction_review");
            await restore.ExecuteNonQueryAsync(CancellationToken.None);
        }
        var details=JsonSerializer.SerializeToElement(new{selection=body,positions=source.GetProperty("positions"),consumptions=source.GetProperty("consumptions"),
            sourceAllocations=Allocations(outcome),outcome,alreadyRecorded,willChange=!alreadyRecorded&&outcome.GetProperty("changed").GetBoolean()},GasOwnedOperations.Json);
        var offer=new LifecycleActionOffer(request.Target,Action,"Correct observed gas title or use",request.Target.BindingId,request.Context.AsOf,null,true,null,0,0)
            {PolicyId=request.PolicyId,PolicyRevision=request.PolicyRevision,Selection=body};
        await admission.RequireCommit(connection,ct);return new(offer,Hash(request,body),details,database.Manifest.CatalogId,admission.Until);
    }
    public async Task<LifecycleActionWrite> Execute(NpgsqlConnection connection,LifecycleActionRequest request,CancellationToken ct)
    {
        var body=Inputs(request);var admission=await GasOwnedOperations.Admission(connection,database,"owned_quantity_reconciliation",body,false,ct);
        if(await Record(connection,request,body,true,ct) is {} receipt)return new(receipt.GetProperty("outcome"),admission);
        var context=await LifecycleNative.Source(connection,database,request.Context,ct);
        _=await Source(connection,request,body,context,ct);
        if(Hash(request,body)!=request.PreviewHash)throw new MarketDataException("gas_quantity_stale_review","The reviewed source, exact quantities or policy changed. Review the complete ordinary correction again.");
        var outcome=(await Record(connection,request,body,false,ct))!.Value;_=Allocations(outcome);return new(outcome,admission);
    }
    private static ImmutableArray<JsonElement> Allocations(JsonElement outcome)
    {
        if(!outcome.GetProperty("changed").GetBoolean())return [];
        return [..outcome.GetProperty("sourceAllocations").EnumerateArray().Select(fact=>
            GasFuturesReconciliationActions.AllocationReview(JsonSerializer.SerializeToElement(new{changed=true,sourceAllocation=fact.Text("sourceAllocation"),sourceAllocations=new[]{fact}},GasOwnedOperations.Json))!.Value)];
    }
    private static string Hash(LifecycleActionRequest request,JsonElement body)=>Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(
        JsonSerializer.Serialize(new{implementation=Version,request.OperationId,request.Context,request.Target,request.Action,request.PolicyId,request.PolicyRevision,selection=body},GasOwnedOperations.Json))));
    public async Task<ImmutableArray<LifecycleActionOffer>> Discover(NpgsqlConnection connection,LifecycleReadContext context,LifecycleTarget? target,LifecycleDiscoveryAccess access,CancellationToken ct)
    {
        if(!access.Allows("ReadGasOwnedHoldings")){if(target is not null)throw new PreviewDeniedException();return [];}
        var state=await GasOwnedOperations.Read(connection,database,context,target?.ScopeId,ct);
        return [..state.GetProperty("accounts").EnumerateArray().Where(account=>account.Text("kind")=="owned_inventory"&&
            (target is null||target.ScopeId==account.Id("id")&&target.BindingId==account.Id("id"))).Select(account=>
        {
            var policies=state.GetProperty("policies").EnumerateArray().Where(policy=>policy.Id("account_id")==account.Id("id")&&policy.Text("action")==Action)
                .OrderByDescending(policy=>policy.GetProperty("revision").GetInt32()).ToImmutableArray();
            var policy=policies.FirstOrDefault();var reason=policies.IsEmpty?"Select a saved manual correction policy for this account.":
                context.AsOf<policy.Instant("from_at")||context.AsOf>=policy.Instant("until_at")?"The selected correction policy is outside its saved action window.":null;
            return new LifecycleActionOffer(new(Handler,account.Id("id"),account.Id("id")),Action,"Correct observed gas title or use",account.Id("id"),context.AsOf,null,reason is null,reason,0,0){
                Subject=new(account.Text("code"),"Owned gas quantity",account.Id("book_id"),account.Text("book_name")),
                PolicyId=policies.IsEmpty?null:policy.Id("policy_id"),PolicyRevision=policies.IsEmpty?null:policy.GetProperty("policy_revision").GetInt32(),RequiredInputs=["positions","consumptions","evidence"]};
        })];
    }
    private static MarketDataException Error(string message)=>new("gas_quantity_input",message);
}
