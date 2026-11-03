using System.Collections.Immutable;
using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using Npgsql;
using OpenEtrm.MarketData;

namespace OpenEtrm.Api;

// A cargo holding scales its retained gross discharge energy once. Original
// input quantity and Incoterms remain contract facts, not alternative holdings.
internal sealed record LngPosition(JsonElement Head,JsonElement Book,JsonElement Physical)
{
 internal Guid Trade=>Head.Id("trade_id");
 internal Guid Binding=>Head.Id("instrument_id");
 internal Guid Definition=>Head.Id("definition_revision_id");
 internal string From=>Head.Text("load_from");
 internal string To=>Head.Text("discharge_to");
 internal string Curve=>Physical.Text("curve_code");
 internal string MarketLabel=>Physical.Text("curve_name");
 internal ReferenceRevision Reference=>new(Physical.Id("reference_catalog_id"),Physical.Id("reference_revision_id"));
 internal ReferenceKey Quantity=>new(Reference,Physical.Text("quantity_code"));
 internal ReferenceKey Currency=>new(Reference,Physical.Text("currency_code"));
 internal ExactDecimal TotalQuantity=>ExactDecimal.Parse(Head.Text("energy_quantity"));
 internal PhysicalLngWriteRequest Terms(Guid frame)=>Head.GetProperty("request").Deserialize<PhysicalLngWriteRequest>(new JsonSerializerOptions(JsonSerializerDefaults.Web))! with{SnapshotId=frame};
 internal TradePositionFact Fact()=>new(Trade,Binding,int.Parse(Head.Text("revision"),CultureInfo.InvariantCulture),TradingReference.Trade(Head),Book.Id("id"),Book.Text("name"),
  LngPositions.Type,MarketLabel,From,To,Head.Text("energy_quantity"),Physical.Text("unit"));
 internal JsonObject Holding()=>JsonSerializer.SerializeToNode(new{
  booking=new{tradeDate=Head.Text("trade_date"),legalEntityId=Head.Id("legal_entity_id"),legalEntityRevision=int.Parse(Head.Text("legal_entity_revision"),CultureInfo.InvariantCulture),
   legalEntity=Head.Text("legal_entity"),counterpartyId=Head.Id("counterparty_id"),counterpartyRevision=int.Parse(Head.Text("counterparty_revision"),CultureInfo.InvariantCulture),counterparty=Head.Text("counterparty")},
  traderId=Head.Id("booker_id"),trader=Head.Text("booker_name"),traderCode=Head.Text("booker_login"),memo=(string?)null,
  tradeId=Trade,bindingId=Binding,instrumentId=Binding,revision=Head.Text("revision"),tradeReference=TradingReference.Trade(Head),instrumentReference=TradingReference.Instrument(Head),
  bookId=Book.Id("id"),book=Book.Text("name"),portfolioId=Book.Id("portfolio_id"),portfolio=Book.Text("portfolio"),
  instrumentType=LngPositions.Type,contractIdentityKind="lng_instrument",contractId=Binding,definitionRevisionId=Definition,
  curve=Curve,projectionCurves=new[]{Curve},marketKey=JsonSerializer.Serialize(new[]{Curve}),marketLabel=MarketLabel,
  commodity=new{code=Physical.Text("commodity_code"),name=Physical.Text("commodity_name")},
  product=Physical.Text("product_name"),loadFacility=Physical.Text("load_facility_name"),loadCustody=Physical.Text("load_custody_name"),
  dischargeFacility=Physical.Text("discharge_facility_name"),dischargeCustody=Physical.Text("discharge_custody_name"),
  loadFrom=Head.Text("load_from"),loadTo=Head.Text("load_to"),dischargeFrom=Head.Text("discharge_from"),dischargeTo=Head.Text("discharge_to"),
  originalQuantity=Head.Text("original_quantity"),originalQuantityCode=Head.Text("quantity_code"),conversionCode=Head.OptionalText("conversion_code"),
  incoterms=new{edition=Head.Text("incoterms_edition_code"),rule=Head.Text("incoterms_rule_code"),namedPlace=Head.Text("incoterms_place_code")},
  direction=Head.Text("direction")=="1"?"receive":"deliver",considerationDirection=Head.Text("cash_direction")=="1"?"receive":"pay",
  deliveryFrom=From,deliveryTo=To,lots=(string?)null,quantity=Head.Text("energy_quantity"),unit=Physical.Text("unit"),quantitySpecification=Quantity,
  currency=Physical.Text("currency"),currencyReference=Currency,quoteMultiplier=Physical.Text("multiplier"),
  executedAt=Head.Text("executed_at"),bookingFacts=new{implementation=Head.Text("booking_implementation"),labelCapturedAt=Head.Text("label_captured_at")},
  executionPrice=(string?)null,fixedPrice=Head.Text("fixed_price"),mark=(string?)null,markMinusPrice=(string?)null,executionPnl=(string?)null,
  settlement="unprocessed",measure=LngPositions.Measure,measureLabel="LNG contractual PV",quantityPolicy="gross_discharge_energy_total",
  presentValue=(string?)null,unavailableReason=(string?)null
 },TradingBlotter.Json)!.AsObject();
}

internal static class LngPositions
{
 internal const string Type="physical_lng";
 internal const string Measure="lng_contractual_present_value";
 private static MarketDataException Missing()=>new("position_facts","Every retained LNG position requires its exact visible product, custody, Incoterms, booking and quote metadata.");
 internal static async Task<ImmutableArray<LngPosition>> Read(NpgsqlConnection connection,PreviewManifest manifest,
  ImmutableArray<Guid> bookIds,ImmutableArray<Guid> portfolioIds,CancellationToken ct,TradePositionSelection? position=null,Guid? trade=null,ImmutableHashSet<Guid>? trades=null)
 {
  var books=await BookScope.Read(connection,manifest.CatalogId,ct);
  var selectedTrade=position?.TradeId??trade;
  if(selectedTrade is {} required&&trades is not null&&!trades.Contains(required))throw TradePositionSelection.Unavailable();
  var scope=selectedTrade is {} one?ImmutableHashSet.Create(one):trades;
  // Recheck authority before interpreting a directory that row security may have filtered.
  var args=new object[]{manifest.CatalogId,bookIds.ToArray(),portfolioIds.ToArray()};
  var inventory=await PreviewDatabase.Rows(connection,scope is null?
   "SELECT value::text FROM jsonb_array_elements(security.lng_position_scope($1,$2::uuid[],$3::uuid[]))":
   "SELECT value::text FROM jsonb_array_elements(security.lng_position_scope($1,$2::uuid[],$3::uuid[],$4::uuid[]))",
   scope is null?args:[..args,scope.ToArray()],ct);
  var selected=BookScope.Select(books,bookIds,portfolioIds).ToImmutableDictionary(b=>b.Id("id"));
  if(selected.IsEmpty)return [];
  var heads=await PreviewDatabase.Rows(connection,"""
   WITH heads AS(SELECT DISTINCT ON(trade_id) * FROM trading.lng_trade_revisions WHERE catalog_id=$1 ORDER BY trade_id,revision DESC)
   SELECT row_to_json(x)::text FROM(
    SELECT r.*,r.instrument_id binding_id,i.definition_revision_id,i.assignment_code,i.load_from,i.load_to,i.discharge_from,i.discharge_to,
     i.incoterms_edition_code,i.incoterms_rule_code,i.incoterms_place_code,i.fixed_price,i.cash_direction,i.payment_at,t.request,
     tr.reference_number trade_reference,ir.reference_number instrument_reference,
     f.trade_date,f.booker_id,f.booker_name,f.booker_login,f.executed_at,f.implementation booking_implementation,f.label_captured_at,
     e.name legal_entity,p.name counterparty
    FROM heads r JOIN trading.lng_instruments i USING(catalog_id,instrument_id)
    JOIN trading.trades t ON(t.catalog_id,t.id)=(r.catalog_id,r.trade_id)
    JOIN trading.trade_references tr ON(tr.catalog_id,tr.trade_id)=(r.catalog_id,r.trade_id)
    JOIN trading.instrument_references ir ON(ir.catalog_id,ir.instrument_id)=(r.catalog_id,r.instrument_id)
    JOIN trading.booking_facts f ON(f.catalog_id,f.trade_id)=(r.catalog_id,r.trade_id)
    JOIN trading.legal_entities e ON(e.catalog_id,e.id,e.revision)=(r.catalog_id,r.legal_entity_id,r.legal_entity_revision)
    JOIN trading.counterparties p ON(p.catalog_id,p.id,p.revision)=(r.catalog_id,r.counterparty_id,r.counterparty_revision)
    WHERE r.book_id=ANY($2::uuid[]) AND($3::uuid IS NULL OR r.trade_id=$3) AND($4::uuid[] IS NULL OR r.trade_id=ANY($4::uuid[])) ORDER BY tr.reference_number,r.instrument_id)x
   """,[manifest.CatalogId,selected.Keys.ToArray(),(object?)selectedTrade??DBNull.Value,(object?)trades?.ToArray()??DBNull.Value],ct);
  PositionInventory.Require(inventory,heads,Missing);
  if(position is not null)position.Require(heads);
  if(heads.IsEmpty)return [];
  var physical=await PreviewDatabase.Rows(connection,"""
   SELECT row_to_json(x)::text FROM(
    SELECT i.instrument_id,a.reference_catalog_id,a.reference_revision_id,a.code assignment_code,co.code commodity_code,a.product_code,a.curve_code,
     c.name curve_name,p.name product_name,load.name load_facility_name,load_custody.name load_custody_name,
     discharge.name discharge_facility_name,discharge_custody.name discharge_custody_name,
     edition.name edition_name,rule.name rule_name,place.name place_name,
     q.quantity_code,q.currency_code,q.multiplier,co.name commodity_name,u.symbol unit,input_unit.symbol input_unit,cu.display_code currency
    FROM trading.lng_instruments i
    JOIN trading.lng_trade_revisions r USING(catalog_id,instrument_id)
    JOIN ref_data.quantity_specifications input_q ON(input_q.catalog_id,input_q.revision_id,input_q.code)=(r.reference_catalog_id,r.reference_revision_id,r.quantity_code)
    JOIN ref_data.units input_unit ON(input_unit.catalog_id,input_unit.revision_id,input_unit.code)=(input_q.catalog_id,input_q.revision_id,input_q.unit_code)
    JOIN market_data.lng_valuation_assignments a ON(a.catalog_id,a.revision_id,a.code)=(i.catalog_id,i.definition_revision_id,i.assignment_code)
    JOIN market_data.curves c ON(c.catalog_id,c.revision_id,c.code)=(a.catalog_id,a.revision_id,a.curve_code)
    JOIN ref_data.lng_products p ON(p.catalog_id,p.revision_id,p.code)=(i.reference_catalog_id,i.reference_revision_id,i.product_code)
    JOIN ref_data.locations load ON(load.catalog_id,load.revision_id,load.code)=(i.reference_catalog_id,i.reference_revision_id,i.load_facility_code)
    JOIN ref_data.locations load_custody ON(load_custody.catalog_id,load_custody.revision_id,load_custody.code)=(i.reference_catalog_id,i.reference_revision_id,i.load_custody_code)
    JOIN ref_data.locations discharge ON(discharge.catalog_id,discharge.revision_id,discharge.code)=(i.reference_catalog_id,i.reference_revision_id,i.discharge_facility_code)
    JOIN ref_data.locations discharge_custody ON(discharge_custody.catalog_id,discharge_custody.revision_id,discharge_custody.code)=(i.reference_catalog_id,i.reference_revision_id,i.discharge_custody_code)
    JOIN ref_data.incoterms_editions edition ON(edition.catalog_id,edition.revision_id,edition.code)=(i.reference_catalog_id,i.reference_revision_id,i.incoterms_edition_code)
    JOIN ref_data.incoterms_rules rule ON(rule.catalog_id,rule.revision_id,rule.edition_code,rule.code)=(i.reference_catalog_id,i.reference_revision_id,i.incoterms_edition_code,i.incoterms_rule_code)
    JOIN ref_data.locations place ON(place.catalog_id,place.revision_id,place.code)=(i.reference_catalog_id,i.reference_revision_id,i.incoterms_place_code)
    JOIN ref_data.quote_specifications q ON(q.catalog_id,q.revision_id,q.code)=(a.reference_catalog_id,a.reference_revision_id,c.quote_code)
    JOIN ref_data.quantity_specifications n ON(n.catalog_id,n.revision_id,n.code)=(q.catalog_id,q.revision_id,q.quantity_code)
    JOIN ref_data.units u ON(u.catalog_id,u.revision_id,u.code)=(n.catalog_id,n.revision_id,n.unit_code)
    JOIN ref_data.commodities co ON(co.catalog_id,co.revision_id,co.code)=(n.catalog_id,n.revision_id,n.commodity_code)
    JOIN ref_data.currencies cu ON(cu.catalog_id,cu.revision_id,cu.code)=(q.catalog_id,q.revision_id,q.currency_code)
    WHERE i.catalog_id=$1 AND i.instrument_id=ANY($2::uuid[]) ORDER BY i.instrument_id)x
   """,[manifest.CatalogId,heads.Select(h=>h.Id("instrument_id")).Distinct().ToArray()],ct);
  var metadata=physical.ToLookup(row=>row.Id("instrument_id"));
  var result=ImmutableArray.CreateBuilder<LngPosition>();
  foreach(var head in heads)
  {
   var rows=metadata[head.Id("instrument_id")].ToArray();
   if(rows.Length!=1||rows[0].Text("assignment_code")!=head.Text("assignment_code")||
    new[]{"curve_name","product_name","load_facility_name","load_custody_name","discharge_facility_name","discharge_custody_name","edition_name","rule_name","place_name","quantity_code","currency_code","commodity_name","unit","input_unit","currency"}.Any(name=>string.IsNullOrWhiteSpace(rows[0].Text(name))))throw Missing();
   result.Add(new(head,selected[head.Id("book_id")],rows[0]));
  }
  await using var permission=ScheduleStore.Command(connection,"SELECT security.trading_access($1,'ReadTrades') AND security.market_access($1,'ReadMarketData')",manifest.CatalogId);
  if(!Equals(await permission.ExecuteScalarAsync(ct),true))throw new PreviewDeniedException();
  return result.ToImmutable();
 }
}
