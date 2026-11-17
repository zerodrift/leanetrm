using System.Collections.Immutable;
using System.Globalization;
using System.Text.Json;
using OpenEtrm.MarketData;
using OpenEtrm.MarketData.Gas;
using OpenEtrm.MarketData.Trading;
using OpenEtrm.Shared.Numerics;

namespace OpenEtrm.Api;

internal sealed record GasTransportRouteCapacityAssessment(Guid EntitlementId,Guid CapacityVersionId,int CapacityGeneration,GasTransportCapacityAssessment Assessment);
internal sealed record GasTransportRoutePreparedAssessment(Guid RoutePlanId,Guid RevisionId,GasTransportRouteAssessment Route,
    ImmutableArray<GasTransportRouteCapacityAssessment> Capacities,ImmutableDictionary<Guid,Guid> Claims);
internal sealed record GasTransportRouteAssessmentRead(Guid PlanId,GasTransportRoutePreparedAssessment? Assessment,string? UnavailableReason);

internal static class GasTransportRouteAssessments
{
    internal static GasTransportRouteProfile Definition(Guid operation,JsonElement command)=>Source(()=>{
        if(operation==Guid.Empty||command.Text("kind")!="define")throw Invalid();
        var reference=new ReferenceRevision(command.Id("referenceCatalogId"),command.Id("referenceRevisionId"));
        var profile=new GasTransportRouteProfile(operation,command.Text("implementation"),new(reference,command.Text("quantity")),command.Text("fractionBasis"),
            command.Text("flowShape"),command.Text("transitLaw"),command.Text("nodeBalanceLaw"),
            [..Rows(command,"nodes").Select(n=>new GasTransportRouteNode(n.Id("nodeId"),new(reference,n.Text("point")),n.Text("role")))],
            [..Rows(command,"segments").Select(s=>new GasTransportRouteSegment(s.Id("segmentId"),s.Id("fromNodeId"),s.Id("toNodeId"),
                GasTransportRouteCommands.Fraction(s.GetProperty("fuelFraction")),GasTransportRouteCommands.Fraction(s.GetProperty("lossFraction")),Lag(s,"transitMicroseconds"))) ]);
        if(command.Text("junctionPolicy")!=GasTransportRouteCommands.Junction||command.Text("membershipPolicy")!=GasTransportRouteCommands.Membership)throw Invalid();
        GasTransportRouteConservation.ValidateProfile(profile);return profile;
    });

    internal static GasTransportRoutePreparedAssessment Plan(Guid operation,JsonElement facts,JsonElement command)=>Source(()=>{
        if(operation==Guid.Empty||command.Text("kind")!="plan"||command.Text("admissionPolicy")!=GasTransportRouteCommands.Adopt)throw Invalid();
        RequireHeads(facts);var profile=Profile(facts);var route=facts.GetProperty("route");var own=Control(facts,command);
        if(command.OptionalId("expectedPreviousId")!=(own is {} previous?previous.Id("id"):(Guid?)null))throw Stale();
        var sourceRows=Rows(command,"sources");var sources=Unique(sourceRows,s=>s.Id("entitlementId"));
        var capacities=Unique(Rows(facts,"sourceCapacities"),c=>c.GetProperty("entitlement").Id("id"));
        var segments=Unique(Rows(facts,"segments"),s=>s.Id("id"));
        var required=segments.Values.Select(s=>s.Id("entitlement_id")).ToHashSet();
        if(!required.SetEquals(sources.Keys)||!required.SetEquals(capacities.Keys))throw Invalid();
        var selections=Rows(command,"sourcePlans");
        if(selections.Length==0||selections.Select(s=>s.Id("firstPlanRevisionId")).Distinct().Count()!=selections.Length||
            selections.Select(s=>s.Id("planRevisionId")).Distinct().Count()!=selections.Length||selections.Any(s=>!required.Contains(s.Id("entitlementId"))))throw Invalid();
        var chosenClaims=new Dictionary<Guid,(JsonElement Claim,Guid Entitlement)>();
        var assessments=ImmutableArray.CreateBuilder<GasTransportRouteCapacityAssessment>();
        foreach(var (entitlement,capacity) in capacities.OrderBy(p=>p.Key))
        {
            RequireCoordinates(facts,capacity);RequireHeads(capacity);var version=capacity.GetProperty("version");var selected=sources[entitlement];
            if(version.Id("entitlement_id")!=entitlement)throw Invalid();
            if(version.Id("id")!=selected.Id("capacityVersionId")||Positive(capacity,"stateVersion")!=Positive(selected,"capacityGeneration"))throw Stale();
            RequireBasis(route,version);
            var complete=GasTransportCapacityAssessments.Current(capacity);
            assessments.Add(new(entitlement,version.Id("id"),Positive(capacity,"stateVersion"),complete));
            var nativePlans=Unique(Rows(capacity,"plans"),p=>p.Id("id"));
            var descriptors=Unique(Rows(capacity,"sourcePlans"),p=>p.Id("firstPlanRevisionId"));
            if(descriptors.Values.Select(p=>p.Id("planRevisionId")).Distinct().Count()!=descriptors.Count||descriptors.Count!=nativePlans.Count)throw Invalid();
            foreach(var descriptor in descriptors.Values)
                if(!nativePlans.ContainsKey(descriptor.Id("planRevisionId")))throw Invalid();
            var selectedPlans=selections.Where(s=>s.Id("entitlementId")==entitlement).ToArray();
            if(selectedPlans.Length==0)throw Invalid();
            var planIds=new HashSet<Guid>();
            foreach(var selectedPlan in selectedPlans)
            {
                if(!descriptors.TryGetValue(selectedPlan.Id("firstPlanRevisionId"),out var descriptor)||
                    descriptor.Id("planRevisionId")!=selectedPlan.Id("planRevisionId"))throw Stale();
                if(Nonnegative(descriptor,"membershipGeneration")!=Nonnegative(selectedPlan,"expectedMembershipGeneration"))throw Stale();
                var nativePlan=nativePlans[descriptor.Id("planRevisionId")];var revision=Positive(nativePlan,"revision");
                if(revision==1?(nativePlan.Id("id")!=descriptor.Id("firstPlanRevisionId")||nativePlan.OptionalId("previous_id") is not null):
                    nativePlan.Id("id")==descriptor.Id("firstPlanRevisionId")||nativePlan.OptionalId("previous_id") is null)throw Invalid();
                RequireMembership(facts,command,descriptor);
                planIds.Add(nativePlan.Id("id"));
            }
            var paths=Unique(Rows(capacity,"paths"),p=>p.Id("id"));
            foreach(var segment in segments.Values.Where(s=>s.Id("entitlement_id")==entitlement))
            {
                if(!paths.TryGetValue(segment.Id("path_id"),out var path))throw Invalid();
                RequireEndpoints(facts,segment,path,route);
            }
            foreach(var claim in Rows(capacity,"claims").Where(c=>planIds.Contains(c.Id("plan_id"))))
                if(!chosenClaims.TryAdd(claim.Id("id"),(claim,entitlement)))throw Invalid();
        }
        var flows=Rows(command,"flows");
        if(flows.Length!=segments.Count||flows.Length!=chosenClaims.Count||flows.Select(f=>f.Id("segmentId")).Distinct().Count()!=flows.Length||
            flows.Select(f=>f.Id("claimId")).Distinct().Count()!=flows.Length)throw Invalid();
        var manifest=ImmutableDictionary.CreateBuilder<Guid,Guid>();
        var selectedFlows=ImmutableArray.CreateBuilder<GasTransportRouteFlow>();
        foreach(var flow in flows)
        {
            if(!segments.TryGetValue(flow.Id("segmentId"),out var segment)||!chosenClaims.TryGetValue(flow.Id("claimId"),out var selected)||
                selected.Entitlement!=segment.Id("entitlement_id")||selected.Claim.Id("path_id")!=segment.Id("path_id")||
                flow.Id("flowId")!=GasTransportRouteCommands.Flow(route.Id("id"),command.Text("businessKey"),segment.Id("id")))throw Invalid();
            var claim=selected.Claim;manifest.Add(segment.Id("id"),claim.Id("id"));
            selectedFlows.Add(new(flow.Id("flowId"),segment.Id("id"),At(claim,"receipt_from"),At(claim,"receipt_to"),Q(claim,"receipt_quantity",profile.Quantity),
                At(claim,"delivery_from"),At(claim,"delivery_to"),Q(claim,"delivery_quantity",profile.Quantity)));
        }
        return new GasTransportRoutePreparedAssessment(GasTransportRouteCommands.LogicalPlan(route.Id("id"),command.Text("businessKey")),operation,
            GasTransportRouteConservation.Assess(profile,selectedFlows.ToImmutable()),assessments.ToImmutable(),manifest.ToImmutable());
    });

    internal static void RequireRelease(JsonElement facts,JsonElement command,DateTimeOffset effectiveAt)=>Source(()=>{
        // A release accepts no new flow or capacity assertion. Future/corrected
        // source heads and other route streams do not prevent freeing this
        // stream's own membership; its exact predecessor still bounds time.
        _ =facts.GetProperty("hasFutureHeads").GetBoolean();_ =Profile(facts);var own=Control(facts,command);
        if(command.Text("kind")!="release"||command.Text("admissionPolicy")!=GasTransportRouteCommands.Release||own is null||
            command.OptionalId("expectedPreviousId")!=own.Value.Id("id")||!TradingInstant.IsSupported(effectiveAt)||effectiveAt<At(own.Value,"effective_at"))throw Stale();
        return true;
    });

    // Old accepted rows remain evidence. A current result is available only when
    // their complete source selection is still current under the supplied cut.
    internal static GasTransportRouteAssessmentRead Current(JsonElement facts,Guid planId)=>Source(()=>{
        var head=Rows(facts,"plans").SingleOrDefault(p=>p.Id("id")==planId);if(head.ValueKind!=JsonValueKind.Object)throw Invalid();
        if(head.Text("kind")=="release")return new GasTransportRouteAssessmentRead(planId,null,"This route plan released its own source memberships; no chosen route is currently assessed.");
        try
        {
            RequireHeads(facts);var capacities=Unique(Rows(facts,"sourceCapacities"),c=>c.GetProperty("entitlement").Id("id"));
            var sourcePlans=Rows(head,"source_plans").Select(p=>{
                if(!capacities.TryGetValue(p.Id("entitlement_id"),out var capacity))throw Stale();
                var descriptor=Rows(capacity,"sourcePlans").SingleOrDefault(s=>s.Id("firstPlanRevisionId")==p.Id("first_plan_revision_id"));
                if(descriptor.ValueKind!=JsonValueKind.Object||descriptor.Id("planRevisionId")!=p.Id("plan_revision_id"))throw Stale();
                return new{entitlementId=p.Id("entitlement_id"),firstPlanRevisionId=p.Id("first_plan_revision_id"),planRevisionId=p.Id("plan_revision_id"),
                    expectedMembershipGeneration=Nonnegative(descriptor,"membershipGeneration")};
            }).ToArray();
            foreach(var flow in Rows(head,"flows"))
            {
                var claim=flow.GetProperty("claim");if(claim.Id("id")!=flow.Id("claim_id"))throw Invalid();
                var current=capacities.Values.SelectMany(c=>Rows(c,"claims")).SingleOrDefault(c=>c.Id("id")==flow.Id("claim_id"));
                if(current.ValueKind!=JsonValueKind.Object)throw Stale();
                foreach(var field in new[]{"id","plan_id","path_id"})if(claim.Id(field)!=current.Id(field))throw Invalid();
                foreach(var field in new[]{"receipt_from","receipt_to","delivery_from","delivery_to"})if(At(claim,field)!=At(current,field))throw Invalid();
                foreach(var field in new[]{"receipt_quantity","delivery_quantity"})if(ExactDecimal.Parse(claim.Text(field))!=ExactDecimal.Parse(current.Text(field)))throw Invalid();
            }
            var command=JsonSerializer.SerializeToElement(new{kind="plan",routeId=facts.GetProperty("route").Id("id"),definitionId=head.Id("definition_id"),
                expectedStateVersion=Positive(facts,"stateVersion"),expectedPreviousId=planId,businessKey=head.Text("business_key"),admissionPolicy=GasTransportRouteCommands.Adopt,
                sources=Rows(head,"sources").Select(s=>new{entitlementId=s.Id("entitlement_id"),capacityVersionId=s.Id("capacity_version_id"),capacityGeneration=Positive(s,"capacity_generation")}),
                sourcePlans,flows=Rows(head,"flows").Select(f=>new{segmentId=f.Id("segment_id"),claimId=f.Id("claim_id"),flowId=f.Id("id")}),evidence=head.Text("evidence")});
            var result=Plan(planId,facts,command);_ =Witness(result);return new GasTransportRouteAssessmentRead(planId,result,null);
        }
        catch(MarketDataException error)when(error.Code is "gas_transport_route_stale" or "gas_transport_route_future_source" or "gas_transport_route_membership" or
            "gas_transport_route_conservation" or "gas_transport_capacity_exceeded")
        {return new GasTransportRouteAssessmentRead(planId,null,error.Message);}
    });

    internal static JsonElement Witness(GasTransportRoutePreparedAssessment value)
    {
        var route=value.Route.RequireFeasible();
        Guid Claim(Guid segment)=>value.Claims.TryGetValue(segment,out var claim)?claim:throw Invalid();
        object Contribution(GasTransportRouteContribution c)=>new{segmentId=c.SegmentId,flowId=c.FlowId,claimId=Claim(c.SegmentId)};
        return JsonSerializer.SerializeToElement(new{implementation="gas_transport_route_witness_v1",definitionId=route.VersionId,routePlanId=value.RoutePlanId,revisionId=value.RevisionId,feasible=route.Feasible,
            capacities=value.Capacities.Select(c=>new{entitlementId=c.EntitlementId,capacityVersionId=c.CapacityVersionId,capacityGeneration=c.CapacityGeneration,
                assessment=GasTransportCapacityAssessments.Witness(c.Assessment,GasTransportCapacityPlanAdmission.FeasibleReplacement)}),
            segments=route.Segments.Select(s=>new{segmentId=s.SegmentId,flowId=s.FlowId,claimId=Claim(s.SegmentId),receiptQuantity=Ratio(s.ReceiptQuantity),fuelQuantity=Ratio(s.FuelQuantity),
                lossQuantity=Ratio(s.LossQuantity),expectedDeliveryQuantity=Ratio(s.ExpectedDeliveryQuantity),deliveryQuantity=Ratio(s.DeliveryQuantity),timingAligned=s.TimingAligned,conserved=s.Conserved,
                intervals=s.Intervals.Select(i=>new{from=LifecycleNative.Instant(i.From),through=LifecycleNative.Instant(i.Through),expectedPerHour=Ratio(i.ExpectedPerHour),
                    deliveredPerHour=Ratio(i.DeliveredPerHour),residualPerHour=Ratio(i.ResidualPerHour),conserved=i.Conserved})}),
            nodes=route.Nodes.Select(n=>new{nodeId=n.NodeId,conserved=n.Conserved,intervals=n.Intervals.Select(i=>new{from=LifecycleNative.Instant(i.From),through=LifecycleNative.Instant(i.Through),
                incoming=i.Incoming.Select(Contribution),outgoing=i.Outgoing.Select(Contribution),incomingPerHour=Ratio(i.IncomingPerHour),outgoingPerHour=Ratio(i.OutgoingPerHour),
                residualPerHour=Ratio(i.ResidualPerHour),conserved=i.Conserved})})});
    }

    static GasTransportRouteProfile Profile(JsonElement facts)
    {
        var route=facts.GetProperty("route");var definition=facts.GetProperty("definition");
        if(route.Id("definition_id")!=definition.Id("id")||definition.Id("route_id")!=route.Id("id"))throw Invalid();
        foreach(var field in new[]{"book_id","legal_entity_id","reference_catalog_id","reference_revision_id"})if(route.Id(field)!=definition.Id(field))throw Invalid();
        if(Positive(route,"legal_entity_revision")!=Positive(definition,"legal_entity_revision"))throw Invalid();
        foreach(var field in new[]{"business_key","product_code","quantity_code","implementation","fraction_basis","flow_shape","transit_law","node_balance_law","junction_policy","membership_policy"})
            if(route.Text(field)!=definition.Text(field))throw Invalid();
        if(route.Text("junction_policy")!=GasTransportRouteCommands.Junction||route.Text("membership_policy")!=GasTransportRouteCommands.Membership)throw Invalid();
        var reference=new ReferenceRevision(route.Id("reference_catalog_id"),route.Id("reference_revision_id"));var nodes=Rows(facts,"nodes");
        if(nodes.Any(n=>n.Id("reference_catalog_id")!=reference.CatalogId||n.Id("reference_revision_id")!=reference.RevisionId||n.Text("product_code")!=route.Text("product_code")))throw Invalid();
        var profile=new GasTransportRouteProfile(definition.Id("id"),route.Text("implementation"),new(reference,route.Text("quantity_code")),route.Text("fraction_basis"),
            route.Text("flow_shape"),route.Text("transit_law"),route.Text("node_balance_law"),
            [..nodes.Select(n=>new GasTransportRouteNode(n.Id("id"),new(reference,n.Text("point_code")),n.Text("role")))],
            [..Rows(facts,"segments").Select(s=>new GasTransportRouteSegment(s.Id("id"),s.Id("from_node_id"),s.Id("to_node_id"),Fraction(s,"fuel"),Fraction(s,"loss"),Lag(s,"transit_microseconds")))]);
        GasTransportRouteConservation.ValidateProfile(profile);return profile;
    }
    static JsonElement? Control(JsonElement facts,JsonElement command)
    {
        var route=facts.GetProperty("route");
        if(command.Id("routeId")!=route.Id("id")||command.Id("definitionId")!=route.Id("definition_id")||Positive(command,"expectedStateVersion")!=Positive(facts,"stateVersion"))throw Stale();
        var plans=Rows(facts,"plans");if(plans.Select(p=>p.Id("id")).Distinct().Count()!=plans.Length||plans.Select(p=>p.Text("business_key")).Distinct(StringComparer.Ordinal).Count()!=plans.Length)throw Invalid();
        foreach(var plan in plans)
            if(plan.Id("definition_id")!=route.Id("definition_id")||plan.Id("stream_id")!=GasTransportRouteCommands.LogicalPlan(route.Id("id"),plan.Text("business_key")))throw Invalid();
        var own=plans.SingleOrDefault(p=>p.Text("business_key")==command.Text("businessKey"));return own.ValueKind==JsonValueKind.Object?own:null;
    }
    static void RequireMembership(JsonElement facts,JsonElement command,JsonElement descriptor)
    {
        var owner=OwnMembership(facts,descriptor);
        if(descriptor.Text("membershipState")=="occupied_v1"||owner is {} stream&&stream!=GasTransportRouteCommands.LogicalPlan(command.Id("routeId"),command.Text("businessKey")))throw Membership();
    }
    internal static Guid? OwnMembership(JsonElement? facts,JsonElement descriptor)
    {
        var own=facts is {} source?Rows(source,"memberships").Where(m=>m.Id("first_plan_revision_id")==descriptor.Id("firstPlanRevisionId")).ToArray():[];
        switch(descriptor.Text("membershipState"))
        {
            case "available_v1":if(own.Length!=0)throw Invalid();return null;
            case "occupied_v1" when own.Length==0:return null;
            case "occupied_v1" or "owned_by_this_route_v1":
                if(own.Length!=1||facts is not {} selected)throw Invalid();
                var heads=Rows(selected,"plans").Where(p=>p.Id("stream_id")==own[0].Id("stream_id")).ToArray();
                if(heads.Length!=1||heads[0].Text("kind")!="plan"||own[0].Id("route_plan_id")!=heads[0].Id("id"))throw Invalid();
                var future=At(heads[0],"effective_at")>At(selected,"asOf");
                if(descriptor.Text("membershipState")=="occupied_v1"?(!future||!selected.GetProperty("hasFutureHeads").GetBoolean()):future)throw Invalid();
                return own[0].Id("stream_id");
            default:throw Invalid();
        }
    }
    static void RequireCoordinates(JsonElement facts,JsonElement capacity)
    {if(facts.Id("contextId")!=capacity.Id("contextId")||At(facts,"asOf")!=At(capacity,"asOf")||At(facts,"knownAt")!=At(capacity,"knownAt"))throw Invalid();}
    static void RequireBasis(JsonElement route,JsonElement version)
    {
        foreach(var field in new[]{"book_id","legal_entity_id","reference_catalog_id","reference_revision_id"})if(route.Id(field)!=version.Id(field))throw Invalid();
        if(Positive(route,"legal_entity_revision")!=Positive(version,"legal_entity_revision")||route.Text("product_code")!=version.Text("product_code")||route.Text("quantity_code")!=version.Text("quantity_code"))throw Invalid();
    }
    static void RequireEndpoints(JsonElement facts,JsonElement segment,JsonElement path,JsonElement route)
    {
        var nodes=Unique(Rows(facts,"nodes"),n=>n.Id("id"));if(!nodes.TryGetValue(segment.Id("from_node_id"),out var from)||!nodes.TryGetValue(segment.Id("to_node_id"),out var to))throw Invalid();
        if(path.Text("product_code")!=route.Text("product_code")||path.Text("pipeline_code")!=from.Text("pipeline_code")||path.Text("pipeline_code")!=to.Text("pipeline_code")||
            path.Text("receipt_zone_code")!=from.Text("zone_code")||path.Text("receipt_point_code")!=from.Text("point_code")||
            path.Text("delivery_zone_code")!=to.Text("zone_code")||path.Text("delivery_point_code")!=to.Text("point_code"))throw Invalid();
    }
    static void RequireHeads(JsonElement facts)
    {if(facts.GetProperty("hasFutureHeads").GetBoolean())throw new MarketDataException("gas_transport_route_future_source","A latest known route or selected capacity head is business-future. Select its effective business context before new adoption.");}
    static SignedRatio Fraction(JsonElement row,string name)=>GasTransportRouteCommands.Fraction(JsonSerializer.SerializeToElement(new{n=row.Text(name+"_n"),d=row.Text(name+"_d")}));
    static long Lag(JsonElement row,string name)
    {var text=row.Text(name);if(!long.TryParse(text,NumberStyles.None,CultureInfo.InvariantCulture,out var lag)||lag<0||text!=lag.ToString(CultureInfo.InvariantCulture))throw Invalid();return lag;}
    static ExactQuantity Q(JsonElement row,string field,ReferenceKey unit)
    {var value=ExactDecimal.Parse(row.Text(field));if(value.CompareTo(default)<0)throw Invalid();return ExactQuantity.FromQuantity(new(value,unit));}
    static object Ratio(ExactQuantity value)=>new{n=value.Numerator,d=value.Denominator};
    static DateTimeOffset At(JsonElement row,string name)=>TradingInstant.Parse(row.Text(name),"gas_transport_route_source");
    static int Positive(JsonElement row,string name)=>Number(row,name,1);
    static int Nonnegative(JsonElement row,string name)=>Number(row,name,0);
    static int Number(JsonElement row,string name,int minimum)
    {var value=row.GetProperty(name);if(value.ValueKind!=JsonValueKind.Number||!value.TryGetInt32(out var n)||n<minimum)throw Invalid();return n;}
    static JsonElement[] Rows(JsonElement row,string name)
    {var value=row.GetProperty(name);if(value.ValueKind!=JsonValueKind.Array)throw Invalid();return[..value.EnumerateArray()];}
    static Dictionary<Guid,JsonElement> Unique(JsonElement[] rows,Func<JsonElement,Guid> key)
    {var result=new Dictionary<Guid,JsonElement>();foreach(var row in rows){var id=key(row);if(id==Guid.Empty||!result.TryAdd(id,row))throw Invalid();}return result;}
    static T Source<T>(Func<T> read)
    {
        try{return read();}
        catch(Exception error)when(error is KeyNotFoundException or InvalidOperationException or FormatException or ArgumentException or OverflowException)
        {throw new MarketDataException("gas_transport_route_source","Retain the complete immutable route graph, exact compatible capacity sources and every selected logical-plan claim under one certified context.",error);}
    }
    static MarketDataException Invalid()=>new("gas_transport_route_source","Retain the complete immutable route graph, exact compatible capacity sources and every selected logical-plan claim under one certified context.");
    static MarketDataException Stale()=>new("gas_transport_route_stale","A selected route, capacity generation, plan head or membership changed. Review the complete source selection again.");
    static MarketDataException Membership()=>new("gas_transport_route_membership","A selected capacity-plan stream is unavailable to this exact route-plan stream. Explicitly release its existing route membership before adoption.");
}
