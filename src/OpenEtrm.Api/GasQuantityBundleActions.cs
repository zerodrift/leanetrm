using System.Collections.Immutable;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Npgsql;
using OpenEtrm.MarketData;

namespace OpenEtrm.Api;

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record GasQuantityBundleSelection(
    [property:JsonRequired]GasOwnedQuantityReconciliationSelection Ordinary,
    [property:JsonRequired]GasFuturesQuantityBatchSelection Physical,
    [property:JsonRequired]Guid PhysicalAccountId,[property:JsonRequired]Guid PhysicalPolicyId,[property:JsonRequired]int PhysicalPolicyRevision);

// Each product supplies its existing source reader, financial plan and native
// seal. Only the reviewed composition and its first outcome belong here.
internal sealed class GasQuantityBundleActions(PreviewDatabase database):ILifecycleActionHandler
{
    internal const string Handler="gas_quantity_bundle";
    internal const string Version="ordinary_futures_quantity_bundle_v1";
    public string Kind=>Handler;
    public ImmutableArray<string> Permissions=>["ReadMarketData","ReadTrades","ReadGasOwnedHoldings","RecordGasOwnedHoldings"];

    private static (GasQuantityBundleSelection Selection,JsonElement Body) Inputs(LifecycleActionRequest request)
    {
        LifecycleNative.Require(request,Handler);
        if(request.Automatic||request.Action!=GasOwnedQuantityReconciliationActions.Action||request.Target.ScopeId!=request.Target.BindingId)
            throw Error("Select the exact owned account and both saved manual correction policies.");
        try
        {
            var selected=request.Selection.Deserialize<GasQuantityBundleSelection>(GasOwnedOperations.Json)??throw new JsonException();
            if(selected.Ordinary is null||selected.Physical is null||selected.PhysicalAccountId==Guid.Empty||selected.PhysicalPolicyId==Guid.Empty||selected.PhysicalPolicyRevision<1||
                selected.Ordinary.Positions.IsDefault||selected.Ordinary.Consumptions.IsDefault||selected.Physical.Corrections.IsDefaultOrEmpty||
                selected.Ordinary.Positions.Length+selected.Ordinary.Consumptions.Length is <1 or >16384||selected.Physical.Corrections.Length>16384||
                selected.Ordinary.Positions.Any(p=>p is null)||selected.Ordinary.Consumptions.Any(p=>p is null)||selected.Physical.Corrections.Any(p=>p is null))
                throw Error("Select the title or use corrections, delivery corrections and the delivery account's saved policy.");
            return(selected,JsonSerializer.SerializeToElement(new{selected.Ordinary,selected.Physical,selected.PhysicalAccountId},GasOwnedOperations.Json));
        }
        catch(JsonException error){throw new MarketDataException("gas_quantity_input","Supply the closed ordinary and physical correction selections.",error);}
    }
    private static LifecycleActionRequest PhysicalRequest(LifecycleActionRequest request,GasQuantityBundleSelection selection)=>request with{
        Target=new(GasFuturesQuantityBatchActions.Handler,selection.PhysicalAccountId,selection.PhysicalAccountId),
        Action=GasFuturesReconciliationActions.Action,PolicyId=selection.PhysicalPolicyId,PolicyRevision=selection.PhysicalPolicyRevision,
        Selection=JsonSerializer.SerializeToElement(selection.Physical,GasOwnedOperations.Json)};
    private async Task<JsonElement?> Record(NpgsqlConnection connection,LifecycleActionRequest request,GasQuantityBundleSelection selection,
        JsonElement body,JsonElement? continuation,bool receipt,CancellationToken ct)
    {
        await using var command=ScheduleStore.Command(connection,
            "SELECT security.reconcile_gas_quantity_bundle($1,$2,$3,$4::jsonb,$5,$6,$7,$8,$9,$10,$11,$12::jsonb)::text",
            database.Manifest.CatalogId,request.OperationId,request.Target.ScopeId,body.GetRawText(),request.Context.AsOf,request.Context.ContextId,
            request.PolicyId,request.PolicyRevision,selection.PhysicalPolicyId,selection.PhysicalPolicyRevision,receipt,
            continuation is {} value?value.GetRawText():DBNull.Value);
        return await command.ExecuteScalarAsync(ct) is string text?JsonSerializer.Deserialize<JsonElement>(text):null;
    }
    private async Task<(JsonElement Ordinary,GasFuturesDeliveryReview Physical)> Sources(NpgsqlConnection connection,LifecycleActionRequest request,
        GasQuantityBundleSelection selection,JsonElement body,ScheduleContext context,CancellationToken ct)
    {
        var ordinary=await new GasOwnedQuantityReconciliationActions(database).Source(connection,request,body.GetProperty("ordinary"),context,ct);
        var physical=await new GasFuturesQuantityBatchActions(database).Plan(connection,PhysicalRequest(request,selection),body.GetProperty("physical"),context,ct);
        return(ordinary,physical);
    }
    public async Task<LifecycleActionReview> Preview(NpgsqlConnection connection,LifecycleActionRequest request,CancellationToken ct)
    {
        var(selected,body)=Inputs(request);
        var admission=await GasOwnedOperations.Admission(connection,database,"quantity_reconciliation_bundle",body,false,ct);
        var context=await LifecycleNative.Source(connection,database,request.Context,ct);
        JsonElement? ordinary=null;GasFuturesDeliveryReview physical=new(null,null);JsonElement outcome;bool alreadyRecorded;
        await using(var save=ScheduleStore.Command(connection,"SAVEPOINT gas_quantity_bundle_review"))await save.ExecuteNonQueryAsync(ct);
        try
        {
            var receipt=await Record(connection,request,selected,body,null,true,ct);alreadyRecorded=receipt is not null;
            if(receipt is {} existing)
            {
                outcome=existing.GetProperty("outcome");
                physical=new(existing.GetProperty("continuation").ValueKind==JsonValueKind.Null?null:existing.GetProperty("continuation"),null);
            }
            else
            {
                (ordinary,physical)=await Sources(connection,request,selected,body,context,ct);
                outcome=(await Record(connection,request,selected,body,physical.Continuation,false,ct))!.Value;
            }
            await using var seal=ScheduleStore.Command(connection,"SELECT security.check_operation_seals()");await seal.ExecuteNonQueryAsync(ct);
        }
        finally
        {
            await using var restore=ScheduleStore.Command(connection,"ROLLBACK TO SAVEPOINT gas_quantity_bundle_review;RELEASE SAVEPOINT gas_quantity_bundle_review");
            await restore.ExecuteNonQueryAsync(CancellationToken.None);
        }
        var ordinaryAllocations=GasOwnedQuantityReconciliationActions.Allocations(outcome.GetProperty("ordinary"));
        var physicalAllocations=GasFuturesQuantityBatchActions.Allocations(outcome.GetProperty("physical"));
        var details=JsonSerializer.SerializeToElement(new{selection=selected,
            ordinary=new{source=ordinary,sourceAllocations=ordinaryAllocations},
            physical=new{principal=physical.Details,sourceAllocations=physicalAllocations},
            principal=physical.Details,sourceAllocations=ordinaryAllocations.AddRange(physicalAllocations),
            corrections=outcome.GetProperty("physical").GetProperty("corrections"),outcome,alreadyRecorded,
            willChange=!alreadyRecorded&&outcome.GetProperty("changed").GetBoolean()},GasOwnedOperations.Json);
        var offer=new LifecycleActionOffer(request.Target,request.Action,"Correct linked title and futures delivery",request.Target.BindingId,request.Context.AsOf,null,true,null,0,0)
            {PolicyId=request.PolicyId,PolicyRevision=request.PolicyRevision,Selection=request.Selection};
        await admission.RequireCommit(connection,ct);return new(offer,Hash(request,physical.Continuation),details,database.Manifest.CatalogId,admission.Until);
    }
    public async Task<LifecycleActionWrite> Execute(NpgsqlConnection connection,LifecycleActionRequest request,CancellationToken ct)
    {
        var(selected,body)=Inputs(request);
        var admission=await GasOwnedOperations.Admission(connection,database,"quantity_reconciliation_bundle",body,false,ct);
        await GasOwnedOperations.AdmitNewCommandInputs(connection,database,request,admission,ct);
        if(await Record(connection,request,selected,body,null,true,ct) is {} receipt)return new(receipt.GetProperty("outcome"),admission);
        var context=await LifecycleNative.Source(connection,database,request.Context,ct);
        var(_,physical)=await Sources(connection,request,selected,body,context,ct);
        if(Hash(request,physical.Continuation)!=request.PreviewHash)
            throw new MarketDataException("gas_quantity_stale_review","The linked source, quantities, saved policies or principal prefix changed. Review the complete correction again.");
        var outcome=(await Record(connection,request,selected,body,physical.Continuation,false,ct))!.Value;
        _=GasOwnedQuantityReconciliationActions.Allocations(outcome.GetProperty("ordinary"));
        _=GasFuturesQuantityBatchActions.Allocations(outcome.GetProperty("physical"));
        return new(outcome,admission);
    }
    private static string Hash(LifecycleActionRequest request,JsonElement? continuation)=>Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(
        JsonSerializer.Serialize(new{implementation=Version,request.OperationId,request.Context,request.Target,request.Action,request.PolicyId,request.PolicyRevision,
            selection=request.Selection,continuation},GasOwnedOperations.Json))));
    public async Task<ImmutableArray<LifecycleActionOffer>> Discover(NpgsqlConnection connection,LifecycleReadContext context,LifecycleTarget? target,LifecycleDiscoveryAccess access,CancellationToken ct)
    {
        if(!access.Allows("ReadGasOwnedHoldings")){if(target is not null)throw new PreviewDeniedException();return [];}
        var ordinary=await new GasOwnedQuantityReconciliationActions(database).Discover(connection,context,
            target is null?null:new(GasOwnedQuantityReconciliationActions.Handler,target.ScopeId,target.BindingId),access,ct);
        var physical=await new GasFuturesQuantityBatchActions(database).Discover(connection,context,null,access,ct);
        return [..ordinary.Select(offer=>offer with{Target=new(Handler,offer.Target.ScopeId,offer.Target.BindingId),
            Label="Correct linked title and futures delivery",Eligible=offer.Eligible&&physical.Any(p=>p.Eligible&&p.Subject?.BookId==offer.Subject?.BookId),
            Reason=offer.Reason??(physical.Any(p=>p.Eligible&&p.Subject?.BookId==offer.Subject?.BookId)?null:
                "Choose a source with an observed futures delivery and its saved manual correction policy."),
            RequiredInputs=["ordinary","physical","physicalAccountId","physicalPolicyId","physicalPolicyRevision"]})];
    }
    private static MarketDataException Error(string message)=>new("gas_quantity_input",message);
}
