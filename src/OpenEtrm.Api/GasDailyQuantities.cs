using System.Collections.Immutable;
using System.Globalization;
using System.Diagnostics;
using System.Numerics;
using System.Text.Json;
using System.Text.Json.Serialization;
using Npgsql;
using OpenEtrm.MarketData;
using OpenEtrm.MarketData.Gas;
using OpenEtrm.MarketData.Trading;
using OpenEtrm.Shared.Numerics;

namespace OpenEtrm.Api;

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record GasDailyReadRequest(FuturesReadRequest Context,Guid TradeId,string SelectionPolicy,bool IncludeHistory=true,bool MeasurePerformance=false);
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record GasDailyWriteRequest(FuturesReadRequest Context,Guid TradeId,int ExpectedRevision,Guid OperationId,Guid ObligationId,string Kind,string Status,string Day,[property:JsonRequired]string? Quantity,string Evidence,[property:JsonRequired]Guid? ExpectedPreviousId);

public sealed class GasDailyQuantities(PreviewDatabase database)
{
 static readonly JsonSerializerOptions Json=new(JsonSerializerDefaults.Web);
 static MarketDataException Invalid(string s)=>new("gas_quantity_input",s);
 static object Q(ExactQuantity q)=>new{q.Numerator,q.Denominator};
 public async Task<object> Read(NpgsqlConnection c,InputFrame frame,GasDailyReadRequest body,CancellationToken ct)
 {
  var timer=Stopwatch.StartNew();
  await using(var authority=ScheduleStore.Command(c,"SELECT security.require_gas_quantity_trade($1,$2,false)",database.Manifest.CatalogId,body.TradeId))await authority.ExecuteNonQueryAsync(ct);
  var header=(await PreviewDatabase.Rows(c,"SELECT row_to_json(r)::text FROM trading.gas_trade_revisions r WHERE(catalog_id,trade_id)=($1,$2) ORDER BY revision DESC LIMIT 1",[database.Manifest.CatalogId,body.TradeId],ct)).Single();
  var obligations=await PreviewDatabase.Rows(c,"""
   SELECT row_to_json(x)::text FROM(SELECT o.*,m.code,m.ratio_n::text,m.ratio_d::text,m.pipeline_code,m.zone_code,m.point_code
   FROM trading.gas_delivery_obligations o JOIN trading.gas_component_terms m USING(catalog_id,instrument_id,slot)
   WHERE(o.catalog_id,o.trade_id)=($1,$2) ORDER BY slot)x
   """,[database.Manifest.CatalogId,body.TradeId],ct);
  var metadataMs=timer.Elapsed.TotalMilliseconds;
  var heads=await QuantityRows(c,body.TradeId,"heads",ct);
  var headsMs=timer.Elapsed.TotalMilliseconds-metadataMs;
  var history=body.IncludeHistory?await QuantityRows(c,body.TradeId,"history",ct):ImmutableArray<JsonElement>.Empty;
  var byDay=heads.GroupBy(h=>(h.Id("obligation_id"),DateWorkspace.Date(h.Text("day"))))
   .ToDictionary(g=>g.Key,g=>g.ToArray());
  var historyMs=timer.Elapsed.TotalMilliseconds-metadataMs-headsMs;
  var programs=await new RetainedScheduleStore(database).LoadPrograms(c,header.Id("instrument_id"),frame.ContextId,frame.At,ct);
  var programsMs=timer.Elapsed.TotalMilliseconds-metadataMs-headsMs-historyMs;
  var rows=new List<object>();
  foreach(var o in obligations)
  {
   var slot=int.Parse(o.Text("slot"),CultureInfo.InvariantCulture);var p=programs.Single(p=>p.Slot==slot);
   var booked=ScheduleNumber.Parse(header.Text("quantity"));var scale=new ExactRatio(BigInteger.Parse(o.Text("ratio_n"))*booked.Numerator,BigInteger.Parse(o.Text("ratio_d"))*booked.Denominator);
   foreach(var contract in GasDailyQuantitySelection.ReadContract(p.Program,scale))
   {
    var matching=byDay.GetValueOrDefault((o.Id("id"),contract.Day),[]);
    var observations=matching.Select(h=>new GasQuantityObservation(h.Id("id"),h.Text("kind"),h.Text("status"),ExactQuantity.FromQuantity(new(ExactDecimal.Parse(h.OptionalText("quantity")??"0"),contract.Quantity.Specification)),h.Text("evidence"))).ToImmutableArray();
    var selected=GasDailyQuantitySelection.Select(contract,observations,body.SelectionPolicy,frame.At,scale);
    rows.Add(new{obligationId=o.Id("id"),sideCode=o.Text("code"),pipeline=o.Text("pipeline_code"),zone=o.Text("zone_code"),deliveryPoint=o.Text("point_code"),day=contract.Day.ToString("yyyy-MM-dd",CultureInfo.InvariantCulture),contract.Start,contract.End,quantitySpecification=contract.Quantity.Specification,
     contractual=Q(contract.Quantity),selected=Q(selected.Quantity),normalizedInput=Q(selected.NormalizedQuantity),selected.Kind,selected.RevisionId,selected.Evidence,contract.Root,contract.Instruction,contract.Grid,
     heads=matching.Select(h=>new{id=h.Id("id"),kind=h.Text("kind"),status=h.Text("status"),quantity=h.OptionalText("quantity"),evidence=h.Text("evidence"),revision=int.Parse(h.Text("revision"),CultureInfo.InvariantCulture)})});
   }
  }
  var selectionMs=timer.Elapsed.TotalMilliseconds-metadataMs-headsMs-historyMs-programsMs;
  await using var permission=ScheduleStore.Command(c,"SELECT security.trading_access($1,'AmendTrade')",database.Manifest.CatalogId);
  return new{tradeId=body.TradeId,expectedRevision=int.Parse(header.Text("revision"),CultureInfo.InvariantCulture),body.SelectionPolicy,canWrite=await permission.ExecuteScalarAsync(ct) is true,rows,history,
   scope="daily_delivery_quantity_inputs_only",valuationEffect="contractual_pv_unchanged",historyLimit=100,
   performance=body.MeasurePerformance?new{metadataMs,headsMs,historyMs,programsMs,selectionMs,headCount=heads.Length,programCount=programs.Length,uniquePrograms=programs.Select(p=>p.Identity.ContentDigest).Distinct().Count(),dayCount=rows.Count}:null};
 }
 async Task<ImmutableArray<JsonElement>> QuantityRows(NpgsqlConnection c,Guid trade,string mode,CancellationToken ct)
 {
  await using var query=ScheduleStore.Command(c,"SELECT security.read_gas_quantity_rows($1,$2,$3)::text",database.Manifest.CatalogId,trade,mode);
  var result=JsonSerializer.Deserialize<JsonElement>((string)(await query.ExecuteScalarAsync(ct))!);
  return [..result.EnumerateArray()];
 }
 public async Task<object> History(NpgsqlConnection c,Guid trade,CancellationToken ct)
 {
  await using(var authority=ScheduleStore.Command(c,"SELECT security.require_gas_quantity_trade($1,$2,false)",database.Manifest.CatalogId,trade))await authority.ExecuteNonQueryAsync(ct);
  return new{tradeId=trade,history=await QuantityRows(c,trade,"history",ct),historyLimit=100};
 }
 public async Task<JsonElement> Status(NpgsqlConnection c,Guid operation,CancellationToken ct)
 {
  if(operation==Guid.Empty)throw Invalid("Supply the retained operation identity.");
  await using var q=ScheduleStore.Command(c,"SELECT security.gas_quantity_status($1,$2,NULL)::text",database.Manifest.CatalogId,operation);
  return JsonSerializer.Deserialize<JsonElement>((string)(await q.ExecuteScalarAsync(ct))!);
 }
 public async Task<JsonElement> Save(NpgsqlConnection c,InputFrame frame,GasDailyWriteRequest body,CancellationToken ct)
 {
  if(body.Context is not{Convention:PhysicalGasStore.Convention,KnowledgeMode:"current",ContextId:null})throw Invalid("Choose a current gas recording context.");
  _=DateWorkspace.Date(body.Day);
  var payload=JsonSerializer.Serialize(body,Json);
  // Replays retain their original outcome even after a later correction.
  await using(var retry=ScheduleStore.Command(c,"SELECT security.gas_quantity_status($1,$2,$3::jsonb)::text",database.Manifest.CatalogId,body.OperationId,payload))
   if(await retry.ExecuteScalarAsync(ct) is string prior){var result=JsonSerializer.Deserialize<JsonElement>(prior);if(result.TryGetProperty("revisionId",out _))return result;}
  if(body.Quantity is {} text){var n=ExactDecimal.Parse(text);if(n.CompareTo(default)<0)throw Invalid("Quantities must be nonnegative native interval totals.");}
  var context=await TradingBookingRead.Run(database,c,frame,(data,pinned)=>Task.FromResult(pinned.ContextId??throw Invalid("Capture the selected certified recording context.")),ct);
  await using var q=ScheduleStore.Command(c,"SELECT security.record_gas_daily_quantity($1,$2::jsonb,$3,$4)::text",database.Manifest.CatalogId,payload,frame.At,context);
  return JsonSerializer.Deserialize<JsonElement>((string)(await q.ExecuteScalarAsync(ct))!);
 }
}
