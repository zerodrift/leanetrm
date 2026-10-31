using System.Collections.Immutable;
using System.Globalization;
using System.Text.Json;
using Npgsql;
using OpenEtrm.MarketData;
using OpenEtrm.MarketData.Gas;
using OpenEtrm.MarketData.Trading;
using OpenEtrm.MarketData.Power;

namespace OpenEtrm.Api;

[System.Text.Json.Serialization.JsonUnmappedMemberHandling(System.Text.Json.Serialization.JsonUnmappedMemberHandling.Disallow)]
public sealed record PhysicalGasWriteRequest(Guid SnapshotId,string? Convention,string? KnowledgeMode,Guid OperationId,Guid BookId,
 Guid DefinitionRevisionId,string? Pipeline,string? Zone,string? DeliveryPoint,string? Product,string? DeliveryFrom,string? DeliveryTo,string? Quantity,string? QuantityBasis,
 string? Direction,string? FixedPrice,[property:System.Text.Json.Serialization.JsonRequired]string? UsageFee,[property:System.Text.Json.Serialization.JsonRequired]string? UsageTariffCode,[property:System.Text.Json.Serialization.JsonRequired]string? UsageFeeDirection,string? PaymentAt,bool? Discounting,
 Guid? LegalEntityId,int? LegalEntityRevision,Guid? CounterpartyId,int? CounterpartyRevision,[property:System.Text.Json.Serialization.JsonRequired]Guid? GasSetupId);
public sealed record PhysicalGasEditorRequest(FuturesReadRequest Context,Guid TradeId);

internal sealed record GasMeterBinding(ReferenceKey? Reference,Guid? SetupId,Guid CatalogId,string Code,string Pipeline,string Identifier,string QuantitySpecification,string ValidFrom,string ValidTo);
internal sealed record GasDeliveryRow(string Day,DateTimeOffset Start,DateTimeOffset End,ExactQuantity Quantity,GasMeterBinding? Meter)
{
 public string QuantitySource=>"contractual";
}
internal sealed record GasTicketValue(string PresentValue,string Currency,List<object> Sides,List<GasDeliveryRow> DeliveryRows);

public sealed class PhysicalGasStore(PreviewDatabase database)
{
 public const string Convention="physical_gas_fixed_quantity_v4";
 private Guid Catalog=>database.Manifest.CatalogId;
 private static MarketDataException Invalid(string text)=>new("gas_input",text);
 private static readonly JsonSerializerOptions Json=new(JsonSerializerDefaults.Web);
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
  var specification=One(data.References["quantity_specifications"].Where(x=>x.Text("code")==quote.Quantity.Code));
  var unit=One(data.References["units"].Where(x=>x.Text("code")==specification.Text("unit_code"))).Text("symbol");
  var currency=One(data.References["currencies"].Where(x=>x.Text("code")==quote.Currency.Code)).Text("display_code");
  var location=One(data.References["locations"].Where(x=>x.Text("code")==curve.Text("location_code")));
  return new{quote=quote.Key.Code,group=row.Text("group_label"),groupAdjustment=row.Text("group_adjustment"),locationAdjustment=row.Text("location_adjustment"),index=target.Text("index_a_code"),curve=row.Text("curve_code"),marketLocation=curve.Text("location_code"),marketLocationName=location.Text("name"),quantityUnit=unit,
   priceUnit=currency+" / "+quote.Multiplier.ToString()+" "+unit,adjustment=row.Text("adjustment")};
 }
 private async Task<ImmutableArray<JsonElement>> Tariffs(NpgsqlConnection c,PreviewData data,Guid? setup,CancellationToken ct)=>
  await PreviewDatabase.Rows(c,"SELECT row_to_json(t)::text FROM market_data.gas_usage_tariffs t WHERE(catalog_id,revision_id,reference_catalog_id,reference_revision_id)=($1,$2,$3,$4) AND setup_id IS NOT DISTINCT FROM $5::uuid ORDER BY code",
   [Catalog,data.DefinitionRevision.RevisionId,data.ReferenceRevision.CatalogId,data.ReferenceRevision.RevisionId,(object?)setup??DBNull.Value],ct);
 internal async Task<string?> UsageFee(NpgsqlConnection c,PreviewData data,PhysicalGasWriteRequest body,JsonElement mapping,CancellationToken ct)
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
 private static ExactDecimal? FeeDirection(PhysicalGasWriteRequest body,string? fee)
 {
  if(fee is null){if(body.UsageFeeDirection is not null)throw Invalid("A fee direction requires an active fee.");return null;}
  return body.UsageFeeDirection switch{"pay"=>ExactDecimal.Parse("-1"),"receive"=>ExactDecimal.Parse("1"),_=>throw Invalid("Select pay or receive on the usage fee side.")};
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
 public async Task<object> CatalogRead(NpgsqlConnection c,PreviewData data,InputFrame frame,CancellationToken ct)
 {
  await using var active=ScheduleStore.Command(c,"SELECT security.current_gas_setup($1,$2)",Catalog,data.DefinitionRevision.RevisionId);
  var selected=await active.ExecuteScalarAsync(ct);Guid? setup=selected is Guid id?id:null;
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
  return new{gasSetupId=setup,locations,tariffs=(await Tariffs(c,data,setup,ct)).Select(t=>new{code=t.Text("code"),name=t.OptionalText("logical_code")??t.Text("code"),pipeline=t.Text("pipeline_code"),product=t.Text("product_code"),quote=t.Text("quote_code"),rate=t.Text("rate"),validFrom=t.Text("valid_from"),validTo=t.Text("valid_to"),basis="contractual_delivery_quantity"}),books,legalEntities=await TradeParties.Catalog(c,Catalog,true,ct),counterparties=await TradeParties.Catalog(c,Catalog,false,ct),
   scope="Future physical gas delivery with fixed contractual quantities, separate fixed consideration and optional usage fee. This profile admits up to 31 days within one forward month, fixed-offset gas days and no discounting. Actuals and settlement remain unavailable."};
 }
 private static object PhysicalDetails(PreviewData data,PhysicalGasWriteRequest body,JsonElement row)=>new{
  pipeline=row.Text("pipeline_code"),pipelineName=row.Text("pipeline_name"),zone=row.Text("zone_code"),zoneName=row.Text("zone_name"),
  deliveryPoint=row.Text("point_code"),deliveryPointName=row.Text("point_name"),clock=row.Text("clock_code"),dayStartMinute=int.Parse(row.Text("day_start_minute"),CultureInfo.InvariantCulture),product=row.Text("product_code"),productName=row.Text("product_name"),
  body.DeliveryFrom,body.DeliveryTo,body.Quantity,body.QuantityBasis,body.Direction,body.GasSetupId,body.UsageTariffCode,valuation=ValuationDetails(data,row)};
 internal async Task<(GasValuationAssignment Assignment,GasDayClock Clock,JsonElement Mapping)> Metadata(NpgsqlConnection c,PreviewData data,PhysicalGasWriteRequest body,CancellationToken ct)
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
 private async Task<(PhysicalGasInstrument Instrument,GasValuationAssignment Assignment,ExactDecimal Quantity,JsonElement Mapping)> Resolve(NpgsqlConnection c,PreviewData data,PhysicalGasWriteRequest body,CancellationToken ct)
 {
  var (assignment,clock,mapping)=await Metadata(c,data,body,ct);var from=Day(body.DeliveryFrom);var to=Day(body.DeliveryTo);var quote=assignment.Quote;
  var basis=body.QuantityBasis switch{"per_operating_day"=>GasQuantityBasis.PerOperatingDay,"period_total_equal_days"=>GasQuantityBasis.PeriodTotalEqualOperatingDays,_=>throw Invalid("Choose the contractual quantity basis.")};
  var quantity=ExactDecimal.Parse(Required(body.Quantity));if(quantity.CompareTo(default)<=0)throw Invalid("Contract quantity must be positive.");
  var physical=new PhysicalGasSide(Guid.NewGuid(),assignment.Location,assignment.Product,new(clock,from,to,basis,new(ExactDecimal.Parse("1"),quote.Quantity),GasFuelConvention.None,default),[assignment]);
  var payment=SchedulePreview.ParseAt(Required(body.PaymentAt));if(payment<clock.Boundary(to))throw Invalid("Payment must be at or after the end of delivery.");
  FixedSide Cash(string price)
  {
   var amount=ExactDecimal.Parse(price);var limit=ExactDecimal.Parse("1000000000000");
   if(amount.CompareTo(limit)>=0||amount.CompareTo(ExactDecimal.Parse("-1000000000000"))<=0)throw Invalid("A gas cash price must have absolute value below 10^12.");
   var total=physical.GenerateSchedule().Quantities.TotalDelivery;
   return new(Guid.NewGuid(),new FlatPrice(new UnitPrice(amount,quote)),quote,physical.UnitQuantity,
    [new(from,to,payment,new(System.Numerics.BigInteger.Parse(total.Numerator),System.Numerics.BigInteger.Parse(total.Denominator)),null)]);
  }
  var direction=body.Direction switch{"receive"=>ExactDecimal.Parse("1"),"deliver"=>ExactDecimal.Parse("-1"),_=>throw Invalid("Choose receive or deliver explicitly.")};
  var usageFee=await UsageFee(c,data,body,mapping,ct);
  var feeDirection=FeeDirection(body,usageFee);
  return(new(Guid.NewGuid(),physical,Cash(Required(body.FixedPrice)),usageFee is {} fee?[Cash(fee)]:[],direction,feeDirection),assignment,quantity,mapping);
 }
 private async Task<GasTicketValue> Value(NpgsqlConnection c,PreviewData data,InputFrame frame,PhysicalGasInstrument? draft,GasValuationAssignment assignment,ExactDecimal quantity,int direction,int? feeDirection,Guid? setup,ImmutableArray<RetainedSideProgram>? retained,CancellationToken ct)
 {
  if(frame.ContextId is not {} contextId)throw Invalid("Gas valuation requires a certified calculation context.");
  var programs=retained??[..draft!.Sides.Select((m,n)=>{var d=ScheduleDefinitionCompiler.Generate(m.Side,ct);var p=BookedSideProgram.Compile(d);return new RetainedSideProgram(n,m.Side.Id,Guid.Empty,new(BookedSideProgram.Digest(p.Write()),d.Manifest,d.Family,d.EconomicIdentity,d.PricingBindingIdentity),p);})];
  var snapshot=data.Snapshot(frame);var context=(await new KnowledgeStore(Catalog).Read(c,contextId,ct)).Context;
  snapshot=MarketSnapshotBinding.Bind(snapshot,contextId,SchedulePreview.ParseAt(context.KnownAt));
  var deps=programs.SelectMany(x=>BookedScheduleLookups.Dated(data,x.Program)).Select(x=>PeriodPriceSelector.ProjectionAt(x,snapshot)).OfType<CurveRequest>().Distinct().ToImmutableArray();
  var market=data.Model.Build(snapshot,deps,ct);
  var sources=new ScheduleMarketSources(market.Graph.Plan.Context,market,null,null,IntervalKnowledgePolicy.ResolvedCutoff,PendingActualsPolicy.RequireActuals,
   [..programs.SelectMany(x=>BookedScheduleLookups.Dated(data,x.Program)).Distinct()],[],[assignment]);
  var discount=new ExplicitDiscounting(ExactDecimal.Parse("1"),DiscountFactorSource.Undiscounted);
  var meters=await Meters(c,data,assignment,setup,ct);
  var sides=new List<object>();var deliveryRows=new List<GasDeliveryRow>();ExactMoney? total=null;
  foreach(var p in programs)
  {
   var result=sources.Evaluate(p.Program,p.SideId,market,discount,ct);var coefficient=ExactDecimal.Parse((p.Slot==0?direction:p.Slot==1?-direction:feeDirection??throw Invalid("A fee requires its own direction.")).ToString(CultureInfo.InvariantCulture));
   var amount=result.PresentValue.Scale(quantity*coefficient);total=total is null?amount:total.Add(amount);
   foreach(var root in result.Roots.Where(root=>root.Measure=="schedule_quantity"))
   {
    var values=result.QuantityValues(root.Name,quantity);var column=result.Values[root.Row].Scenario;
    var grid=p.Program.Document.Grids[column.Grid??throw Invalid("Physical quantities require a retained gas-day grid.")];
    for(var cell=0;cell<values.Length;cell++)
    {
     var interval=grid.Interval(cell);
     deliveryRows.Add(new(Date(interval.OperatingDate),interval.Start,interval.End,values[cell],Meter(data,meters,interval.OperatingDate,setup)));
    }
   }
   sides.Add(new{slot=p.Slot,economicClass=p.Slot==0?"physical_value":"contractual_cash",role=p.Slot switch{0=>"delivery",1=>"consideration",_=>"usage_fee"},presentValue=amount.ToExactString(),rows=p.Program.Document.Instructions.Length});
  }
  return new(total!.ToExactString(),assignment.Quote.Currency.Code,sides,deliveryRows);
 }
 public async Task<object> Preview(NpgsqlConnection c,PreviewData data,InputFrame frame,PhysicalGasWriteRequest body,CancellationToken ct)
 {
  var r=await Resolve(c,data,body,ct);var value=await Value(c,data,frame,r.Instrument,r.Assignment,r.Quantity,body.Direction=="receive"?1:-1,body.UsageFeeDirection switch{"pay"=>-1,"receive"=>1,_=>(int?)null},body.GasSetupId,null,ct);
  return new{physical=PhysicalDetails(data,body,r.Mapping),valuation=value,schedules=r.Instrument.Sides.Select((m,n)=>new{slot=n,definition=ScheduleDefinitionCompiler.Generate(m.Side,ct)})};
 }
 public async Task<object> Instructions(NpgsqlConnection c,PreviewData data,InputFrame frame,PhysicalGasWriteRequest body,CancellationToken ct)
 {
  var r=await Resolve(c,data,body,ct);
  var value=await Value(c,data,frame,r.Instrument,r.Assignment,r.Quantity,body.Direction=="receive"?1:-1,body.UsageFeeDirection switch{"pay"=>-1,"receive"=>1,_=>(int?)null},body.GasSetupId,null,ct);
  if(value.DeliveryRows.Any(row=>row.Meter is null))throw Invalid("Every instruction day requires an eligible direct-meter assignment. Contractual valuation remains available without one.");
  if(value.DeliveryRows.Any(row=>row.Meter!.QuantitySpecification!=r.Assignment.Quote.Quantity.Code))throw Invalid("Meter instructions require the same native quantity specification; measurement conversion is not supported.");
  return new{status="unsubmitted_contractual_schedule",referenceRevision=data.ReferenceRevision,pipeline=body.Pipeline,zone=body.Zone,deliveryPoint=body.DeliveryPoint,direction=body.Direction,rows=value.DeliveryRows};
 }
 internal async Task<RetainedScheduleTerms> Prepare(NpgsqlConnection c,InputFrame frame,PhysicalGasWriteRequest body,CancellationToken ct)=>
  await TradingBookingRead.Run(database,c,frame,async(data,pinned)=>{
   var r=await Resolve(c,data,body,ct);_=await Value(c,data,pinned,r.Instrument,r.Assignment,r.Quantity,body.Direction=="receive"?1:-1,body.UsageFeeDirection switch{"pay"=>-1,"receive"=>1,_=>(int?)null},body.GasSetupId,null,ct);
   return new RetainedScheduleTerms(data.DefinitionRevision,data.ReferenceRevision,r.Instrument);
  },ct,gasMarket:true);
 internal static async Task RequirePortfolioScope(NpgsqlConnection c,Guid catalog,ImmutableArray<Guid> books,ImmutableArray<Guid> portfolios,CancellationToken ct)
 {
  await using var q=ScheduleStore.Command(c,"SELECT security.require_supported_gas_portfolio_scope($1,$2::uuid[],$3::uuid[])",catalog,books.ToArray(),portfolios.ToArray());
  try{await q.ExecuteNonQueryAsync(ct);}catch(PostgresException e)when(e.MessageText=="gas_portfolio_scope"){
   throw new MarketDataException("gas_portfolio_scope","This close contains physical gas obligations outside its supported valuation and settlement conventions. Use the trading workspace for contractual gas PV and explicit portfolio-risk exclusions.");
  }
 }
 public async Task<object> Directory(NpgsqlConnection c,CancellationToken ct)=>new{trades=await PreviewDatabase.Rows(c,
  "SELECT row_to_json(x)::text FROM(SELECT r.trade_id,r.book_id,b.name book,r.quantity,r.direction,i.delivery_from,i.delivery_to,i.assignment_code FROM trading.gas_trade_revisions r JOIN trading.gas_instruments i USING(catalog_id,instrument_id) JOIN trading.books b ON(b.catalog_id,b.id)=(r.catalog_id,r.book_id) WHERE r.catalog_id=$1 AND NOT i.composition ORDER BY r.publication_order DESC LIMIT 256)x",[Catalog],ct)};
 public async Task<JsonElement> Status(NpgsqlConnection c,Guid operation,CancellationToken ct)
 {
  await using var q=ScheduleStore.Command(c,"SELECT security.gas_operation_status($1,$2)::text",Catalog,operation);
  return JsonSerializer.Deserialize<JsonElement>((string)(await q.ExecuteScalarAsync(ct))!);
 }
 public async Task<JsonElement> Save(NpgsqlConnection c,InputFrame frame,PhysicalGasWriteRequest body,CancellationToken ct)
 {
  await using var q=ScheduleStore.Command(c,"SELECT security.book_physical_gas($1,$2::jsonb,$3)::text",Catalog,JsonSerializer.Serialize(body,Json),frame.At);
  return JsonSerializer.Deserialize<JsonElement>((string)(await q.ExecuteScalarAsync(ct))!);
 }
 public async Task<object> Editor(NpgsqlConnection c,PreviewData data,InputFrame frame,Guid trade,CancellationToken ct)
 {
  var row=One(await PreviewDatabase.Rows(c,"SELECT row_to_json(x)::text FROM(SELECT r.*,i.definition_revision_id,i.assignment_code,i.delivery_from,i.delivery_to,i.quantity_basis,i.fixed_price,i.usage_fee,i.usage_tariff_code,i.usage_fee_direction,i.gas_setup_id,i.payment_at,a.pipeline_code,a.zone_code,a.point_code,a.product_code FROM trading.gas_trade_revisions r JOIN trading.gas_instruments i USING(catalog_id,instrument_id) JOIN market_data.gas_valuation_assignments a ON(a.catalog_id,a.revision_id,a.code)=(i.catalog_id,i.definition_revision_id,i.assignment_code) WHERE r.catalog_id=$1 AND r.trade_id=$2 AND NOT i.composition)x",[Catalog,trade],ct));
  // Resolve source metadata from exact authorized definitions; booked instructions are loaded, never regenerated for execution.
  var body=new PhysicalGasWriteRequest(frame.Id,Convention,"current",Guid.Empty,row.Id("book_id"),row.Id("definition_revision_id"),row.Text("pipeline_code"),row.Text("zone_code"),row.Text("point_code"),row.Text("product_code"),row.Text("delivery_from"),row.Text("delivery_to"),row.Text("quantity"),row.Text("quantity_basis"),row.Text("direction")=="1"?"receive":"deliver",row.Text("fixed_price"),row.OptionalText("usage_tariff_code") is null?row.OptionalText("usage_fee"):null,row.OptionalText("usage_tariff_code"),row.OptionalText("usage_fee_direction") switch{"-1"=>"pay","1"=>"receive",_=>null},row.Text("payment_at"),false,row.Id("legal_entity_id"),int.Parse(row.Text("legal_entity_revision"),CultureInfo.InvariantCulture),row.Id("counterparty_id"),int.Parse(row.Text("counterparty_revision"),CultureInfo.InvariantCulture),row.OptionalText("gas_setup_id") is {} gasSetup?Guid.Parse(gasSetup):null);
  var metadata=await Metadata(c,data,body,ct);
  var programs=await new RetainedScheduleStore(database).LoadPrograms(c,row.Id("instrument_id"),frame.ContextId,frame.ContextId is null?null:frame.At,ct);
  var value=await Value(c,data,frame,null,metadata.Assignment,ExactDecimal.Parse(body.Quantity!),body.Direction=="receive"?1:-1,body.UsageFeeDirection switch{"pay"=>-1,"receive"=>1,_=>(int?)null},body.GasSetupId,programs,ct);
  return new{tradeId=trade,terms=body,physical=PhysicalDetails(data,body,metadata.Mapping),valuation=value,schedules=programs.Select(p=>new{p.Slot,p.ScheduleId,p.Identity,program=p.Program.Document})};
 }
}
