using System.Collections.Immutable;
using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using Npgsql;
using OpenEtrm.MarketData;

namespace OpenEtrm.Api;

// A fixed-total crude trade has one retained instrument binding. Its delivery
// and cash sides are independently signed obligations, never separate holdings.
internal sealed record CrudePosition(JsonElement Head,JsonElement Book,JsonElement Physical)
{
 internal Guid Trade=>Head.Id("trade_id");
 internal Guid Binding=>Head.Id("instrument_id");
 internal Guid Definition=>Head.Id("definition_revision_id");
 internal string From=>Head.Text("delivery_from");
 internal string To=>Head.Text("delivery_to");
 internal string Curve=>Physical.Text("curve_code");
 internal string MarketLabel=>Physical.Text("curve_name");
 internal ReferenceRevision Reference=>new(Physical.Id("reference_catalog_id"),Physical.Id("reference_revision_id"));
 internal ReferenceKey Quantity=>new(Reference,Physical.Text("quantity_code"));
 internal ReferenceKey Currency=>new(Reference,Physical.Text("currency_code"));
 internal ExactDecimal TotalQuantity=>ExactDecimal.Parse(Head.Text("quantity"));
 internal PhysicalCrudeWriteRequest Terms(Guid frame)=>Head.GetProperty("request").Deserialize<PhysicalCrudeWriteRequest>(new JsonSerializerOptions(JsonSerializerDefaults.Web))! with{SnapshotId=frame};
 internal TradePositionFact Fact()=>new(Trade,Binding,int.Parse(Head.Text("revision"),CultureInfo.InvariantCulture),TradingReference.Trade(Head),Book.Id("id"),Book.Text("name"),
  CrudePositions.Type,MarketLabel,From,To,Head.Text("quantity"),Physical.Text("unit"));
 internal JsonObject Holding()=>JsonSerializer.SerializeToNode(new{
  booking=new{tradeDate=Head.Text("trade_date"),legalEntityId=Head.Id("legal_entity_id"),legalEntityRevision=int.Parse(Head.Text("legal_entity_revision"),CultureInfo.InvariantCulture),
   legalEntity=Head.Text("legal_entity"),counterpartyId=Head.Id("counterparty_id"),counterpartyRevision=int.Parse(Head.Text("counterparty_revision"),CultureInfo.InvariantCulture),counterparty=Head.Text("counterparty")},
  traderId=Head.Id("booker_id"),trader=Head.Text("booker_name"),traderCode=Head.Text("booker_login"),memo=(string?)null,
  tradeId=Trade,bindingId=Binding,instrumentId=Binding,revision=Head.Text("revision"),tradeReference=TradingReference.Trade(Head),instrumentReference=TradingReference.Instrument(Head),
  bookId=Book.Id("id"),book=Book.Text("name"),portfolioId=Book.Id("portfolio_id"),portfolio=Book.Text("portfolio"),
  instrumentType=CrudePositions.Type,contractIdentityKind="crude_instrument",contractId=Binding,definitionRevisionId=Definition,
  curve=Curve,projectionCurves=new[]{Curve},marketKey=JsonSerializer.Serialize(new[]{Curve}),marketLabel=MarketLabel,
  commodity=new{code=Physical.Text("commodity_code"),name=Physical.Text("commodity_name")},
  grade=Physical.Text("grade_name"),facility=Physical.Text("facility_name"),custodyPoint=Physical.Text("custody_point_name"),
  direction=Head.Text("direction")=="1"?"receive":"deliver",considerationDirection=Head.Text("consideration_direction")=="1"?"receive":"pay",
  handlingFeeDirection=Head.OptionalText("handling_fee_direction") switch{"1"=>"receive","-1"=>"pay",_=>null},
  deliveryFrom=From,deliveryTo=To,lots=(string?)null,quantity=Head.Text("quantity"),unit=Physical.Text("unit"),quantitySpecification=Quantity,
  currency=Physical.Text("currency"),currencyReference=Currency,quoteMultiplier=Physical.Text("multiplier"),
  executedAt=Head.Text("executed_at"),bookingFacts=new{implementation=Head.Text("booking_implementation"),labelCapturedAt=Head.Text("label_captured_at")},
  executionPrice=(string?)null,fixedPrice=Head.Text("fixed_price"),mark=(string?)null,markMinusPrice=(string?)null,executionPnl=(string?)null,
  settlement="unprocessed",measure=CrudePositions.Measure,measureLabel="Crude contractual PV",quantityPolicy="standard_volume_total",
  presentValue=(string?)null,unavailableReason=(string?)null
 },TradingBlotter.Json)!.AsObject();
}

internal static class CrudePositions
{
 internal const string Type="physical_crude";
 internal const string Measure="crude_contractual_present_value";
 private static MarketDataException Missing()=>new("position_facts","Every retained crude position requires its exact visible grade, custody, booking and quote metadata.");
 internal static void RequireInventory(ImmutableArray<JsonElement> expected,ImmutableArray<JsonElement> actual)
 {
  static (Guid Trade,Guid Binding,int Revision,Guid Book,Guid Definition,int Reference) Key(JsonElement row)=>
   (row.Id("trade_id"),row.Id("binding_id"),int.Parse(row.Text("revision"),CultureInfo.InvariantCulture),row.Id("book_id"),row.Id("definition_revision_id"),int.Parse(row.Text("trade_reference"),CultureInfo.InvariantCulture));
  if(expected.Select(Key).Distinct().Count()!=expected.Length||actual.Select(Key).Distinct().Count()!=actual.Length||
   !expected.Select(Key).ToHashSet().SetEquals(actual.Select(Key)))throw Missing();
 }
 internal static async Task<ImmutableArray<CrudePosition>> Read(NpgsqlConnection connection,PreviewManifest manifest,
  ImmutableArray<Guid> bookIds,ImmutableArray<Guid> portfolioIds,CancellationToken ct,TradePositionSelection? position=null,Guid? trade=null)
 {
  var books=await BookScope.Read(connection,manifest.CatalogId,ct);
  var selected=BookScope.Select(books,bookIds,portfolioIds).ToImmutableDictionary(b=>b.Id("id"));
  if(selected.IsEmpty)return [];
  var inventory=await PreviewDatabase.Rows(connection,"SELECT value::text FROM jsonb_array_elements(security.crude_position_scope($1,$2::uuid[],$3::uuid[]))",[manifest.CatalogId,bookIds.ToArray(),portfolioIds.ToArray()],ct);
  var selectedTrade=position?.TradeId??trade;
  if(selectedTrade is {} tradeId)inventory=[..inventory.Where(row=>row.Id("trade_id")==tradeId)];
  var heads=await PreviewDatabase.Rows(connection,"""
   WITH heads AS(SELECT DISTINCT ON(trade_id) * FROM trading.crude_trade_revisions WHERE catalog_id=$1 ORDER BY trade_id,revision DESC)
   SELECT row_to_json(x)::text FROM(
    SELECT r.*,r.instrument_id binding_id,i.definition_revision_id,i.assignment_code,i.delivery_from,i.delivery_to,
     i.quantity_basis,i.tolerance,i.fixed_price,i.consideration_direction,i.handling_fee,i.handling_fee_direction,i.payment_at,t.request,
     tr.reference_number trade_reference,ir.reference_number instrument_reference,
     f.trade_date,f.booker_id,f.booker_name,f.booker_login,f.executed_at,f.implementation booking_implementation,f.label_captured_at,
     e.name legal_entity,p.name counterparty
    FROM heads r JOIN trading.crude_instruments i USING(catalog_id,instrument_id)
    JOIN trading.trades t ON(t.catalog_id,t.id)=(r.catalog_id,r.trade_id)
    JOIN trading.trade_references tr ON(tr.catalog_id,tr.trade_id)=(r.catalog_id,r.trade_id)
    JOIN trading.instrument_references ir ON(ir.catalog_id,ir.instrument_id)=(r.catalog_id,r.instrument_id)
    JOIN trading.booking_facts f ON(f.catalog_id,f.trade_id)=(r.catalog_id,r.trade_id)
    JOIN trading.legal_entities e ON(e.catalog_id,e.id,e.revision)=(r.catalog_id,r.legal_entity_id,r.legal_entity_revision)
    JOIN trading.counterparties p ON(p.catalog_id,p.id,p.revision)=(r.catalog_id,r.counterparty_id,r.counterparty_revision)
    WHERE r.book_id=ANY($2::uuid[]) AND($3::uuid IS NULL OR r.trade_id=$3) ORDER BY tr.reference_number,r.instrument_id)x
   """,[manifest.CatalogId,selected.Keys.ToArray(),(object?)selectedTrade??DBNull.Value],ct);
  RequireInventory(inventory,heads);
  if(position is not null)position.Require(heads);
  if(heads.IsEmpty)return [];
  var physical=await PreviewDatabase.Rows(connection,"""
   SELECT row_to_json(x)::text FROM(
    SELECT i.instrument_id,a.reference_catalog_id,a.reference_revision_id,a.code assignment_code,a.commodity_code,a.grade_code,a.facility_code,a.custody_point_code,a.curve_code,
     c.name curve_name,g.name grade_name,g.standard_temperature,g.temperature_scale,facility.name facility_name,custody.name custody_point_name,
     q.quantity_code,q.currency_code,q.multiplier,co.name commodity_name,u.symbol unit,cu.display_code currency
    FROM trading.crude_instruments i
    JOIN market_data.crude_valuation_assignments a ON(a.catalog_id,a.revision_id,a.code)=(i.catalog_id,i.definition_revision_id,i.assignment_code)
    JOIN market_data.curves c ON(c.catalog_id,c.revision_id,c.code)=(a.catalog_id,a.revision_id,a.curve_code)
    JOIN ref_data.crude_grades g ON(g.catalog_id,g.revision_id,g.code)=(a.reference_catalog_id,a.reference_revision_id,a.grade_code)
    JOIN ref_data.locations facility ON(facility.catalog_id,facility.revision_id,facility.code)=(a.reference_catalog_id,a.reference_revision_id,a.facility_code)
    JOIN ref_data.locations custody ON(custody.catalog_id,custody.revision_id,custody.code)=(a.reference_catalog_id,a.reference_revision_id,a.custody_point_code)
    JOIN ref_data.quote_specifications q ON(q.catalog_id,q.revision_id,q.code)=(a.reference_catalog_id,a.reference_revision_id,c.quote_code)
    JOIN ref_data.quantity_specifications n ON(n.catalog_id,n.revision_id,n.code)=(q.catalog_id,q.revision_id,q.quantity_code)
    JOIN ref_data.units u ON(u.catalog_id,u.revision_id,u.code)=(n.catalog_id,n.revision_id,n.unit_code)
    JOIN ref_data.commodities co ON(co.catalog_id,co.revision_id,co.code)=(n.catalog_id,n.revision_id,n.commodity_code)
    JOIN ref_data.currencies cu ON(cu.catalog_id,cu.revision_id,cu.code)=(q.catalog_id,q.revision_id,q.currency_code)
    WHERE i.catalog_id=$1 AND i.instrument_id=ANY($2::uuid[]) ORDER BY i.instrument_id)x
   """,[manifest.CatalogId,heads.Select(h=>h.Id("instrument_id")).Distinct().ToArray()],ct);
  var metadata=physical.ToLookup(row=>row.Id("instrument_id"));
  var result=ImmutableArray.CreateBuilder<CrudePosition>();
  foreach(var head in heads)
  {
   var rows=metadata[head.Id("instrument_id")].ToArray();
   if(rows.Length!=1||rows[0].Text("assignment_code")!=head.Text("assignment_code")||
    new[]{"curve_name","grade_name","facility_name","custody_point_name","quantity_code","currency_code","commodity_name","unit","currency"}.Any(name=>string.IsNullOrWhiteSpace(rows[0].Text(name))))throw Missing();
   result.Add(new(head,selected[head.Id("book_id")],rows[0]));
  }
  await using var permission=ScheduleStore.Command(connection,"SELECT security.trading_access($1,'ReadTrades') AND security.market_access($1,'ReadMarketData')",manifest.CatalogId);
  if(!Equals(await permission.ExecuteScalarAsync(ct),true))throw new PreviewDeniedException();
  return result.ToImmutable();
 }
}
