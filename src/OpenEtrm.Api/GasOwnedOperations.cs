using System.Collections.Immutable;
using System.Text.Json;
using System.Text.Json.Serialization;
using Npgsql;
using OpenEtrm.MarketData;

namespace OpenEtrm.Api;

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record GasOwnedConfigurationRequest(Guid OperationId,LifecycleReadContext Context,Guid AccountId,string Action,JsonElement Selection);
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record GasOwnedReadRequest(LifecycleReadContext Context,[property:JsonRequired]Guid? AccountId);
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record GasOwnedAccountSelection(string Code,string Kind,Guid BookId,Guid LegalEntityId,int LegalEntityRevision,
    [property:JsonRequired]Guid? CounterpartyId,[property:JsonRequired]int? CounterpartyRevision,Guid ReferenceCatalogId,Guid ReferenceRevisionId,
    string Quantity,string Location,Guid DefinitionRevisionId,string MarkCurve,Guid MarkContractId,string Currency,string TitlePolicy,string LossPolicy,
    [property:JsonRequired]DateTimeOffset EffectiveAt);
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record GasOwnedEvidenceSelection(string SourceKey,string Kind,string Quantity,[property:JsonRequired]DateTimeOffset OccurredAt,
    string Evidence,[property:JsonRequired]int ExpectedRevision);
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record GasOwnedPolicySelection(string Action,Guid PolicyId,int PolicyRevision,[property:JsonRequired]DateTimeOffset EffectiveAt);
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record GasOwnedTitleSelection(Guid EvidenceId,string Quantity,[property:JsonRequired]DateTimeOffset TitleAt,
    [property:JsonRequired]DateTimeOffset AvailableAt,string OriginPolicy,string Carrying);
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record GasOwnedCashoutSelection(string Source,string Role,[property:JsonRequired]DateTimeOffset ObservedAt,
    [property:JsonRequired]DateTimeOffset CashDueAt,string Rounding);
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record GasOwnedClaimSelection(Guid EvidenceId,string Quantity,[property:JsonRequired]DateTimeOffset EffectiveAt,
    [property:JsonRequired]DateTimeOffset DueAt,string Carrying,string SettlementMode,[property:JsonRequired]GasOwnedCashoutSelection? Cashout);
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record GasOwnedBorrowSelection(Guid AgreementId,Guid EvidenceId,string Quantity,[property:JsonRequired]DateTimeOffset TitleAt,
    [property:JsonRequired]DateTimeOffset AvailableAt,[property:JsonRequired]DateTimeOffset ReturnAt,string ReturnMarkCurve,Guid ReturnMarkContractId,
    string Carrying,string FeeAmount,[property:JsonRequired]DateTimeOffset? FeeDueAt,Guid LenderId,int LenderRevision);
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record GasOwnedReservationSelection(Guid LotId,string BusinessKey,string Quantity,[property:JsonRequired]DateTimeOffset EffectiveAt);
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record GasOwnedReleaseSelection(Guid ReservationId,[property:JsonRequired]DateTimeOffset EffectiveAt);
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record GasOwnedDispatchSelection(Guid LotId,Guid EvidenceId,string Quantity,[property:JsonRequired]Guid? ReservationId,
    [property:JsonRequired]DateTimeOffset EffectiveAt);
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record GasOwnedRepaymentSelection(Guid LoanId,Guid LotId,Guid EvidenceId,string Quantity,[property:JsonRequired]Guid? ReservationId,
    [property:JsonRequired]DateTimeOffset EffectiveAt);
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record GasOwnedSettlementSelection(Guid PositionId,string Quantity,[property:JsonRequired]Guid? OwnedAccountId,
    [property:JsonRequired]Guid? LotId,[property:JsonRequired]Guid? ReservationId,Guid EvidenceId,[property:JsonRequired]DateTimeOffset EffectiveAt,
    [property:JsonRequired]DateTimeOffset? AvailableAt,[property:JsonRequired]Guid? SourceObservationId);
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record GasOwnedPaymentSelection(Guid FeeId,string BusinessKey,[property:JsonRequired]int ExpectedRevision,
    [property:JsonRequired]DateTimeOffset PaidAt,string Amount,string Evidence);
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record GasOwnedMarkSelection(Guid PositionId,Guid DefinitionRevisionId,string MarkCurve,Guid MarkContractId,
    [property:JsonRequired]int ExpectedRevision,[property:JsonRequired]DateTimeOffset EffectiveAt);
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record GasOwnedProjectionSupplySelection([property:JsonRequired]Guid? LotId,[property:JsonRequired]Guid? IncomingPositionId,
    [property:JsonRequired]Guid? ReservationId,string Quantity);
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record GasOwnedProjectionPerformanceSelection(Guid PositionId,string Quantity,[property:JsonRequired]Guid? DestinationAccountId,
    [property:JsonRequired]Guid? InventoryDefinitionRevisionId,[property:JsonRequired]string? InventoryCurve,[property:JsonRequired]Guid? InventoryContractId,
    [property:JsonRequired]string? Availability,ImmutableArray<GasOwnedProjectionSupplySelection> Supply);
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record GasOwnedProjectionProfileSelection(Guid ProfileId,[property:JsonRequired]int ExpectedRevision,string Implementation,
    string QuantityDisposition,[property:JsonRequired]DateTimeOffset EffectiveAt,ImmutableArray<GasOwnedProjectionPerformanceSelection> Performances);

internal sealed record GasOwnedWriteAdmission(Guid Catalog,DateTimeOffset Until,bool Configuring,bool RequiresJobs):ICommitAdmission
{
    public async Task RequireCommit(NpgsqlConnection connection,CancellationToken ct)
    {
        await using var command=ScheduleStore.Command(connection,"SELECT security.require_gas_owned_commit($1,$2,$3,$4)",Catalog,Until,Configuring,RequiresJobs);
        await command.ExecuteNonQueryAsync(ct);
    }
}
internal sealed record GasOwnedOperationReadAdmission(Guid Catalog,DateTimeOffset Until):ICommitAdmission
{
    public async Task RequireCommit(NpgsqlConnection connection,CancellationToken ct)
    {
        await using var command=ScheduleStore.Command(connection,"SELECT security.require_gas_owned_preview($1,$2)",Catalog,Until);
        await command.ExecuteNonQueryAsync(ct);
    }
}
internal static class GasOwnedOperations
{
    internal const string Handler="gas_owned";
    private sealed class InstantJson:JsonConverter<DateTimeOffset>
    {
        public override DateTimeOffset Read(ref Utf8JsonReader reader,Type type,JsonSerializerOptions options)
        {
            var at=reader.GetDateTimeOffset();_=LifecycleNative.Instant(at);return at;
        }
        public override void Write(Utf8JsonWriter writer,DateTimeOffset value,JsonSerializerOptions options)=>writer.WriteStringValue(LifecycleNative.Instant(value));
    }
    internal static readonly JsonSerializerOptions Json=Options();
    private static JsonSerializerOptions Options()
    {
        var options=new JsonSerializerOptions(LifecycleNative.Json){RespectNullableAnnotations=true,RespectRequiredConstructorParameters=true};
        options.Converters.Add(new InstantJson());return options;
    }
    internal static readonly ImmutableDictionary<string,string> Commands=new Dictionary<string,string>{
        ["gas.owned.receive_title"]="receive_title",["gas.owned.reserve"]="reserve",["gas.owned.release"]="release",["gas.owned.dispatch"]="dispatch",
        ["gas.owned.loss"]="loss",["gas.operator.record"]="operator_claim",["gas.operator.settle"]="operator_settle",["gas.loan.borrow"]="borrow",
        ["gas.loan.repay"]="repay",["gas.cash.record_payment"]="fee_payment"}.ToImmutableDictionary(StringComparer.Ordinal);
    internal static JsonElement Selection(string command,JsonElement selection)
    {
        if(selection.ValueKind!=JsonValueKind.Object)throw Error("Select the explicit typed gas operation inputs.");
        try
        {
            object value=command switch{
                "account"=>Read<GasOwnedAccountSelection>(),"evidence"=>Read<GasOwnedEvidenceSelection>(),"bind_policy"=>Read<GasOwnedPolicySelection>(),
                "receive_title"=>Read<GasOwnedTitleSelection>(),"operator_claim"=>Read<GasOwnedClaimSelection>(),"borrow"=>Read<GasOwnedBorrowSelection>(),
                "reserve"=>Read<GasOwnedReservationSelection>(),"release"=>Read<GasOwnedReleaseSelection>(),"dispatch" or "loss"=>Read<GasOwnedDispatchSelection>(),
                "repay"=>Read<GasOwnedRepaymentSelection>(),"operator_settle"=>Read<GasOwnedSettlementSelection>(),"fee_payment"=>Read<GasOwnedPaymentSelection>(),
                "mark_binding"=>Read<GasOwnedMarkSelection>(),"projection_profile"=>Read<GasOwnedProjectionProfileSelection>(),
                _=>throw Error("Select a registered native gas operation.")};
            return JsonSerializer.SerializeToElement(value,value.GetType(),Json);
        }
        catch(JsonException error){throw new MarketDataException("gas_owned_input","Supply only the selected gas operation's typed fields, explicit null choices and UTC instants.",error);}
        T Read<T>() where T:class=>selection.Deserialize<T>(Json)??throw new JsonException();
    }
    internal static async Task<GasOwnedWriteAdmission> Admission(NpgsqlConnection connection,PreviewDatabase database,string command,JsonElement body,bool automatic,CancellationToken ct)
    {
        var row=await LifecycleNative.Read(connection,"SELECT security.gas_owned_admission($1,$2,$3::jsonb,$4)::text",ct,database.Manifest.CatalogId,command,body.GetRawText(),automatic);
        var admission=new GasOwnedWriteAdmission(database.Manifest.CatalogId,row.Instant("until"),row.GetProperty("configuring").GetBoolean(),row.GetProperty("requiresJobs").GetBoolean());
        database.RegisterCommitAdmission(connection,admission);return admission;
    }
    internal static async Task<JsonElement?> Receipt(NpgsqlConnection connection,PreviewDatabase database,Guid operation,LifecycleReadContext context,Guid account,string command,
        JsonElement body,Guid? policy,int? policyRevision,bool automatic,CancellationToken ct)
    {
        await using var call=Native(connection,database,"gas_owned_command_receipt",operation,context,account,command,body,policy,policyRevision,automatic);
        return await call.ExecuteScalarAsync(ct) is string result?JsonSerializer.Deserialize<JsonElement>(result):null;
    }
    internal static async Task<JsonElement> Record(NpgsqlConnection connection,PreviewDatabase database,Guid operation,LifecycleReadContext context,Guid account,string command,
        JsonElement body,Guid? policy,int? policyRevision,bool automatic,CancellationToken ct)
    {
        await using var call=Native(connection,database,"record_gas_owned",operation,context,account,command,body,policy,policyRevision,automatic);
        return JsonSerializer.Deserialize<JsonElement>((string)(await call.ExecuteScalarAsync(ct))!);
    }
    private static NpgsqlCommand Native(NpgsqlConnection connection,PreviewDatabase database,string function,Guid operation,LifecycleReadContext context,Guid account,string command,
        JsonElement body,Guid? policy,int? policyRevision,bool automatic)=>ScheduleStore.Command(connection,
        $"SELECT security.{function}($1,$2,$3,$4,$5::jsonb,$6,$7,$8::uuid,$9::integer,$10)::text",database.Manifest.CatalogId,operation,command,account,body.GetRawText(),
        context.AsOf,context.ContextId,policy is {} id?id:DBNull.Value,policyRevision is {} version?version:DBNull.Value,automatic);
    internal static async Task<JsonElement> Read(NpgsqlConnection connection,PreviewDatabase database,LifecycleReadContext context,Guid? account,CancellationToken ct)
    {
        var row=await LifecycleNative.Read(connection,"SELECT security.read_gas_owned_operations($1,$2,$3,$4::uuid)::text",ct,database.Manifest.CatalogId,context.ContextId,context.AsOf,account is {} id?id:DBNull.Value);
        if(row.Id("contextId")!=context.ContextId||row.Instant("asOf")!=context.AsOf||DateWorkspace.Date(row.Text("effectiveOn"))!=context.BusinessDate)
            throw new MarketDataException("gas_owned_context","Retain the exact gas source business date and instant.");
        database.RegisterCommitAdmission(connection,new GasOwnedOperationReadAdmission(database.Manifest.CatalogId,row.Instant("authorityUntil")));
        return row;
    }
    internal static async Task<JsonElement> ReadScoped(NpgsqlConnection connection,PreviewDatabase database,GasOwnedReadRequest request,CancellationToken ct)
    {
        var source=await LifecycleNative.Source(connection,database,request.Context,ct);
        await using(var save=ScheduleStore.Command(connection,"SAVEPOINT gas_owned_read"))await save.ExecuteNonQueryAsync(ct);
        try
        {
            await PinnedContext.BindExact(connection,database.Manifest.CatalogId,source,ct);
            await TradingContext.Bind(connection,database.Manifest.CatalogId,source,request.Context.AsOf,ct);
            return await Read(connection,database,request.Context,request.AccountId,ct);
        }
        finally
        {
            await using var restore=ScheduleStore.Command(connection,"ROLLBACK TO SAVEPOINT gas_owned_read;RELEASE SAVEPOINT gas_owned_read");
            await restore.ExecuteNonQueryAsync(CancellationToken.None);
        }
    }
    internal static async Task<LifecycleActionWrite> Configure(NpgsqlConnection connection,PreviewDatabase database,GasOwnedConfigurationRequest request,CancellationToken ct)
    {
        if(request.OperationId==Guid.Empty||request.AccountId==Guid.Empty||request.Context is null||request.Context.ContextId==Guid.Empty||request.Context.BusinessDate==default||
            request.Action is not("account" or "evidence" or "bind_policy" or "mark_binding" or "projection_profile"))throw Error("Supply the exact gas account, source and configuration operation.");
        _=LifecycleNative.Instant(request.Context.AsOf);
        var body=Selection(request.Action,request.Selection);
        var admission=await Admission(connection,database,request.Action,body,false,ct);
        if(request.Action is "mark_binding" or "projection_profile")
        {
            if(await Projection(true) is {} receipt)return new(receipt,admission);
            await LifecycleNative.Source(connection,database,request.Context,ct);
            return new((await Projection(false))!.Value,admission);
        }
        if(await Receipt(connection,database,request.OperationId,request.Context,request.AccountId,request.Action,body,null,null,false,ct) is {} prior)return new(prior,admission);
        await LifecycleNative.Source(connection,database,request.Context,ct);
        return new(await Record(connection,database,request.OperationId,request.Context,request.AccountId,request.Action,body,null,null,false,ct),admission);
        async Task<JsonElement?> Projection(bool receiptOnly)
        {
            await using var call=ScheduleStore.Command(connection,"SELECT security.configure_gas_owned_projection($1,$2,$3,$4,$5::jsonb,$6,$7,$8)::text",
                database.Manifest.CatalogId,request.OperationId,request.Action,request.AccountId,body.GetRawText(),request.Context.AsOf,request.Context.ContextId,receiptOnly);
            return await call.ExecuteScalarAsync(ct) is string result?JsonSerializer.Deserialize<JsonElement>(result):null;
        }
    }
    internal static MarketDataException Error(string message)=>new("gas_owned_input",message);
    internal static string Refusal(string code)=>code switch{
        "gas_owned_denied"=>"This gas operation requires current authorization and the original admission must remain valid until it completes.",
        "gas_owned_context" or "gas_owned_preview_stale"=>"Gas facts changed after these inputs were captured. Select current inputs and review the operation again.",
        "gas_owned_conflict"=>"This request or source revision changed. Retry its original inputs or reload the current revision.",
        "gas_owned_policy_changed"=>"The saved gas action policy changed. Reload it and review the operation again.",
        "gas_owned_source_already_applied" or "gas_operator_reconciliation" or "gas_owned_reconciliation"=>"This source has already affected gas holdings. Reconcile its accepted effects before applying changed evidence.",
        "gas_stock_capacity" or "gas_stock_reservation" or "gas_stock_availability"=>"The identified owned stock is unavailable or already committed. Select sufficient physically available stock and compatible reservations.",
        "gas_operator_final_source"=>"Select the single final cashout observation from the saved source, role and fixing instant under these captured inputs.",
        "gas_owned_payment_capacity"=>"Recorded payments would exceed this cash obligation. Review the existing allocations and corrections.",
        "gas_owned_payment_origin"=>"This payment precedes the owned cash obligation. Record an advance under a separately supported contract instead of allocating it to this obligation.",
        "gas_owned_scope"=>"Choose a readable gas account or a smaller operational scope.",
        "gas_owned_policy_required"=>"These books contain independently owned gas. Select the whole-book gas valuation policy before running a complete portfolio calculation.",
        "gas_owned_projection_policy_required"=>"These books include source-known gas ownership or obligations. Select an explicit saved gas projection profile for their economic horizon.",
        _=>"Supply compatible exact gas terms, accepted evidence, quantities and explicit timing choices for this operation."};
}
