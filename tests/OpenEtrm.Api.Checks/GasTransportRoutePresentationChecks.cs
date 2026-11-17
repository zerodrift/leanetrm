using System.Text.Json;
using OpenEtrm.Api;

internal static class GasTransportRoutePresentationChecks
{
    internal static void Run()
    {
        var define=GasTransportRouteCommands.Normalize(GasTransportRouteAdapterChecks.Operation,GasTransportRouteAdapterChecks.E(GasTransportRouteAdapterChecks.Define()));
        var facts=GasTransportRouteAdapterChecks.Facts(define);
        var plan=GasTransportRouteCommands.Normalize(GasTransportRouteAdapterChecks.Id(20),GasTransportRouteAdapterChecks.E(GasTransportRouteAdapterChecks.Plan(define)));
        var assessment=GasTransportRouteAssessments.Plan(GasTransportRouteAdapterChecks.Id(20),GasTransportRouteAdapterChecks.E(facts),plan);
        var tables=JsonSerializer.SerializeToElement(GasTransportRoutePresentation.Tables(assessment,GasTransportRouteAdapterChecks.E(facts),plan));
        if(tables.GetArrayLength()!=3)throw new Exception("Review needs separate segment quantities, delivery timing/rates and intermediate-node balances.");
        var quantities=tables[0].GetProperty("rows").EnumerateArray().ToArray();
        var first=quantities.Single(row=>row[0].GetString()=="ah");
        if(first[1].GetString()!="100"||first[2].GetString()!="2"||first[3].GetString()!="0"||first[4].GetString()!="98"||first[5].GetString()!="98")
            throw new Exception("Review must retain the independent100 receipt, fuel2, loss0 and98 delivery exactly.");
        if(quantities.Length!=7||quantities.Single(row=>row[0].GetString()=="hm")[1].GetString()!="0")
            throw new Exception("The explicit idle segment must remain visible alongside all seven declared segments.");
        var balances=tables[2].GetProperty("rows").EnumerateArray().ToArray();
        var hub=balances.Single(row=>row[0].GetString()=="h");
        if(balances.Length!=4||hub[4].GetString()!="98"||hub[5].GetString()!="98"||hub[6].GetString()!="0"||hub[7].GetString()!="ah"||hub[8].GetString()!="hb, hc, hm")
            throw new Exception("A reader must see exact98/hour at the split and its named incoming/outgoing contributors, including idle flow.");
        foreach(var section in new[]{"nodes","segments"})
            foreach(var member in facts[section]!.AsArray())
                if(tables.GetRawText().Contains(member!["id"]!.GetValue<Guid>().ToString(),StringComparison.OrdinalIgnoreCase))
                    throw new Exception("Opaque technical graph identities must not replace their saved human references in review.");
        if(!tables[0].GetProperty("label").GetString()!.Contains("synthetic chosen route",StringComparison.Ordinal))
            throw new Exception("Every independently assessed plan needs its own business reference on its tables.");
        if(GasTransportRoutePresentation.Tables(null,GasTransportRouteAdapterChecks.E(facts),null).Length!=0)
            throw new Exception("Unavailable or released plans must not fabricate successful zero-flow tables.");
        var broken=GasTransportRouteAdapterChecks.Facts(define);broken["nodes"]![0]!.AsObject().Remove("node_key");
        GasTransportRouteAdapterChecks.Reject(()=>GasTransportRoutePresentation.Tables(assessment,GasTransportRouteAdapterChecks.E(broken),plan),"gas_transport_route_source");
        GasTransportRouteAdapterChecks.Reject(()=>GasTransportRoutePresentation.Tables(assessment,null,plan),"gas_transport_route_source");
        var graph=JsonSerializer.SerializeToElement(GasTransportRoutePresentation.GraphTables(GasTransportRouteAdapterChecks.E(facts)));
        if(graph.GetArrayLength()!=2||graph[0].GetProperty("rows").GetArrayLength()!=6||graph[1].GetProperty("rows").GetArrayLength()!=7)
            throw new Exception("A saved route without any adopted plan must still expose its six nodes and seven immutable segment terms.");
        var ah=graph[1].GetProperty("rows").EnumerateArray().Single(row=>row[0].GetString()=="ah");
        if(ah[1].GetString()!="a"||ah[2].GetString()!="h"||ah[5].GetString()!="1/50"||ah[6].GetString()!="0/1"||ah[7].GetString()!="3600000000")
            throw new Exception("The immutable graph must display saved fuel, loss and elapsed microseconds before adoption.");
        var claims=facts["sourceCapacities"]!.AsArray().SelectMany(c=>c!["claims"]!.AsArray()).ToDictionary(c=>c!["id"]!.GetValue<Guid>(),c=>c!);
        var accepted=GasTransportRouteAdapterChecks.Obj(new{id=GasTransportRouteAdapterChecks.Id(20),business_key="synthetic chosen route",kind="plan",
            assessment=GasTransportRouteAssessments.Witness(assessment),flows=plan.GetProperty("flows").EnumerateArray().Select(f=>new{
                id=f.Id("flowId"),segment_id=f.Id("segmentId"),claim_id=f.Id("claimId"),claim=GasTransportRouteAdapterChecks.E(claims[f.Id("claimId")])})});
        var historical=JsonSerializer.SerializeToElement(GasTransportRoutePresentation.RecordedTables(GasTransportRouteAdapterChecks.E(facts),GasTransportRouteAdapterChecks.E(accepted)));
        if(historical.GetArrayLength()<2||historical.EnumerateArray().Any(t=>!t.GetProperty("label").GetString()!.Contains("historical evidence",StringComparison.Ordinal)))
            throw new Exception("A stale plan must retain its accepted source and assessment tables with an explicit historical-evidence label.");
        var recorded=historical[0].GetProperty("rows").EnumerateArray().Single(row=>row[0].GetString()=="ah");
        if(recorded[1].GetString()!="100"||recorded[2].GetString()!="2"||recorded[4].GetString()!="98")
            throw new Exception("Unavailable current assessment must not erase the independently accepted original receipt/fuel/delivery evidence.");
    }
}
