using System.Text.Json;
using OpenEtrm.Api;
using OpenEtrm.MarketData;

internal static class DeliveryWorkbenchChecks
{
    static JsonElement Json(object value)=>JsonSerializer.SerializeToElement(value,GasOwnedOperations.Json);
    static readonly Guid Route=Guid.Parse("f1000000-0000-0000-0000-000000000001"),Plan=Guid.Parse("f1000000-0000-0000-0000-000000000002"),Node=Guid.Parse("f1000000-0000-0000-0000-000000000003"),Target=Guid.Parse("f1000000-0000-0000-0000-000000000004");
    internal static void Run()
    {
        var body=Json(new{kind="allocate",scopeId=Route,routePlanId=Plan,expectedPreviousId=(Guid?)null,expectedRouteStateVersion=2,policy="exact_terminal_native_interval_v1",evidence="Synthetic reviewed assignment",allocations=new[]{new{nodeId=Node,obligationId=Target,tradeRevision=1,day="2032-01-03",quantity="60.00"}}});
        var canonical=DeliveryWorkbenchCommands.Normalize(body);
        if(canonical.GetProperty("allocations")[0].GetProperty("quantity").GetString()!="60")throw new Exception("One exact assignment must keep its native target and normalize decimal spelling.");
        var edit=System.Text.Json.Nodes.JsonNode.Parse(body.GetRawText())!.AsObject();edit["allocations"]![0]!["quantity"]="0";
        Reject(JsonSerializer.SerializeToElement(edit));
        edit=System.Text.Json.Nodes.JsonNode.Parse(body.GetRawText())!.AsObject();edit["allocations"]!.AsArray().Add(edit["allocations"]![0]!.DeepClone());Reject(JsonSerializer.SerializeToElement(edit));
        edit=System.Text.Json.Nodes.JsonNode.Parse(body.GetRawText())!.AsObject();edit["extra"]="unsupported";Reject(JsonSerializer.SerializeToElement(edit));
        edit=System.Text.Json.Nodes.JsonNode.Parse(body.GetRawText())!.AsObject();edit["kind"]="release";Reject(JsonSerializer.SerializeToElement(edit));
        edit=System.Text.Json.Nodes.JsonNode.Parse(body.GetRawText())!.AsObject();edit["policy"]="inferred";Reject(JsonSerializer.SerializeToElement(edit));
        Console.WriteLine("PASS delivery workbench commands: closed explicit assignments, exact decimals, unique native cells and explicit release");
    }
    static void Reject(JsonElement body)
    {try{DeliveryWorkbenchCommands.Normalize(body);}catch(MarketDataException e)when(e.Code=="delivery_workbench_input"){return;}throw new Exception("An incomplete, duplicate or inferred delivery assignment must refuse.");}
}
