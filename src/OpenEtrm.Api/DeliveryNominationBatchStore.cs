using System.Collections.Immutable;
using System.Globalization;
using System.Security.Cryptography;
using System.Text.Json;
using Npgsql;
using OpenEtrm.MarketData;
using OpenEtrm.MarketData.Trading;

namespace OpenEtrm.Api;

internal sealed record DeliveryNominationBatchAdmission(Guid Catalog,Guid Plan,bool Writing,DateTimeOffset Until):ICommitAdmission
{
    public async Task RequireCommit(NpgsqlConnection c,CancellationToken ct)
    {
        await using var q=ScheduleStore.Command(c,"SELECT security.require_delivery_nomination_batch($1,$2,$3,$4)",Catalog,Plan,Writing,Until);
        await q.ExecuteNonQueryAsync(ct);
    }
}

// A reviewed atomic group of the existing manual nomination command. It owns
// only its command receipt; operator evidence and quantity tracks keep their
// existing native owners and do not become transport-plan quantities.
public sealed class DeliveryNominationBatchStore(PreviewDatabase database)
{
    private static readonly JsonSerializerOptions Json=new(JsonSerializerDefaults.Web){AllowDuplicateProperties=false};
    private Guid Catalog=>database.Manifest.CatalogId;
    private static MarketDataException Error(string code,string message)=>new("delivery_nomination_batch_"+code,message);
    private sealed record PreparedChild(GasOperationCommand Command,Guid ContextId,object Calculation);
    private sealed record Prepared(DeliveryNominationBatchInput Input,string Hash,ImmutableArray<PreparedChild> Commands,object[] Rows,ScheduleContext OperatingContext);
    private Task<JsonElement> Native(NpgsqlConnection c,string sql,CancellationToken ct,params object[] args)=>LifecycleNative.Read(c,sql,ct,args);
    private async Task Capture(NpgsqlConnection c,Guid plan,bool writing,CancellationToken ct)
    {
        await using var q=ScheduleStore.Command(c,"SELECT security.delivery_nomination_batch_authority($1,$2)",Catalog,writing);
        var until=new DateTimeOffset((DateTime)(await q.ExecuteScalarAsync(ct))!);
        database.RegisterCommitAdmission(c,new DeliveryNominationBatchAdmission(Catalog,plan,writing,until));
    }
    private async Task RequireOwner(NpgsqlConnection c,string owner,CancellationToken ct)
    {
        await using var q=ScheduleStore.Command(c,"SELECT security.gas_transport_route_owner($1)",Catalog);
        if(string.IsNullOrWhiteSpace(owner)||!Equals(await q.ExecuteScalarAsync(ct),owner))throw Error("owner_changed","Return to the original account to reconcile its nomination batch.");
    }
    private Task<JsonElement> Plan(NpgsqlConnection c,Guid plan,bool current,CancellationToken ct)=>
        Native(c,"SELECT security.read_delivery_route_allocations($1,$2,$3)::text",ct,Catalog,plan,current);
    private async Task<(ScheduleContext Context,DateTimeOffset At,bool Working)> PlanContext(NpgsqlConnection c,Guid plan,CancellationToken ct)
    {
        // This profile checks the selected allocation on its retained operating
        // business date under current knowledge. A financial frame's intraday
        // timestamp cannot replace the native end-of-business-date clock.
        var retained=await Plan(c,plan,false,ct);
        var effective=TradingInstant.Parse(retained.GetProperty("plan").Text("effective_at"),"delivery_nomination_batch_source");
        var day=DateOnly.FromDateTime(effective.UtcDateTime).ToString("yyyy-MM-dd",CultureInfo.InvariantCulture);
        var contexts=new OperationalBusinessContext(database);
        return await contexts.Resolve(c,new(day,"current",null,OperationalBusinessContext.Convention),null,"delivery_nomination_batch_context",ct);
    }
    private static ImmutableArray<(Guid Obligation,Guid Trade,int Revision,string Day,ExactDecimal Quantity)> Targets(JsonElement plan)
    {
        return [..plan.GetProperty("allocations").EnumerateArray().GroupBy(row=>(Obligation:row.Id("obligation_id"),Trade:row.Id("trade_id"),
            Revision:row.GetProperty("trade_revision").GetInt32(),Day:row.Text("day"))).Select(group=>
                (group.Key.Obligation,group.Key.Trade,group.Key.Revision,group.Key.Day,group.Select(row=>ExactDecimal.Parse(row.GetProperty("quantity").ValueKind==JsonValueKind.String?row.Text("quantity"):row.GetProperty("quantity").GetRawText())).Aggregate(default(ExactDecimal),(sum,value)=>sum+value)))
            .OrderBy(row=>row.Obligation.ToString("D"),StringComparer.Ordinal).ThenBy(row=>row.Day,StringComparer.Ordinal)];
    }
    private static void RequireTargets(DeliveryNominationBatchInput input,JsonElement plan)
    {
        var targets=Targets(plan);
        if(targets.Length!=input.Entries.Length)throw Error("scope","Nominate the complete selected allocation plan, with one explicit entry per obligation and native day.");
        foreach(var (target,entry) in targets.Zip(input.Entries))
            if(target.Obligation!=entry.ObligationId||target.Trade!=entry.TradeId||target.Revision!=entry.ExpectedRevision||target.Day!=entry.Day||
                target.Quantity!=ExactDecimal.Parse(entry.Details.Quantity))throw Error("scope","Nomination scope and quantities must equal the exact committed allocation plan.");
    }
    internal static async Task<object> Choices(NpgsqlConnection c,Guid catalog,JsonElement allocationPlan,DateTimeOffset at,CancellationToken ct)
    {
        var entries=new List<object>();
        foreach(var target in Targets(allocationPlan))
        {
            var day=DateWorkspace.Date(target.Day);
            var state=await LifecycleNative.Read(c,"SELECT security.read_gas_operations($1,$2,$3)::text",ct,catalog,target.Trade,day);
            var accounts=state.GetProperty("accounts").EnumerateArray().Where(account=>account.Id("trade_id")==target.Trade&&
                account.Id("origin_obligation_id")==target.Obligation&&account.Text("holder_role")=="legal_entity"&&account.GetProperty("active").GetBoolean()&&
                DateWorkspace.Date(account.Text("valid_from"))<=day&&day<DateWorkspace.Date(account.Text("valid_to")))
                .Select(account=>new{id=account.Id("id"),versionId=account.Id("version_id"),label=account.Text("external_reference"),expectedPreviousId=(Guid?)null}).ToArray();
            await using var reference=ScheduleStore.Command(c,"SELECT reference_number FROM trading.trade_references WHERE(catalog_id,trade_id)=($1,$2)",catalog,target.Trade);
            var number=Convert.ToInt32(await reference.ExecuteScalarAsync(ct),CultureInfo.InvariantCulture);
            entries.Add(new{obligationId=target.Obligation,tradeId=target.Trade,expectedRevision=target.Revision,day=target.Day,quantity=target.Quantity.ToString(),
                label="TR-"+number.ToString("D6",CultureInfo.InvariantCulture)+" · "+target.Day,accounts});
        }
        return new{allocationPlanId=allocationPlan.GetProperty("plan").Id("id"),profile=DeliveryNominationBatchContracts.Profile,entries,
            channels=new[]{new{value="portal",label="Operator portal"},new{value="edi",label="EDI"},new{value="email",label="Email"},new{value="phone",label="Phone"},new{value="manual_import",label="Manual import"}},
            boundary="Record new manually submitted request references. This operation transmits nothing; response and schedule adoption remain separate."};
    }
    private async Task<Prepared> Prepare(NpgsqlConnection c,InputFrame frame,Guid operation,DeliveryNominationBatchInput input,CancellationToken ct)
    {
        if(operation==Guid.Empty)throw DeliveryNominationBatchContracts.Invalid();
        input=DeliveryNominationBatchContracts.Normalize(input);
        // Capture every certified context before any native child publication.
        // A later capture must never certify this transaction's uncommitted facts.
        var operating=await PlanContext(c,input.AllocationPlanId,ct);
        await using(var save=ScheduleStore.Command(c,"SAVEPOINT delivery_batch_operating_read"))await save.ExecuteNonQueryAsync(ct);
        try
        {
            await new OperationalBusinessContext(database).BindResolved(c,operating,ct);
            var plan=await Plan(c,input.AllocationPlanId,true,ct);RequireTargets(input,plan);
        }
        finally
        {
            await using var restore=ScheduleStore.Command(c,"ROLLBACK TO SAVEPOINT delivery_batch_operating_read;RELEASE SAVEPOINT delivery_batch_operating_read");
            await restore.ExecuteNonQueryAsync(CancellationToken.None);
        }
        // Each native read binds once within its own established savepoint.
        // Neither source inspection nor nomination pricing can replace the
        // other's business/knowledge scope on an already-bound request.
        return await TradingBookingRead.Run(database,c,frame,async(_,pinned)=>
        {
            var commands=ImmutableArray.CreateBuilder<PreparedChild>();var rows=new List<object>();
            var operations=new GasDeliveryOperationsStore(database);
            foreach(var entry in input.Entries)
            {
                var request=new GasOperationPreviewRequest(input.Context,entry.TradeId,entry.ExpectedRevision,entry.Day,"nomination",JsonSerializer.SerializeToElement(entry.Details,Json));
                var prepared=await operations.Prepare(c,pinned,request,ct);
                commands.Add(new(new(request,DeliveryNominationBatchContracts.Child(operation,commands.Count),prepared.Hash),prepared.ContextId,prepared.Calculation));
                rows.Add(new{entry.TradeId,entry.ObligationId,entry.Day,quantity=entry.Details.Quantity,entry.Details.ExternalReference,entry.Details.SubmittedAt,review=prepared.Review});
            }
            var digest=Convert.ToHexStringLower(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(new{input,pinned.Id,pinned.At,commands=commands.Select(child=>child.Command).ToArray()},Json)));
            return new Prepared(input,digest,commands.ToImmutable(),rows.ToArray(),operating.Context);
        },ct);
    }
    public async Task<object> Preview(NpgsqlConnection c,InputFrame frame,DeliveryNominationBatchPreviewRequest request,CancellationToken ct)
    {
        var input=DeliveryNominationBatchContracts.Normalize(request.Input);
        await Capture(c,input.AllocationPlanId,false,ct);await RequireOwner(c,request.OwnerId,ct);
        var prepared=await Prepare(c,frame,request.OperationId,input,ct);
        return new{request.OperationId,request.OwnerId,allocationPlanId=input.AllocationPlanId,requestedInput=request.Input,input=prepared.Input,previewHash=prepared.Hash,
            overviewSections=new[]{new{label="Manual nomination batch",values=new[]{new{label="Scope",value="Complete exact committed delivery allocations"},
                new{label="Source clock",value=prepared.OperatingContext.EffectiveOn+" · UTC end of allocation business date · current knowledge"},
                new{label="Native nomination frame",value=LifecycleNative.Instant(frame.At)},
                new{label="Effect",value="Record all reviewed requests atomically. No external transmission, confirmation or schedule adoption."}}}},
            tables=new[]{new{label="Reviewed native requests",columns=new[]{"Trade","Native day","Requested quantity","External request","Submitted at"},
                rows=prepared.Input.Entries.Select(entry=>new[]{entry.TradeId.ToString("D"),entry.Day,entry.Details.Quantity,entry.Details.ExternalReference,entry.Details.SubmittedAt}).ToArray(),instantColumns=new[]{4}}},reviews=prepared.Rows};
    }
    private JsonElement Payload(DeliveryNominationBatchInput input,string hash)=>JsonSerializer.SerializeToElement(new{input,previewHash=hash},Json);
    public async Task<JsonElement> Record(NpgsqlConnection c,InputFrame frame,DeliveryNominationBatchRecordRequest request,CancellationToken ct)
    {
        var input=DeliveryNominationBatchContracts.Normalize(request.Input);
        if(request.OperationId==Guid.Empty||request.PreviewHash is not {Length:64}||request.PreviewHash.Any(value=>!char.IsAsciiHexDigitLower(value)))throw DeliveryNominationBatchContracts.Invalid();
        await Capture(c,input.AllocationPlanId,true,ct);await RequireOwner(c,request.OwnerId,ct);
        var payload=Payload(input,request.PreviewHash);
        var prior=await Native(c,"SELECT security.delivery_nomination_batch_status($1,$2,$3::jsonb)::text",ct,Catalog,request.OperationId,payload.GetRawText());
        if(prior.GetProperty("found").GetBoolean())return Receipt(request.OwnerId,prior);
        var prepared=await Prepare(c,frame,request.OperationId,input,ct);
        if(prepared.Hash!=request.PreviewHash)throw Error("stale_preview","Allocation or nomination inputs changed. Review the complete current batch again.");
        var nominations=new List<Guid>();
        foreach(var child in prepared.Commands)
        {
            // Reuse the same native command as the individual operation, with
            // all managed reviews/context certificates prepared before writes.
            var outcome=await Native(c,"SELECT security.record_gas_operations($1,$2::jsonb,$3,$4,$5::jsonb)::text",ct,
                Catalog,JsonSerializer.Serialize(child.Command,Json),frame.At,child.ContextId,JsonSerializer.Serialize(child.Calculation,Json));
            nominations.Add(outcome.Id("id"));
        }
        var recorded=await Native(c,"SELECT security.record_delivery_nomination_batch($1,$2,$3::jsonb,$4::uuid[],$5)::text",ct,Catalog,request.OperationId,payload.GetRawText(),nominations.ToArray(),
            prepared.OperatingContext.ContextId??throw Error("context","Retain the exact certified allocation source context."));
        return Receipt(request.OwnerId,recorded);
    }
    public async Task<JsonElement> Status(NpgsqlConnection c,FacilityStatusRequest request,CancellationToken ct)
    {
        if(request.OperationId==Guid.Empty)throw DeliveryNominationBatchContracts.Invalid();
        await Capture(c,Guid.Empty,false,ct);await RequireOwner(c,request.OwnerId,ct);
        var value=await Native(c,"SELECT security.delivery_nomination_batch_status($1,$2,NULL)::text",ct,Catalog,request.OperationId);
        return Receipt(request.OwnerId,value);
    }
    private static JsonElement Receipt(string owner,JsonElement value)=>value.GetProperty("found").GetBoolean()?
        JsonSerializer.SerializeToElement(new{found=true,ownerId=owner,operationId=value.Id("operationId"),allocationPlanId=value.Id("allocationPlanId"),
            input=value.GetProperty("request").GetProperty("input"),previewHash=value.GetProperty("request").Text("previewHash"),outcome=value.GetProperty("outcome")},Json):
        JsonSerializer.SerializeToElement(new{found=false,ownerId=owner,operationId=value.Id("operationId")},Json);
}
