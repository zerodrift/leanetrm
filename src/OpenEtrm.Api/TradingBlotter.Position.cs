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
    foreach(var row in rows)FutureTerms(row,data);
    break;
   case "financial_swap" or "heat_rate":
    rows=[..(await swaps.Holdings(connection,data,frame,[],[],ct,selected)).Rows];
    break;
   case "ppa":
    rows=(await PpaBlotter.Read(connection,database,data,frame,[],[],ct,selected)).ToArray();
    break;
   case "physical_gas":
    var gas=await GasBlotter.Run(connection,database,frame,[],[],ct,selected);
    var gasRows=new List<JsonObject>();await foreach(var unit in gas.Units(ct))gasRows.AddRange(unit);
    rows=[..gasRows];
    break;
   case "physical_crude":
    var crude=await CrudeBlotter.Run(connection,database,frame,[],[],ct,selected);
    var crudeRows=new List<JsonObject>();await foreach(var unit in crude.Units(ct))crudeRows.AddRange(unit);
    rows=[..crudeRows];
    break;
   default:throw TradePositionSelection.Unavailable();
  }
  var matching=rows.Where(r=>Guid.Parse(r["tradeId"]!.GetValue<string>())==selected.TradeId&&
   Guid.Parse(r["bindingId"]!.GetValue<string>())==selected.BindingId&&r["instrumentType"]!.GetValue<string>()==selected.InstrumentType).ToArray();
  if(matching.Length!=1)throw TradePositionSelection.Unavailable();
  var result=matching[0];
  var formats=await DealAmountFormats.All(connection,manifest.CatalogId,ct);
  AmountFormats(result,formats.GetValueOrDefault(selected.TradeId));
  return result;
 }
 private void FutureTerms(JsonObject row,PreviewData data)
 {
  var curve=data.Definitions["curves"].Single(c=>c.Text("code")==row["curve"]!.GetValue<string>());
  var quote=data.Quotes.Single(q=>q.Key.Code==curve.Text("quote_code"));
  row["quantitySpecification"]=JsonSerializer.SerializeToNode(quote.Quantity,Json);row["currencyReference"]=JsonSerializer.SerializeToNode(quote.Currency,Json);
  row["definitionRevisionId"]=manifest.RevisionId.ToString();row["instrumentType"]="future";row["presentValue"]=null;row["fixedPrice"]=null;row["direction"]=null;
 }
 internal static void AmountFormats(JsonObject row,Dictionary<string,DealAmountFormats.Selection>? formats)
 {
  row["amountFormat"]=JsonSerializer.SerializeToNode(DealAmountFormats.Resolve(formats,"trade"),Json);
  row["fixedAmountFormat"]=JsonSerializer.SerializeToNode(DealAmountFormats.Resolve(formats,"fixed"),Json);
 }
}
