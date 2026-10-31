using System.Collections.Immutable;
using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using Npgsql;
using OpenEtrm.MarketData;

namespace OpenEtrm.Api;

// One gas trade holds one retained instrument; its immutable instrument identity
// is also its position binding. Physical sides remain obligations of that position.
internal sealed record GasPosition(JsonElement Head,JsonElement Book,ImmutableArray<JsonElement> Physical)
{
 internal Guid Trade=>Head.Id("trade_id");
 internal Guid Binding=>Head.Id("instrument_id");
 internal Guid Definition=>Head.Id("definition_revision_id");
 internal string From=>Physical.Select(p=>p.Text("delivery_from")).Min(StringComparer.Ordinal)!;
 internal string To=>Physical.Select(p=>p.Text("delivery_to")).Max(StringComparer.Ordinal)!;
 internal string[] Curves=>[..Physical.Select(p=>p.Text("curve_code")).Distinct().Order(StringComparer.Ordinal)];
 internal string MarketLabel=>string.Join(" / ",Physical.Select(p=>p.Text("curve_name")).Distinct().Order(StringComparer.Ordinal));
 internal ReferenceRevision Reference=>new(Physical[0].Id("reference_catalog_id"),Physical[0].Id("reference_revision_id"));
 internal ReferenceKey Quantity=>new(Reference,Physical[0].Text("quantity_code"));
 internal ReferenceKey Currency=>new(Reference,Physical[0].Text("currency_code"));
 internal TradePositionFact Fact()=>new(Trade,Binding,int.Parse(Head.Text("revision"),CultureInfo.InvariantCulture),TradingReference.Trade(Head),Book.Id("id"),Book.Text("name"),
  GasPositions.Type,MarketLabel,From,To,null,null);
 internal JsonObject Holding()=>JsonSerializer.SerializeToNode(new{
  booking=new{tradeDate=Head.Text("trade_date"),legalEntityId=Head.Id("legal_entity_id"),legalEntityRevision=int.Parse(Head.Text("legal_entity_revision"),CultureInfo.InvariantCulture),
   legalEntity=Head.Text("legal_entity"),counterpartyId=Head.Id("counterparty_id"),counterpartyRevision=int.Parse(Head.Text("counterparty_revision"),CultureInfo.InvariantCulture),counterparty=Head.Text("counterparty")},
  traderId=Head.Id("booker_id"),trader=Head.Text("booker_name"),traderCode=Head.Text("booker_login"),memo=(string?)null,
  tradeId=Trade,bindingId=Binding,instrumentId=Binding,revision=Head.Text("revision"),tradeReference=TradingReference.Trade(Head),instrumentReference=TradingReference.Instrument(Head),
  bookId=Book.Id("id"),book=Book.Text("name"),portfolioId=Book.Id("portfolio_id"),portfolio=Book.Text("portfolio"),
  instrumentType=GasPositions.Type,gasComposition=Head.GetProperty("composition").GetBoolean(),contractIdentityKind="gas_instrument",contractId=Binding,definitionRevisionId=Definition,
  curve=Curves[0],projectionCurves=Curves,marketKey=JsonSerializer.Serialize(Curves),marketLabel=MarketLabel,
  commodity=new{code=Physical[0].Text("commodity_code"),name=Physical[0].Text("commodity_name")},direction="multi_side",
  deliveryFrom=From,deliveryTo=To,lots=(string?)null,quantity=(string?)null,unit=Physical[0].Text("unit"),quantitySpecification=Quantity,
  currency=Physical[0].Text("currency"),currencyReference=Currency,quoteMultiplier=Physical[0].Text("multiplier"),
  executedAt=Head.Text("executed_at"),bookingFacts=new{implementation=Head.Text("booking_implementation"),labelCapturedAt=Head.Text("label_captured_at")},
  executionPrice=(string?)null,fixedPrice=(string?)null,mark=(string?)null,markMinusPrice=(string?)null,executionPnl=(string?)null,
  settlement="unprocessed",measure="gas_contractual_present_value",measureLabel="Contractual PV",quantityPolicy="contractual_side_schedules_v1",
  presentValue=(string?)null,unavailableReason=(string?)null
 },TradingBlotter.Json)!.AsObject();
}

internal static class GasPositions
{
 internal const string Type="physical_gas";
 internal const string RiskUnavailable="physical_gas_risk_unavailable";
 internal static async Task<ImmutableArray<ExcludedPosition>> RiskExclusions(NpgsqlConnection connection,Guid catalog,ImmutableArray<Guid> books,CancellationToken ct)
 {
  var rows=await PreviewDatabase.Rows(connection,"SELECT value::text FROM jsonb_array_elements(security.gas_position_scope($1,$2::uuid[],$3::uuid[]))",[catalog,books.ToArray(),Array.Empty<Guid>()],ct);
  return [..rows.Select(r=>new ExcludedPosition(r.Id("trade_id"),r.Id("binding_id"),r.Id("book_id"),TradingReference.Trade(r),Type,RiskUnavailable))];
 }
 internal static async Task<ImmutableArray<GasPosition>> Read(NpgsqlConnection connection,PreviewManifest manifest,
  ImmutableArray<Guid> bookIds,ImmutableArray<Guid> portfolioIds,CancellationToken ct,TradePositionSelection? position=null,Guid? trade=null)
 {
  var books=await new FuturesStore(manifest).Books(connection,ct);FuturesStore.ValidateScope(books,bookIds,portfolioIds);
  var selected=books.Where(b=>(bookIds.IsEmpty||bookIds.Contains(b.Id("id")))&&(portfolioIds.IsEmpty||portfolioIds.Contains(b.Id("portfolio_id"))))
   .ToImmutableDictionary(b=>b.Id("id"));
  if(selected.IsEmpty)return [];
  var heads=await PreviewDatabase.Rows(connection,"""
   WITH heads AS(SELECT DISTINCT ON(trade_id) * FROM trading.gas_trade_revisions WHERE catalog_id=$1 ORDER BY trade_id,revision DESC)
   SELECT row_to_json(x)::text FROM(
    SELECT r.*,r.instrument_id binding_id,i.definition_revision_id,i.composition,i.gas_setup_id,i.quantity_basis,i.fixed_price,i.usage_fee,i.usage_tariff_code,i.usage_fee_direction,i.payment_at,
     tr.reference_number trade_reference,ir.reference_number instrument_reference,
     f.trade_date,f.booker_id,f.booker_name,f.booker_login,f.executed_at,f.implementation booking_implementation,f.label_captured_at,
     e.name legal_entity,p.name counterparty
    FROM heads r JOIN trading.gas_instruments i USING(catalog_id,instrument_id)
    JOIN trading.trade_references tr ON(tr.catalog_id,tr.trade_id)=(r.catalog_id,r.trade_id)
    JOIN trading.instrument_references ir ON(ir.catalog_id,ir.instrument_id)=(r.catalog_id,r.instrument_id)
    JOIN trading.booking_facts f ON(f.catalog_id,f.trade_id)=(r.catalog_id,r.trade_id)
    JOIN trading.legal_entities e ON(e.catalog_id,e.id,e.revision)=(r.catalog_id,r.legal_entity_id,r.legal_entity_revision)
    JOIN trading.counterparties p ON(p.catalog_id,p.id,p.revision)=(r.catalog_id,r.counterparty_id,r.counterparty_revision)
    WHERE r.book_id=ANY($2::uuid[]) AND($3::uuid IS NULL OR r.trade_id=$3) ORDER BY tr.reference_number,r.instrument_id)x
   """,[manifest.CatalogId,selected.Keys.ToArray(),(position?.TradeId??trade) is {} tradeId?(object)tradeId:DBNull.Value],ct);
  if(position is not null)position.Require(heads);
  if(heads.IsEmpty)return [];
  var physical=await PreviewDatabase.Rows(connection,"""
   SELECT row_to_json(x)::text FROM(
    SELECT m.*,a.reference_catalog_id,a.reference_revision_id,a.curve_code,a.pipeline_code,a.zone_code,a.point_code,a.product_code,c.name curve_name,
     q.quantity_code,q.currency_code,q.multiplier,n.commodity_code,co.name commodity_name,u.symbol unit,cu.display_code currency
    FROM(
     SELECT i.catalog_id,i.instrument_id,i.definition_revision_id,i.assignment_code,0 slot,i.delivery_from,i.delivery_to
      FROM trading.gas_instruments i WHERE i.catalog_id=$1 AND i.instrument_id=ANY($2::uuid[]) AND NOT i.composition
     UNION ALL SELECT m.catalog_id,m.instrument_id,m.definition_revision_id,m.assignment_code,m.slot,m.delivery_from,m.delivery_to
      FROM trading.gas_component_terms m WHERE m.catalog_id=$1 AND m.instrument_id=ANY($2::uuid[]) AND m.role='physical'
    )m
    JOIN market_data.gas_valuation_assignments a ON(a.catalog_id,a.revision_id,a.code)=(m.catalog_id,m.definition_revision_id,m.assignment_code)
    JOIN market_data.curves c ON(c.catalog_id,c.revision_id,c.code)=(a.catalog_id,a.revision_id,a.curve_code)
    JOIN ref_data.quote_specifications q ON(q.catalog_id,q.revision_id,q.code)=(a.reference_catalog_id,a.reference_revision_id,c.quote_code)
    JOIN ref_data.quantity_specifications n ON(n.catalog_id,n.revision_id,n.code)=(q.catalog_id,q.revision_id,q.quantity_code)
    JOIN ref_data.units u ON(u.catalog_id,u.revision_id,u.code)=(n.catalog_id,n.revision_id,n.unit_code)
    JOIN ref_data.commodities co ON(co.catalog_id,co.revision_id,co.code)=(n.catalog_id,n.revision_id,n.commodity_code)
    JOIN ref_data.currencies cu ON(cu.catalog_id,cu.revision_id,cu.code)=(q.catalog_id,q.revision_id,q.currency_code)
    ORDER BY m.instrument_id,m.slot)x
   """,[manifest.CatalogId,heads.Select(h=>h.Id("instrument_id")).Distinct().ToArray()],ct);
  var byInstrument=physical.ToLookup(p=>p.Id("instrument_id"));
  var result=ImmutableArray.CreateBuilder<GasPosition>();
  foreach(var head in heads)
  {
   var members=byInstrument[head.Id("instrument_id")].ToImmutableArray();
   if(members.IsEmpty||members.Select(m=>(m.Id("reference_catalog_id"),m.Id("reference_revision_id"),m.Text("quantity_code"),m.Text("currency_code"))).Distinct().Count()!=1)
    throw new MarketDataException("position_facts","The gas instrument requires its exact visible physical-side metadata and common native quote.");
   result.Add(new(head,selected[head.Id("book_id")],members));
  }
  return result.ToImmutable();
 }
}
