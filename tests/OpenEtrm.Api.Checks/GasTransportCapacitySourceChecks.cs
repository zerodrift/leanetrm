using System.Text.Json;
using System.Text.Json.Nodes;
using OpenEtrm.Api;
using OpenEtrm.MarketData;

internal static class GasTransportCapacitySourceChecks
{
    internal static void Run()
    {
        var id=Guid.Parse("73333333-3333-3333-3333-333333333333");
        var context=new ScheduleContext("2032-03-14",12,"2032-03-15T03:00:00Z",Guid.NewGuid());
        var at=DateTimeOffset.Parse("2032-03-14T23:59:59.999999Z");
        var facts=JsonSerializer.SerializeToNode(new{contextId=context.ContextId,asOf=at,knownAt=context.KnownAt,hasFutureHeads=false,
            entitlement=new{id},version=new{id=Guid.NewGuid(),entitlement_id=id}})!.AsObject();
        JsonElement E(JsonObject value)=>JsonSerializer.SerializeToElement(value);
        GasTransportCapacityStore.RequireSource(E(facts),context,at,id);
        var wrong=JsonNode.Parse(facts.ToJsonString())!.AsObject();wrong["entitlement"]!["id"]=Guid.NewGuid();
        Reject(()=>GasTransportCapacityStore.RequireSource(E(wrong),context,at,id),"gas_transport_capacity_source");
        var cut=JsonNode.Parse(facts.ToJsonString())!.AsObject();cut["knownAt"]="2032-03-15T04:00:00Z";
        Reject(()=>GasTransportCapacityStore.RequireSource(E(cut),context,at,id),"gas_transport_capacity_context");
        var missing=JsonNode.Parse(facts.ToJsonString())!.AsObject();missing.Remove("hasFutureHeads");
        Reject(()=>GasTransportCapacityStore.RequireSource(E(missing),context,at,id),"gas_transport_capacity_source");
        Reject(()=>GasTransportCapacityStore.Permission(default),"gas_transport_capacity_input");
        if(GasTransportCapacityStore.Permission(JsonSerializer.SerializeToElement(new{kind="define"}))!="ConfigureGasServices"||
            GasTransportCapacityStore.Permission(JsonSerializer.SerializeToElement(new{kind="plan"}))!="RecordGasOperations")
            throw new Exception("Capacity configuration and chosen-plan permissions became interchangeable.");
        var originalUntil=DateTimeOffset.Parse("2032-03-15T04:00:00.123456Z");
        var recovery=JsonSerializer.SerializeToElement(new{kind="plan",authorityUntil=originalUntil});
        var admission=GasTransportCapacityStore.StatusAdmission(id,recovery);
        if(admission.Catalog!=id||admission.Kind!="plan"||admission.Until!=originalUntil)
            throw new Exception("Status recovery renewed the original native kind deadline before managed final publication.");
        var absent=GasTransportCapacityStore.StatusAdmission(id,JsonSerializer.SerializeToElement(new{found=false,authorityUntil=originalUntil}));
        if(absent.Catalog!=id||absent.Kind is not null||absent.Until!=originalUntil)
            throw new Exception("An absent receipt must retain its original base read deadline through owner lookup and commit, without requiring a write grant.");
        Reject(()=>GasTransportCapacityStore.StatusAdmission(id,JsonSerializer.SerializeToElement(new{found=false})),"gas_transport_capacity_source");
        Reject(()=>GasTransportCapacityStore.StatusAdmission(id,JsonSerializer.SerializeToElement(new{found="false",authorityUntil=originalUntil})),"gas_transport_capacity_source");
        Reject(()=>GasTransportCapacityStore.StatusAdmission(id,JsonSerializer.SerializeToElement(new{kind="plan"})),"gas_transport_capacity_source");
        Reject(()=>GasTransportCapacityStore.StatusAdmission(id,JsonSerializer.SerializeToElement(new{kind="unknown",authorityUntil=originalUntil})),"gas_transport_capacity_source");
        var request=new{context=new FacilityQuantityContext("2032-03-14","current",null,FacilityQuantityStore.Convention),ownerId="synthetic actor",operationId=Guid.NewGuid(),
            effectiveAt="2032-03-14T08:00:00.000001Z",command=new{kind="plan"}};
        var wire=JsonSerializer.SerializeToElement(request,GasOwnedOperations.Json);
        var parsed=wire.Deserialize<GasTransportCapacityWriteRequest>(GasOwnedOperations.Json)!;
        if(parsed.EffectiveAt!=request.effectiveAt)throw new Exception("The operation boundary truncated the original microsecond input.");
        var unknown=JsonNode.Parse(wire.GetRawText())!.AsObject();unknown["assumeFeasible"]=true;
        try{JsonSerializer.Deserialize<GasTransportCapacityWriteRequest>(unknown,GasOwnedOperations.Json);throw new Exception("Unknown operation fields were accepted.");}catch(JsonException){}
        Console.WriteLine("PASS Transport source boundary: exact owner/version/cut, mandatory future-source proof, independent permissions and original wire precision");
    }
    static void Reject(Action action,string code)
    {try{action();}catch(MarketDataException error)when(error.Code==code){return;}throw new Exception("Expected "+code);}
}
