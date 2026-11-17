using System.Text.Json;
using System.Text.Json.Nodes;
using OpenEtrm.Api;
using OpenEtrm.MarketData;

internal static class GasTransportFinancialSourceChecks
{
    internal static void Run()
    {
        var catalog=Guid.NewGuid();var trade=Guid.NewGuid();var until=DateTimeOffset.Parse("2032-03-16T00:00:00.000001Z");
        var context=new LifecycleReadContext(Guid.NewGuid(),DateTimeOffset.Parse("2032-03-14T08:00:00.000001Z"),new(2032,3,14));
        var known=DateTimeOffset.Parse("2032-03-15T00:00:00Z");
        var source=JsonSerializer.SerializeToNode(new{implementation="physical_gas_transport_direct_rights_v1",tradeId=trade,
            instrumentId=Guid.NewGuid(),revision=1,bookId=Guid.NewGuid(),contextId=context.ContextId,asOf=context.AsOf,
            effectiveOn="2032-03-14",knownAt=known,authorityUntil=until})!.AsObject();
        JsonElement E(JsonObject row)=>JsonSerializer.SerializeToElement(row);
        GasTransportFinancialStore.RequireSource(E(source),context,trade,known);
        foreach(var field in new[]{"tradeId","contextId","instrumentId","bookId"})
        {
            var wrong=JsonNode.Parse(source.ToJsonString())!.AsObject();wrong[field]=field is "bookId" or "instrumentId"?Guid.Empty:Guid.NewGuid();
            Reject(()=>GasTransportFinancialStore.RequireSource(E(wrong),context,trade,known));
        }
        foreach(var field in new[]{"asOf","knownAt"})
        {
            var wrong=JsonNode.Parse(source.ToJsonString())!.AsObject();wrong[field]="2032-03-14T09:00:00.000001Z";
            Reject(()=>GasTransportFinancialStore.RequireSource(E(wrong),context,trade,known));
        }
        var day=JsonNode.Parse(source.ToJsonString())!.AsObject();day["effectiveOn"]="2032-03-15";
        Reject(()=>GasTransportFinancialStore.RequireSource(E(day),context,trade,known));
        var absent=GasTransportFinancialStore.StatusAdmission(catalog,JsonSerializer.SerializeToElement(new{found=false,authorityUntil=until}));
        if(absent.Kind is not null||absent.Trade is not null||absent.Until!=until)
            throw new Exception("An absent financial receipt gained conditional write authority or renewed its original deadline.");
        foreach(var kind in new[]{"adopt_capacity_version","adopt_rights_mark","expire"})
        {
            var saved=GasTransportFinancialStore.StatusAdmission(catalog,JsonSerializer.SerializeToElement(new{found=true,kind,tradeId=trade,authorityUntil=until}));
            if(saved.Catalog!=catalog||saved.Kind!=kind||saved.Trade!=trade||saved.Until!=until)
                throw new Exception("Financial recovery lost its exact target, operation authority or original deadline.");
        }
        Reject(()=>GasTransportFinancialStore.StatusAdmission(catalog,JsonSerializer.SerializeToElement(new{found=true,kind="reopen",tradeId=trade,authorityUntil=until})));
        Reject(()=>GasTransportFinancialStore.StatusAdmission(catalog,JsonSerializer.SerializeToElement(new{found=true,kind="expire",tradeId=trade})));
        Console.WriteLine("PASS financial Transport source: exact native identity and temporal coordinates, original conditional recovery admission and absent receipt boundary");
    }
    static void Reject(Action action)
    {try{action();}catch(MarketDataException error)when(error.Code=="gas_transport_financial_source"){return;}throw new Exception("Expected financial Transport source refusal.");}
}
