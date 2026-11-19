using System.Text.Json;
using System.Text.Json.Nodes;
using Npgsql;
using OpenEtrm.MarketData;

namespace OpenEtrm.Api;

public sealed partial class TradingBlotter
{
 // Reuse each product's ordinary Holding projection, with selection pushed into
 // its metadata read before it loads schedules or prepares unit valuation.
 internal async Task<JsonObject> Position(NpgsqlConnection connection,PreviewData data,InputFrame frame,TradePositionSelection selected,CancellationToken ct)
 {
  JsonObject[] rows;
  switch(selected.InstrumentType)
  {
   case "future":
    var read=JsonSerializer.SerializeToNode(await futures.Position(connection,data,frame,selected,ct),Json)!.AsObject();
    rows=read["holdings"]!.AsArray().Select(n=>n!.DeepClone().AsObject()).ToArray();
    break;
   case "financial_swap" or "heat_rate":
    rows=[..(await swaps.Holdings(connection,data,frame,[],[],ct,selected)).Rows];
    break;
   case "ppa":
    rows=(await PpaBlotter.Read(connection,database,data,frame,[],[],ct,selected)).ToArray();
    break;
   case GasParkLoanTicket.Product:
    var parkLoan=await GasParkLoanBlotter.Run(connection,database,frame,[],[],ct,selected);
    var parkLoanRows=new List<JsonObject>();await foreach(var unit in parkLoan.Units(ct))parkLoanRows.AddRange(unit);
    rows=[..parkLoanRows];
    break;
   case GasParkCustodyTicket.Product:
    var custody=await GasParkCustodyBlotter.Run(connection,database,frame,[],[],ct,selected);
    var custodyRows=new List<JsonObject>();await foreach(var unit in custody.Units(ct))custodyRows.AddRange(unit);
    rows=[..custodyRows];
    break;
   case GasTransportFinancialTicket.Product:
    var transport=await GasTransportFinancialBlotter.Run(connection,database,frame,[],[],ct,selected);
    var transportRows=new List<JsonObject>();await foreach(var unit in transport.Units(ct))transportRows.AddRange(unit);
    rows=[..transportRows];
    break;
   case "physical_gas":
    var gas=await GasBlotter.Run(connection,database,frame,[],[],ct,selected);
    var gasRows=new List<JsonObject>();await foreach(var unit in gas.Units(ct))gasRows.AddRange(unit);
    rows=[..gasRows];
    break;
   case "physical_lng":
    var lng=await LngBlotter.Run(connection,database,frame,[],[],ct,selected);
    var lngRows=new List<JsonObject>();await foreach(var unit in lng.Units(ct))lngRows.AddRange(unit);
    rows=[..lngRows];
    break;
   case "physical_crude":
    var crude=await CrudeBlotter.Run(connection,database,frame,[],[],ct,selected);
    var crudeRows=new List<JsonObject>();await foreach(var unit in crude.Units(ct))crudeRows.AddRange(unit);
    rows=[..crudeRows];
    break;
   case FuturesOptionPositions.Type:
    var options=await FuturesOptionBlotter.Run(connection,database,data,frame,[],[],ct,selected);
    var optionRows=new List<JsonObject>();await foreach(var unit in options.Units(ct))optionRows.AddRange(unit);
    rows=[..optionRows];
    break;
   case OperatingPositions.Type:
    var operating=await OperatingBlotter.Run(connection,database,frame,[],[],ct,selected);
    var operatingRows=new List<JsonObject>();await foreach(var unit in operating.Units(ct))operatingRows.AddRange(unit);
    rows=[..operatingRows];
    break;
   case RecPositions.Type:
    var rec=await RecBlotter.Run(connection,database,frame,[],[],ct,selected);
    var recRows=new List<JsonObject>();await foreach(var unit in rec.Units(ct))recRows.AddRange(unit);
    rows=[..recRows];
    break;
   case HeatRateCallPositions.Type:
    var heatRateCall=await HeatRateCallBlotter.Run(connection,database,frame,[],[],ct,selected);
    var heatRateCallRows=new List<JsonObject>();await foreach(var unit in heatRateCall.Units(ct))heatRateCallRows.AddRange(unit);
    rows=[..heatRateCallRows];
    break;
   case GasStoragePositions.Type:
    var gasStorage=await GasStorageBlotter.Run(connection,database,frame,[],[],ct,selected);
    var gasStorageRows=new List<JsonObject>();await foreach(var unit in gasStorage.Units(ct))gasStorageRows.AddRange(unit);
    rows=[..gasStorageRows];
    break;
   default:throw TradePositionSelection.Unavailable();
  }
  var matching=rows.Where(r=>Guid.Parse(r["tradeId"]!.GetValue<string>())==selected.TradeId&&
   Guid.Parse(r["bindingId"]!.GetValue<string>())==selected.BindingId&&r["instrumentType"]!.GetValue<string>()==selected.InstrumentType).ToArray();
  if(matching.Length!=1)throw TradePositionSelection.Unavailable();
  var result=matching[0];
  var formats=await DealAmountFormats.All(connection,manifest.CatalogId,ct);
  Listed(result,formats.GetValueOrDefault(selected.TradeId),sideSchedules);
  return result;
 }
 // A row as the Blotter lists it: its deal's display formats, and whether its position has Side schedules to read.
 internal static void Listed(JsonObject row,Dictionary<string,DealAmountFormats.Selection>? formats,Func<string,bool> sideSchedules)
 {
  AmountFormats(row,formats);
  row["sideSchedules"]=sideSchedules(row["instrumentType"]!.GetValue<string>());
 }
 static void AmountFormats(JsonObject row,Dictionary<string,DealAmountFormats.Selection>? formats)
 {
  row["amountFormat"]=JsonSerializer.SerializeToNode(DealAmountFormats.Resolve(formats,"trade"),Json);
  row["fixedAmountFormat"]=JsonSerializer.SerializeToNode(DealAmountFormats.Resolve(formats,"fixed"),Json);
 }
}
