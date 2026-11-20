using System.Collections.Immutable;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Npgsql;
using OpenEtrm.MarketData;

namespace OpenEtrm.Api;

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record GasLoanFeeSelection(
    [property:JsonRequired]Guid LoanId,[property:JsonRequired]int ExpectedRevision,
    [property:JsonRequired]string Amount,[property:JsonRequired]DateTimeOffset? DueAt,
    [property:JsonRequired]DateTimeOffset EffectiveAt,[property:JsonRequired]string Implementation,
    [property:JsonRequired]string Evidence);

internal sealed class GasLoanFeeActions(PreviewDatabase database):ILifecycleActionHandler
{
    internal const string Handler="gas_loan_fee_amendment";
    internal const string Action="gas.loan.amend_fee";
    internal const string Version="gross_timed_fixed_fee_adjustments_v1";
    public string Kind=>Handler;
    public ImmutableArray<string> Permissions=>["ReadMarketData","ReadTrades","ReadGasOwnedHoldings","RecordGasOwnedHoldings"];
    private static JsonElement Inputs(LifecycleActionRequest request)
    {
        LifecycleNative.Require(request,Handler);
        if(request.Automatic||request.Action!=Action)throw Error("Select this loan and its saved manual fee-amendment policy.");
        try
        {
            var selected=request.Selection.Deserialize<GasLoanFeeSelection>(GasOwnedOperations.Json)??throw new JsonException();
            if(selected.LoanId!=request.Target.BindingId||selected.ExpectedRevision<1||string.IsNullOrWhiteSpace(selected.Amount)||
                selected.Implementation!=Version||string.IsNullOrWhiteSpace(selected.Evidence)||selected.Evidence.Length>2000)
                throw Error("Specify the exact loan fee revision, revised amount and due date, effective time, adjustment rule and evidence.");
            return JsonSerializer.SerializeToElement(selected,GasOwnedOperations.Json);
        }
        catch(JsonException error){throw new MarketDataException("gas_fee_terms_input","Supply the complete fee amendment with an explicit nullable due date.",error);}
    }
    private async Task<JsonElement?> Record(NpgsqlConnection connection,LifecycleActionRequest request,JsonElement body,bool receipt,CancellationToken ct)
    {
        await using var command=ScheduleStore.Command(connection,"SELECT security.amend_gas_loan_fee_terms($1,$2,$3,$4::jsonb,$5,$6,$7,$8,$9)::text",
            database.Manifest.CatalogId,request.OperationId,request.Target.ScopeId,body.GetRawText(),request.Context.AsOf,request.Context.ContextId,request.PolicyId,request.PolicyRevision,receipt);
        return await command.ExecuteScalarAsync(ct) is string text?JsonSerializer.Deserialize<JsonElement>(text):null;
    }
    private async Task<JsonElement> Source(NpgsqlConnection connection,LifecycleActionRequest request,CancellationToken ct)
    {
        var context=await LifecycleNative.Source(connection,database,request.Context,ct);
        await using(var save=ScheduleStore.Command(connection,"SAVEPOINT gas_loan_fee_source"))await save.ExecuteNonQueryAsync(ct);
        try
        {
            await PinnedContext.BindExact(connection,database.Manifest.CatalogId,context,ct);
            await TradingContext.Bind(connection,database.Manifest.CatalogId,context,request.Context.AsOf,ct);
            var source=await ReadSource(connection,database,request.Context,request.Target.BindingId,ct);
            if(source.Id("accountId")!=request.Target.ScopeId)throw Error("Retain this loan's exact account and certified source.");
            return source;
        }
        finally
        {
            await using var restore=ScheduleStore.Command(connection,"ROLLBACK TO SAVEPOINT gas_loan_fee_source;RELEASE SAVEPOINT gas_loan_fee_source");
            await restore.ExecuteNonQueryAsync(CancellationToken.None);
        }
    }
    public async Task<LifecycleActionReview> Preview(NpgsqlConnection connection,LifecycleActionRequest request,CancellationToken ct)
    {
        var body=Inputs(request);var admission=await GasOwnedOperations.Admission(connection,database,"loan_fee_amendment",body,false,ct);
        var source=await Source(connection,request,ct);
        await using(var save=ScheduleStore.Command(connection,"SAVEPOINT gas_loan_fee_review"))await save.ExecuteNonQueryAsync(ct);
        JsonElement outcome;bool alreadyRecorded;
        try
        {
            var receipt=await Record(connection,request,body,true,ct);alreadyRecorded=receipt is not null;
            outcome=receipt is {} retained?retained.GetProperty("outcome"):(await Record(connection,request,body,false,ct))!.Value;
            await using var complete=ScheduleStore.Command(connection,"SELECT security.check_operation_seals()");await complete.ExecuteNonQueryAsync(ct);
        }
        finally
        {
            await using var restore=ScheduleStore.Command(connection,"ROLLBACK TO SAVEPOINT gas_loan_fee_review;RELEASE SAVEPOINT gas_loan_fee_review");
            await restore.ExecuteNonQueryAsync(CancellationToken.None);
        }
        var root=source.GetProperty("rootFee");
        var details=JsonSerializer.SerializeToElement(new{selection=body,terms=source.GetProperty("terms"),rootFee=root.ValueKind==JsonValueKind.Null?(JsonElement?)null:CashReview(root),
            cash=source.GetProperty("cash").EnumerateArray().Select(CashReview).ToImmutableArray(),
            payments=source.GetProperty("payments").EnumerateArray().Select(row=>new{id=row.Id("id"),feeId=row.Id("fee_id"),businessKey=row.Text("business_key"),
                revision=row.GetProperty("revision").GetInt32(),amount=row.Text("amount"),paidAt=row.GetProperty("paid_at"),evidence=row.Text("evidence"),
                publicationOrder=row.GetProperty("publication_order").GetInt32()}).ToImmutableArray(),currentPaid=source.GetProperty("currentPaid"),outcome,
            alreadyRecorded,willChange=!alreadyRecorded&&outcome.GetProperty("changed").GetBoolean()},GasOwnedOperations.Json);
        var offer=new LifecycleActionOffer(request.Target,Action,"Amend loan fee",request.Target.BindingId,request.Context.AsOf,null,true,null,0,0)
            {PolicyId=request.PolicyId,PolicyRevision=request.PolicyRevision,Selection=body};
        await admission.RequireCommit(connection,ct);return new(offer,Hash(request,body),details,database.Manifest.CatalogId,admission.Until);
    }
    public async Task<LifecycleActionWrite> Execute(NpgsqlConnection connection,LifecycleActionRequest request,CancellationToken ct)
    {
        var body=Inputs(request);var admission=await GasOwnedOperations.Admission(connection,database,"loan_fee_amendment",body,false,ct);
        if(await Record(connection,request,body,true,ct) is {} receipt)return new(receipt.GetProperty("outcome"),admission);
        _=await Source(connection,request,ct);
        if(Hash(request,body)!=request.PreviewHash)throw new MarketDataException("gas_fee_stale_review","The reviewed loan, fee terms, source or policy changed. Review the amendment again.");
        return new((await Record(connection,request,body,false,ct))!.Value,admission);
    }
    private static string Hash(LifecycleActionRequest request,JsonElement body)=>Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(
        JsonSerializer.Serialize(new{implementation=Version,request.OperationId,request.Context,request.Target,request.Action,request.PolicyId,request.PolicyRevision,selection=body},GasOwnedOperations.Json))));
    private static JsonElement CashReview(JsonElement row)=>JsonSerializer.SerializeToElement(new{id=row.Id("id"),amount=row.Text("amount"),currency=row.Text("currency_code"),
        dueAt=row.GetProperty("due_at"),effectiveAt=row.GetProperty("effective_at"),publicationOrder=row.GetProperty("publication_order").GetInt32()},GasOwnedOperations.Json);
    internal static async Task<JsonElement> ReadSource(NpgsqlConnection connection,PreviewDatabase database,LifecycleReadContext context,Guid loan,CancellationToken ct)
    {
        var source=await LifecycleNative.Read(connection,"SELECT security.read_gas_loan_fee_terms($1,$2,$3,$4)::text",ct,database.Manifest.CatalogId,loan,context.ContextId,context.AsOf);
        database.RegisterCommitAdmission(connection,new GasOwnedOperationReadAdmission(database.Manifest.CatalogId,source.Instant("authorityUntil")));
        if(source.Text("implementation")!="gas_fixed_loan_fee_terms_source_v1"||source.Id("loanId")!=loan||source.Id("contextId")!=context.ContextId||source.Instant("asOf")!=context.AsOf)
            throw Error("Retain the exact loan fee and its certified source.");
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
            var reference=evidence[positions[loan.Id("return_position_id")].Id("evidence_id")].Text("source_key");
            var policies=state.GetProperty("policies").EnumerateArray().Where(p=>p.Id("account_id")==account.Id("id")&&p.Text("action")==Action)
                .OrderByDescending(p=>p.GetProperty("revision").GetInt32()).ToImmutableArray();
            var policy=policies.FirstOrDefault();var reason=policies.IsEmpty?"Select a saved manual fee-amendment policy for this account.":
                context.AsOf<policy.Instant("from_at")||context.AsOf>=policy.Instant("until_at")?"The fee-amendment policy is outside its saved action window.":null;
            return new LifecycleActionOffer(new(Handler,account.Id("id"),loan.Id("id")),Action,"Amend loan fee",loan.Id("id"),context.AsOf,null,reason is null,reason,0,0){
                Subject=new(account.Text("code")+" / "+reference,"Title-transfer loan fee",account.Id("book_id"),account.Text("book_name")),
                PolicyId=policies.IsEmpty?null:policy.Id("policy_id"),PolicyRevision=policies.IsEmpty?null:policy.GetProperty("policy_revision").GetInt32(),
                RequiredInputs=["amount","dueAt","effectiveAt","implementation","evidence"]};
        })];
    }
    private static MarketDataException Error(string message)=>new("gas_fee_terms_input",message);
}
