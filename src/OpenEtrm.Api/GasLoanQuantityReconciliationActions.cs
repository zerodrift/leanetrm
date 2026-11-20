using System.Collections.Immutable;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Npgsql;
using OpenEtrm.MarketData;
using OpenEtrm.MarketData.Gas;

namespace OpenEtrm.Api;

internal sealed class GasLoanQuantityReconciliationActions(PreviewDatabase database):ILifecycleActionHandler
{
    internal const string Handler="gas_loan_quantity_reconciliation";
    internal const string ConsumptionSourceHandler="gas_loan_consumption_quantity_source";
    internal const string Action="gas.loan.reconcile_quantity";
    internal const string Version="gas_loan_quantity_reconciliation_v1";
    internal const string RetainFees="retain_fixed_fee_memberships_v1";
    public string Kind=>Handler;
    public ImmutableArray<string> Permissions=>["ReadMarketData","ReadTrades","ReadGasOwnedHoldings","RecordGasOwnedHoldings"];

    private static JsonElement Inputs(LifecycleActionRequest request)
    {
        LifecycleNative.Require(request,Handler);
        if(request.Automatic||request.Action!=Action)throw Error("Select the exact commodity loan and its saved manual correction policy.");
        try
        {
            var selected=request.Selection.Deserialize<GasLoanQuantityReconciliationSelection>(GasOwnedOperations.Json)??throw new JsonException();
            if(selected.LoanId!=request.Target.BindingId||selected.ExpectedRevision<1||selected.StockExpectedRevision<1||selected.ReturnExpectedRevision<1||
                selected.TitleEvidenceId==Guid.Empty||selected.AgreementEvidenceId==Guid.Empty||string.IsNullOrWhiteSpace(selected.Quantity)||
                selected.SourceAllocation is not(GasSourceAllocation.Preserve or GasSourceAllocation.Withdraw)||
                selected.FeeDisposition!=RetainFees||selected.Consumptions.IsDefault||selected.Consumptions.Length>16384||
                string.IsNullOrWhiteSpace(selected.Evidence)||selected.Evidence.Length>2000||
                selected.Consumptions.Any(row=>row is null||row.ConsumptionId==Guid.Empty||row.ExpectedRevision<1||row.LotId==Guid.Empty||row.EvidenceId==Guid.Empty||
                    row.ReservationId==Guid.Empty||string.IsNullOrWhiteSpace(row.Quantity)||row.SourceAllocation is not(GasSourceAllocation.Preserve or GasSourceAllocation.Withdraw))||
                selected.Consumptions.Select(row=>row.ConsumptionId).Distinct().Count()!=selected.Consumptions.Length)
                throw Error("Name the exact loan, stock and return revisions, both observed source heads and every affected resource use with an explicit fee law.");
            return JsonSerializer.SerializeToElement(selected,GasOwnedOperations.Json);
        }
        catch(JsonException error){throw new MarketDataException("gas_loan_quantity_input","Supply the closed loan and resource corrections with explicit source allocation and fee disposition.",error);}
    }
    private async Task<JsonElement?> Record(NpgsqlConnection connection,LifecycleActionRequest request,JsonElement body,bool receipt,CancellationToken ct)
    {
        await using var command=ScheduleStore.Command(connection,"SELECT security.reconcile_gas_loan_quantities($1,$2,$3,$4::jsonb,$5,$6,$7,$8,$9)::text",
            database.Manifest.CatalogId,request.OperationId,request.Target.ScopeId,body.GetRawText(),request.Context.AsOf,request.Context.ContextId,request.PolicyId,request.PolicyRevision,receipt);
        return await command.ExecuteScalarAsync(ct) is string text?JsonSerializer.Deserialize<JsonElement>(text):null;
    }
    private async Task<JsonElement> Source(NpgsqlConnection connection,LifecycleActionRequest request,ScheduleContext context,CancellationToken ct)
    {
        await using(var save=ScheduleStore.Command(connection,"SAVEPOINT gas_loan_correction_source"))await save.ExecuteNonQueryAsync(ct);
        try
        {
            await PinnedContext.BindExact(connection,database.Manifest.CatalogId,context,ct);
            await TradingContext.Bind(connection,database.Manifest.CatalogId,context,request.Context.AsOf,ct);
            var source=await ReadSource(connection,database,request.Context,request.Target.BindingId,ct);
            if(source.Id("accountId")!=request.Target.ScopeId)
                throw Error("Retain this exact account, certified source and complete native correction dependency facts.");
            return source;
        }
        finally
        {
            await using var restore=ScheduleStore.Command(connection,"ROLLBACK TO SAVEPOINT gas_loan_correction_source;RELEASE SAVEPOINT gas_loan_correction_source");
            await restore.ExecuteNonQueryAsync(CancellationToken.None);
        }
    }
    public async Task<LifecycleActionReview> Preview(NpgsqlConnection connection,LifecycleActionRequest request,CancellationToken ct)
    {
        var body=Inputs(request);var admission=await GasOwnedOperations.Admission(connection,database,"loan_quantity_reconciliation",body,false,ct);
        var context=await LifecycleNative.Source(connection,database,request.Context,ct);
        var source=await Source(connection,request,context,ct);
        await using(var save=ScheduleStore.Command(connection,"SAVEPOINT gas_loan_correction_review"))await save.ExecuteNonQueryAsync(ct);
        JsonElement outcome;bool alreadyRecorded;
        try
        {
            var receipt=await Record(connection,request,body,true,ct);alreadyRecorded=receipt is not null;
            outcome=receipt is {} retained?retained.GetProperty("outcome"):(await Record(connection,request,body,false,ct))!.Value;
            await using var complete=ScheduleStore.Command(connection,"SELECT security.check_operation_seals()");await complete.ExecuteNonQueryAsync(ct);
        }
        finally
        {
            await using var restore=ScheduleStore.Command(connection,"ROLLBACK TO SAVEPOINT gas_loan_correction_review;RELEASE SAVEPOINT gas_loan_correction_review");
            await restore.ExecuteNonQueryAsync(CancellationToken.None);
        }
        var consumptions=source.GetProperty("consumptions").EnumerateArray().ToImmutableDictionary(row=>row.Id("id"));
        var repaymentFacts=source.GetProperty("repayments").EnumerateArray().Select(row=>
        {
            var use=consumptions[row.Id("consumption_id")];
            return new{repaymentId=row.Id("id"),loanId=row.Id("loan_id"),consumptionId=use.Id("id"),originalQuantity=use.Text("originalQuantity"),
                currentQuantity=use.Text("quantity"),currentRevision=use.GetProperty("financialRevision").GetInt32(),lotId=use.Id("lot_id"),effectiveAt=use.Instant("effective_at")};
        }).ToImmutableArray();
        var details=JsonSerializer.SerializeToElement(new{selection=body,quantityUnit=source.Text("quantityUnit"),currency=source.Text("currency"),repaymentFacts,
            loan=source.GetProperty("loan"),stock=source.GetProperty("stock"),returnObligation=source.GetProperty("return"),
            funding=source.GetProperty("funding"),consumptions=source.GetProperty("consumptions"),repayments=source.GetProperty("repayments"),
            fees=source.GetProperty("fees"),payments=source.GetProperty("payments"),
            sourceAllocations=Allocations(outcome),outcome,alreadyRecorded,willChange=!alreadyRecorded&&outcome.GetProperty("changed").GetBoolean()},GasOwnedOperations.Json);
        var offer=new LifecycleActionOffer(request.Target,Action,"Correct commodity loan quantity",request.Target.BindingId,request.Context.AsOf,null,true,null,0,0)
            {PolicyId=request.PolicyId,PolicyRevision=request.PolicyRevision,Selection=body};
        await admission.RequireCommit(connection,ct);return new(offer,Hash(request,body),details,database.Manifest.CatalogId,admission.Until);
    }
    public async Task<LifecycleActionWrite> Execute(NpgsqlConnection connection,LifecycleActionRequest request,CancellationToken ct)
    {
        var body=Inputs(request);var admission=await GasOwnedOperations.Admission(connection,database,"loan_quantity_reconciliation",body,false,ct);
        if(await Record(connection,request,body,true,ct) is {} receipt)return new(receipt.GetProperty("outcome"),admission);
        var context=await LifecycleNative.Source(connection,database,request.Context,ct);
        _=await Source(connection,request,context,ct);
        if(Hash(request,body)!=request.PreviewHash)throw new MarketDataException("gas_quantity_stale_review","The reviewed source, exact quantities or policy changed. Review the complete loan correction again.");
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
    internal static async Task<JsonElement> ReadSource(NpgsqlConnection connection,PreviewDatabase database,LifecycleReadContext context,Guid loan,CancellationToken ct)
    {
        var source=await LifecycleNative.Read(connection,"SELECT security.read_gas_loan_reconciliation($1,$2,$3,$4)::text",ct,
            database.Manifest.CatalogId,loan,context.ContextId,context.AsOf);
        database.RegisterCommitAdmission(connection,new GasOwnedOperationReadAdmission(database.Manifest.CatalogId,source.Instant("authorityUntil")));
        if(source.Text("implementation")!="gas_loan_quantity_reconciliation_source_v1"||source.Text("scope")!="gas_loan_reconciliation_dependency_facts_v1"||
            source.Id("loanId")!=loan||source.Id("contextId")!=context.ContextId||source.Instant("asOf")!=context.AsOf)
            throw Error("Retain the exact loan, certified source and native correction dependency facts.");
        return source;
    }
    public async Task<ImmutableArray<LifecycleActionOffer>> Discover(NpgsqlConnection connection,LifecycleReadContext context,LifecycleTarget? target,LifecycleDiscoveryAccess access,CancellationToken ct)
    {
        if(!access.Allows("ReadGasOwnedHoldings")){if(target is not null)throw new PreviewDeniedException();return [];}
        await using(var capture=ScheduleStore.Command(connection,"SELECT security.gas_futures_delivery_discovery_authority($1)",database.Manifest.CatalogId))
            database.RegisterCommitAdmission(connection,new GasOwnedOperationReadAdmission(database.Manifest.CatalogId,new DateTimeOffset((DateTime)(await capture.ExecuteScalarAsync(ct))!)));
        var state=await GasOwnedOperations.Read(connection,database,context,target?.ScopeId,ct);
        var accounts=state.GetProperty("accounts").EnumerateArray().ToImmutableDictionary(row=>row.Id("id"));
        var positions=state.GetProperty("positions").EnumerateArray().ToImmutableDictionary(row=>row.Id("id"));
        var evidence=state.GetProperty("evidence").EnumerateArray().ToImmutableDictionary(row=>row.Id("id"));
        return [..state.GetProperty("loans").EnumerateArray().Where(loan=>target is null||target.ScopeId==loan.Id("account_id")&&target.BindingId==loan.Id("id")).Select(loan=>
        {
            var account=accounts[loan.Id("account_id")];
            var sourceKey=evidence[positions[loan.Id("return_position_id")].Id("evidence_id")].Text("source_key");
            var policies=state.GetProperty("policies").EnumerateArray().Where(policy=>policy.Id("account_id")==account.Id("id")&&policy.Text("action")==Action)
                .OrderByDescending(policy=>policy.GetProperty("revision").GetInt32()).ToImmutableArray();
            var policy=policies.FirstOrDefault();var reason=policies.IsEmpty?"Select a saved manual loan correction policy for this account.":
                context.AsOf<policy.Instant("from_at")||context.AsOf>=policy.Instant("until_at")?"The correction policy is outside its saved action window.":null;
            return new LifecycleActionOffer(new(Handler,account.Id("id"),loan.Id("id")),Action,"Correct commodity loan quantity",loan.Id("id"),context.AsOf,null,reason is null,reason,0,0){
                Subject=new(account.Text("code")+" / "+sourceKey,"Title-transfer gas loan",account.Id("book_id"),account.Text("book_name")),
                PolicyId=policies.IsEmpty?null:policy.Id("policy_id"),PolicyRevision=policies.IsEmpty?null:policy.GetProperty("policy_revision").GetInt32(),
                RequiredInputs=["quantity","titleEvidenceId","agreementEvidenceId","sourceAllocation","feeDisposition","consumptions","evidence"]};
        })];
    }
    private static MarketDataException Error(string message)=>new("gas_loan_quantity_input",message);
}
