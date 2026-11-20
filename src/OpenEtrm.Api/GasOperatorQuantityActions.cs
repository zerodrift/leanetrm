using System.Collections.Immutable;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Npgsql;
using OpenEtrm.MarketData;
using OpenEtrm.MarketData.Gas;

namespace OpenEtrm.Api;

internal sealed class GasOperatorQuantityActions(PreviewDatabase database):ILifecycleActionHandler
{
    internal const string Handler="gas_operator_quantity_reconciliation";
    internal const string Action="gas.operator.reconcile_quantity";
    internal const string Version="gas_operator_quantity_reconciliation_v1";
    internal const string ClaimSourceHandler="gas_operator_claim_quantity_source";
    internal const string SettlementSourceHandler="gas_operator_settlement_quantity_source";
    internal const string ConsumptionSourceHandler="gas_operator_consumption_quantity_source";
    internal const string SettlementOrder="replay_original_settlement_publication_prefix_v1";
    private const int MaximumMembers=16384;
    private const long MaximumSourceBytes=16777216;
    public string Kind=>Handler;
    public ImmutableArray<string> Permissions=>["ReadMarketData","ReadTrades","ReadGasOwnedHoldings","RecordGasOwnedHoldings"];

    private static JsonElement Inputs(LifecycleActionRequest request)
    {
        LifecycleNative.Require(request,Handler);
        if(request.Automatic||request.Action!=Action)
            throw Error("Select the operator account, exact root claim and saved manual correction policy.");
        try
        {
            var selected=request.Selection.Deserialize<GasOperatorQuantitySelection>(GasOwnedOperations.Json)??throw new JsonException();
            if(selected.Implementation!=Version||selected.RootClaimId!=request.Target.BindingId||selected.Claims.IsDefaultOrEmpty||
                selected.Claims.Length>MaximumMembers||selected.Consumptions.IsDefault||selected.Consumptions.Length>MaximumMembers||
                selected.Cashout is null||selected.Cashout.Ordering!=SettlementOrder||
                string.IsNullOrWhiteSpace(selected.Evidence)||selected.Evidence.Length>2000||
                selected.Claims.Any(claim=>claim is null||claim.ClaimId==Guid.Empty||claim.ExpectedRevision<1||claim.EvidenceId==Guid.Empty||
                    string.IsNullOrWhiteSpace(claim.Quantity)||!Allocation(claim.SourceAllocation)||claim.Settlements.IsDefault||
                    claim.Settlements.Any(step=>step is null||step.SettlementId==Guid.Empty||step.ExpectedRevision<1||
                        string.IsNullOrWhiteSpace(step.Quantity)||step.EvidenceId==Guid.Empty||step.SourceObservationId==Guid.Empty||
                        step.ExpectedReceiptRevision is <1||step.ExpectedConsumptionRevision is <1||step.LotId==Guid.Empty||
                        step.ReservationId==Guid.Empty||!Allocation(step.SourceAllocation)))||
                selected.Consumptions.Any(row=>row is null||row.ConsumptionId==Guid.Empty||row.ExpectedRevision<1||row.LotId==Guid.Empty||
                    row.EvidenceId==Guid.Empty||row.ReservationId==Guid.Empty||string.IsNullOrWhiteSpace(row.Quantity)||!Allocation(row.SourceAllocation)))
                throw Error("Supply complete claim and settlement revisions, exact source/counterpart choices and the explicit cashout adjustment contract.");
            if(selected.Claims.Sum(claim=>(long)claim.Settlements.Length)+selected.Claims.Length+selected.Consumptions.Length>MaximumMembers)
                throw Error("The complete operator correction exceeds its member budget.");
            var steps=selected.Claims.SelectMany(claim=>claim.Settlements).ToImmutableArray();
            if(!selected.Claims.Any(claim=>claim.ClaimId==selected.RootClaimId)||
                selected.Claims.Select(claim=>claim.ClaimId).Distinct().Count()!=selected.Claims.Length||
                steps.Select(step=>step.SettlementId).Distinct().Count()!=steps.Length||
                selected.Consumptions.Select(row=>row.ConsumptionId).Distinct().Count()!=selected.Consumptions.Length)
                throw Error("Include the exact root claim once and each selected claim, settlement and ordinary use once within the bounded correction.");
            return JsonSerializer.SerializeToElement(selected,GasOwnedOperations.Json);
        }
        catch(JsonException error)
        {
            throw new MarketDataException("gas_operator_quantity_input","Supply the closed operator correction, including explicit nullable counterpart and adjustment-date choices.",error);
        }
    }

    private static bool Allocation(string value)=>value is GasSourceAllocation.Preserve or GasSourceAllocation.Withdraw;
    private async Task<JsonElement?> Record(NpgsqlConnection connection,LifecycleActionRequest request,JsonElement body,bool receipt,CancellationToken ct)
    {
        await using var command=ScheduleStore.Command(connection,"SELECT security.reconcile_gas_operator_quantities($1,$2,$3,$4::jsonb,$5,$6,$7,$8,$9)::text",
            database.Manifest.CatalogId,request.OperationId,request.Target.ScopeId,body.GetRawText(),request.Context.AsOf,request.Context.ContextId,
            request.PolicyId,request.PolicyRevision,receipt);
        return await command.ExecuteScalarAsync(ct) is string text?JsonSerializer.Deserialize<JsonElement>(text):null;
    }
    private async Task<ImmutableArray<JsonElement>> Source(NpgsqlConnection connection,LifecycleActionRequest request,JsonElement body,CancellationToken ct)
    {
        var context=await LifecycleNative.Source(connection,database,request.Context,ct);
        await using(var save=ScheduleStore.Command(connection,"SAVEPOINT gas_operator_correction_source"))await save.ExecuteNonQueryAsync(ct);
        try
        {
            await PinnedContext.BindExact(connection,database.Manifest.CatalogId,context,ct);
            await TradingContext.Bind(connection,database.Manifest.CatalogId,context,request.Context.AsOf,ct);
            var sources=ImmutableArray.CreateBuilder<JsonElement>();long bytes=0;
            foreach(var claim in body.GetProperty("claims").EnumerateArray())
            {
                var source=await ReadSource(connection,database,request.Context,claim.Id("claimId"),ct);
                if(source.Id("accountId")!=request.Target.ScopeId)throw Error("Keep every selected claim in the exact operator account.");
                bytes+=Encoding.UTF8.GetByteCount(source.GetRawText());
                if(bytes>MaximumSourceBytes)throw new MarketDataException("gas_owned_scope","The complete operator correction source exceeds its encoded input budget.");
                sources.Add(source);
            }
            return sources.ToImmutable();
        }
        finally
        {
            await using var restore=ScheduleStore.Command(connection,"ROLLBACK TO SAVEPOINT gas_operator_correction_source;RELEASE SAVEPOINT gas_operator_correction_source");
            await restore.ExecuteNonQueryAsync(CancellationToken.None);
        }
    }
    internal static async Task<JsonElement> ReadSource(NpgsqlConnection connection,PreviewDatabase database,LifecycleReadContext context,Guid claim,CancellationToken ct)
    {
        var source=await LifecycleNative.Read(connection,"SELECT security.read_gas_operator_quantity_reconciliation($1,$2,$3,$4)::text",ct,
            database.Manifest.CatalogId,claim,context.ContextId,context.AsOf);
        database.RegisterCommitAdmission(connection,new GasOwnedOperationReadAdmission(database.Manifest.CatalogId,source.Instant("authorityUntil")));
        if(source.Text("implementation")!="gas_operator_quantity_reconciliation_source_v1"||source.Text("scope")!="gas_operator_reconciliation_dependency_facts_v1"||
            source.Id("claimId")!=claim||source.Id("contextId")!=context.ContextId||source.Instant("asOf")!=context.AsOf)
            throw Error("Retain the exact operator claim and its complete certified dependency source.");
        return source;
    }
    public async Task<LifecycleActionReview> Preview(NpgsqlConnection connection,LifecycleActionRequest request,CancellationToken ct)
    {
        var body=Inputs(request);var admission=await GasOwnedOperations.Admission(connection,database,"operator_quantity_reconciliation",body,false,ct);
        var sources=await Source(connection,request,body,ct);
        await using(var save=ScheduleStore.Command(connection,"SAVEPOINT gas_operator_quantity_review"))await save.ExecuteNonQueryAsync(ct);
        JsonElement outcome;bool alreadyRecorded;
        try
        {
            var receipt=await Record(connection,request,body,true,ct);alreadyRecorded=receipt is not null;
            outcome=receipt is {} retained?retained.GetProperty("outcome"):(await Record(connection,request,body,false,ct))!.Value;
            await using var complete=ScheduleStore.Command(connection,"SELECT security.check_operation_seals()");await complete.ExecuteNonQueryAsync(ct);
        }
        finally
        {
            await using var restore=ScheduleStore.Command(connection,"ROLLBACK TO SAVEPOINT gas_operator_quantity_review;RELEASE SAVEPOINT gas_operator_quantity_review");
            await restore.ExecuteNonQueryAsync(CancellationToken.None);
        }
        var account=sources[0].GetProperty("accounts").EnumerateArray().Single(row=>row.Id("id")==request.Target.ScopeId);
        var details=JsonSerializer.SerializeToElement(new{selection=body,quantityUnit=account.Text("quantity_code"),currency=account.Text("currency_code"),sources,outcome,alreadyRecorded,
            willChange=!alreadyRecorded&&outcome.GetProperty("changed").GetBoolean()},GasOwnedOperations.Json);
        var offer=new LifecycleActionOffer(request.Target,Action,"Correct operator quantity or cashout",request.Target.BindingId,request.Context.AsOf,null,true,null,0,0)
            {PolicyId=request.PolicyId,PolicyRevision=request.PolicyRevision,Selection=body};
        await admission.RequireCommit(connection,ct);return new(offer,Hash(request,body),details,database.Manifest.CatalogId,admission.Until);
    }
    public async Task<LifecycleActionWrite> Execute(NpgsqlConnection connection,LifecycleActionRequest request,CancellationToken ct)
    {
        var body=Inputs(request);var admission=await GasOwnedOperations.Admission(connection,database,"operator_quantity_reconciliation",body,false,ct);
        await GasOwnedOperations.AdmitNewCommandInputs(connection,database,request,admission,ct);
        if(await Record(connection,request,body,true,ct) is {} receipt)return new(receipt.GetProperty("outcome"),admission);
        _=await Source(connection,request,body,ct);
        if(Hash(request,body)!=request.PreviewHash)
            throw new MarketDataException("gas_quantity_stale_review","The reviewed operator source, quantities, cashout terms or policy changed. Review the complete correction again.");
        return new((await Record(connection,request,body,false,ct))!.Value,admission);
    }
    private static string Hash(LifecycleActionRequest request,JsonElement body)=>Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(
        JsonSerializer.Serialize(new{implementation=Version,request.OperationId,request.Context,request.Target,request.Action,request.PolicyId,request.PolicyRevision,selection=body},GasOwnedOperations.Json))));

    public async Task<ImmutableArray<LifecycleActionOffer>> Discover(NpgsqlConnection connection,LifecycleReadContext context,LifecycleTarget? target,LifecycleDiscoveryAccess access,CancellationToken ct)
    {
        if(!access.Allows("ReadGasOwnedHoldings")){if(target is not null)throw new PreviewDeniedException();return [];}
        await using(var capture=ScheduleStore.Command(connection,"SELECT security.gas_futures_delivery_discovery_authority($1)",database.Manifest.CatalogId))
            database.RegisterCommitAdmission(connection,new GasOwnedOperationReadAdmission(database.Manifest.CatalogId,new DateTimeOffset((DateTime)(await capture.ExecuteScalarAsync(ct))!)));
        var state=await GasOwnedOperations.Read(connection,database,context,target?.ScopeId,ct);
        var accounts=state.GetProperty("accounts").EnumerateArray().ToImmutableDictionary(row=>row.Id("id"));
        var evidence=state.GetProperty("evidence").EnumerateArray().ToImmutableDictionary(row=>row.Id("id"));
        return [..state.GetProperty("positions").EnumerateArray().Where(row=>row.Text("kind")=="operator_claim"&&
            (target is null||target.ScopeId==row.Id("account_id")&&target.BindingId==row.Id("id"))).Select(claim=>
        {
            var account=accounts[claim.Id("account_id")];
            var policies=state.GetProperty("policies").EnumerateArray().Where(row=>row.Id("account_id")==account.Id("id")&&row.Text("action")==Action)
                .OrderByDescending(row=>row.GetProperty("revision").GetInt32()).ToImmutableArray();
            var policy=policies.FirstOrDefault();var reason=policies.IsEmpty?"Select a saved manual operator correction policy for this account.":
                context.AsOf<policy.Instant("from_at")||context.AsOf>=policy.Instant("until_at")?"The operator correction policy is outside its saved action window.":null;
            return new LifecycleActionOffer(new(Handler,account.Id("id"),claim.Id("id")),Action,"Correct operator quantity or cashout",claim.Id("id"),context.AsOf,null,reason is null,reason,0,0){
                Subject=new(account.Text("code")+" / "+evidence[claim.Id("evidence_id")].Text("source_key"),"Operator gas balance",account.Id("book_id"),account.Text("book_name")),
                PolicyId=policies.IsEmpty?null:policy.Id("policy_id"),PolicyRevision=policies.IsEmpty?null:policy.GetProperty("policy_revision").GetInt32(),
                RequiredInputs=["claims","consumptions","cashout","evidence"]};
        })];
    }
    private static MarketDataException Error(string message)=>new("gas_operator_quantity_input",message);
}
