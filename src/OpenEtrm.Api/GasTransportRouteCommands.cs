using System.Globalization;
using System.Numerics;
using System.Text.Json;
using System.Text.Json.Nodes;
using OpenEtrm.MarketData;
using OpenEtrm.MarketData.Gas;
using OpenEtrm.Shared.Commands;
using OpenEtrm.Shared.Numerics;

namespace OpenEtrm.Api;

// Canonical notation and technical identities only. Source quantities and stage
// windows are read from exact native capacity claims, never entered here.
internal static class GasTransportRouteCommands
{
    internal const string Junction="same_exact_pipeline_point_handoff_v1",Membership="whole_capacity_plan_stream_exclusive_v1",
        Adopt="complete_current_sources_and_conserving_route_v1",Release="explicit_route_membership_release_v1";
    internal static Guid LogicalPlan(Guid route,string key)=>OperationIdentity.For("gas_transport_route_plan_v1:"+key,route);
    internal static Guid Flow(Guid route,string key,Guid segment)=>OperationIdentity.For("gas_transport_route_flow_v1:"+segment.ToString("D"),LogicalPlan(route,key));
    internal static JsonElement Normalize(Guid operation,JsonElement input)
    {
        try{return NormalizeCore(operation,input);}
        catch(MarketDataException error)when(error.Code!="gas_transport_route_input"){throw Invalid();}
        catch(Exception error)when(error is KeyNotFoundException or InvalidOperationException or FormatException or ArgumentException or OverflowException){throw Invalid();}
    }
    static JsonElement NormalizeCore(Guid operation,JsonElement input)
    {
        if(operation==Guid.Empty)throw Invalid();
        var kind=Text(input,"kind");
        string[] fields=kind switch{
            "define"=>["bookId","legalEntityId","legalEntityRevision","referenceCatalogId","referenceRevisionId","product","quantity","implementation",
                "fractionBasis","flowShape","transitLaw","nodeBalanceLaw","junctionPolicy","membershipPolicy","nodes","segments"],
            "plan"=>["definitionId","admissionPolicy","sources","sourcePlans","flows"],
            "release"=>["definitionId","admissionPolicy"],_=>throw Invalid()};
        Keys(input,["kind","routeId","expectedStateVersion","expectedPreviousId","businessKey","evidence",..fields]);
        var key=Key(input,"businessKey");var state=Integer(input,"expectedStateVersion",0);var previous=Id(input,"expectedPreviousId",true);
        var route=Id(input,"routeId",kind=="define");
        if(kind=="define")
        {
            var canonical=OperationIdentity.For("gas_transport_route_v1:"+key,Id(input,"bookId")!.Value);
            if(state!=0||previous is not null||route is {} supplied&&supplied!=canonical)throw Invalid();route=canonical;
        }
        if(route is null||kind!="define"&&state<1||kind=="release"&&previous is null)throw Invalid();
        var body=new JsonObject{["kind"]=kind,["routeId"]=route,["expectedStateVersion"]=state,["expectedPreviousId"]=previous,
            ["businessKey"]=key,["evidence"]=GasOperationText.Evidence(Text(input,"evidence"),"gas_transport_route_input")};
        if(kind=="define")
        {
            foreach(var name in new[]{"bookId","legalEntityId","referenceCatalogId","referenceRevisionId"})body[name]=Id(input,name);
            body["legalEntityRevision"]=Integer(input,"legalEntityRevision",1);
            foreach(var name in new[]{"product","quantity"})body[name]=Code(input,name);
            foreach(var (name,value) in new[]{("implementation",GasTransportRouteProfile.Version),("fractionBasis",GasTransportRouteProfile.GrossReceiptFractions),
                ("flowShape",GasTransportRouteProfile.UniformSegmentFlow),("transitLaw",GasTransportRouteProfile.FixedTransit),("nodeBalanceLaw",GasTransportRouteProfile.NoNodeStorage),
                ("junctionPolicy",Junction),("membershipPolicy",Membership)})
            {if(Text(input,name)!=value)throw Invalid();body[name]=value;}
            body["nodes"]=Nodes(route.Value,input.GetProperty("nodes"));
            body["segments"]=Segments(route.Value,input.GetProperty("segments"),body["nodes"]!.AsArray());
        }
        else
        {
            body["definitionId"]=Id(input,"definitionId");var policy=kind=="plan"?Adopt:Release;
            if(Text(input,"admissionPolicy")!=policy)throw Invalid();body["admissionPolicy"]=policy;
            if(kind=="plan")
            {
                body["sources"]=Sources(input.GetProperty("sources"));body["sourcePlans"]=SourcePlans(input.GetProperty("sourcePlans"));
                body["flows"]=Flows(route.Value,key,input.GetProperty("flows"));
                var entitlements=body["sources"]!.AsArray().Select(r=>r!["entitlementId"]!.GetValue<Guid>()).ToHashSet();
                var selected=body["sourcePlans"]!.AsArray().Select(r=>r!["entitlementId"]!.GetValue<Guid>()).ToHashSet();
                if(!entitlements.SetEquals(selected))throw Invalid();
            }
        }
        return JsonSerializer.SerializeToElement(body);
    }
    static JsonArray Nodes(Guid route,JsonElement input)
    {
        var rows=Rows(input,2,GasTransportRouteConservation.MaximumNodes).Select(row=>{
            Keys(row,["nodeKey","role","pipeline","zone","point"],["nodeId"]);var key=Key(row,"nodeKey");
            var id=OperationIdentity.For("gas_transport_route_node_v1:"+key,route);OptionalIdentity(row,"nodeId",id);
            var role=Text(row,"role");if(role is not(GasTransportRouteNode.Source or GasTransportRouteNode.Transit or GasTransportRouteNode.Sink))throw Invalid();
            return new JsonObject{["nodeId"]=id,["nodeKey"]=key,["role"]=role,["pipeline"]=Code(row,"pipeline"),["zone"]=Code(row,"zone"),["point"]=Code(row,"point")};
        }).OrderBy(row=>row["nodeId"]!.GetValue<Guid>()).ToArray();
        if(rows.Select(row=>row["nodeId"]!.GetValue<Guid>()).Distinct().Count()!=rows.Length)throw Invalid();
        return Array(rows);
    }
    static JsonArray Segments(Guid route,JsonElement input,JsonArray nodes)
    {
        var endpoints=nodes.ToDictionary(n=>n!["nodeKey"]!.GetValue<string>(),n=>n!["nodeId"]!.GetValue<Guid>(),StringComparer.Ordinal);
        var rows=Rows(input,1,GasTransportRouteConservation.MaximumSegments).Select(row=>{
            Keys(row,["segmentKey","entitlementId","pathId","fromNodeKey","toNodeKey","fuelFraction","lossFraction","transitMicroseconds"],
                ["segmentId","fromNodeId","toNodeId"]);
            var key=Key(row,"segmentKey");var id=OperationIdentity.For("gas_transport_route_segment_v1:"+key,route);OptionalIdentity(row,"segmentId",id);
            var from=Key(row,"fromNodeKey");var to=Key(row,"toNodeKey");
            if(!endpoints.TryGetValue(from,out var start)||!endpoints.TryGetValue(to,out var end)||start==end)throw Invalid();
            OptionalIdentity(row,"fromNodeId",start);OptionalIdentity(row,"toNodeId",end);
            var fuel=Fraction(row.GetProperty("fuelFraction"));var loss=Fraction(row.GetProperty("lossFraction"));
            if((fuel+loss).CompareTo(new SignedRatio(1,1))>0)throw Invalid();
            var lag=Unsigned(Text(row,"transitMicroseconds"));if(lag>long.MaxValue)throw Invalid();
            return new JsonObject{["segmentId"]=id,["segmentKey"]=key,["entitlementId"]=Id(row,"entitlementId"),["pathId"]=Id(row,"pathId"),
                ["fromNodeKey"]=from,["toNodeKey"]=to,["fromNodeId"]=start,["toNodeId"]=end,
                ["fuelFraction"]=Ratio(fuel),["lossFraction"]=Ratio(loss),["transitMicroseconds"]=lag.ToString(CultureInfo.InvariantCulture)};
        }).OrderBy(row=>row["segmentId"]!.GetValue<Guid>()).ToArray();
        if(rows.Select(row=>row["segmentId"]!.GetValue<Guid>()).Distinct().Count()!=rows.Length||
            rows.Select(row=>(row["entitlementId"]!.GetValue<Guid>(),row["pathId"]!.GetValue<Guid>())).Distinct().Count()!=rows.Length)throw Invalid();
        return Array(rows);
    }
    static JsonArray Sources(JsonElement input)
    {
        var rows=Rows(input,1,GasTransportRouteConservation.MaximumSegments).Select(row=>{
            Keys(row,["entitlementId","capacityVersionId","capacityGeneration"]);
            return new JsonObject{["entitlementId"]=Id(row,"entitlementId"),["capacityVersionId"]=Id(row,"capacityVersionId"),["capacityGeneration"]=Integer(row,"capacityGeneration",1)};
        }).OrderBy(row=>row["entitlementId"]!.GetValue<Guid>()).ToArray();
        if(rows.Select(row=>row["entitlementId"]!.GetValue<Guid>()).Distinct().Count()!=rows.Length)throw Invalid();return Array(rows);
    }
    static JsonArray SourcePlans(JsonElement input)
    {
        var rows=Rows(input,1,GasTransportRouteConservation.MaximumSegments).Select(row=>{
            Keys(row,["entitlementId","firstPlanRevisionId","planRevisionId","expectedMembershipGeneration"]);
            return new JsonObject{["entitlementId"]=Id(row,"entitlementId"),["firstPlanRevisionId"]=Id(row,"firstPlanRevisionId"),
                ["planRevisionId"]=Id(row,"planRevisionId"),["expectedMembershipGeneration"]=Integer(row,"expectedMembershipGeneration",0)};
        }).OrderBy(row=>row["entitlementId"]!.GetValue<Guid>()).ThenBy(row=>row["firstPlanRevisionId"]!.GetValue<Guid>()).ToArray();
        if(rows.Select(row=>row["firstPlanRevisionId"]!.GetValue<Guid>()).Distinct().Count()!=rows.Length||
            rows.Select(row=>row["planRevisionId"]!.GetValue<Guid>()).Distinct().Count()!=rows.Length)throw Invalid();return Array(rows);
    }
    static JsonArray Flows(Guid route,string key,JsonElement input)
    {
        var rows=Rows(input,1,GasTransportRouteConservation.MaximumSegments).Select(row=>{
            Keys(row,["segmentId","claimId"],["flowId"]);var segment=Id(row,"segmentId")!.Value;var id=Flow(route,key,segment);OptionalIdentity(row,"flowId",id);
            return new JsonObject{["segmentId"]=segment,["claimId"]=Id(row,"claimId"),["flowId"]=id};
        }).OrderBy(row=>row["segmentId"]!.GetValue<Guid>()).ToArray();
        if(rows.Select(row=>row["segmentId"]!.GetValue<Guid>()).Distinct().Count()!=rows.Length||rows.Select(row=>row["claimId"]!.GetValue<Guid>()).Distinct().Count()!=rows.Length)throw Invalid();
        return Array(rows);
    }
    internal static SignedRatio Fraction(JsonElement input)
    {
        Keys(input,["n","d"]);var n=Unsigned(Text(input,"n"));var d=Unsigned(Text(input,"d"));
        if(d.IsZero||n.GetBitLength()>4096||d.GetBitLength()>4096||n>d)throw Invalid();return new(n,d);
    }
    static BigInteger Unsigned(string value)
    {if(value.Length==0||value.Length>1234||value.Length>1&&value[0]=='0'||value.Any(c=>c is <'0' or >'9'))throw Invalid();return BigInteger.Parse(value,CultureInfo.InvariantCulture);}
    static JsonObject Ratio(SignedRatio value)=>new(){["n"]=value.Numerator.ToString(CultureInfo.InvariantCulture),["d"]=value.Denominator.ToString(CultureInfo.InvariantCulture)};
    static JsonArray Array(IEnumerable<JsonObject> rows)=>new(rows.Select(row=>(JsonNode)row).ToArray());
    static JsonElement[] Rows(JsonElement input,int minimum,int maximum)
    {if(input.ValueKind!=JsonValueKind.Array||input.GetArrayLength()<minimum||input.GetArrayLength()>maximum)throw Invalid();return[..input.EnumerateArray()];}
    static void Keys(JsonElement input,string[] required,string[]? optional=null)
    {
        if(input.ValueKind!=JsonValueKind.Object)throw Invalid();optional??=[];var names=input.EnumerateObject().Select(p=>p.Name).ToArray();
        if(names.Length!=names.Distinct(StringComparer.Ordinal).Count()||required.Any(k=>!names.Contains(k,StringComparer.Ordinal))||
            names.Any(k=>!required.Contains(k,StringComparer.Ordinal)&&!optional.Contains(k,StringComparer.Ordinal)))throw Invalid();
    }
    static string Text(JsonElement input,string field)
    {var value=input.GetProperty(field);if(value.ValueKind!=JsonValueKind.String)throw Invalid();return value.GetString()!;}
    static string Key(JsonElement input,string field)=>GasOperationText.Key(Text(input,field),"gas_transport_route_input");
    static string Code(JsonElement input,string field)
    {var value=Text(input,field);if(value.Length is <1 or >64||value[0] is <'a' or >'z'||value.Any(c=>!(c is >='a' and <='z' or >='0' and <='9' or '_' or '.' or '-')))throw Invalid();return value;}
    static Guid? Id(JsonElement input,string field,bool nullable=false)
    {var value=input.GetProperty(field);if(nullable&&value.ValueKind==JsonValueKind.Null)return null;if(value.ValueKind!=JsonValueKind.String||!Guid.TryParse(value.GetString(),out var id)||id==Guid.Empty)throw Invalid();return id;}
    static void OptionalIdentity(JsonElement row,string field,Guid id)
    {if(row.TryGetProperty(field,out _)&&Id(row,field)!=id)throw Invalid();}
    static int Integer(JsonElement input,string field,int minimum)
    {var value=input.GetProperty(field);return value.ValueKind==JsonValueKind.Number&&value.TryGetInt32(out var n)&&n>=minimum?n:throw Invalid();}
    static MarketDataException Invalid()=>new("gas_transport_route_input","Supply complete explicit immutable route terms, canonical exact fractions, source selections, membership heads and evidence; remove unknown fields.");
}
