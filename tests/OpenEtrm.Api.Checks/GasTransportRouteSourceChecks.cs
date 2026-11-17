using System.Text.Json;
using System.Text.Json.Nodes;
using OpenEtrm.Api;
using OpenEtrm.MarketData;

internal static class GasTransportRouteSourceChecks
{
    internal static void Run()
    {
        var route=Guid.Parse("74444444-4444-4444-4444-444444444444");var definition=Guid.Parse("75555555-5555-5555-5555-555555555555");
        var context=new ScheduleContext("2032-03-14",12,"2032-03-15T03:00:00Z",Guid.NewGuid());
        var at=DateTimeOffset.Parse("2032-03-14T23:59:59.999999Z");
        var facts=JsonSerializer.SerializeToNode(new{contextId=context.ContextId,asOf=at,knownAt=context.KnownAt,hasFutureHeads=false,
            route=new{id=route,definition_id=definition},definition=new{id=definition,route_id=route}})!.AsObject();
        JsonElement E(JsonObject value)=>JsonSerializer.SerializeToElement(value);
        GasTransportRouteStore.RequireSource(E(facts),context,at,route);
        var wrong=JsonNode.Parse(facts.ToJsonString())!.AsObject();wrong["route"]!["id"]=Guid.NewGuid();
        Reject(()=>GasTransportRouteStore.RequireSource(E(wrong),context,at,route),"gas_transport_route_source");
        var lineage=JsonNode.Parse(facts.ToJsonString())!.AsObject();lineage["definition"]!["id"]=Guid.NewGuid();
        Reject(()=>GasTransportRouteStore.RequireSource(E(lineage),context,at,route),"gas_transport_route_source");
        var cut=JsonNode.Parse(facts.ToJsonString())!.AsObject();cut["knownAt"]="2032-03-15T04:00:00Z";
        Reject(()=>GasTransportRouteStore.RequireSource(E(cut),context,at,route),"gas_transport_route_context");
        var missing=JsonNode.Parse(facts.ToJsonString())!.AsObject();missing.Remove("hasFutureHeads");
        Reject(()=>GasTransportRouteStore.RequireSource(E(missing),context,at,route),"gas_transport_route_source");
        Reject(()=>GasTransportRouteStore.Permission(default),"gas_transport_route_input");
        foreach(var pair in new[]{("define","ConfigureGasServices"),("plan","RecordGasOperations"),("release","RecordGasOperations")})
            if(GasTransportRouteStore.Permission(JsonSerializer.SerializeToElement(new{kind=pair.Item1}))!=pair.Item2)
                throw new Exception("Route definition, adoption and own release lost their explicit operation authority.");
        var until=DateTimeOffset.Parse("2032-03-15T04:00:00.123456Z");
        var admission=GasTransportRouteStore.StatusAdmission(route,JsonSerializer.SerializeToElement(new{kind="release",authorityUntil=until}));
        if(admission.Catalog!=route||admission.Kind!="release"||admission.Until!=until)throw new Exception("Route recovery renewed its original conditional deadline.");
        Reject(()=>GasTransportRouteStore.StatusAdmission(route,JsonSerializer.SerializeToElement(new{kind="release"})),"gas_transport_route_source");
        Reject(()=>GasTransportRouteStore.StatusAdmission(route,JsonSerializer.SerializeToElement(new{kind="unknown",authorityUntil=until})),"gas_transport_route_source");
        var absent=GasTransportRouteStore.StatusAdmission(route,JsonSerializer.SerializeToElement(new{found=false,authorityUntil=until}));
        if(absent.Catalog!=route||absent.Kind is not null||absent.Until!=until)
            throw new Exception("An absent route operation must retain its original base read deadline without requiring write authority.");
        Reject(()=>GasTransportRouteStore.StatusAdmission(route,JsonSerializer.SerializeToElement(new{found=false})),"gas_transport_route_source");
        var wire=JsonSerializer.SerializeToElement(new{context=new FacilityQuantityContext("2032-03-14","current",null,FacilityQuantityStore.Convention),ownerId="synthetic actor",
            operationId=Guid.NewGuid(),effectiveAt="2032-03-14T08:00:00.000001Z",command=new{kind="release"}},GasOwnedOperations.Json);
        var parsed=wire.Deserialize<GasTransportRouteWriteRequest>(GasOwnedOperations.Json)!;
        if(parsed.EffectiveAt!="2032-03-14T08:00:00.000001Z")throw new Exception("The route boundary truncated the original effective instant.");
        var unknown=JsonNode.Parse(wire.GetRawText())!.AsObject();unknown["assumeAvailable"]=true;
        try{JsonSerializer.Deserialize<GasTransportRouteWriteRequest>(unknown,GasOwnedOperations.Json);throw new Exception("Unknown route operation fields were accepted.");}catch(JsonException){}
        Console.WriteLine("PASS route operation source: exact route/definition/context lineage, mandatory future proof, explicit permissions and original recovery deadline");
    }
    static void Reject(Action action,string code)
    {try{action();}catch(MarketDataException error)when(error.Code==code){return;}throw new Exception("Expected "+code);}
}
