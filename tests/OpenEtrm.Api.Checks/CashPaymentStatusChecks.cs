using System.Text.Json;
using OpenEtrm.Api;
using OpenEtrm.MarketData;
using OpenEtrm.MarketData.Trading;

internal static class CashPaymentStatusChecks
{
    internal static void Run()
    {
        var request=new RecordCashPaymentRequest(Guid.NewGuid(),"current",Guid.NewGuid(),Guid.NewGuid(),Guid.NewGuid(),Guid.NewGuid(),0,
            "2032-01-15T10:00:00+00:00","-5.000",RecordedCashValuation.Policy);
        var payload=CashPaymentStore.Payload(request);
        var expected=JsonSerializer.SerializeToElement(new{operationId=request.OperationId,allocationId=request.AllocationId,tradeId=request.TradeId,
            bindingId=request.BindingId,expectedRevision=0,paidAt=request.PaidAt,amount=request.Amount,policy=request.Policy});
        if(!JsonElement.DeepEquals(payload,expected)||payload.TryGetProperty("snapshotId",out _)||payload.TryGetProperty("knowledgeMode",out _))
            throw new Exception("Record and readonly receipt recovery must share the exact existing native financial request; navigation frames do not define cash facts.");
        try{CashPaymentStore.Payload(request with{KnowledgeMode="retained"});throw new Exception("Cash recording and recovery require the original explicit current-mode request.");}
        catch(MarketDataException error)when(error.Code=="historical_read_only"){}
        if(!JsonElement.DeepEquals(payload,CashPaymentStore.Payload(request with{SnapshotId=Guid.NewGuid()})))
            throw new Exception("A readonly first-receipt lookup must not depend on resolving an evicted navigation frame.");
        Console.WriteLine("PASS ordinary cash recovery payload: identical native financial identity, explicit current mode and frame-independent receipt lookup");
    }
}
