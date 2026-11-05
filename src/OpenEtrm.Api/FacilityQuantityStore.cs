using System.Collections.Immutable;
using System.Text.Json;
using System.Text.Json.Serialization;
using Npgsql;
using OpenEtrm.MarketData;
using OpenEtrm.MarketData.Physical;
using OpenEtrm.MarketData.Trading;
using OpenEtrm.Shared.Temporal;
using OpenEtrm.Shared.Numerics;

namespace OpenEtrm.Api;

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record FacilityQuantityContext(string EffectiveOn,string KnowledgeMode,[property:JsonRequired] ScheduleContext? Retained,string Convention);
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record FacilityDirectoryRequest(FacilityQuantityContext Context);
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record FacilityReadRequest(FacilityQuantityContext Context,Guid LedgerId);
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record FacilityWriteRequest(FacilityQuantityContext Context,Guid OperationId,string EffectiveAt,JsonElement Command,[property:JsonRequired]string OwnerId);
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record FacilityStatusRequest(Guid OperationId,[property:JsonRequired]string OwnerId);
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record FacilityPreviewRequest(FacilityQuantityContext Context,Guid LedgerId,Guid SourceRevisionId,
    [property:JsonRequired] Guid? ExpectedPreviousId,ImmutableArray<FacilityAllocationInput> Allocations,[property:JsonRequired]string OwnerId);
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record FacilityAllocationInput(Guid ObligationId,int TradeRevision,string Quantity);
public sealed record FacilityQuantityChoice(Guid ObligationId,Guid TradeId,int TradeRevision,string PointCode,
    string Point,string Commodity,string Day,string SideCode,string Book,string Portfolio,string Counterparty,string Unit,
    string Direction,string Maximum,DateTimeOffset Start,DateTimeOffset End,[property:JsonIgnore] ExactQuantity Capacity);

public sealed class FacilityQuantityStore(PreviewDatabase database)
{
    public const string Convention="facility_business_date_end_utc_v1";
    private static MarketDataException Invalid()=>new("facility_quantity_input","Choose an explicit facility context and complete native-quantity command.");

    private static DateOnly Day(FacilityQuantityContext context)
    {
        if(context is null||context.Convention!=Convention)throw Invalid();
        return DateWorkspace.Date(context.EffectiveOn);
    }
    private async Task<(ScheduleContext Context,DateTimeOffset At,bool Working)> Bind(NpgsqlConnection c,
        FacilityQuantityContext request,ScheduleContext? selected,CancellationToken ct)
    {
        var day=Day(request);var store=new KnowledgeStore(database.Manifest.CatalogId);ScheduleContext context;
        await Authorize(c,ct);
        var working=request.KnowledgeMode=="current"&&request.Retained is null&&selected is null;
        if(working)context=(await store.Capture(c,new(request.EffectiveOn,null,"current"),ct)).Context;
        else if(request.KnowledgeMode=="retained"&&request.Retained is {} retained&&selected is not null&&
            retained.ContextId==selected.ContextId&&retained.EffectiveOn==request.EffectiveOn)
            context=await store.Retained(c,retained,ct);
        else throw new MarketDataException("temporal_context","Use the selected working context or the exact historical context applied in the header.");
        await PinnedContext.BindExact(c,database.Manifest.CatalogId,context,ct);
        // The selected contract reads the entire UTC business date; it is separate
        // from native gas-day geometry and never selects a pricing snapshot.
        var at=UserTime.EndOfDay(day,"UTC");
        await using var bind=ScheduleStore.Command(c,"SELECT security.use_trading_context($1,$2)",database.Manifest.CatalogId,at);
        await bind.ExecuteNonQueryAsync(ct);
        return(context,at,working);
    }
    private async Task Authorize(NpgsqlConnection c,CancellationToken ct)
    {
        await using var command=ScheduleStore.Command(c,"SELECT security.require_facility_quantities($1,false)",database.Manifest.CatalogId);
        await command.ExecuteNonQueryAsync(ct);
    }
    private async Task<string> Owner(NpgsqlConnection c,CancellationToken ct)
    {
        await using var command=ScheduleStore.Command(c,"SELECT security.facility_quantity_owner($1)",database.Manifest.CatalogId);
        return (string)(await command.ExecuteScalarAsync(ct))!;
    }
    private async Task RequireOwner(NpgsqlConnection c,string expected,CancellationToken ct)
    {
        if(expected is null||expected!=await Owner(c,ct))
            throw new MarketDataException("facility_quantity_owner_changed","The account changed. Return to the original account before reconciling this request.");
    }
    private async Task<JsonElement> Json(NpgsqlConnection c,string sql,CancellationToken ct,params object?[] args)
    {
        await using var command=ScheduleStore.Command(c,sql,args.Select(a=>a??DBNull.Value).ToArray());
        return JsonSerializer.Deserialize<JsonElement>((string)(await command.ExecuteScalarAsync(ct))!);
    }
    private async Task<ImmutableArray<FacilityQuantityChoice>> Choices(NpgsqlConnection c,Guid? ledger,DateTimeOffset at,CancellationToken ct)
    {
        var rows=await Json(c,"SELECT security.facility_quantity_choices($1,$2::uuid,$3)::text",ct,database.Manifest.CatalogId,ledger,at);
        return [..rows.EnumerateArray().Select(r=>{
            var specification=new ReferenceKey(new(r.Id("reference_catalog_id"),r.Id("reference_revision_id")),r.Text("quantity_code"));
            var n=ScheduleNumber.Parse(r.Text("quantity_n"));var d=ScheduleNumber.Parse(r.Text("quantity_d"));
            var capacity=ExactQuantity.FromQuantity(new(ExactDecimal.Parse("1"),specification)).Scale(new ExactRatio(n.Numerator*d.Denominator,n.Denominator*d.Numerator));
            return new FacilityQuantityChoice(r.Id("obligationId"),r.Id("trade_id"),r.GetProperty("trade_revision").GetInt32(),r.Text("point_code"),
                r.Text("point"),r.Text("commodity"),r.Text("day"),r.Text("sideCode"),r.Text("book"),r.Text("portfolio"),r.Text("counterparty"),r.Text("unit"),
                r.GetProperty("direction").GetInt32() switch{1=>"Receive",-1=>"Deliver",_=>throw Invalid()},capacity.ToExactString(),
                SchedulePreview.ParseAt(r.Text("native_from")),SchedulePreview.ParseAt(r.Text("native_to")),capacity);
        })];
    }
    private Task<JsonElement> Facts(NpgsqlConnection c,Guid ledger,CancellationToken ct)=>
        Json(c,"SELECT security.read_facility_quantity_ledger($1,$2)::text",ct,database.Manifest.CatalogId,ledger);
    internal Task<JsonElement> TradeAllocations(NpgsqlConnection c,Guid trade,CancellationToken ct)=>
        Json(c,"SELECT security.read_trade_facility_allocations($1,$2)::text",ct,database.Manifest.CatalogId,trade);
    private static ReferenceKey Specification(JsonElement facts)
    {
        var l=facts.GetProperty("ledger");
        return new(new(l.Id("reference_catalog_id"),l.Id("reference_revision_id")),l.Text("quantity_code"));
    }
    private static Quantity? ReadQuantity(JsonElement fact,ReferenceKey specification)=>
        fact.ValueKind==JsonValueKind.Null||fact.GetProperty("quantity").ValueKind==JsonValueKind.Null?null:
        new(ExactDecimal.Parse(fact.Text("quantity")),specification);
    private static FacilityQuantityBalance Evaluate(JsonElement facts,ImmutableArray<Quantity>? proposed=null)
    {
        var specification=Specification(facts);var limit=facts.GetProperty("limit");
        if(limit.ValueKind!=JsonValueKind.Null&&limit.Text("implementation")!=FacilityQuantityBalance.Implementation)throw Invalid();
        var claims=proposed??[..facts.GetProperty("allocations").EnumerateArray().Select(r=>new Quantity(ExactDecimal.Parse(r.Text("quantity")),specification))];
        return FacilityQuantityBalance.Evaluate(specification,ReadQuantity(facts.GetProperty("source"),specification),claims,ReadQuantity(limit,specification));
    }
    private static object Balance(JsonElement facts,FacilityQuantityBalance balance)
    {
        var limit=facts.GetProperty("limit");
        return new{allocated=balance.Allocated.Amount.ToString(),available=balance.Available?.Amount.ToString(),excess=balance.Excess?.Amount.ToString(),
            constraint=balance.Constraint switch{FacilityConstraintState.Breached=>"breached",FacilityConstraintState.WithinLimit=>"within_limit",FacilityConstraintState.Unassessed=>"unassessed",_=>throw Invalid()},
            implementation=FacilityQuantityBalance.Implementation,
            sourceRevisionId=facts.GetProperty("source").ValueKind==JsonValueKind.Null?(Guid?)null:facts.GetProperty("source").Id("id"),
            limitRevisionId=limit.ValueKind==JsonValueKind.Null?(Guid?)null:limit.Id("id"),
            planRevisionId=facts.GetProperty("plan").ValueKind==JsonValueKind.Null?(Guid?)null:facts.GetProperty("plan").Id("id")};
    }
    public async Task<object> Directory(NpgsqlConnection c,FacilityDirectoryRequest request,ScheduleContext? selected,CancellationToken ct,bool capturedWorkingRead=false)
    {
        var scope=await Bind(c,request.Context,selected,ct);
        var ledgers=await Json(c,"SELECT security.facility_quantity_directory($1)::text",ct,database.Manifest.CatalogId);
        var canRecord=(scope.Working||capturedWorkingRead)&&await PreviewDatabase.HasPermission(c,"RecordFacilityQuantities",ct);
        var choices=canRecord?await Choices(c,null,scope.At,ct):ImmutableArray<FacilityQuantityChoice>.Empty;
        await Authorize(c,ct);
        return new{ownerId=await Owner(c,ct),context=scope.Context,at=scope.At,ledgers=ledgers.EnumerateArray().Take(200).ToArray(),hasMore=ledgers.GetArrayLength()>200,
            choices=choices.Take(1000).ToArray(),choicesTruncated=choices.Length>1000,canRecord,
            canConfigure=scope.Working&&await PreviewDatabase.HasPermission(c,"ConfigureFacilityLimits",ct),historyLimit=100};
    }
    public async Task<object> Read(NpgsqlConnection c,FacilityReadRequest request,ScheduleContext? selected,CancellationToken ct)
    {
        if(request.LedgerId==Guid.Empty)throw Invalid();
        var scope=await Bind(c,request.Context,selected,ct);var facts=await Facts(c,request.LedgerId,ct);
        var choices=await Choices(c,request.LedgerId,scope.At,ct);
        await Authorize(c,ct);
        return new{ownerId=await Owner(c,ct),context=scope.Context,at=scope.At,facts,choices=choices.Take(1000).ToArray(),choicesTruncated=choices.Length>1000,balance=Balance(facts,Evaluate(facts)),
            canRecord=scope.Working&&await PreviewDatabase.HasPermission(c,"RecordFacilityQuantities",ct),
            canConfigure=scope.Working&&await PreviewDatabase.HasPermission(c,"ConfigureFacilityLimits",ct),
            canAdopt=scope.Working&&await PreviewDatabase.HasPermission(c,"RecordFacilityQuantities",ct)&&await PreviewDatabase.HasPermission(c,"AmendTrade",ct)};
    }
    public async Task<object> Preview(NpgsqlConnection c,FacilityPreviewRequest request,ScheduleContext? selected,CancellationToken ct,bool capturedWorkingRead=false)
    {
        await RequireOwner(c,request.OwnerId,ct);
        if(request.Allocations.IsDefault||request.Allocations.Length>100||request.Allocations.Select(a=>a.ObligationId).Distinct().Count()!=request.Allocations.Length)throw Invalid();
        var scope=await Bind(c,request.Context,selected,ct);
        if(!scope.Working&&!capturedWorkingRead)throw new MarketDataException("historical_read_only","Return to working data before preparing allocations.");
        var facts=await Facts(c,request.LedgerId,ct);var source=facts.GetProperty("source");var plan=facts.GetProperty("plan");
        if(source.ValueKind==JsonValueKind.Null||source.Id("id")!=request.SourceRevisionId||
            (plan.ValueKind==JsonValueKind.Null?(Guid?)null:plan.Id("id"))!=request.ExpectedPreviousId)
            throw new MarketDataException("facility_quantity_conflict",Refusal("facility_quantity_conflict"));
        var choices=await Choices(c,request.LedgerId,scope.At,ct);var specification=Specification(facts);
        var claims=ImmutableArray.CreateBuilder<Quantity>();
        foreach(var item in request.Allocations)
        {
            var matches=choices.Where(r=>r.ObligationId==item.ObligationId).ToArray();
            if(matches.Length!=1)throw new MarketDataException("facility_quantity_target",Refusal("facility_quantity_target"));
            var target=matches[0];var capacity=new FacilityTargetCapacity(item.ObligationId,target.TradeRevision,target.Capacity);
            var quantity=new Quantity(ExactDecimal.Parse(item.Quantity),specification);capacity.Require(item.TradeRevision,quantity);claims.Add(quantity);
        }
        var balance=Evaluate(facts,claims.ToImmutable()).RequireAvailable();await Authorize(c,ct);
        return new{context=scope.Context,at=scope.At,balance=Balance(facts,balance),request.Allocations};
    }
    private Task<JsonElement> Outcome(NpgsqlConnection c,Guid operation,CancellationToken ct)=>
        Json(c,"SELECT security.facility_quantity_status($1,$2)::text",ct,database.Manifest.CatalogId,operation);
    public async Task<JsonElement> Status(NpgsqlConnection c,FacilityStatusRequest request,CancellationToken ct)
    {
        await RequireOwner(c,request.OwnerId,ct);var operation=request.OperationId;
        if(operation==Guid.Empty)throw Invalid();
        var outcome=await Outcome(c,operation,ct);
        return outcome.TryGetProperty("found",out _)?outcome:JsonSerializer.SerializeToElement(new{operationId=operation,outcome});
    }
    public async Task<JsonElement> Save(NpgsqlConnection c,FacilityWriteRequest request,ScheduleContext? selected,CancellationToken ct)
    {
        await RequireOwner(c,request.OwnerId,ct);
        var day=Day(request.Context);
        if(selected is not null||request.Context.KnowledgeMode!="current"||request.Context.Retained is not null)
            throw new MarketDataException("historical_read_only","Return to working data before recording facility quantities.");
        if(request.Command.ValueKind!=JsonValueKind.Object||request.OperationId==Guid.Empty)throw Invalid();
        var at=TradingInstant.Parse(request.EffectiveAt,"facility_quantity_input");
        if(DateOnly.FromDateTime(at.UtcDateTime)!=day)throw Invalid();
        // Reconcile before capturing any new inputs. The command function itself
        // verifies canonical payload equality when an operation has an outcome.
        var prior=await Outcome(c,request.OperationId,ct);
        if(prior.TryGetProperty("found",out _))
            _=await new KnowledgeStore(database.Manifest.CatalogId).Capture(c,new(request.Context.EffectiveOn,null,"current"),ct);
        var outcome=await Json(c,"SELECT security.record_facility_quantity_command($1,$2,$3::jsonb,$4)::text",ct,
            database.Manifest.CatalogId,request.OperationId,request.Command.GetRawText(),at);
        return JsonSerializer.SerializeToElement(new{operationId=request.OperationId,outcome});
    }

    public static string Refusal(string code)=>code switch{
        "facility_quantity_conflict"=>"The source, target or allocation plan changed. Refresh, or reconcile the original request.",
        "facility_quantity_overallocated"=>"The allocations exceed the measured quantity. Correct the allocation amounts.",
        "facility_quantity_target"=>"Choose compatible booked obligations in this exact native period, basis and entitlement, within their quantities.",
        "facility_quantity_scope"=>"This profile uses the exact delivery point as its facility boundary.",
        "facility_quantity_scope_change"=>"This logical facility day already has different reference or clock pins. An explicit continuity operation is required.",
        "facility_quantity_interval"=>"Use the selected business date and record measured quantities only after the native interval ends.",
        "facility_quantity_context"=>"Apply an exact business date and knowledge context before reading facility quantities.",
        "facility_quantity_denied"=>"Your current account does not have access to these facility quantities.",
        _=>"Supply the complete facility command, exact references, native quantities and source evidence."};
}
