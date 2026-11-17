using System.Collections.Immutable;
using System.Text.Json;
using Npgsql;
using OpenEtrm.MarketData;
using OpenEtrm.MarketData.Physical;
using OpenEtrm.MarketData.Trading;

namespace OpenEtrm.Api;

public sealed partial class DeliveryWorkbenchStore
{
    private async Task<object> GasRead(NpgsqlConnection c,Guid route,(ScheduleContext Context,DateTimeOffset At,bool Working) scope,CancellationToken ct)
    {
        var state=await Native(c,"SELECT security.delivery_route_allocation_state($1,$2)::text",ct,Catalog,route);var facts=state.GetProperty("source");
        GasTransportRouteStore.RequireSource(facts,scope.Context,scope.At,route);
        var visible=facts.GetProperty("definition").ValueKind==JsonValueKind.Object;
        var plans=Rows(facts,"plans");var allocations=Rows(state,"plans");
        var choices=visible?await Native(c,"SELECT security.delivery_route_allocation_choices($1,$2)::text",ct,Catalog,route):Json(Array.Empty<object>());
        var assessments=visible?plans.Select(p=>GasTransportRouteAssessments.Current(facts,p.Id("id"))).ToArray():[];
        DeliveryAssessment? assessment=null;
        if(assessments.Length==1&&assessments[0].Assessment is {} native)
            assessment=new GasPipelineDeliveryBehavior().Assess(GasInput(facts,plans.Single(p=>p.Id("id")==native.RevisionId),native));
        var forms=visible?GasForms(facts,allocations,choices,assessments):Empty;
        var overview=new List<object>{new{label="Gas delivery source",values=new[]{new{label="Boundary",value="Assignments link existing route terminal claims to existing booked native occurrences. They create no second movement or capacity debit."},
            new{label="Interval policy",value="Exact complete native intervals; no inferred proration or conversion."},
            new{label="Assessment",value=assessments.Length>1?"Multiple route plan streams are shown individually below; no stream is selected implicitly.":assessment is null?"No current conserving route is available for a common delivery assessment.":"The selected route retains complete current feasible capacity and conserving segment claims."}}}};
        foreach(var a in assessments)overview.Add(new{label=plans.Single(p=>p.Id("id")==a.PlanId).Text("business_key"),values=new[]{new{label="Current route assessment",value=a.Assessment is null?a.UnavailableReason??"Unassessed":"Complete source capacity and route conservation assessed"}}});
        object? nominationBatch=null;var applicablePlans=new List<JsonElement>();
        foreach(var plan in allocations)
        {
            var applicable=false;
            if(plan.Text("kind")=="allocate"&&At(plan,"effective_at")<=scope.At)
            {
                await using(var save=ScheduleStore.Command(c,"SAVEPOINT delivery_allocation_applicability"))await save.ExecuteNonQueryAsync(ct);
                try
                {
                    var exact=await Native(c,"SELECT security.read_delivery_route_allocations($1,$2,true)::text",ct,Catalog,plan.Id("id"));
                    applicable=true;
                    applicablePlans.Add(exact);
                }
                catch(PostgresException e)when(e.MessageText is "delivery_workbench_stale" or "delivery_workbench_target" or "delivery_workbench_geometry" or "delivery_workbench_conservation")
                {applicable=false;}
                finally
                {
                    await using var restore=ScheduleStore.Command(c,"ROLLBACK TO SAVEPOINT delivery_allocation_applicability;RELEASE SAVEPOINT delivery_allocation_applicability");await restore.ExecuteNonQueryAsync(CancellationToken.None);
                }
            }
            overview.Add(new{label="Allocation revision "+plan.GetProperty("revision").GetInt32(),values=new[]{new{label="State",value=plan.Text("kind")=="release"?"Released":applicable?"Current exact source and booked targets":"Recorded evidence; source or target changed, or revision is future-effective. Refresh and explicitly revise before fulfillment."},new{label="Evidence",value=plan.Text("evidence")}}});
        }
        // A batch must have one explicit applicable source. Released or stale
        // evidence remains inspectable and never becomes an implicit choice.
        if(applicablePlans.Count==1&&scope.Working)nominationBatch=await DeliveryNominationBatchStore.Choices(c,Catalog,applicablePlans[0],scope.At,ct);
        var tables=new List<object>();
        if(visible)
        {
            tables.AddRange(GasTransportRoutePresentation.GraphTables(facts));
            foreach(var a in assessments)tables.AddRange(a.Assessment is {} current?GasTransportRoutePresentation.Tables(current,facts,null):GasTransportRoutePresentation.RecordedTables(facts,plans.Single(p=>p.Id("id")==a.PlanId)));
        }
        foreach(var plan in allocations)tables.Add(new{label="Native assignments · revision "+plan.GetProperty("revision").GetInt32(),columns=new[]{"Terminal node","Obligation","Trade revision","Native day","Quantity"},
            rows=Rows(plan,"allocations").Select(row=>new[]{Rows(facts,"nodes").Single(n=>n.Id("id")==row.Id("node_id")).Text("node_key"),row.Id("obligation_id").ToString(),row.GetProperty("trade_revision").GetInt32().ToString(System.Globalization.CultureInfo.InvariantCulture),row.Text("day"),Number(row,"quantity")}).ToArray()});
        tables.Add(new{label="Assignment revision history",columns=new[]{"Revision","State","Effective at","Source route plan","Previous assignment","Evidence"},instantColumns=new[]{2},
            rows=Rows(state,"history").Select(p=>new[]{p.GetProperty("revision").GetInt32().ToString(System.Globalization.CultureInfo.InvariantCulture),p.Text("kind"),p.Text("effective_at"),p.Id("route_plan_id").ToString(),p.OptionalId("previous_id")?.ToString()??"None",p.Text("evidence")}).ToArray()});
        return new{profile=GasProfile,ownerId=await Owner(c,true,ct),context=scope.Context,at=scope.At,historical=!scope.Working,scopeId=route,facts=state,forms,configuration=Empty,
            canRecord=scope.Working&&forms.Count!=0&&await PreviewDatabase.HasPermission(c,"RecordGasOperations",ct),canConfigure=false,assessment,
            assessmentQuantityLabel=assessment is null?null:await GasUnit(c,facts,ct),nominationBatch,overviewSections=overview.ToArray(),tables=tables.ToArray()};
    }
    private static ImmutableDictionary<string,FacilityOperationForm> GasForms(JsonElement facts,JsonElement[] allocations,JsonElement choices,GasTransportRouteAssessmentRead[] assessments)
    {
        var forms=ImmutableDictionary.CreateBuilder<string,FacilityOperationForm>();var route=facts.GetProperty("route");var plans=Rows(facts,"plans");
        GasFormField Ref(string name,string label,ImmutableArray<string> members,ImmutableArray<GasFormChoice> values)=>new(name,label,"reference",false,values,[],members);
        GasFormField Text(string name,string label)=>new(name,label,"text",false,[],[],[]);
        var sourceChoices=assessments.Where(a=>a.Assessment is not null).Select(a=>{
            var p=plans.Single(p=>p.Id("id")==a.PlanId);var previous=allocations.SingleOrDefault(r=>r.Id("route_stream_id")==p.Id("stream_id"));
            return new GasFormChoice(p.Id("id").ToString(),p.Text("business_key")+" · exact route revision "+p.GetProperty("revision").GetInt32(),new{routePlanId=p.Id("id"),expectedPreviousId=previous.ValueKind==JsonValueKind.Object?(Guid?)previous.Id("id"):null});
        }).ToImmutableArray();
        var targets=choices.EnumerateArray().Select(t=>new GasFormChoice(t.Id("nodeId")+"@"+t.Id("obligationId")+"@"+t.Text("day"),
            t.Text("node")+" · "+t.Text("tradeReference")+" · "+t.Text("day")+" · "+t.Text("point")+" · maximum "+t.Text("contractual"),
            new{nodeId=t.Id("nodeId"),obligationId=t.Id("obligationId"),tradeRevision=t.GetProperty("tradeRevision").GetInt32(),day=t.Text("day")})).ToImmutableArray();
        if(sourceChoices.Length!=0&&targets.Length!=0)forms.Add("allocate",new("Assign a complete terminal delivery plan","RecordGasOperations",Json(new{kind="allocate",scopeId=route.Id("id"),expectedRouteStateVersion=facts.GetProperty("stateVersion").GetInt32(),policy=DeliveryWorkbenchCommands.Policy}),[
            Ref("source","Exact current conserving route plan and allocation predecessor",["routePlanId","expectedPreviousId"],sourceChoices),
            new("allocations","Complete replacement native assignments","array",false,[],[Ref("target","Exact terminal and booked native occurrence",["nodeId","obligationId","tradeRevision","day"],targets),Text("quantity","Positive exact native quantity")],[]){MinItems=1},Text("evidence","Reviewed source and assignment evidence")]));
        var release=allocations.Where(a=>a.Text("kind")=="allocate"&&At(a,"effective_at")<=At(facts,"asOf")).Select(p=>new GasFormChoice(p.Id("id").ToString(),"Allocation revision "+p.GetProperty("revision").GetInt32(),
            new{routePlanId=p.Id("route_plan_id"),expectedPreviousId=p.Id("id")})).ToImmutableArray();
        if(release.Length!=0)forms.Add("release",new("Release this native assignment plan","RecordGasOperations",Json(new{kind="release",scopeId=route.Id("id"),expectedRouteStateVersion=facts.GetProperty("stateVersion").GetInt32(),policy=DeliveryWorkbenchCommands.Policy,allocations=Array.Empty<object>()}),[
            Ref("source","Exact allocation head to release",["routePlanId","expectedPreviousId"],release),Text("evidence","Release evidence")]));
        return forms.ToImmutable();
    }
    private Task<JsonElement> AllocationStatus(NpgsqlConnection c,Guid operation,CancellationToken ct)=>Native(c,"SELECT security.delivery_route_allocation_status($1,$2)::text",ct,Catalog,operation);
    private Task<JsonElement> RecordNative(NpgsqlConnection c,Guid operation,JsonElement command,DateTimeOffset at,Guid context,CancellationToken ct)=>
        Native(c,"SELECT security.record_delivery_route_allocation($1,$2,$3::jsonb,$4,$5)::text",ct,Catalog,operation,command.GetRawText(),at,context);
    private async Task<object> Write(NpgsqlConnection c,DeliveryWorkbenchWriteRequest request,ScheduleContext? selected,string path,bool preview,CancellationToken ct)
    {
        if(Profile(path)!=GasProfile)throw new MarketDataException("delivery_workbench_native_operation","Use the selected trade's native receipt and dispatch operations; transfer title and cash retain their original owner.");
        if(request.OperationId==Guid.Empty||request.Context is null)throw DeliveryWorkbenchCommands.Invalid();
        await Capture(c,true,true,ct);await RequireOwner(c,request.OwnerId,true,ct);
        var command=DeliveryWorkbenchCommands.Normalize(request.Command);var at=TradingInstant.Parse(request.EffectiveAt,"delivery_workbench_input");
        if(selected is not null||request.Context.KnowledgeMode!="current"||request.Context.Retained is not null)throw new MarketDataException("historical_read_only","Return to working data before reviewing delivery assignments.");
        if(DateOnly.FromDateTime(at.UtcDateTime)!=DateWorkspace.Date(request.Context.EffectiveOn))throw DeliveryWorkbenchCommands.Invalid();
        var prior=await AllocationStatus(c,request.OperationId,ct);var recorded=prior.GetProperty("found").GetBoolean();
        var scope=await contexts.Resolve(c,request.Context,selected,"delivery_workbench_context",ct);
        JsonElement outcome;DeliveryAssessment? assessment=null;string? assessmentQuantityLabel=null;
        if(preview){await using var save=ScheduleStore.Command(c,"SAVEPOINT delivery_workbench_preview");await save.ExecuteNonQueryAsync(ct);}
        try
        {
            outcome=await RecordNative(c,request.OperationId,command,at,scope.Context.ContextId??throw new MarketDataException("delivery_workbench_context","Retain the certified operating context identity."),ct);
            if(!recorded&&outcome.GetProperty("changed").GetBoolean()&&command.Text("kind")=="allocate")
            {
                var source=await Native(c,"SELECT security.read_gas_transport_route($1,$2)::text",ct,Catalog,command.Id("scopeId"));
                GasTransportRouteStore.RequireSource(source,scope.Context,scope.At,command.Id("scopeId"));
                var current=GasTransportRouteAssessments.Current(source,command.Id("routePlanId"));
                if(current.Assessment is not {} native)throw new MarketDataException("delivery_workbench_stale",current.UnavailableReason??Refusal("delivery_workbench_stale"));
                var plan=Rows(source,"plans").Single(p=>p.Id("id")==command.Id("routePlanId"));
                var input=GasInput(source,plan,native);assessment=new GasPipelineDeliveryBehavior().Assess(input);
                RequireManagedAllocations(source,plan,command,input.Route.Quantity);
                var targets=await Native(c,"SELECT security.delivery_route_allocation_choices($1,$2)::text",ct,Catalog,command.Id("scopeId"));
                RequireManagedTargets(source,plan,command,targets,input.Route.Quantity);
                assessmentQuantityLabel=await GasUnit(c,source,ct);
            }
        }
        finally
        {
            if(preview){await using var restore=ScheduleStore.Command(c,"ROLLBACK TO SAVEPOINT delivery_workbench_preview;RELEASE SAVEPOINT delivery_workbench_preview");await restore.ExecuteNonQueryAsync(CancellationToken.None);}
        }
        var acknowledgement=recorded||!outcome.GetProperty("changed").GetBoolean();
        return new{profile=GasProfile,ownerId=request.OwnerId,operationId=request.OperationId,scopeId=command.Id("scopeId"),kind=command.Text("kind"),context=scope.Context,at=scope.At,effectiveAt=at,command,outcome,acknowledgement,assessment,
            assessmentQuantityLabel,
            overviewSections=new[]{new{label="Delivery assignment review",values=new[]{new{label="Operation",value=acknowledgement?"Acknowledge the original accepted fact":command.Text("kind")=="release"?"Release this allocation stream":"Record a complete replacement of exact terminal assignments"},new{label="Boundary",value="Source transport claims, booked quantities, nomination evidence and contractual valuation retain their native owners."}}}},
            tables=new[]{new{label="Reviewed native assignments",columns=new[]{"Terminal","Obligation","Trade revision","Native day","Quantity"},rows=Rows(command,"allocations").Select(a=>new[]{a.Id("nodeId").ToString(),a.Id("obligationId").ToString(),a.GetProperty("tradeRevision").GetInt32().ToString(System.Globalization.CultureInfo.InvariantCulture),a.Text("day"),a.Text("quantity")}).ToArray()}}};
    }
    private static void RequireManagedAllocations(JsonElement facts,JsonElement plan,JsonElement command,ReferenceKey specification)
    {
        // The shared native balance kernel checks each complete terminal source.
        // SQL separately enforces retained target geometry and cross-stream
        // conservation at the serialized write boundary.
        foreach(var group in Rows(command,"allocations").GroupBy(a=>a.Id("nodeId")))
        {
            var node=Rows(facts,"nodes").Single(n=>n.Id("id")==group.Key);var receipt=node.Text("role")=="external_receipt_v1";
            var segments=Rows(facts,"segments").Where(s=>s.Id(receipt?"from_node_id":"to_node_id")==group.Key).Select(s=>s.Id("id")).ToHashSet();
            var available=Rows(plan,"flows").Where(f=>segments.Contains(f.Id("segment_id"))).Select(f=>ExactDecimal.Parse(f.GetProperty("claim").Text(receipt?"receipt_quantity":"delivery_quantity"))).Aggregate(default(ExactDecimal),(sum,q)=>sum+q);
            var claims=group.Select(a=>new Quantity(ExactDecimal.Parse(a.Text("quantity")),specification)).ToImmutableArray();
            FacilityQuantityBalance.Evaluate(specification,new(available,specification),claims,null).RequireAvailable();
        }
    }
    private static void RequireManagedTargets(JsonElement facts,JsonElement plan,JsonElement command,JsonElement targets,ReferenceKey specification)
    {
        foreach(var group in Rows(command,"allocations").GroupBy(a=>(Obligation:a.Id("obligationId"),Day:a.Text("day"))))
        {
            var quantity=group.Select(a=>ExactDecimal.Parse(a.Text("quantity"))).Aggregate(default(ExactDecimal),(sum,q)=>sum+q);
            foreach(var row in group)
            {
                var target=targets.EnumerateArray().SingleOrDefault(t=>t.Id("nodeId")==row.Id("nodeId")&&t.Id("obligationId")==group.Key.Obligation&&t.Text("day")==group.Key.Day);
                if(target.ValueKind!=JsonValueKind.Object)throw new MarketDataException("delivery_workbench_target",Refusal("delivery_workbench_target"));
                var maximum=ScheduleNumber.Parse(target.Text("contractual"));
                var cap=new FacilityTargetCapacity(group.Key.Obligation,target.GetProperty("tradeRevision").GetInt32(),ExactQuantity.FromQuantity(new(ExactDecimal.Parse("1"),specification)).Scale(new OpenEtrm.Shared.Numerics.ExactRatio(maximum.Numerator,maximum.Denominator)));
                cap.Require(row.GetProperty("tradeRevision").GetInt32(),new(quantity,specification));
                var node=Rows(facts,"nodes").Single(n=>n.Id("id")==row.Id("nodeId"));var receipt=node.Text("role")=="external_receipt_v1";
                var segments=Rows(facts,"segments").Where(s=>s.Id(receipt?"from_node_id":"to_node_id")==row.Id("nodeId")).Select(s=>s.Id("id")).ToHashSet();
                foreach(var flow in Rows(plan,"flows").Where(f=>segments.Contains(f.Id("segment_id"))))
                {
                    var claim=flow.GetProperty("claim");
                    if(At(claim,receipt?"receipt_from":"delivery_from")!=At(target,"nativeFrom")||At(claim,receipt?"receipt_to":"delivery_to")!=At(target,"nativeTo"))
                        throw new MarketDataException("delivery_workbench_geometry",Refusal("delivery_workbench_geometry"));
                }
            }
        }
    }
    public Task<object> Preview(NpgsqlConnection c,DeliveryWorkbenchWriteRequest request,ScheduleContext? selected,string path,CancellationToken ct)=>Write(c,request,selected,path,true,ct);
    public Task<object> Record(NpgsqlConnection c,DeliveryWorkbenchWriteRequest request,ScheduleContext? selected,string path,CancellationToken ct)=>Write(c,request,selected,path,false,ct);
    public async Task<object> Status(NpgsqlConnection c,FacilityStatusRequest request,string path,CancellationToken ct)
    {
        var profile=Profile(path);if(profile!=GasProfile)throw new MarketDataException("delivery_workbench_native_operation","Reconcile receipt and dispatch operations using their original native operation status.");
        if(request.OperationId==Guid.Empty)throw DeliveryWorkbenchCommands.Invalid();var value=await AllocationStatus(c,request.OperationId,ct);
        var found=value.GetProperty("found").GetBoolean();var until=TradingInstant.Parse(value.Text("authorityUntil"),"delivery_workbench_source");
        database.RegisterCommitAdmission(c,new GasTransportRouteAdmission(Catalog,found?"plan":null,until));await RequireOwner(c,request.OwnerId,true,ct);
        return found?new{profile,found,operationId=request.OperationId,scopeId=value.Id("scopeId"),kind=value.Text("kind"),command=DeliveryWorkbenchCommands.Normalize(value.GetProperty("command")),effectiveAt=At(value,"effectiveAt"),outcome=value.GetProperty("outcome")}:
            (object)new{profile,found,operationId=request.OperationId};
    }
}
