using System.Text.Json;
using System.Text.Json.Nodes;
using OpenEtrm.Api;
using OpenEtrm.MarketData;

internal static class GasTransportFinancialCommandChecks
{
    internal static void Run()
    {
        var trade=Guid.NewGuid();var state=Guid.NewGuid();var capacity=Guid.NewGuid();
        var adopt=JsonSerializer.SerializeToElement(new{kind="adopt_capacity_version",tradeId=trade,
            expectedFinancialStateId=state,capacityVersionId=capacity,evidence=" Reviewed replacement "});
        var normalized=GasTransportFinancialCommands.Normalize(adopt);
        if(normalized.Id("tradeId")!=trade||normalized.Id("expectedFinancialStateId")!=state||
            normalized.Id("capacityVersionId")!=capacity||normalized.Text("evidence")!="Reviewed replacement")
            throw new Exception("Financial capacity adoption lost its exact state or evidence.");
        var mark=JsonSerializer.SerializeToNode(new{kind="adopt_rights_mark",tradeId=trade,financialStateId=state,
            expectedPreviousMarkId=(Guid?)null,valuationAt="2032-03-14T08:00:00.000001Z",
            marketDefinitionRevisionId=Guid.NewGuid(),curveCode="transport_rights",contractId=Guid.NewGuid(),
            seriesId=Guid.NewGuid(),observationId=Guid.NewGuid(),source="direct",quote="package",evidence="Synthetic direct mark"})!.AsObject();
        var result=GasTransportFinancialCommands.Normalize(JsonSerializer.SerializeToElement(mark));
        if(result.GetProperty("expectedPreviousMarkId").ValueKind!=JsonValueKind.Null||
            result.Text("valuationAt")!="2032-03-14T08:00:00.000001Z")
            throw new Exception("Mark adoption lost an explicit absent predecessor or exact instant.");
        foreach(var field in new[]{"amount","gasQuantity","businessKey","perspective","capacityGeneration"})
        {
            var extra=JsonNode.Parse(mark.ToJsonString())!.AsObject();extra[field]="1";
            Reject(()=>GasTransportFinancialCommands.Normalize(JsonSerializer.SerializeToElement(extra)));
        }
        var missing=JsonNode.Parse(mark.ToJsonString())!.AsObject();missing.Remove("expectedPreviousMarkId");
        Reject(()=>GasTransportFinancialCommands.Normalize(JsonSerializer.SerializeToElement(missing)));
        var clock=JsonNode.Parse(mark.ToJsonString())!.AsObject();clock["valuationAt"]="2032-03-14T03:00:00-05:00";
        Reject(()=>GasTransportFinancialCommands.Normalize(JsonSerializer.SerializeToElement(clock)));
        var duplicate=JsonDocument.Parse("{\"kind\":\"expire\",\"tradeId\":\""+trade+"\",\"tradeId\":\""+Guid.NewGuid()+
            "\",\"expectedFinancialStateId\":\""+state+"\",\"evidence\":\"Synthetic expiry\"}");
        Reject(()=>GasTransportFinancialCommands.Normalize(duplicate.RootElement));
        var expire=GasTransportFinancialCommands.Normalize(JsonSerializer.SerializeToElement(new{
            kind="expire",tradeId=trade,expectedFinancialStateId=state,policyId=Guid.NewGuid(),policyRevision=1,evidence="Synthetic expiry"}));
        if(expire.Id("expectedFinancialStateId")!=state||expire.Text("kind")!="expire")
            throw new Exception("Expiry did not retain the explicit financial-state target.");
        Reject(()=>GasTransportFinancialCommands.Normalize(JsonSerializer.SerializeToElement(new{
            kind="expire",tradeId=Guid.Empty,expectedFinancialStateId=state,evidence="Synthetic expiry"})));
        Reject(()=>GasTransportFinancialCommands.Normalize(JsonSerializer.SerializeToElement(new{
            kind="expire",tradeId=trade,expectedFinancialStateId=state,evidence="No selected lifecycle policy"})));
        Reject(()=>GasTransportFinancialCommands.Normalize(JsonSerializer.SerializeToElement(new{
            kind="expire",tradeId=trade,expectedFinancialStateId=state,policyId=Guid.NewGuid(),policyRevision=0,evidence="Unsupported policy revision"})));
        Reject(()=>GasTransportFinancialCommands.Normalize(JsonSerializer.SerializeToElement(new{
            kind="reopen",tradeId=trade,expectedFinancialStateId=state,evidence="Unsupported"})));
        Console.WriteLine("PASS financial Transport commands: exact state/source selection, explicit predecessor, microsecond clock and closed operation fields");
    }
    static void Reject(Action action)
    {try{action();}catch(MarketDataException error)when(error.Code=="gas_transport_financial_input"){return;}throw new Exception("Expected financial Transport input refusal.");}
}
