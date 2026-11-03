using System.Collections.Immutable;
using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using Npgsql;
using OpenEtrm.MarketData;
using OpenEtrm.MarketData.Crude;
using OpenEtrm.MarketData.Power;
using OpenEtrm.MarketData.Trading;

namespace OpenEtrm.Api;

[System.Text.Json.Serialization.JsonUnmappedMemberHandling(System.Text.Json.Serialization.JsonUnmappedMemberHandling.Disallow)]
public sealed record PhysicalCrudeWriteRequest(Guid SnapshotId,string? Convention,string? KnowledgeMode,string? Profile,
 Guid OperationId,Guid BookId,Guid DefinitionRevisionId,string? AssignmentCode,string? DeliveryFrom,string? DeliveryTo,
 string? Quantity,string? QuantityBasis,string? Tolerance,string? Direction,string? ConsiderationDirection,string? FixedPrice,string? PaymentAt,bool? Discounting,
 [property:System.Text.Json.Serialization.JsonRequired]string? HandlingFee,[property:System.Text.Json.Serialization.JsonRequired]string? HandlingFeeDirection,
 Guid? LegalEntityId,int? LegalEntityRevision,Guid? CounterpartyId,int? CounterpartyRevision,
 [property:System.Text.Json.Serialization.JsonIgnore(Condition=System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]Guid? CrudeSetupId=null,
 [property:System.Text.Json.Serialization.JsonIgnore(Condition=System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]CrudePhysicalSelection? Physical=null);
public sealed record PhysicalCrudeEditorRequest(FuturesReadRequest Context,Guid TradeId);

[System.Text.Json.Serialization.JsonUnmappedMemberHandling(System.Text.Json.Serialization.JsonUnmappedMemberHandling.Disallow)]
public sealed record CrudePhysicalSelection(Guid ReferenceCatalogId,Guid ReferenceRevisionId,string Commodity,string Grade,
 string Facility,string CustodyPoint,string QuantityCode,string Clock);
[System.Text.Json.Serialization.JsonUnmappedMemberHandling(System.Text.Json.Serialization.JsonUnmappedMemberHandling.Disallow)]
public sealed record CrudeResolveRequest(Guid SnapshotId,string? Convention,string? KnowledgeMode,
 CrudePhysicalSelection Physical,string DeliveryFrom,string DeliveryTo);

public sealed class PhysicalCrudeStore(PreviewDatabase database)
{
 public const string Convention="physical_crude_fixed_volume_v1";
 public const string Profile="physical_crude_standard_volume_v1";
 // The one admitted profile's name; any other retained code is refused rather than shown as a code.
 public static string ProfileName(string? code)=>code==Profile?"Fixed standard volume at 60°F · zero tolerance · undiscounted":throw Invalid("The crude trade names an unsupported contract profile.");
 private Guid Catalog=>database.Manifest.CatalogId;
 private static readonly JsonSerializerOptions Json=new(JsonSerializerDefaults.Web);
 private static MarketDataException Invalid(string message)=>new("crude_input",message);
 private static string Required(string? value)=>!string.IsNullOrWhiteSpace(value)?value:throw Invalid("Choose every required crude term explicitly.");
 private static T One<T>(IEnumerable<T> values){var items=values.Take(2).ToArray();return items.Length==1?items[0]:throw Invalid("The selected exact crude definition is missing or ambiguous.");}
 private static ExactDecimal Sign(string? value,bool physical=false)=>value switch
 {"receive"=>ExactDecimal.Parse("1"),"pay" when !physical=>ExactDecimal.Parse("-1"),"deliver" when physical=>ExactDecimal.Parse("-1"),_=>throw Invalid("Choose each side direction explicitly.")};
 private static DateTimeOffset Instant(string? value)=>TradingInstant.Parse(Required(value),"crude_input");
 public async Task<object> References(NpgsqlConnection c,CancellationToken ct)
 {
  var revision=await new CrudeSetupEditor(database).DeliveryRevision(c,ct);
  return new{deliveryReferences=await CrudeDeliveryReferences.Read(c,revision,ct),clocks=await CrudeSetupEditor.ProfileClocks(c,revision,ct),
   books=await PreviewDatabase.Rows(c,"SELECT row_to_json(b)::text FROM trading.books b WHERE catalog_id=$1 ORDER BY name",[Catalog],ct),
   legalEntities=await TradeParties.Catalog(c,Catalog,true,ct),counterparties=await TradeParties.Catalog(c,Catalog,false,ct),
   canBook=await PreviewDatabase.HasPermission(c,"BookTrade",ct),
   profiles=new[]{new{code=Profile,quantityBasis="standard_volume_total",tolerance="0",discounting=false,label=ProfileName(Profile)}},
   scope="Select the physical commodity, grade, facility, custody point, volume specification and clock separately. Pricing is resolved from their maintained mapping and complete delivery interval. Separate-master delivery-to-cash remains unsupported."};
 }
 internal async Task RecheckReferences(NpgsqlConnection c,CancellationToken ct)
 {
  var definition=database.Manifest.ReferenceCatalog?.DefinitionRevisionId??database.Manifest.RevisionId;
  var revision=database.Manifest.ReferenceCatalog?.ReferenceRevisionId??database.Manifest.ReferenceRevisionId;
  await using var authority=ScheduleStore.Command(c,"""
   SELECT security.market_access($1,'ReadMarketData') AND EXISTS(
    SELECT 1 FROM market_data.revisions r WHERE(r.catalog_id,r.id,r.reference_revision_id)=($1,$2,$3) AND r.state='sealed'
    AND security.market_fact_visible(r.catalog_id,r.id,NULL) AND security.reference_access(r.reference_catalog_id,'ReadMarketData')
    AND security.reference_version_visible(r.reference_catalog_id,r.reference_revision_id))
   """,Catalog,definition,revision);
  if(!Equals(await authority.ExecuteScalarAsync(ct),true))throw new PreviewDeniedException();
 }

 private async Task<JsonElement> ResolveMapping(NpgsqlConnection c,CrudePhysicalSelection physical,DateTimeOffset from,DateTimeOffset to,DateTimeOffset valuation,CancellationToken ct)
 {
  await using var query=ScheduleStore.Command(c,"SELECT security.resolve_crude_delivery($1,$2::jsonb,$3,$4,$5)::text",
   Catalog,JsonSerializer.Serialize(physical,Json),from,to,valuation);
  return JsonSerializer.Deserialize<JsonElement>((string)(await query.ExecuteScalarAsync(ct))!);
 }

 public async Task<object> ResolveReference(NpgsqlConnection c,InputFrame frame,CrudeResolveRequest body,CancellationToken ct)
 {
  if(body.Convention!=Convention||body.KnowledgeMode!="current")throw Invalid("Resolve physical terms under the explicit current crude contract.");
  var revision=await new CrudeSetupEditor(database).DeliveryRevision(c,ct);
  if(body.Physical is null||body.Physical.ReferenceCatalogId!=revision.CatalogId||body.Physical.ReferenceRevisionId!=revision.RevisionId)
   throw Invalid("Use the exact declared delivery reference revision.");
  var selected=await ResolveMapping(c,body.Physical,Instant(body.DeliveryFrom),Instant(body.DeliveryTo),frame.At,ct);
  var sourceRevision=selected.Id("definitionRevisionId");
  _=CrudeSetupEditor.SourceFrame(database.Manifest,frame,sourceRevision);
  var data=await database.Read(c,ct,selectedRevision:sourceRevision,observationIds:[],observationScope:ObservationReadScope.ExactPins);
  var row=One((await Assignments(c,data,ct)).Where(a=>a.Text("code")==selected.Text("assignmentCode")));
  // This validates physical quantity equivalence and monthly coverage without reading a mark.
  var request=new PhysicalCrudeWriteRequest(frame.Id,Convention,"current",Profile,Guid.Empty,Guid.Empty,sourceRevision,
   row.Text("code"),body.DeliveryFrom,body.DeliveryTo,"1","standard_volume_total","0","receive","pay","0",body.DeliveryTo,false,
   null,null,null,null,null,null,row.OptionalId("setup_id"),body.Physical);
  _=await Metadata(c,data,request,ct,frame.At);
  var curve=One(data.Definitions["curves"].Where(r=>r.Text("code")==row.Text("curve_code")));
  var quote=One(data.Quotes.Where(q=>q.Key.Code==curve.Text("quote_code")));
  var unit=Unit(data,quote.Quantity);
  var currency=One(data.References["currencies"].Where(r=>r.Text("code")==quote.Currency.Code)).Text("display_code");
  return new{code=row.Text("code"),definitionRevisionId=sourceRevision,crudeSetupId=row.OptionalId("setup_id"),
   physical=body.Physical,curve=curve.Text("code"),curveName=curve.Text("name"),
   quantityUnit=unit,priceUnit=Presentation.PriceUnitLabel.Format(PriceUnit.FromQuote(quote),currency,unit)};
 }
 private async Task<ImmutableArray<JsonElement>> Assignments(NpgsqlConnection c,PreviewData data,CancellationToken ct,bool currentOnly=false)
 {
  var heads=currentOnly?await CrudeSetupEditor.CurrentSetups(c,Catalog,ct):null;
  return await PreviewDatabase.Rows(c,"""
  SELECT row_to_json(x)::text FROM(SELECT a.*,v.code setup_code,g.name grade_name,g.quantity_code,g.standard_temperature,g.temperature_scale,
   k.name commodity_name,f.name facility_name,p.name custody_point_name
  FROM market_data.crude_valuation_assignments a
  LEFT JOIN market_data.crude_setup_versions v ON(v.catalog_id,v.id)=(a.catalog_id,a.setup_id)
  JOIN ref_data.crude_grades g ON(g.catalog_id,g.revision_id,g.code,g.commodity_code)=(a.delivery_catalog_id,a.delivery_revision_id,a.grade_code,a.commodity_code)
  JOIN ref_data.commodities k ON(k.catalog_id,k.revision_id,k.code)=(a.delivery_catalog_id,a.delivery_revision_id,a.commodity_code)
  JOIN ref_data.locations f ON(f.catalog_id,f.revision_id,f.code)=(a.delivery_catalog_id,a.delivery_revision_id,a.facility_code)
  JOIN ref_data.locations p ON(p.catalog_id,p.revision_id,p.code)=(a.delivery_catalog_id,a.delivery_revision_id,a.custody_point_code)
  WHERE(a.catalog_id,a.revision_id)=($1,$2) AND($3::uuid[] IS NULL OR a.setup_id IS NULL OR a.setup_id=ANY($3::uuid[]))
  ORDER BY a.setup_id NULLS FIRST,a.code)x
  """,[Catalog,data.DefinitionRevision.RevisionId,(object?)heads??DBNull.Value],ct);
 }
 private static string Unit(PreviewData data,ReferenceKey quantity)
 {
  var specification=One(data.References["quantity_specifications"].Where(x=>x.Text("code")==quantity.Code));
  return One(data.References["units"].Where(x=>x.Text("code")==specification.Text("unit_code"))).Text("symbol");
 }
 public async Task<object> CatalogRead(NpgsqlConnection c,PreviewData data,InputFrame frame,CancellationToken ct)
 {
  var assignments=(await Assignments(c,data,ct,true)).Select(row=>{
   var curve=One(data.Definitions["curves"].Where(x=>x.Text("code")==row.Text("curve_code")));
   var quote=One(data.Quotes.Where(x=>x.Key.Code==curve.Text("quote_code")));
   var unit=Unit(data,quote.Quantity);
   var currency=One(data.References["currencies"].Where(x=>x.Text("code")==quote.Currency.Code)).Text("display_code");
   return new{code=row.Text("code"),crudeSetupId=row.OptionalText("setup_id"),bindingName=row.OptionalText("setup_code")??row.Text("code"),definitionRevisionId=data.DefinitionRevision.RevisionId,commodity=row.Text("commodity_code"),commodityName=row.Text("commodity_name"),grade=row.Text("grade_code"),gradeName=row.Text("grade_name"),facility=row.Text("facility_code"),facilityName=row.Text("facility_name"),custodyPoint=row.Text("custody_point_code"),custodyPointName=row.Text("custody_point_name"),quantityCode=quote.Quantity.Code,quantityName=One(data.References["quantity_specifications"].Where(q=>q.Text("code")==quote.Quantity.Code)).Text("code")+" · "+unit,quantityUnit=unit,priceUnit=Presentation.PriceUnitLabel.Format(PriceUnit.FromQuote(quote),currency,unit),curve=row.Text("curve_code"),curveName=curve.Text("name"),clock=row.Text("clock_code"),clockName=row.Text("clock_code"),standardTemperature=row.Text("standard_temperature"),temperatureScale=row.Text("temperature_scale")};
  }).ToArray();
  return new{assignments,books=await PreviewDatabase.Rows(c,"SELECT row_to_json(b)::text FROM trading.books b WHERE catalog_id=$1 ORDER BY name",[Catalog],ct),
   legalEntities=await TradeParties.Catalog(c,Catalog,true,ct),counterparties=await TradeParties.Catalog(c,Catalog,false,ct),canBook=await PreviewDatabase.HasPermission(c,"BookTrade",ct),
   profiles=new[]{new{code=Profile,quantityBasis="standard_volume_total",tolerance="0",discounting=false,label=ProfileName(Profile)}},
   scope="Future fixed total standard volume at 60°F, one exact grade and custody point within one forward month. Independently directed physical delivery, fixed consideration and optional handling fee. Imported assignments retain their supported Delivery and cash workflow. Separately maintained delivery setups currently support contractual booking and pre-delivery valuation; their receipt/inventory/cash workflow is not yet qualified."};
 }
 internal async Task<CrudeValuationAssignment> Metadata(NpgsqlConnection c,PreviewData data,PhysicalCrudeWriteRequest body,CancellationToken ct,DateTimeOffset? resolveAt=null)
 {
  if(body.Convention!=Convention||body.KnowledgeMode!="current"||body.Profile!=Profile||body.QuantityBasis!="standard_volume_total"||body.Tolerance is null||ExactDecimal.Parse(body.Tolerance)!=default||body.Discounting!=false||body.DefinitionRevisionId!=data.DefinitionRevision.RevisionId)
   throw Invalid("Choose the explicit fixed-total 60°F standard-volume, zero-tolerance, undiscounted profile and exact current source revision.");
  var row=One((await Assignments(c,data,ct)).Where(x=>x.Text("code")==body.AssignmentCode));
  var physical=new CrudePhysicalSelection(row.Id("delivery_catalog_id"),row.Id("delivery_revision_id"),row.Text("commodity_code"),row.Text("grade_code"),
   row.Text("facility_code"),row.Text("custody_point_code"),row.Text("quantity_code"),row.Text("clock_code"));
  if(body.Physical is not null&&body.Physical!=physical)throw Invalid("The resolved pricing pins do not match the selected physical parameters.");
  if(resolveAt is {} valuation)
  {
   var resolved=await ResolveMapping(c,physical,Instant(body.DeliveryFrom),Instant(body.DeliveryTo),valuation,ct);
   if(resolved.Id("definitionRevisionId")!=body.DefinitionRevisionId||resolved.Text("assignmentCode")!=body.AssignmentCode||resolved.OptionalId("crudeSetupId")!=body.CrudeSetupId)
    throw new MarketDataException("crude_setup_conflict","The physical parameters resolve to a different current pricing mapping. Resolve and preview the current terms again.");
  }
  var setup=row.OptionalText("setup_id") is {} setupText?Guid.Parse(setupText):(Guid?)null;
  if(setup!=body.CrudeSetupId)throw Invalid("Select the exact published delivery setup associated with this assignment.");
  if(setup is {} id)
  {
   var editor=new CrudeSetupEditor(database);
   return await editor.Assignment(c,data,await editor.Version(c,id,ct),new(Instant(body.DeliveryFrom),Instant(body.DeliveryTo)),ct);
  }
  if(row.Id("reference_catalog_id")!=data.ReferenceRevision.CatalogId||row.Id("reference_revision_id")!=data.ReferenceRevision.RevisionId)throw Invalid("The assignment requires its exact reference revision.");
  var window=new CrudeDeliveryWindow(Instant(body.DeliveryFrom),Instant(body.DeliveryTo));
  var curve=One(data.Definitions["curves"].Where(x=>x.Text("code")==row.Text("curve_code")));
  var quote=One(data.Quotes.Where(x=>x.Key.Code==curve.Text("quote_code")));
  var target=One(data.References["settlement_targets"].Where(x=>x.Text("code")==curve.Text("settlement_target_code")));
  if(curve.Text("commodity_code")!=row.Text("commodity_code")||target.Text("kind")!="index"||target.OptionalText("index_b_code") is not null||quote.Kind!=QuoteKind.Outright||quote.Quantity.Code!=row.Text("quantity_code"))throw Invalid("The assignment requires one compatible outright index forward and exact standard-volume quantity.");
  var members=data.Definitions["curve_members"].Where(x=>x.Text("curve_code")==row.Text("curve_code")).Select(x=>x.Id("contract_id")).ToHashSet();
  var contract=One(data.Contracts.Where(x=>members.Contains(x.Key.Id)&&window.IsCoveredBy(x)));
  ReferenceKey R(string code)=>new(data.ReferenceRevision,code);
  var q=One(data.References["quantity_specifications"].Where(x=>x.Text("code")==row.Text("quantity_code")));
  var quantity=new QuantitySpecification(R(q.Text("code")),R(q.Text("commodity_code")),R(q.Text("basis_code")),R(q.Text("unit_code")),R(q.Text("dimension_code")));
  var clock=row.Text("clock_code");
  if(One(data.References["clock_rules"].Where(x=>x.Text("code")==clock)).Text("kind")!="fixed_offset"||One(data.References["clock_rules"].Where(x=>x.Text("code")==clock)).Text("fixed_offset_seconds")!="0")throw Invalid("The crude profile requires an explicit UTC clock.");
  var selection=PriceSettlementSelection.CurveTarget(R(target.Text("index_a_code")));
  return new(R(row.Text("code")),R(row.Text("commodity_code")),R(row.Text("grade_code")),new(R(row.Text("facility_code")),R(row.Text("custody_point_code"))),quantity,R(clock),ExactDecimal.Parse(row.Text("standard_temperature")),row.Text("temperature_scale"),selection,quote,new(new(new(data.DefinitionRevision,row.Text("curve_code")),contract.Key),contract),One(PriceSettlementInputs.Fixings(data,selection,quote,[contract])));
 }
 private async Task<(PhysicalCrudeInstrument Instrument,CrudeValuationAssignment Assignment,ExactDecimal Quantity)> Resolve(NpgsqlConnection c,PreviewData data,PhysicalCrudeWriteRequest body,DateTimeOffset valuation,CancellationToken ct)
 {
  var assignment=await Metadata(c,data,body,ct,valuation);var quantity=ExactDecimal.Parse(Required(body.Quantity));
  if(quantity.CompareTo(default)<=0)throw Invalid("The standard-volume quantity must be positive and below 10^24.");
  var physical=new PhysicalCrudeSide(Guid.NewGuid(),assignment,Instant(body.DeliveryFrom),Instant(body.DeliveryTo),ExactDecimal.Parse(Required(body.Tolerance)));
  var payment=Instant(body.PaymentAt);
  if(payment<physical.DeliveryTo)throw Invalid("Payment must be at or after delivery ends.");
  FixedSide Cash(string value)
  {
   var amount=ExactDecimal.Parse(value);
   if(amount.CompareTo(ExactDecimal.Parse("1000000000000"))>=0||amount.CompareTo(ExactDecimal.Parse("-1000000000000"))<=0)throw Invalid("Cash prices must have absolute value below 10^12.");
   return new(Guid.NewGuid(),new FlatPrice(new UnitPrice(amount,assignment.Quote)),assignment.Quote,physical.UnitQuantity,[new(physical.DeliveryDateFrom,physical.DeliveryDateTo,payment,new(System.Numerics.BigInteger.One,System.Numerics.BigInteger.One),null)]);
  }
  if(body.HandlingFee is null&&body.HandlingFeeDirection is not null)throw Invalid("A handling-fee direction requires a handling fee.");
  return(new(Guid.NewGuid(),physical,Cash(Required(body.FixedPrice)),body.HandlingFee is {} fee?Cash(fee):null,Sign(body.Direction,true),Sign(body.ConsiderationDirection),body.HandlingFee is null?null:Sign(body.HandlingFeeDirection)),assignment,quantity);
 }
 private async Task<object> Value(NpgsqlConnection c,PreviewData data,InputFrame frame,PhysicalCrudeWriteRequest body,PhysicalCrudeInstrument? draft,CrudeValuationAssignment assignment,ExactDecimal quantity,ImmutableArray<RetainedSideProgram>? retained,CancellationToken ct)
 {
  if(frame.ContextId is not {} contextId)throw Invalid("Crude valuation requires a certified calculation context.");
  if(frame.At>=Instant(body.DeliveryFrom))throw new MarketDataException("crude_lifecycle","This valuation profile applies only before delivery starts. Use Delivery and cash to inspect accepted receipts, invoices and payments.");
  var programs=retained??[..draft!.Sides.Select((m,n)=>{var d=ScheduleDefinitionCompiler.Generate(m.Side,ct);var p=BookedSideProgram.Compile(d);return new RetainedSideProgram(n,m.Side.Id,Guid.Empty,new(BookedSideProgram.Digest(p.Write()),d.Manifest,d.Family,d.EconomicIdentity,d.PricingBindingIdentity),p);})];
  var context=(await new KnowledgeStore(Catalog).Read(c,contextId,ct)).Context;
  var snapshot=MarketSnapshotBinding.Bind(data.Snapshot(frame),contextId,SchedulePreview.ParseAt(context.KnownAt));
  var deps=new[]{assignment.Lookup}.Select(x=>PeriodPriceSelector.ProjectionAt(x,snapshot)).OfType<CurveRequest>().Distinct().ToImmutableArray();
  var market=data.Model.Build(snapshot,deps,ct);var sources=ScheduleMarketSources.ForPhysicalCrude(market,[assignment]);
  var discount=new ExplicitDiscounting(ExactDecimal.Parse("1"),DiscountFactorSource.Undiscounted);
  var sides=new List<object>();ExactMoney? total=null;
  foreach(var program in programs)
  {
   var coefficient=program.Slot switch{0=>Sign(body.Direction,true),1=>Sign(body.ConsiderationDirection),2 when body.HandlingFee is not null=>Sign(body.HandlingFeeDirection),_=>throw Invalid("Unexpected retained crude membership.")};
   var result=sources.Evaluate(program.Program,program.SideId,market,discount,ct);
   var amount=result.PresentValue.Scale(quantity*coefficient);total=total is null?amount:total.Add(amount);
   sides.Add(new{role=program.Slot switch{0=>"delivery",1=>"consideration",_=>"handling_fee"},direction=program.Slot switch{0=>body.Direction,1=>body.ConsiderationDirection,_=>body.HandlingFeeDirection},presentValue=amount.ToExactString(),quantity=quantity.ToString()});
  }
  if(body.CrudeSetupId is {} setup)
  {
   await using var authority=ScheduleStore.Command(c,"SELECT security.crude_setup_visible($1,$2)",Catalog,setup);
   if(!Equals(await authority.ExecuteScalarAsync(ct),true))throw new PreviewDeniedException();
  }
  var currency=One(data.References["currencies"].Where(x=>x.Text("code")==assignment.Quote.Currency.Code)).Text("display_code");
  var unit=Unit(data,assignment.Quantity.Key);
  var source=One(data.Definitions["curves"].Where(x=>x.Text("code")==assignment.Projection.Market.Curve.Code)).Text("name");
  return new{presentValue=total!.ToExactString(),currency,sides,body.DeliveryFrom,body.DeliveryTo,quantity=quantity.ToString(),quantityUnit=unit,
   pricingSource=source,priceUnit=Presentation.PriceUnitLabel.Format(PriceUnit.FromQuote(assignment.Quote),currency,unit),profileName=ProfileName(body.Profile)};
 }
 public async Task<object> Preview(NpgsqlConnection c,PreviewData data,InputFrame frame,PhysicalCrudeWriteRequest body,CancellationToken ct)
 {var r=await Resolve(c,data,body,frame.At,ct);return await Value(c,data,frame,body,r.Instrument,r.Assignment,r.Quantity,null,ct);}
 internal async Task<RetainedScheduleTerms> Prepare(NpgsqlConnection c,InputFrame frame,PhysicalCrudeWriteRequest body,CancellationToken ct)=>await TradingBookingRead.Run(database,c,frame,async(data,pinned)=>{
  var r=await Resolve(c,data,body,pinned.At,ct);_=await Value(c,data,pinned,body,r.Instrument,r.Assignment,r.Quantity,null,ct);return new RetainedScheduleTerms(data.DefinitionRevision,data.ReferenceRevision,r.Instrument);
 },ct,physicalMarket:PhysicalMarketSelection.Crude,crudeSetup:body.CrudeSetupId);
 public async Task<JsonElement> Status(NpgsqlConnection c,Guid operation,CancellationToken ct)
 {await using var q=ScheduleStore.Command(c,"SELECT security.crude_operation_status($1,$2)::text",Catalog,operation);return JsonSerializer.Deserialize<JsonElement>((string)(await q.ExecuteScalarAsync(ct))!);}
 public async Task<JsonElement> Save(NpgsqlConnection c,InputFrame frame,PhysicalCrudeWriteRequest body,CancellationToken ct)
 {await using var q=ScheduleStore.Command(c,"SELECT security.book_physical_crude($1,$2::jsonb,$3)::text",Catalog,JsonSerializer.Serialize(body,Json),frame.At);return JsonSerializer.Deserialize<JsonElement>((string)(await q.ExecuteScalarAsync(ct))!);}
 public async Task<object> Editor(NpgsqlConnection c,PreviewData data,InputFrame frame,Guid trade,CancellationToken ct)
 {
  var row=One(await PreviewDatabase.Rows(c,"SELECT json_build_object('request',t.request,'instrument_id',r.instrument_id,'revision',r.revision)::text FROM trading.crude_trade_revisions r JOIN trading.trades t ON(t.catalog_id,t.id)=(r.catalog_id,r.trade_id) WHERE(r.catalog_id,r.trade_id)=($1,$2)",[Catalog,trade],ct));
  var body=row.GetProperty("request").Deserialize<PhysicalCrudeWriteRequest>(Json)! with{SnapshotId=frame.Id};
  var assignment=await Metadata(c,data,body,ct);
  var programs=await new RetainedScheduleStore(database).LoadPrograms(c,row.Id("instrument_id"),frame.ContextId,frame.ContextId is null?null:frame.At,ct);
  return new{tradeId=trade,revision=int.Parse(row.Text("revision"),CultureInfo.InvariantCulture),terms=body,valuation=await Value(c,data,frame,body,null,assignment,ExactDecimal.Parse(Required(body.Quantity)),programs,ct)};
 }
 internal static async Task RequireCloseScope(NpgsqlConnection connection,Guid catalog,ImmutableArray<Guid> books,ImmutableArray<Guid> portfolios,CancellationToken ct)
 {
  await using var command=ScheduleStore.Command(connection,"SELECT security.require_supported_crude_portfolio_scope($1,$2::uuid[],$3::uuid[])",catalog,books.ToArray(),portfolios.ToArray());
  try{await command.ExecuteNonQueryAsync(ct);}catch(PostgresException e)when(e.MessageText=="crude_portfolio_scope")
  {throw new MarketDataException("crude_close_scope","Automated closes do not yet support physical crude obligations. Use the common blotter and portfolio risk for contractual valuation.");}
 }
 // Each booking by its trade reference beside its key, which stays the identity that opens it.
 public async Task<object> Directory(NpgsqlConnection c,CancellationToken ct)=>new{trades=(await PreviewDatabase.Rows(c,"""
  SELECT json_build_object('tradeId',r.trade_id,'trade_reference',tr.reference_number,'revision',r.revision,'instrumentId',r.instrument_id,'bookId',r.book_id,'bookName',b.name,
  'direction',CASE WHEN r.direction=1 THEN 'receive' ELSE 'deliver' END,'quantity',r.quantity::text,'quantityUnit',u.symbol,
  'deliveryFrom',i.delivery_from,'deliveryTo',i.delivery_to,'gradeName',g.name,'facilityName',f.name,'custodyPointName',p.name)::text
  FROM trading.crude_trade_revisions r JOIN trading.crude_instruments i USING(catalog_id,instrument_id)
  JOIN trading.trade_references tr ON(tr.catalog_id,tr.trade_id)=(r.catalog_id,r.trade_id)
  JOIN trading.books b ON(b.catalog_id,b.id)=(r.catalog_id,r.book_id)
  JOIN market_data.crude_valuation_assignments a ON(a.catalog_id,a.revision_id,a.code)=(i.catalog_id,i.definition_revision_id,i.assignment_code)
  JOIN ref_data.crude_grades g ON(g.catalog_id,g.revision_id,g.code)=(a.delivery_catalog_id,a.delivery_revision_id,a.grade_code)
  JOIN ref_data.locations f ON(f.catalog_id,f.revision_id,f.code)=(a.delivery_catalog_id,a.delivery_revision_id,a.facility_code)
  JOIN ref_data.locations p ON(p.catalog_id,p.revision_id,p.code)=(a.delivery_catalog_id,a.delivery_revision_id,a.custody_point_code)
  JOIN market_data.curves curve ON(curve.catalog_id,curve.revision_id,curve.code)=(a.catalog_id,a.revision_id,a.curve_code)
  JOIN ref_data.quote_specifications quote ON(quote.catalog_id,quote.revision_id,quote.code)=(a.reference_catalog_id,a.reference_revision_id,curve.quote_code)
  JOIN ref_data.quantity_specifications q ON(q.catalog_id,q.revision_id,q.code)=(quote.catalog_id,quote.revision_id,quote.quantity_code)
  JOIN ref_data.units u ON(u.catalog_id,u.revision_id,u.code)=(q.catalog_id,q.revision_id,q.unit_code)
  WHERE r.catalog_id=$1 ORDER BY r.publication_order DESC LIMIT 256
  """,[Catalog],ct)).Select(row=>{var named=JsonNode.Parse(row.GetRawText())!.AsObject();named.Remove("trade_reference");named["tradeReference"]=TradingReference.Trade(row);return named;}).ToArray()};
}
