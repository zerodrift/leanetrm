using System.Text.Json;
using OpenEtrm.Api;
using OpenEtrm.MarketData;
internal static class CrudeLifecycleChecks
{
 internal static void Run()
 {
  var body=JsonSerializer.SerializeToElement(new{context=new{snapshotId=Guid.NewGuid(),convention="physical_crude_fixed_volume_v1",knowledgeMode="current"},tradeId=Guid.NewGuid(),expectedRevision=1,expectedVersion=0,operationId=Guid.NewGuid(),businessKey="synthetic_receipt",profile="physical_crude_receipt_cash_v1",effectiveAt="2040-01-02T00:00:00Z",evidence="Synthetic certified volume",quantity="1",titleTransferred=true,disposition="owned_inventory",saleTradeId=(Guid?)null,saleRevision=(int?)null});
  CrudeLifecycleStore.Validate(body,"receipt");
  foreach(var bad in new[]{body.GetRawText().Replace("\"titleTransferred\":true","\"titleTransferred\":false"),body.GetRawText().Replace("\"quantity\":\"1\"","\"quantity\":\"0\""),body.GetRawText().Replace("\"quantity\":\"1\"","\"quantity\":\"1\",\"inferredTitle\":true")})
  {
   try{CrudeLifecycleStore.Validate(JsonDocument.Parse(bad).RootElement,"receipt");throw new Exception("Unsupported receipt fact accepted");}
   catch(MarketDataException){}
  }
  foreach(var field in new[]{"context","operationId","expectedVersion","quantity","titleTransferred","saleRevision"})
  {
   var node=System.Text.Json.Nodes.JsonNode.Parse(body.GetRawText())!.AsObject();node.Remove(field);
   try{CrudeLifecycleStore.Validate(JsonSerializer.SerializeToElement(node),"receipt");throw new Exception("Incomplete receipt accepted");}catch(MarketDataException){}
  }
  foreach(var malformed in new[]{"\"not_an_integer\"","null","{}"})
  {
   var invalid=body.GetRawText().Replace("\"expectedVersion\":0","\"expectedVersion\":"+malformed);
   try{CrudeLifecycleStore.Validate(JsonDocument.Parse(invalid).RootElement,"receipt");throw new Exception("Malformed version accepted");}catch(MarketDataException){}
  }
  var reversal=System.Text.Json.Nodes.JsonNode.Parse(body.GetRawText())!.AsObject();
  foreach(var key in new[]{"quantity","titleTransferred","disposition","saleTradeId","saleRevision"})reversal.Remove(key);
  reversal["paymentId"]=Guid.NewGuid().ToString();reversal["reversalPolicy"]="crude_record_correction_v1";
  CrudeLifecycleStore.Validate(JsonSerializer.SerializeToElement(reversal),"payment_reversal");
  foreach(var key in new[]{"paymentId","reversalPolicy"})
  {
   var invalid=reversal.DeepClone().AsObject();invalid.Remove(key);
   try{CrudeLifecycleStore.Validate(JsonSerializer.SerializeToElement(invalid),"payment_reversal");throw new Exception("Incomplete record reversal accepted");}catch(MarketDataException){}
  }
  reversal["amount"]="1";
  try{CrudeLifecycleStore.Validate(JsonSerializer.SerializeToElement(reversal),"payment_reversal");throw new Exception("Partial or bank-refund amount accepted");}catch(MarketDataException){}
  var allocation=System.Text.Json.Nodes.JsonNode.Parse(body.GetRawText())!.AsObject();
  allocation.Remove("titleTransferred");allocation.Remove("disposition");allocation["receiptId"]=Guid.NewGuid().ToString();allocation["saleTradeId"]=Guid.NewGuid().ToString();allocation["saleRevision"]=1;
  CrudeLifecycleStore.Validate(JsonSerializer.SerializeToElement(allocation),"allocation");
  var dispatch=allocation.DeepClone().AsObject();dispatch.Remove("saleTradeId");dispatch.Remove("saleRevision");dispatch["titleTransferred"]=true;
  CrudeLifecycleStore.Validate(JsonSerializer.SerializeToElement(dispatch),"dispatch");
  dispatch["allocationId"]=null;
  try{CrudeLifecycleStore.Validate(JsonSerializer.SerializeToElement(dispatch),"dispatch");throw new Exception("Explicit null allocation accepted instead of an absent legacy source or exact allocation identity");}catch(MarketDataException){}
  var salesInvoice=allocation.DeepClone().AsObject();
  foreach(var key in new[]{"quantity","receiptId","saleTradeId","saleRevision"})salesInvoice.Remove(key);
  salesInvoice["profile"]="physical_crude_sale_cash_v1";salesInvoice["dispatchId"]=Guid.NewGuid().ToString();salesInvoice["component"]="consideration";salesInvoice["decimalPlaces"]=2;salesInvoice["roundingPolicy"]="crude_cumulative_receipt_cash_v1";
  CrudeLifecycleStore.Validate(JsonSerializer.SerializeToElement(salesInvoice),"invoice");
  salesInvoice["receiptId"]=Guid.NewGuid().ToString();
  try{CrudeLifecycleStore.Validate(JsonSerializer.SerializeToElement(salesInvoice),"invoice");throw new Exception("Sales invoice accepted ambiguous source kinds");}catch(MarketDataException){}
  Console.WriteLine("PASS crude lifecycle command boundary: explicit transferred title, positive certified volume and closed command fields");
 }
}
