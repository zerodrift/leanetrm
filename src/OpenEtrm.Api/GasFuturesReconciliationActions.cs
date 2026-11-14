using System.Collections.Immutable;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Npgsql;
using OpenEtrm.MarketData;
using OpenEtrm.MarketData.Gas;
using OpenEtrm.MarketData.Trading;

namespace OpenEtrm.Api;

internal sealed class GasFuturesReconciliationActions(PreviewDatabase database):ILifecycleActionHandler
{
    internal const string Handler="gas_futures_reconciliation";
    internal const string Action="gas.futures.reconcile_quantity";
    internal const string Version="gas_futures_quantity_reconciliation_v1";
    public string Kind=>Handler;
    public ImmutableArray<string> Permissions=>["ReadMarketData","ReadTrades","ReadGasOwnedHoldings","RecordGasOwnedHoldings"];

    private static (GasFuturesQuantityReconciliationSelection Selection,JsonElement Body) Inputs(LifecycleActionRequest request)
    {
        LifecycleNative.Require(request,Handler);
        if(request.Automatic||request.Action!=Action)throw Error("Review an observed physical quantity correction under its selected manual action policy.");
        try
        {
            var selected=request.Selection.Deserialize<GasFuturesQuantityReconciliationSelection>(GasOwnedOperations.Json)??throw new JsonException();
            if(selected.Supply.IsDefault||selected.PositionId!=request.Target.ScopeId||selected.PerformanceId!=request.Target.BindingId||
                selected.SourceAllocation is not(GasSourceAllocation.Preserve or GasSourceAllocation.Withdraw))
                throw Error("The quantity amendment must name this exact native obligation and observed performance.");
            return(selected,JsonSerializer.SerializeToElement(selected,GasOwnedOperations.Json));
        }
        catch(JsonException error){throw new MarketDataException("gas_quantity_input","Supply the closed quantity amendment, explicit retraction and identified stock-use revisions.",error);}
    }
    private async Task<JsonElement?> Record(NpgsqlConnection connection,LifecycleActionRequest request,JsonElement body,JsonElement? continuation,bool receipt,CancellationToken ct)
    {
        await using var command=ScheduleStore.Command(connection,
            "SELECT security.reconcile_gas_futures_delivery($1,$2,$3,$4::jsonb,$5,$6,$7,$8,$9,$10::jsonb)::text",
            database.Manifest.CatalogId,request.OperationId,request.Target.ScopeId,body.GetRawText(),request.Context.AsOf,request.Context.ContextId,
            request.PolicyId,request.PolicyRevision,receipt,continuation is {} value?value.GetRawText():DBNull.Value);
        return await command.ExecuteScalarAsync(ct) is string text?JsonSerializer.Deserialize<JsonElement>(text):null;
    }
    private async Task<GasFuturesDeliveryReview> Plan(NpgsqlConnection connection,LifecycleActionRequest request,GasFuturesQuantityReconciliationSelection selection,ScheduleContext context,CancellationToken ct)
    {
        await using(var save=ScheduleStore.Command(connection,"SAVEPOINT gas_quantity_source"))await save.ExecuteNonQueryAsync(ct);
        try
        {
            await PinnedContext.BindExact(connection,database.Manifest.CatalogId,context,ct);
            await TradingContext.Bind(connection,database.Manifest.CatalogId,context,request.Context.AsOf,ct);
            var source=await LifecycleNative.Read(connection,"SELECT security.read_gas_futures_reconciliation($1,$2,$3,$4,$5)::text",ct,
                database.Manifest.CatalogId,selection.PositionId,selection.PerformanceId,context.ContextId!.Value,request.Context.AsOf);
            var profile=FuturesPhysicalDeliveryStore.Materialize(source);var position=source.GetProperty("position");var link=source.GetProperty("link");var final=source.GetProperty("final");
            database.RegisterCommitAdmission(connection,new FuturesPhysicalReadAdmission(database.Manifest.CatalogId,profile.Id,profile.Revision,source.Instant("authorityUntil")));
            var performance=source.GetProperty("performance");
            if(source.Text("reconciliationProfile")!=Version||performance.Id("id")!=selection.PerformanceId||performance.GetProperty("revision").GetInt32()!=selection.ExpectedRevision)
                throw new MarketDataException("gas_quantity_revision","The selected financial performance changed. Reload its source and review the amendment again.");
            var original=new Quantity(position.Number("quantity"),profile.Terms.Quantity);
            var frame=new InputFrame(request.Context.SnapshotId??Guid.NewGuid(),"Physical quantity correction",request.Context.AsOf,request.Context.BusinessDate,[],
                ImmutableDictionary<string,ImmutableArray<Guid>>.Empty,request.Context.ContextId);
            var retained=await GasFuturesDeliveryStore.ReadPrincipal(connection,database,frame,new(link.Id("trade_id"),link.Id("binding_id")),profile.Configuration,original,ct);
            var price=ExactUnitPrice.Parse(final.Text("final_price"),new(profile.Terms.Currency,profile.Terms.Quantity,profile.QuoteMultiplier));
            return GasFuturesDeliveryStore.Correct(retained,selection,price,link.Id("final_occurrence_id"),final.Id("source_observation_id"));
        }
        finally
        {
            await using var restore=ScheduleStore.Command(connection,"ROLLBACK TO SAVEPOINT gas_quantity_source;RELEASE SAVEPOINT gas_quantity_source");await restore.ExecuteNonQueryAsync(CancellationToken.None);
        }
    }
    public async Task<LifecycleActionReview> Preview(NpgsqlConnection connection,LifecycleActionRequest request,CancellationToken ct)
    {
        var(selection,body)=Inputs(request);var admission=await GasOwnedOperations.Admission(connection,database,"futures_quantity_reconciliation",body,false,ct);
        // This durable original-source admission must survive the producer's
        // preview rollback, just like the original read deadline in memory.
        var context=await LifecycleNative.Source(connection,database,request.Context,ct);
        await using(var save=ScheduleStore.Command(connection,"SAVEPOINT gas_quantity_review"))await save.ExecuteNonQueryAsync(ct);
        GasFuturesDeliveryReview principal=new(null,null);JsonElement outcome;
        try
        {
            if(await Record(connection,request,body,null,true,ct) is {} receipt)
            {
                outcome=receipt.GetProperty("outcome");
                principal=new(receipt.GetProperty("continuation").ValueKind==JsonValueKind.Null?null:receipt.GetProperty("continuation"),null);
            }
            else
            {
                principal=await Plan(connection,request,selection,context,ct);
                outcome=(await Record(connection,request,body,principal.Continuation,false,ct))!.Value;
            }
            await using var complete=ScheduleStore.Command(connection,"SET CONSTRAINTS ALL IMMEDIATE");await complete.ExecuteNonQueryAsync(ct);
        }
        finally
        {
            await using var restore=ScheduleStore.Command(connection,"ROLLBACK TO SAVEPOINT gas_quantity_review;RELEASE SAVEPOINT gas_quantity_review");await restore.ExecuteNonQueryAsync(CancellationToken.None);
        }
        var details=JsonSerializer.SerializeToElement(new{selection=body,principal=principal.Details,sourceAllocations=AllocationReview(outcome),willChange=outcome.GetProperty("changed").GetBoolean(),
            acknowledgedRevision=outcome.GetProperty("acknowledgedRevision").GetInt32(),currentRevision=outcome.GetProperty("currentRevision").GetInt32()},GasOwnedOperations.Json);
        var offer=new LifecycleActionOffer(request.Target,Action,"Correct observed physical quantity",request.Target.BindingId,request.Context.AsOf,null,true,null,selection.ExpectedRevision,selection.ExpectedPrincipalVersion)
            {PolicyId=request.PolicyId,PolicyRevision=request.PolicyRevision,Selection=body};
        await admission.RequireCommit(connection,ct);return new(offer,Hash(request,body,principal.Continuation),details,database.Manifest.CatalogId,admission.Until);
    }
    public async Task<LifecycleActionWrite> Execute(NpgsqlConnection connection,LifecycleActionRequest request,CancellationToken ct)
    {
        var(selection,body)=Inputs(request);var admission=await GasOwnedOperations.Admission(connection,database,"futures_quantity_reconciliation",body,false,ct);
        if(await Record(connection,request,body,null,true,ct) is {} receipt)return new(receipt.GetProperty("outcome"),admission);
        var context=await LifecycleNative.Source(connection,database,request.Context,ct);
        var principal=await Plan(connection,request,selection,context,ct);
        if(Hash(request,body,principal.Continuation)!=request.PreviewHash)
            throw new MarketDataException("gas_quantity_stale_review","The quantity, identified stock or principal prefix changed. Review the full correction again.");
        var outcome=(await Record(connection,request,body,principal.Continuation,false,ct))!.Value;
        _=AllocationReview(outcome);return new(outcome,admission);
    }
    internal static JsonElement? AllocationReview(JsonElement outcome)
    {
        if(!outcome.GetProperty("changed").GetBoolean())return null;
        var policy=outcome.Text("sourceAllocation");var rows=outcome.GetProperty("sourceAllocations").EnumerateArray().ToImmutableArray();
        if(rows.IsEmpty||rows.Select(row=>(row.Id("accountId"),row.Text("sourceKey"))).Distinct().Count()!=rows.Length)throw Error("Retain every distinct native source allocation in this correction.");
        var reviewed=rows.Select(row=>
        {
            var specification=new ReferenceKey(new(row.Id("referenceCatalogId"),row.Id("referenceRevisionId")),row.Text("quantity"));
            Quantity Q(string field)=>new(row.Number(field),specification);
            var capacity=Q("capacity");var allocated=Q("allocated");Quantity? previouslyUnused;Quantity unused;
            if(row.GetProperty("newEnrollment").GetBoolean())
            {
                if(policy!=GasSourceAllocation.Preserve||row.OptionalId("oldEvidenceId") is not null||row.OptionalText("oldCapacity") is not null||Q("oldAllocated").Amount!=default)
                    throw Error("A new source enrollment must be explicit and cannot represent a withdrawal of a previously allocated source.");
                previouslyUnused=null;unused=GasSourceAllocation.Enroll(capacity,allocated);
            }
            else
            {
                if(row.OptionalId("oldEvidenceId") is null||row.OptionalText("oldCapacity") is null)throw Error("Retain the exact previously allocated source revision.");
                var plan=GasSourceAllocation.Plan(Q("oldCapacity"),Q("oldAllocated"),capacity,allocated,policy);
                previouslyUnused=plan.PreviouslyUnallocated;unused=plan.Unallocated;
            }
            return new{accountId=row.Id("accountId"),sourceKey=row.Text("sourceKey"),oldEvidenceId=row.OptionalId("oldEvidenceId"),sourceEvidenceId=row.Id("sourceEvidenceId"),
                oldCapacity=row.OptionalText("oldCapacity"),capacity=capacity.Amount.ToString(),oldAllocated=row.Text("oldAllocated"),allocated=allocated.Amount.ToString(),
                previouslyUnused=previouslyUnused?.Amount.ToString(),unused=unused.Amount.ToString(),quantity=specification};
        });
        return JsonSerializer.SerializeToElement(new{policy,sources=reviewed},GasOwnedOperations.Json);
    }
    private static string Hash(LifecycleActionRequest request,JsonElement body,JsonElement? continuation)=>Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(
        JsonSerializer.Serialize(new{implementation=Version,request.OperationId,request.Context,request.Target,request.Action,request.PolicyId,request.PolicyRevision,selection=body,continuation},GasOwnedOperations.Json))));
    public async Task<ImmutableArray<LifecycleActionOffer>> Discover(NpgsqlConnection connection,LifecycleReadContext context,LifecycleTarget? target,LifecycleDiscoveryAccess access,CancellationToken ct)
    {
        if(!access.Allows("ReadGasOwnedHoldings"))
        {
            if(target is not null)throw new PreviewDeniedException();return [];
        }
        await using(var capture=ScheduleStore.Command(connection,"SELECT security.gas_futures_delivery_discovery_authority($1)",database.Manifest.CatalogId))
            database.RegisterCommitAdmission(connection,new GasOwnedOperationReadAdmission(database.Manifest.CatalogId,new DateTimeOffset((DateTime)(await capture.ExecuteScalarAsync(ct))!)));
        var source=await LifecycleNative.Read(connection,"SELECT security.read_gas_futures_reconciliations($1,$2,$3)::text",ct,database.Manifest.CatalogId,context.ContextId,context.AsOf);
        database.RegisterCommitAdmission(connection,new GasOwnedOperationReadAdmission(database.Manifest.CatalogId,source.Instant("authorityUntil")));
        return [..source.GetProperty("corrections").EnumerateArray().Where(row=>target is null||target.ScopeId==row.Id("positionId")&&target.BindingId==row.Id("performanceId")).Select(row=>
        {
            var policy=row.OptionalId("policyId");var reason=policy is null?"Select a saved correction policy for this account.":null;
            return new LifecycleActionOffer(new(Kind,row.Id("positionId"),row.Id("performanceId")),Action,"Correct observed physical quantity",row.Id("performanceId"),context.AsOf,null,reason is null,reason,
                row.GetProperty("financialRevision").GetInt32(),row.GetProperty("principalVersion").GetInt32()){
                Subject=new(row.Text("reference"),"Physical delivery quantity",row.Id("bookId"),row.Text("book")),PolicyId=policy,
                PolicyRevision=policy is null?null:row.GetProperty("policyRevision").GetInt32(),AvailableQuantity=row.Text("quantity"),
                RequiredInputs=["positionId","performanceId","expectedRevision","expectedPrincipalVersion","quantity","retracted","titleEvidenceId","supply","sourceAllocation","evidence"],
                InputChoices=ImmutableDictionary<string,ImmutableArray<LifecycleInputChoice>>.Empty.Add("sourceAllocation",
                    [new(GasSourceAllocation.Preserve,"Preserve unused source quantity"),new(GasSourceAllocation.Withdraw,"Withdraw all source allocations")])};
        })];
    }
    private static MarketDataException Error(string message)=>new("gas_quantity_input",message);
}
