using System.Numerics;
using System.Text.Json;
using System.Text.Json.Nodes;
using OpenEtrm.Api;
using OpenEtrm.MarketData;
using OpenEtrm.MarketData.Gas;

internal static class GasTransportRouteAdapterChecks
{
    internal static Guid Id(int n)=>Guid.Parse($"75111111-1111-1111-1111-{n:000000000000}");
    internal static readonly Guid Reference=Id(1),Revision=Id(2),Book=Id(3),Entity=Id(4),Operation=Id(5);
    internal static JsonObject Obj(object value)=>JsonSerializer.SerializeToNode(value)!.AsObject();
    internal static JsonElement E(JsonNode value)=>JsonSerializer.SerializeToElement(value);
    internal static JsonObject Define()=>Obj(new{kind="define",routeId=(Guid?)null,expectedStateVersion=0,expectedPreviousId=(Guid?)null,
        businessKey="synthetic split and merge",bookId=Book,legalEntityId=Entity,legalEntityRevision=1,
        referenceCatalogId=Reference,referenceRevisionId=Revision,product="synthetic_gas",quantity="synthetic_native_quantity",
        implementation="gas_transport_chosen_uniform_route_v1",fractionBasis="fuel_and_loss_each_fraction_of_gross_receipt_v1",
        flowShape="one_explicit_uniform_receipt_and_delivery_per_segment_v1",transitLaw="fixed_nonnegative_elapsed_microseconds_v1",
        nodeBalanceLaw="simultaneous_inflow_equals_outflow_no_storage_v1",junctionPolicy="same_exact_pipeline_point_handoff_v1",
        membershipPolicy="whole_capacity_plan_stream_exclusive_v1",
        nodes=new[]{Node("a","external_receipt_v1"),Node("h","no_storage_transit_v1"),Node("b","no_storage_transit_v1"),
            Node("c","no_storage_transit_v1"),Node("m","no_storage_transit_v1"),Node("d","external_delivery_v1")},
        segments=new[]{Segment("ah",0,"a","h","2","100","3600000000"),Segment("hb",1,"h","b"),Segment("hc",1,"h","c"),
            Segment("bm",2,"b","m",lag:"3600000000"),Segment("cm",2,"c","m",lag:"3600000000"),Segment("md",3,"m","d"),
            Segment("hm",1,"h","m",lag:"3600000000")},evidence="Synthetic complete route terms"});
    static object Node(string key,string role)=>new{nodeKey=key,role,pipeline="synthetic_pipeline",zone="synthetic_zone",point="synthetic_"+key};
    static object Segment(string key,int scope,string from,string to,string n="0",string d="1",string lag="0")=>new{segmentKey=key,
        entitlementId=Id(100+scope),pathId=Id(200+Array.IndexOf(new[]{"ah","hb","hc","bm","cm","md","hm"},key)),fromNodeKey=from,toNodeKey=to,
        fuelFraction=new{n,d},lossFraction=new{n="0",d="1"},transitMicroseconds=lag};

    internal static void Run()
    {
        var raw=Define();var command=GasTransportRouteCommands.Normalize(Operation,E(raw));
        Same(command,GasTransportRouteCommands.Normalize(Operation,command));
        var again=GasTransportRouteCommands.Normalize(Id(6),E(Define()));
        if(command.Id("routeId")!=again.Id("routeId")||command.GetProperty("nodes").GetRawText()!=again.GetProperty("nodes").GetRawText()||
            command.GetProperty("segments").GetRawText()!=again.GetProperty("segments").GetRawText())
            throw new Exception("Identical graph definitions must preserve route, node and segment identities independently of the request id.");
        var ah=command.GetProperty("segments").EnumerateArray().Single(s=>s.Text("segmentKey")=="ah");
        if(ah.GetProperty("fuelFraction").Text("n")!="1"||ah.GetProperty("fuelFraction").Text("d")!="50")
            throw new Exception("Explicit rational fuel2/100 must reduce to1/50 without decimal rounding.");
        var reordered=Define();Reverse(reordered,"nodes");Reverse(reordered,"segments");
        Same(command,GasTransportRouteCommands.Normalize(Operation,E(reordered)));
        var invalid=Define();invalid.Remove("junctionPolicy");Reject(()=>GasTransportRouteCommands.Normalize(Operation,E(invalid)),"gas_transport_route_input");
        invalid=Define();invalid["inferConnections"]=true;Reject(()=>GasTransportRouteCommands.Normalize(Operation,E(invalid)),"gas_transport_route_input");
        invalid=Define();invalid["nodes"]![0]!["nodeId"]=Id(90);Reject(()=>GasTransportRouteCommands.Normalize(Operation,E(invalid)),"gas_transport_route_input");
        invalid=Define();invalid["segments"]![0]!["fuelFraction"]!["d"]="0";Reject(()=>GasTransportRouteCommands.Normalize(Operation,E(invalid)),"gas_transport_route_input");
        invalid=Define();invalid["segments"]![0]!["fuelFraction"]!["n"]="01";Reject(()=>GasTransportRouteCommands.Normalize(Operation,E(invalid)),"gas_transport_route_input");
        invalid=Define();invalid["segments"]![0]!["lossFraction"]!["n"]="1";Reject(()=>GasTransportRouteCommands.Normalize(Operation,E(invalid)),"gas_transport_route_input");
        invalid=Define();invalid["segments"]![0]!["transitMicroseconds"]="9223372036854775808";Reject(()=>GasTransportRouteCommands.Normalize(Operation,E(invalid)),"gas_transport_route_input");
        invalid=Define();invalid["segments"]![0]!["transitMicroseconds"]=1;Reject(()=>GasTransportRouteCommands.Normalize(Operation,E(invalid)),"gas_transport_route_input");
        invalid=Define();invalid["segments"]![0]!["fuelFraction"]!["n"]=(BigInteger.One<<4096).ToString();
        invalid["segments"]![0]!["fuelFraction"]!["d"]=(BigInteger.One<<4096).ToString();
        Reject(()=>GasTransportRouteCommands.Normalize(Operation,E(invalid)),"gas_transport_route_input");
        invalid=Define();invalid["segments"]![1]!["pathId"]=invalid["segments"]![0]!["pathId"]!.DeepClone();
        invalid["segments"]![1]!["entitlementId"]=invalid["segments"]![0]!["entitlementId"]!.DeepClone();
        Reject(()=>GasTransportRouteCommands.Normalize(Operation,E(invalid)),"gas_transport_route_input");
        invalid=Define();invalid["expectedStateVersion"]=1;Reject(()=>GasTransportRouteCommands.Normalize(Operation,E(invalid)),"gas_transport_route_input");
        var duplicate=command.GetRawText().Replace("\"kind\":\"define\"","\"kind\":\"define\",\"kind\":\"define\"",StringComparison.Ordinal);
        using(var doc=JsonDocument.Parse(duplicate))Reject(()=>GasTransportRouteCommands.Normalize(Operation,doc.RootElement),"gas_transport_route_input");
        var profile=GasTransportRouteAssessments.Definition(Operation,command);GasTransportRouteConservation.ValidateProfile(profile);
        var cyclic=profile with{Segments=profile.Segments.Add(profile.Segments[0] with{Id=Id(99),FromNodeId=profile.Segments[0].ToNodeId,ToNodeId=profile.Segments[0].FromNodeId})};
        Reject(()=>GasTransportRouteConservation.ValidateProfile(cyclic),"gas_transport_route_input");
        var facts=Facts(command);var plan=GasTransportRouteCommands.Normalize(Id(20),E(Plan(command)));
        var rawFacts=E(facts);var assessment=GasTransportRouteAssessments.Plan(Id(20),rawFacts,plan);
        if(!assessment.Route.Feasible||assessment.Route.Segments.Length!=7||assessment.Capacities.Length!=4||
            assessment.Route.Segments.Single(s=>s.SegmentId==ah.Id("segmentId")).FuelQuantity.ToExactString()!="2")
            throw new Exception("The complete exact native claim source must retain100 receipt, fuel2, split60+38, merge98 and explicit idle0.");
        var witness=GasTransportRouteAssessments.Witness(assessment);
        if(witness.Id("routePlanId")!=GasTransportRouteCommands.LogicalPlan(command.Id("routeId"),plan.Text("businessKey"))||witness.Id("revisionId")!=Id(20))
            throw new Exception("A witness must separately attest the stable logical route-plan stream and its exact immutable revision.");
        if(witness.GetProperty("capacities").GetArrayLength()!=4||witness.GetProperty("segments").EnumerateArray().Any(s=>s.Id("claimId")==Guid.Empty)||
            witness.GetProperty("nodes").EnumerateArray().SelectMany(n=>n.GetProperty("intervals").EnumerateArray()).Any(i=>i.GetProperty("residualPerHour").Text("n")!="0"))
            throw new Exception("Independent native proof needs the full capacity witnesses and every exact source claim/conservation interval.");
        Same(rawFacts,E(facts));
        var bad=Plan(command);bad["flows"]!.AsArray().RemoveAt(0);RejectPlan(command,bad,"gas_transport_route_source");
        bad=Plan(command);bad["sourcePlans"]!.AsArray().RemoveAt(0);RejectPlan(command,bad,"gas_transport_route_input");
        bad=Plan(command);bad["flows"]![0]!["receiptQuantity"]="100";Reject(()=>GasTransportRouteCommands.Normalize(Id(20),E(bad)),"gas_transport_route_input");
        bad=Plan(command);bad["sources"]![0]!["capacityGeneration"]=9;RejectPlan(command,bad,"gas_transport_route_stale");
        bad=Plan(command);bad["sourcePlans"]![0]!["expectedMembershipGeneration"]=1;RejectPlan(command,bad,"gas_transport_route_stale");
        var corrupt=Facts(command);corrupt["sourceCapacities"]![0]!["version"]!["book_id"]=Id(98);RejectSource(corrupt,plan);
        corrupt=Facts(command);corrupt["sourceCapacities"]![0]!["version"]!["reference_revision_id"]=Id(98);RejectSource(corrupt,plan);
        corrupt=Facts(command);corrupt["sourceCapacities"]![0]!["knownAt"]="2032-03-14T18:00:00Z";RejectSource(corrupt,plan);
        corrupt=Facts(command);corrupt["sourceCapacities"]![0]!["paths"]![0]!["receipt_zone_code"]="synthetic_other_zone";RejectSource(corrupt,plan);
        corrupt=Facts(command);corrupt["sourceCapacities"]![0]!["sourcePlans"]![0]!.AsObject().Remove("membershipState");RejectSource(corrupt,plan);
        corrupt=Facts(command);corrupt["sourceCapacities"]![0]!["sourcePlans"]![0]!["membershipState"]="occupied_v1";
        Reject(()=>GasTransportRouteAssessments.Plan(Id(20),E(corrupt),plan),"gas_transport_route_membership");
        corrupt=Facts(command);corrupt["sourceCapacities"]![0]!["hasFutureHeads"]=true;
        Reject(()=>GasTransportRouteAssessments.Plan(Id(20),E(corrupt),plan),"gas_transport_route_future_source");
        corrupt=Facts(command);corrupt["sourceCapacities"]![1]!["claims"]!.AsArray().Add(corrupt["sourceCapacities"]![1]!["claims"]![0]!.DeepClone());
        corrupt["sourceCapacities"]![1]!["claims"]!.AsArray()[^1]!["id"]=Id(97);RejectSource(corrupt,plan);
        corrupt=Facts(command);corrupt["sourceCapacities"]![0]!["claims"]![0]!["delivery_quantity"]="99";
        var breached=GasTransportRouteAssessments.Plan(Id(20),E(corrupt),plan);
        if(breached.Route.Feasible||breached.Route.Segments.Single(s=>s.SegmentId==ah.Id("segmentId")).Intervals.All(i=>i.ResidualPerHour.ToExactString()!="1"))
            throw new Exception("Observed chosen-plan conservation breaches must remain signed and unclipped.");
        Reject(()=>GasTransportRouteAssessments.Witness(breached),"gas_transport_route_conservation");
        var oldHead=Id(30);var retained=Facts(command);retained["plans"]=new JsonArray(Obj(new{id=oldHead,stream_id=GasTransportRouteCommands.LogicalPlan(command.Id("routeId"),"synthetic chosen route"),
            business_key="synthetic chosen route",revision=1,previous_id=(Guid?)null,definition_id=Operation,kind="plan",effective_at="2032-03-14T15:00:00Z"}));
        retained["sourceCapacities"]=new JsonArray();
        var release=Obj(new{kind="release",routeId=command.Id("routeId"),definitionId=Operation,expectedStateVersion=1,expectedPreviousId=oldHead,
            businessKey="synthetic chosen route",admissionPolicy="explicit_route_membership_release_v1",evidence="Synthetic own membership release"});
        var normalizedRelease=GasTransportRouteCommands.Normalize(Id(31),E(release));
        GasTransportRouteAssessments.RequireRelease(E(retained),normalizedRelease,DateTimeOffset.Parse("2032-03-14T16:00:00Z"));
        retained["hasFutureHeads"]=true;
        GasTransportRouteAssessments.RequireRelease(E(retained),normalizedRelease,DateTimeOffset.Parse("2032-03-14T16:00:00Z"));
        retained["plans"]!.AsArray().Add(Obj(new{id=Id(32),stream_id=GasTransportRouteCommands.LogicalPlan(command.Id("routeId"),"synthetic unrelated future route plan"),
            business_key="synthetic unrelated future route plan",revision=1,previous_id=(Guid?)null,definition_id=Operation,kind="plan",effective_at="2032-03-14T18:00:00Z"}));
        GasTransportRouteAssessments.RequireRelease(E(retained),normalizedRelease,DateTimeOffset.Parse("2032-03-14T16:00:00Z"));
        Reject(()=>GasTransportRouteAssessments.RequireRelease(E(retained),normalizedRelease,DateTimeOffset.Parse("2032-03-14T14:00:00Z")),"gas_transport_route_stale");
        release["expectedPreviousId"]=null;Reject(()=>GasTransportRouteCommands.Normalize(Id(31),E(release)),"gas_transport_route_input");
        var accepted=Accepted(command,plan,witness);var acceptedBytes=E(accepted);
        var current=GasTransportRouteAssessments.Current(acceptedBytes,Id(20));
        if(current.Assessment is null||current.UnavailableReason is not null||current.Assessment.RoutePlanId!=witness.Id("routePlanId"))
            throw new Exception("The exact current retained source must independently reconstruct its accepted route assessment.");
        var wrongMembership=Accepted(command,plan,witness);wrongMembership["memberships"]![0]!["route_plan_id"]=Id(98);
        Reject(()=>GasTransportRouteAssessments.Current(E(wrongMembership),Id(20)),"gas_transport_route_source");
        accepted["sourceCapacities"]![0]!["stateVersion"]=11;
        var staleRead=GasTransportRouteAssessments.Current(E(accepted),Id(20));
        if(staleRead.Assessment is not null||string.IsNullOrWhiteSpace(staleRead.UnavailableReason)||
            accepted["plans"]![0]!["assessment"]!.ToJsonString()!=JsonNode.Parse(witness.GetRawText())!.ToJsonString())
            throw new Exception("Source correction must make current assessment unavailable while preserving the accepted historical witness and flow provenance.");
        Same(acceptedBytes,E(Accepted(command,plan,witness)));
        var tiny=Tiny();var tinyCommand=GasTransportRouteCommands.Normalize(Id(50),E(tiny));var tinySource=TinyFacts(tinyCommand);
        var tinyPlan=GasTransportRouteCommands.Normalize(Id(51),E(TinyPlan(tinyCommand)));
        var tinyResult=GasTransportRouteAssessments.Plan(Id(51),E(tinySource),tinyPlan);
        var tinySegment=tinyResult.Route.Segments.Single();
        if(!tinyResult.Route.Feasible||tinySegment.ExpectedDeliveryQuantity.ToExactString()!="0.5"||tinySegment.Intervals.Single().ExpectedPerHour.ToExactString()!="600000000")
            throw new Exception("Decimal native quantities over3 microseconds with7 microseconds lag must retain exact half delivery and600000000/hour.");
        tiny["segments"]![0]!["fuelFraction"]=Obj(new{n="1",d="3"});tiny["segments"]![0]!["lossFraction"]=Obj(new{n="0",d="1"});
        tinyCommand=GasTransportRouteCommands.Normalize(Id(50),E(tiny));tinySource=TinyFacts(tinyCommand);
        tinySource["sourceCapacities"]![0]!["claims"]![0]!["delivery_quantity"]="0.666666666666666667";
        tinyPlan=GasTransportRouteCommands.Normalize(Id(51),E(TinyPlan(tinyCommand)));
        var approximation=GasTransportRouteAssessments.Plan(Id(51),E(tinySource),tinyPlan);
        if(approximation.Route.Feasible)throw new Exception("A decimal approximation to2/3 cannot silently satisfy exact conservation.");
        Reject(()=>GasTransportRouteAssessments.Witness(approximation),"gas_transport_route_conservation");
    }
    static JsonObject Accepted(JsonElement command,JsonElement plan,JsonElement witness)
    {
        var facts=Facts(command);var claims=facts["sourceCapacities"]!.AsArray().SelectMany(c=>c!["claims"]!.AsArray()).ToDictionary(c=>c!["id"]!.GetValue<Guid>(),c=>c!);
        facts["plans"]=new JsonArray(Obj(new{id=Id(20),stream_id=witness.Id("routePlanId"),business_key=plan.Text("businessKey"),revision=1,previous_id=(Guid?)null,
            definition_id=Operation,kind="plan",evidence="Synthetic complete chosen source plan",effective_at="2032-03-14T15:00:00Z",assessment=witness,
            sources=plan.GetProperty("sources").EnumerateArray().Select(s=>new{entitlement_id=s.Id("entitlementId"),capacity_version_id=s.Id("capacityVersionId"),capacity_generation=s.GetProperty("capacityGeneration").GetInt32()}),
            source_plans=plan.GetProperty("sourcePlans").EnumerateArray().Select(s=>new{entitlement_id=s.Id("entitlementId"),first_plan_revision_id=s.Id("firstPlanRevisionId"),plan_revision_id=s.Id("planRevisionId"),membership_generation=0}),
            flows=plan.GetProperty("flows").EnumerateArray().Select(f=>new{id=f.Id("flowId"),segment_id=f.Id("segmentId"),claim_id=f.Id("claimId"),claim=E(claims[f.Id("claimId")])})}));
        facts["memberships"]=new JsonArray(plan.GetProperty("sourcePlans").EnumerateArray().Select(s=>(JsonNode)Obj(new{first_plan_revision_id=s.Id("firstPlanRevisionId"),stream_id=witness.Id("routePlanId"),route_plan_id=Id(20)})).ToArray());
        foreach(var capacity in facts["sourceCapacities"]!.AsArray())
        {capacity!["sourcePlans"]![0]!["membershipState"]="owned_by_this_route_v1";capacity["sourcePlans"]![0]!["membershipGeneration"]=1;}
        return facts;
    }
    static JsonObject Tiny()
    {
        var definition=Define();definition["nodes"]=new JsonArray(Obj(Node("a",GasTransportRouteNode.Source)),Obj(Node("h",GasTransportRouteNode.Sink)));
        definition["segments"]=new JsonArray(Obj(Segment("ah",0,"a","h","1","3","7")));definition["segments"]![0]!["lossFraction"]=Obj(new{n="1",d="6"});
        return definition;
    }
    static JsonObject TinyFacts(JsonElement command)
    {
        var facts=Facts(command);facts["sourceCapacities"]=new JsonArray(Capacity(command,0));var claim=facts["sourceCapacities"]![0]!["claims"]![0]!;
        claim["receipt_from"]="2032-03-14T00:00:00Z";claim["receipt_to"]="2032-03-14T00:00:00.000003Z";claim["receipt_quantity"]="1";
        claim["delivery_from"]="2032-03-14T00:00:00.000007Z";claim["delivery_to"]="2032-03-14T00:00:00.000010Z";claim["delivery_quantity"]="0.5";
        foreach(var field in new[]{"agreement","receipts","deliveries","paths"})
        {
            var limits=field=="agreement"?facts["sourceCapacities"]![0]![field]!:facts["sourceCapacities"]![0]![field]![0]!["limits"]!;
            limits[0]!["maximum_per_hour"]="2000000000";
        }
        return facts;
    }
    static JsonObject TinyPlan(JsonElement command)
    {var plan=Plan(command);foreach(var field in new[]{"sources","sourcePlans"})while(plan[field]!.AsArray().Count>1)plan[field]!.AsArray().RemoveAt(1);return plan;}
    static void RejectPlan(JsonElement command,JsonObject plan,string code)=>Reject(()=>GasTransportRouteAssessments.Plan(Id(20),E(Facts(command)),GasTransportRouteCommands.Normalize(Id(20),E(plan))),code);
    static void RejectSource(JsonObject source,JsonElement plan)=>Reject(()=>GasTransportRouteAssessments.Plan(Id(20),E(source),plan),"gas_transport_route_source");
    internal static JsonObject Plan(JsonElement command)=>Obj(new{kind="plan",routeId=command.Id("routeId"),definitionId=Operation,expectedStateVersion=1,expectedPreviousId=(Guid?)null,
        businessKey="synthetic chosen route",admissionPolicy="complete_current_sources_and_conserving_route_v1",
        sources=Enumerable.Range(0,4).Select(i=>new{entitlementId=Id(100+i),capacityVersionId=Id(600+i),capacityGeneration=10+i}),
        sourcePlans=Enumerable.Range(0,4).Select(i=>new{entitlementId=Id(100+i),firstPlanRevisionId=Id(300+i),planRevisionId=Id(400+i),expectedMembershipGeneration=0}),
        flows=command.GetProperty("segments").EnumerateArray().Select(s=>new{segmentId=s.Id("segmentId"),claimId=Id(500+Array.IndexOf(new[]{"ah","hb","hc","bm","cm","md","hm"},s.Text("segmentKey")))}),
        evidence="Synthetic complete chosen source plan"});
    internal static JsonObject Facts(JsonElement command)
    {
        var route=Obj(new{id=command.Id("routeId"),definition_id=Operation,business_key=command.Text("businessKey"),book_id=Book,legal_entity_id=Entity,legal_entity_revision=1,
            reference_catalog_id=Reference,reference_revision_id=Revision,product_code="synthetic_gas",quantity_code="synthetic_native_quantity",
            implementation=command.Text("implementation"),fraction_basis=command.Text("fractionBasis"),flow_shape=command.Text("flowShape"),transit_law=command.Text("transitLaw"),
            node_balance_law=command.Text("nodeBalanceLaw"),junction_policy=command.Text("junctionPolicy"),membership_policy=command.Text("membershipPolicy")});
        var definition=route.DeepClone().AsObject();definition["id"]=Operation;definition["route_id"]=command.Id("routeId");
        var nodes=command.GetProperty("nodes").EnumerateArray().Select(n=>Obj(new{id=n.Id("nodeId"),node_key=n.Text("nodeKey"),role=n.Text("role"),pipeline_code=n.Text("pipeline"),
            zone_code=n.Text("zone"),point_code=n.Text("point"),reference_catalog_id=Reference,reference_revision_id=Revision,product_code="synthetic_gas"}));
        var segments=command.GetProperty("segments").EnumerateArray().Select(s=>Obj(new{id=s.Id("segmentId"),segment_key=s.Text("segmentKey"),entitlement_id=s.Id("entitlementId"),path_id=s.Id("pathId"),
            from_node_id=s.Id("fromNodeId"),to_node_id=s.Id("toNodeId"),fuel_n=s.GetProperty("fuelFraction").Text("n"),fuel_d=s.GetProperty("fuelFraction").Text("d"),
            loss_n=s.GetProperty("lossFraction").Text("n"),loss_d=s.GetProperty("lossFraction").Text("d"),transit_microseconds=s.Text("transitMicroseconds")}));
        return new(){["route"]=route,["definition"]=definition,["nodes"]=new JsonArray(nodes.Cast<JsonNode>().ToArray()),["segments"]=new JsonArray(segments.Cast<JsonNode>().ToArray()),
            ["stateVersion"]=1,["hasFutureHeads"]=false,["contextId"]=Id(900),["asOf"]="2032-03-14T16:00:00Z",["knownAt"]="2032-03-14T17:00:00Z",
            ["plans"]=new JsonArray(),["memberships"]=new JsonArray(),["sourceCapacities"]=new JsonArray(Enumerable.Range(0,4).Select(i=>(JsonNode)Capacity(command,i)).ToArray())};
    }
    static JsonObject Capacity(JsonElement command,int i)
    {
        const string start="2032-03-14T00:00:00Z",end="2032-03-14T04:00:00Z";
        static object[] Limits()=>[new{from=start,through=end,maximum_per_hour="1000"}];
        var nodes=command.GetProperty("nodes").EnumerateArray().ToDictionary(n=>n.Id("nodeId"));
        var segments=command.GetProperty("segments").EnumerateArray().Where(s=>s.Id("entitlementId")==Id(100+i)).ToArray();
        var paths=segments.Select(s=>Obj(new{id=s.Id("pathId"),path_key=s.Text("segmentKey"),pipeline_code="synthetic_pipeline",receipt_zone_code="synthetic_zone",
            receipt_point_code=nodes[s.Id("fromNodeId")].Text("point"),delivery_zone_code="synthetic_zone",delivery_point_code=nodes[s.Id("toNodeId")].Text("point"),
            product_code="synthetic_gas",limits=Limits()})).ToArray();
        var amounts=new[]{"100","60","38","60","38","98","0"};
        var claims=segments.Select(s=>{var n=Array.IndexOf(new[]{"ah","hb","hc","bm","cm","md","hm"},s.Text("segmentKey"));var r=n==0?0:n==5?2:1;var d=n is 0 or 3 or 4 or 6?r+1:r;
            string At(int hour)=>$"2032-03-14T{hour:00}:00:00Z";
            return Obj(new{id=Id(500+n),plan_id=Id(400+i),path_id=s.Id("pathId"),receipt_from=At(r),receipt_to=At(r+1),receipt_quantity=amounts[n],
                delivery_from=At(d),delivery_to=At(d+1),delivery_quantity=n==0?"98":amounts[n]});});
        return new(){["entitlement"]=Obj(new{id=Id(100+i),business_key="synthetic source "+i}),["stateVersion"]=10+i,["hasFutureHeads"]=false,
            ["contextId"]=Id(900),["asOf"]="2032-03-14T16:00:00Z",["knownAt"]="2032-03-14T17:00:00Z",
            ["version"]=Obj(new{id=Id(600+i),entitlement_id=Id(100+i),reference_catalog_id=Reference,reference_revision_id=Revision,book_id=Book,legal_entity_id=Entity,legal_entity_revision=1,
                product_code="synthetic_gas",quantity_code="synthetic_native_quantity",from_at=start,through_at=end,implementation=GasTransportCapacityProfile.Version,
                rate_basis=GasTransportCapacityProfile.ElapsedHour,flow_shape=GasTransportCapacityProfile.UniformStages,agreement_basis=GasTransportCapacityProfile.GrossReceipts,delivery_basis=GasTransportCapacityProfile.GrossDeliveries}),
            ["paths"]=new JsonArray(paths.Cast<JsonNode>().ToArray()),["agreement"]=JsonSerializer.SerializeToNode(Limits()),
            ["receipts"]=new JsonArray(paths.Select(p=>p["receipt_point_code"]!.GetValue<string>()).Distinct().Select(p=>(JsonNode)Obj(new{point_code=p,limits=Limits()})).ToArray()),
            ["deliveries"]=new JsonArray(paths.Select(p=>p["delivery_point_code"]!.GetValue<string>()).Distinct().Select(p=>(JsonNode)Obj(new{point_code=p,limits=Limits()})).ToArray()),
            ["plans"]=new JsonArray(Obj(new{id=Id(400+i),business_key="synthetic capacity plan "+i,revision=2,previous_id=Id(300+i),version_id=Id(600+i)})),
            ["claims"]=new JsonArray(claims.Cast<JsonNode>().ToArray()),["sourcePlans"]=new JsonArray(Obj(new{firstPlanRevisionId=Id(300+i),planRevisionId=Id(400+i),membershipGeneration=0,membershipState="available_v1"}))};
    }
    static void Reverse(JsonObject value,string field)=>value[field]=new JsonArray(value[field]!.AsArray().Reverse().Select(v=>v!.DeepClone()).ToArray());
    internal static void Same(JsonElement left,JsonElement right)
    {if(left.GetRawText()!=right.GetRawText())throw new Exception("Canonical route input changed across retry or unordered-set permutation.");}
    internal static void Reject(Action action,string code)
    {try{action();}catch(MarketDataException error)when(error.Code==code){return;}throw new Exception("Expected "+code+".");}
}
