using System.Text.Json;
using System.Text.Json.Serialization;
using Npgsql;
using OpenEtrm.MarketData;
using OpenEtrm.MarketData.Trading;

namespace OpenEtrm.Api;

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record SwapValuationReceiptRead(Guid Id);

public sealed partial class SwapStore
{
 private async Task<Guid?> RetainValuation(NpgsqlConnection connection,InstrumentValuation instrument,Quantity quantity,object terms,object result,CancellationToken ct,string schema="fixing_aware_swap_valuation_v1")
 {
  if(instrument.Snapshot.FixingContextId is not {} contextId)return null;
  var fixings=instrument.Sides.SelectMany((side,slot)=>side.Value.Payments.SelectMany(p=>p.Resets.SelectMany((reset,ordinal)=>
   FixingLineage(instrument.Snapshot,reset.Price.Fixing).Select(f=>new{sideSlot=slot,paymentOrdinal=p.Plan.Key.Ordinal,resetOrdinal=ordinal,revision=f.Revision,fixingId=f.Key.Id})))).ToArray();
  var market=instrument.Snapshot.Selections.Select(s=>new{seriesId=s.Input.Series.Id,inputUse=s.Input.Use.ToString(),observationId=s.Observation.Key.Id});
  var json=new JsonSerializerOptions(JsonSerializerDefaults.Web){Converters={new ExactDecimalJsonConverter()}};
  string Digest(object value)=>Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(JsonSerializer.Serialize(value,json))));
  var termsDigest=Digest(terms);
  var inputDigest=Digest(new{instrument.Snapshot.Id,instrument.Snapshot.Definitions,instrument.Snapshot.ValuationAt,instrument.Snapshot.FixingContextId,market,fixings});
  await using var command=ScheduleStore.Command(connection,"SELECT security.retain_swap_valuation($1,$2,$3,$4,$5,$6,$7,$8,$9::numeric,$10,$11,$12,$13,$14::jsonb,$15::jsonb,$16::jsonb,$17::jsonb,$18)",
   manifest.CatalogId,contextId,instrument.Snapshot.Id,instrument.Snapshot.Definitions.RevisionId,quantity.Specification.Revision.CatalogId,quantity.Specification.Revision.RevisionId,
   quantity.Specification.Code,instrument.Currency.Code,quantity.Amount.ToString(),instrument.Snapshot.ValuationAt,termsDigest,inputDigest,instrument.NumericalPolicy,
   JsonSerializer.Serialize(terms,json),JsonSerializer.Serialize(result,json),JsonSerializer.Serialize(fixings,json),JsonSerializer.Serialize(market,json),schema);
  return(Guid)(await command.ExecuteScalarAsync(ct))!;
 }
 public async Task<object> ValuationReceipt(NpgsqlConnection connection,SwapValuationReceiptRead body,CancellationToken ct)
 {
  var rows=await PreviewDatabase.Rows(connection,"SELECT row_to_json(r)::text FROM trading.swap_valuation_receipts r WHERE(catalog_id,id)=($1,$2)",[manifest.CatalogId,body.Id],ct);
  if(rows.Length!=1)throw new MarketDataException("index_fixing_receipt","The retained valuation is unavailable under current authority and the selected context.");
  var fixingPins=await PreviewDatabase.Rows(connection,"SELECT row_to_json(p)::text FROM trading.swap_valuation_fixing_pins p WHERE(catalog_id,receipt_id)=($1,$2) ORDER BY side_slot,payment_ordinal,reset_ordinal,lineage_revision",[manifest.CatalogId,body.Id],ct);
  var marketPins=await PreviewDatabase.Rows(connection,"SELECT row_to_json(p)::text FROM trading.swap_valuation_market_pins p WHERE(catalog_id,receipt_id)=($1,$2) ORDER BY series_id,input_use",[manifest.CatalogId,body.Id],ct);
  await using(var authority=ScheduleStore.Command(connection,"SELECT security.swap_valuation_access($1,$2)",manifest.CatalogId,body.Id))
   if(!Equals(await authority.ExecuteScalarAsync(ct),true))throw new PreviewDeniedException();
  return new{receipt=rows[0],fixingPins,marketPins};
 }
}
