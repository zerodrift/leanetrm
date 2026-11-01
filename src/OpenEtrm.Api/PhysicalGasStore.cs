using System.Collections.Immutable;
using System.Globalization;
using System.Text.Json;
using Npgsql;
using OpenEtrm.MarketData;
using OpenEtrm.MarketData.Gas;
using OpenEtrm.MarketData.Trading;

namespace OpenEtrm.Api;

// The terms of a single-point gas trade, as it was booked: the retained terms of stored trades and the gas path a composition side selects.
public sealed record PhysicalGasTerms(Guid SnapshotId,string? Convention,string? KnowledgeMode,Guid OperationId,Guid BookId,
 Guid DefinitionRevisionId,string? Pipeline,string? Zone,string? DeliveryPoint,string? Product,string? DeliveryFrom,string? DeliveryTo,string? Quantity,string? QuantityBasis,
 string? Direction,string? FixedPrice,string? UsageFee,string? UsageTariffCode,string? UsageFeeDirection,string? PaymentAt,bool? Discounting,
 Guid? LegalEntityId,int? LegalEntityRevision,Guid? CounterpartyId,int? CounterpartyRevision,Guid? GasSetupId);
public sealed record PhysicalGasEditorRequest(FuturesReadRequest Context,Guid TradeId);

internal sealed record GasMeterBinding(ReferenceKey? Reference,Guid? SetupId,Guid CatalogId,string Code,string Pipeline,string Identifier,string QuantitySpecification,string ValidFrom,string ValidTo);

public sealed class PhysicalGasStore(PreviewDatabase database)
{
 public const string Convention="physical_gas_fixed_quantity_v4";
 private Guid Catalog=>database.Manifest.CatalogId;
 private static MarketDataException Invalid(string text)=>new("gas_input",text);
 private static string Required(string? s)=>!string.IsNullOrWhiteSpace(s)?s:throw Invalid("Choose every required gas term explicitly.");
 private static DateOnly Day(string? s)=>DateWorkspace.Date(Required(s));
 private static string Date(DateOnly d)=>d.ToString("yyyy-MM-dd",CultureInfo.InvariantCulture);
 private static T One<T>(IEnumerable<T> source){var values=source.Take(2).ToArray();return values.Length==1?values[0]:throw Invalid("The selected exact gas definition is missing or ambiguous.");}
 private async Task<ImmutableArray<JsonElement>> Assignments(NpgsqlConnection c,PreviewData data,Guid? setup,CancellationToken ct)=>
  await PreviewDatabase.Rows(c,"""
   SELECT row_to_json(x)::text FROM(SELECT a.*,coalesce(g.logical_code,g.code) group_label,CASE WHEN a.setup_id IS NULL THEN e.clock_code ELSE sp.clock_code END clock_code,CASE WHEN a.setup_id IS NULL THEN e.day_start_minute ELSE sp.day_start_minute END day_start_minute,p.name pipeline_name,z.name zone_name,l.name point_name,k.name product_name
   FROM market_data.gas_valuation_assignments a
   LEFT JOIN ref_data.gas_delivery_conventions e ON(e.catalog_id,e.revision_id,e.pipeline_code,e.zone_code,e.point_code,e.product_code)=(a.reference_catalog_id,a.reference_revision_id,a.pipeline_code,a.zone_code,a.point_code,a.product_code)
   LEFT JOIN market_data.gas_setup_points sp ON(sp.catalog_id,sp.setup_id,sp.pipeline_code,sp.zone_code,sp.point_code,sp.product_code)=(a.catalog_id,a.setup_id,a.pipeline_code,a.zone_code,a.point_code,a.product_code)
   JOIN market_data.gas_valuation_groups g ON(g.catalog_id,g.revision_id,g.code)=(a.catalog_id,a.revision_id,a.group_code)
   JOIN ref_data.locations p ON(p.catalog_id,p.revision_id,p.code)=(a.reference_catalog_id,a.reference_revision_id,a.pipeline_code)
   JOIN ref_data.locations z ON(z.catalog_id,z.revision_id,z.code)=(a.reference_catalog_id,a.reference_revision_id,a.zone_code)
   JOIN ref_data.locations l ON(l.catalog_id,l.revision_id,l.code)=(a.reference_catalog_id,a.reference_revision_id,a.point_code)
   JOIN ref_data.commodities k ON(k.catalog_id,k.revision_id,k.code)=(a.reference_catalog_id,a.reference_revision_id,a.product_code)
   WHERE a.catalog_id=$1 AND a.revision_id=$2 AND a.setup_id IS NOT DISTINCT FROM $3::uuid ORDER BY a.code)x
   """,[Catalog,data.DefinitionRevision.RevisionId,(object?)setup??DBNull.Value],ct);
 private static object ValuationDetails(PreviewData data,JsonElement row)
 {
  var curve=One(data.Definitions["curves"].Where(x=>x.Text("code")==row.Text("curve_code")));
  var quote=One(data.Quotes.Where(x=>x.Key.Code==curve.Text("quote_code")));
  var target=One(data.References["settlement_targets"].Where(x=>x.Text("code")==curve.Text("settlement_target_code")));
  if(target.Text("kind")!="index"||target.OptionalText("index_b_code") is not null||quote.Kind!=QuoteKind.Outright||curve.Text("commodity_code")!=row.Text("product_code"))
   throw Invalid("The physical valuation mapping requires one compatible outright index curve.");
  var unit=data.QuantityUnit(quote.Quantity.Code);
  var currency=One(data.References["currencies"].Where(x=>x.Text("code")==quote.Currency.Code)).Text("display_code");
  var location=One(data.References["locations"].Where(x=>x.Text("code")==curve.Text("location_code")));
  return new{quote=quote.Key.Code,group=row.Text("group_label"),groupAdjustment=row.Text("group_adjustment"),locationAdjustment=row.Text("location_adjustment"),index=target.Text("index_a_code"),curve=row.Text("curve_code"),marketLocation=curve.Text("location_code"),marketLocationName=location.Text("name"),quantityUnit=unit,
   priceUnit=currency+" / "+quote.Multiplier.ToString()+" "+unit,adjustment=row.Text("adjustment")};
 }
 private async Task<ImmutableArray<JsonElement>> Tariffs(NpgsqlConnection c,PreviewData data,Guid? setup,CancellationToken ct)=>
  await PreviewDatabase.Rows(c,"SELECT row_to_json(t)::text FROM market_data.gas_usage_tariffs t WHERE(catalog_id,revision_id,reference_catalog_id,reference_revision_id)=($1,$2,$3,$4) AND setup_id IS NOT DISTINCT FROM $5::uuid ORDER BY code",
   [Catalog,data.DefinitionRevision.RevisionId,data.ReferenceRevision.CatalogId,data.ReferenceRevision.RevisionId,(object?)setup??DBNull.Value],ct);
 internal async Task<string?> UsageFee(NpgsqlConnection c,PreviewData data,PhysicalGasTerms body,JsonElement mapping,CancellationToken ct)
 {
  if(body.UsageTariffCode is null)return body.UsageFee;
  if(body.UsageFee is not null)throw Invalid("Select either a configured usage tariff or a manual fee, not both.");
  var row=One((await Tariffs(c,data,body.GasSetupId,ct)).Where(t=>t.Text("code")==body.UsageTariffCode));
  var curve=One(data.Definitions["curves"].Where(v=>v.Text("code")==mapping.Text("curve_code")));
  if(row.Text("pipeline_code")!=body.Pipeline||row.Text("product_code")!=body.Product||row.Text("quote_code")!=curve.Text("quote_code")||
     Day(row.Text("valid_from"))>Day(body.DeliveryFrom)||Day(row.Text("valid_to"))<Day(body.DeliveryTo))
   throw Invalid("The usage tariff must cover this exact pipeline, product, quote and complete delivery period.");
  return row.Text("rate");
 }
 internal async Task<ImmutableArray<JsonElement>> Meters(NpgsqlConnection c,PreviewData data,GasValuationAssignment assignment,Guid? setup,CancellationToken ct)=>
  setup is null?await PreviewDatabase.Rows(c,"SELECT row_to_json(m)::text FROM ref_data.gas_meter_assignments m WHERE(catalog_id,revision_id,pipeline_code,zone_code,point_code,product_code)=($1,$2,$3,$4,$5,$6) ORDER BY valid_from,code",
   [data.ReferenceRevision.CatalogId,data.ReferenceRevision.RevisionId,assignment.Location.Pipeline.Code,assignment.Location.Zone.Code,assignment.Point.Code,assignment.Product.Code],ct):
  await PreviewDatabase.Rows(c,"SELECT row_to_json(m)::text FROM market_data.gas_setup_meters m WHERE(catalog_id,setup_id,pipeline_code,zone_code,point_code,product_code)=($1,$2,$3,$4,$5,$6) ORDER BY valid_from,code",
   [Catalog,setup.Value,assignment.Location.Pipeline.Code,assignment.Location.Zone.Code,assignment.Point.Code,assignment.Product.Code],ct);
 internal static GasMeterBinding? Meter(PreviewData data,ImmutableArray<JsonElement> meters,DateOnly day,Guid? setup)
 {
  var matched=meters.Where(m=>Day(m.Text("valid_from"))<=day&&day<Day(m.Text("valid_to"))).ToArray();
  if(matched.Length==0)return null;
  if(matched.Length!=1)throw Invalid("The operating day has ambiguous direct-meter assignments.");
  var row=matched[0];
  return new(setup is null?new ReferenceKey(data.ReferenceRevision,row.Text("code")):null,setup,setup is null?data.ReferenceRevision.CatalogId:data.DefinitionRevision.CatalogId,row.Text("code"),row.Text("pipeline_code"),row.Text("identifier"),row.Text("quantity_code"),row.Text("valid_from"),row.Text("valid_to"));
 }
 // The gas setup the bound context resolves, with its publication order, which names it; none where it resolves none.
 internal async Task<(Guid? Id,int? Publication)> CurrentSetup(NpgsqlConnection c,PreviewData data,CancellationToken ct)
 {
  await using var active=ScheduleStore.Command(c,"SELECT id,publication_order FROM market_data.gas_setup_versions WHERE(catalog_id,id)=($1,security.current_gas_setup($1,$2))",Catalog,data.DefinitionRevision.RevisionId);
  await using var selected=await active.ExecuteReaderAsync(ct);
  return await selected.ReadAsync(ct)?(selected.GetGuid(0),selected.GetInt32(1)):(null,null);
 }
 public async Task<object> CatalogRead(NpgsqlConnection c,PreviewData data,InputFrame frame,CancellationToken ct)
 {
  var (setup,publication)=await CurrentSetup(c,data,ct);
  var mappings=await Assignments(c,data,setup,ct);
  var paths=setup is null?await PreviewDatabase.Rows(c,"""
   SELECT row_to_json(x)::text FROM(SELECT a.*,e.clock_code,e.day_start_minute,c.commodity_code product_code,p.name pipeline_name,z.name zone_name,l.name point_name,k.name product_name
   FROM ref_data.pipeline_delivery_points a
   JOIN ref_data.commodity_locations c ON(c.catalog_id,c.revision_id,c.location_code)=(a.catalog_id,a.revision_id,a.point_code)
   JOIN ref_data.commodity_locations pc ON(pc.catalog_id,pc.revision_id,pc.location_code,pc.commodity_code)=(a.catalog_id,a.revision_id,a.pipeline_code,c.commodity_code)
   JOIN ref_data.gas_delivery_conventions e ON(e.catalog_id,e.revision_id,e.pipeline_code,e.zone_code,e.point_code,e.product_code)=(a.catalog_id,a.revision_id,a.pipeline_code,a.zone_code,a.point_code,c.commodity_code)
   JOIN ref_data.locations p ON(p.catalog_id,p.revision_id,p.code)=(a.catalog_id,a.revision_id,a.pipeline_code)
   JOIN ref_data.locations z ON(z.catalog_id,z.revision_id,z.code)=(a.catalog_id,a.revision_id,a.zone_code)
   JOIN ref_data.locations l ON(l.catalog_id,l.revision_id,l.code)=(a.catalog_id,a.revision_id,a.point_code)
   JOIN ref_data.commodities k ON(k.catalog_id,k.revision_id,k.code)=(a.catalog_id,a.revision_id,c.commodity_code)
   WHERE a.catalog_id=$1 AND a.revision_id=$2 ORDER BY p.name,z.name,l.name)x
   """,[data.ReferenceRevision.CatalogId,data.ReferenceRevision.RevisionId],ct):
  await PreviewDatabase.Rows(c,"""
   SELECT row_to_json(x)::text FROM(SELECT a.*,p.name pipeline_name,z.name zone_name,l.name point_name,k.name product_name
   FROM market_data.gas_setup_points a
   JOIN ref_data.locations p ON(p.catalog_id,p.revision_id,p.code)=(a.reference_catalog_id,a.reference_revision_id,a.pipeline_code)
   JOIN ref_data.locations z ON(z.catalog_id,z.revision_id,z.code)=(a.reference_catalog_id,a.reference_revision_id,a.zone_code)
   JOIN ref_data.locations l ON(l.catalog_id,l.revision_id,l.code)=(a.reference_catalog_id,a.reference_revision_id,a.point_code)
   JOIN ref_data.commodities k ON(k.catalog_id,k.revision_id,k.code)=(a.reference_catalog_id,a.reference_revision_id,a.product_code)
   WHERE(a.catalog_id,a.setup_id)=($1,$2) ORDER BY p.name,z.name,l.name)x
   """,[Catalog,setup.Value],ct);
  var locations=paths.Select(path=>{
   var matched=mappings.Where(m=>new[]{"pipeline_code","zone_code","point_code","product_code"}.All(key=>m.Text(key)==path.Text(key))).ToArray();
   if(matched.Length>1)throw Invalid("The physical path has ambiguous valuation mappings.");
   return new{definitionRevisionId=data.DefinitionRevision.RevisionId,pipeline=path.Text("pipeline_code"),pipelineName=path.Text("pipeline_name"),zone=path.Text("zone_code"),zoneName=path.Text("zone_name"),
    deliveryPoint=path.Text("point_code"),deliveryPointName=path.Text("point_name"),clock=path.Text("clock_code"),dayStartMinute=int.Parse(path.Text("day_start_minute"),CultureInfo.InvariantCulture),product=path.Text("product_code"),productName=path.Text("product_name"),
    valuation=matched.Length==1?ValuationDetails(data,matched[0]):null};
  }).ToArray();
  var books=await PreviewDatabase.Rows(c,"SELECT row_to_json(b)::text FROM trading.books b WHERE catalog_id=$1 ORDER BY name",[Catalog],ct);
  return new{gasSetupId=setup,gasSetupPublication=publication,locations,tariffs=(await Tariffs(c,data,setup,ct)).Select(t=>new{code=t.Text("code"),name=t.OptionalText("logical_code")??t.Text("code"),pipeline=t.Text("pipeline_code"),product=t.Text("product_code"),quote=t.Text("quote_code"),rate=t.Text("rate"),validFrom=t.Text("valid_from"),validTo=t.Text("valid_to"),basis="contractual_delivery_quantity"}),books,legalEntities=await TradeParties.Catalog(c,Catalog,true,ct),counterparties=await TradeParties.Catalog(c,Catalog,false,ct),
   scope="Future physical gas delivery with fixed contractual quantities, separate fixed consideration and optional usage fee. This profile admits up to 31 days within one forward month, fixed-offset gas days and no discounting. Actuals and settlement remain unavailable."};
 }
 internal async Task<(GasValuationAssignment Assignment,GasDayClock Clock,JsonElement Mapping)> Metadata(NpgsqlConnection c,PreviewData data,PhysicalGasTerms body,CancellationToken ct)
 {
  if(body.Convention!=Convention||body.KnowledgeMode!="current"||body.Discounting!=false||body.DefinitionRevisionId!=data.DefinitionRevision.RevisionId)
   throw Invalid("Select current exact gas inputs and explicit no-discounting policy.");
  var matched=(await Assignments(c,data,body.GasSetupId,ct)).Where(x=>x.Text("pipeline_code")==body.Pipeline&&x.Text("zone_code")==body.Zone&&x.Text("point_code")==body.DeliveryPoint&&x.Text("product_code")==body.Product).ToArray();
  if(matched.Length==0)throw Invalid("No valuation index is configured for the selected pipeline, zone, delivery location and product.");
  var row=One(matched);
  if(row.Id("reference_catalog_id")!=data.ReferenceRevision.CatalogId||row.Id("reference_revision_id")!=data.ReferenceRevision.RevisionId)throw Invalid("The assignment requires its exact reference revision.");
  var from=Day(body.DeliveryFrom);var to=Day(body.DeliveryTo);
  if(from>=to||to.DayNumber-from.DayNumber>31)throw Invalid("Choose a gas delivery period of 1–31 operating days within one supported forward month.");
  var curve=One(data.Definitions["curves"].Where(x=>x.Text("code")==row.Text("curve_code")));
  if(curve.Text("commodity_code")!=row.Text("product_code"))throw Invalid("The selected gas product must match its mapped valuation curve.");
  var quote=One(data.Quotes.Where(x=>x.Key.Code==curve.Text("quote_code")));
  var target=One(data.References["settlement_targets"].Where(x=>x.Text("code")==curve.Text("settlement_target_code")));
  if(quote.Kind!=QuoteKind.Outright||target.Text("kind")!="index")throw Invalid("Gas valuation requires an outright index forward.");
  var members=data.Definitions["curve_members"].Where(x=>x.Text("curve_code")==row.Text("curve_code")).Select(x=>x.Id("contract_id")).ToHashSet();
  var contract=One(data.Contracts.Where(x=>members.Contains(x.Key.Id)&&x.Kind==ContractKind.Forward&&x.DeliveryFrom<=from&&x.DeliveryTo>=to&&x.DeliveryFrom.Day==1&&x.DeliveryFrom.AddMonths(1)==x.DeliveryTo));
  ReferenceKey R(string code)=>new(data.ReferenceRevision,code);
  var selection=PriceSettlementSelection.CurveTarget(R(target.Text("index_a_code")));
  var fixing=One(PriceSettlementInputs.Fixings(data,selection,quote,[contract]));
  var assignment=new GasValuationAssignment(R(row.Text("code")),new(R(row.Text("pipeline_code")),R(row.Text("zone_code")),R(row.Text("point_code"))),R(row.Text("product_code")),contract.DeliveryFrom,contract.DeliveryTo,selection,quote,
   new(new(new(data.DefinitionRevision,row.Text("curve_code")),contract.Key),contract),fixing,new(ExactDecimal.Parse(row.Text("adjustment")),quote));
  var clockCode=row.Text("clock_code");
  if(One(data.References["clock_rules"].Where(x=>x.Text("code")==clockCode)).Text("kind")!="fixed_offset")throw Invalid("This booking profile requires a fixed-offset delivery clock.");
  var clock=new GasDayClock(R(clockCode),PpaQuantitiesStore.Clock(data,clockCode).Rules,int.Parse(row.Text("day_start_minute"),CultureInfo.InvariantCulture));
  return(assignment,clock,row);
 }
 internal static async Task RequirePortfolioScope(NpgsqlConnection c,Guid catalog,ImmutableArray<Guid> books,ImmutableArray<Guid> portfolios,CancellationToken ct)
 {
  await using var q=ScheduleStore.Command(c,"SELECT security.require_supported_gas_portfolio_scope($1,$2::uuid[],$3::uuid[])",catalog,books.ToArray(),portfolios.ToArray());
  try{await q.ExecuteNonQueryAsync(ct);}catch(PostgresException e)when(e.MessageText=="gas_portfolio_scope"){
   throw new MarketDataException("gas_portfolio_scope","This close contains physical gas obligations outside its supported valuation and settlement conventions. Use the trading workspace for contractual gas PV and explicit portfolio-risk exclusions.");
  }
 }
 public async Task<JsonElement> Status(NpgsqlConnection c,Guid operation,CancellationToken ct)
 {
  await using var q=ScheduleStore.Command(c,"SELECT security.gas_operation_status($1,$2)::text",Catalog,operation);
  return JsonSerializer.Deserialize<JsonElement>((string)(await q.ExecuteScalarAsync(ct))!);
 }
 // A booked single-point trade's retained terms, read as the request that booked it under this read's snapshot, and its instrument.
 internal async Task<(PhysicalGasTerms Terms,Guid Instrument)> Retained(NpgsqlConnection c,InputFrame frame,Guid trade,CancellationToken ct)
 {
  var row=One(await PreviewDatabase.Rows(c,"SELECT row_to_json(x)::text FROM(SELECT r.*,i.definition_revision_id,i.assignment_code,i.delivery_from,i.delivery_to,i.quantity_basis,i.fixed_price,i.usage_fee,i.usage_tariff_code,i.usage_fee_direction,i.gas_setup_id,i.payment_at,a.pipeline_code,a.zone_code,a.point_code,a.product_code FROM trading.gas_trade_revisions r JOIN trading.gas_instruments i USING(catalog_id,instrument_id) JOIN market_data.gas_valuation_assignments a ON(a.catalog_id,a.revision_id,a.code)=(i.catalog_id,i.definition_revision_id,i.assignment_code) WHERE r.catalog_id=$1 AND r.trade_id=$2 AND NOT i.composition)x",[Catalog,trade],ct));
  var body=new PhysicalGasTerms(frame.Id,Convention,"current",Guid.Empty,row.Id("book_id"),row.Id("definition_revision_id"),row.Text("pipeline_code"),row.Text("zone_code"),row.Text("point_code"),row.Text("product_code"),row.Text("delivery_from"),row.Text("delivery_to"),row.Text("quantity"),row.Text("quantity_basis"),row.Text("direction")=="1"?"receive":"deliver",row.Text("fixed_price"),row.OptionalText("usage_tariff_code") is null?row.OptionalText("usage_fee"):null,row.OptionalText("usage_tariff_code"),row.OptionalText("usage_fee_direction") switch{"-1"=>"pay","1"=>"receive",_=>null},row.Text("payment_at"),false,row.Id("legal_entity_id"),int.Parse(row.Text("legal_entity_revision"),CultureInfo.InvariantCulture),row.Id("counterparty_id"),int.Parse(row.Text("counterparty_revision"),CultureInfo.InvariantCulture),row.OptionalText("gas_setup_id") is {} gasSetup?Guid.Parse(gasSetup):null);
  return(body,row.Id("instrument_id"));
 }
}
