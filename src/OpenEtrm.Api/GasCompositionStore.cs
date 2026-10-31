using System.Collections.Immutable;
using System.Globalization;
using System.Numerics;
using System.Text.Json;
using System.Text.Json.Serialization;
using Npgsql;
using OpenEtrm.MarketData;
using OpenEtrm.MarketData.Gas;
using OpenEtrm.MarketData.Trading;
using OpenEtrm.MarketData.Power;
using OpenEtrm.Shared.Numerics;

namespace OpenEtrm.Api;

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record GasQuantityLinkRequest(string Source,string Formula,string Fraction);
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record GasPhysicalRequest(string Code,string Pipeline,string Zone,string DeliveryPoint,string Product,string Direction,string DeliveryFrom,string DeliveryTo,string QuantityBasis,[property:JsonRequired]string? Quantity,[property:JsonRequired]GasQuantityLinkRequest? Link);
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record GasCashRequest(string Code,string Role,string Direction,string Source,string Stage,[property:JsonRequired]string? Price,[property:JsonRequired]string? TariffCode,string PaymentAt);
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record GasCompositionRequest(Guid SnapshotId,string Convention,string KnowledgeMode,string Profile,Guid OperationId,Guid BookId,Guid DefinitionRevisionId,[property:JsonRequired]Guid? GasSetupId,bool? Discounting,Guid? LegalEntityId,int? LegalEntityRevision,Guid? CounterpartyId,int? CounterpartyRevision,ImmutableArray<GasPhysicalRequest> PhysicalSides,ImmutableArray<GasCashRequest> CashSides);
internal sealed record GasCompositionRow(int Slot,string Code,string Role,int Direction,ExactRatio Ratio,ExactRatio Receipt,string? Source,string? Stage,string? Formula,string? Fraction,GasPhysicalRequest? Physical,GasCashRequest? Cash,GasValuationAssignment Assignment);
internal sealed record GasCompositionDraft(GasCompositionInstrument Instrument,ImmutableArray<GasCompositionRow> Members);

public sealed class GasCompositionStore(PreviewDatabase database)
{
 const string Profile="fixed_gas_composition_v1";
 readonly PhysicalGasStore gas=new(database);
 static readonly JsonSerializerOptions Json=new(JsonSerializerDefaults.Web);
 static MarketDataException Invalid(string s)=>new("gas_composition_input",s);
 static ExactDecimal Number(string s)
 {
  var n=ExactDecimal.Parse(s);var dot=s.IndexOf('.');
  if(dot>=0&&s.Length-dot-1>18)throw Invalid("Use exact values with up to 18 fractional digits and quantities below 10^24.");
  return n;
 }
 static int Sign(string s,bool physical)=>s switch{ "receive"=>1,"pay" when !physical=>-1,"deliver" when physical=>-1,_=>throw Invalid("Choose a direction on each side.")};
 static GasQuantityStage Stage(string s)=>s switch{"receipt"=>GasQuantityStage.Receipt,"delivery"=>GasQuantityStage.Delivery,"fuel"=>GasQuantityStage.Fuel,_=>throw Invalid("Select an explicit receipt, delivery or fuel quantity stage.")};
 static object Ratio(ExactQuantity q)=>new{numerator=q.Numerator,denominator=q.Denominator};
 static PhysicalGasWriteRequest Selection(GasCompositionRequest body,GasPhysicalRequest p)=>new(body.SnapshotId,body.Convention,body.KnowledgeMode,body.OperationId,body.BookId,body.DefinitionRevisionId,p.Pipeline,p.Zone,p.DeliveryPoint,p.Product,p.DeliveryFrom,p.DeliveryTo,p.Quantity,p.QuantityBasis,p.Direction,null,null,null,null,null,body.Discounting,body.LegalEntityId,body.LegalEntityRevision,body.CounterpartyId,body.CounterpartyRevision,body.GasSetupId);
 async Task<GasCompositionDraft> Resolve(NpgsqlConnection c,PreviewData data,GasCompositionRequest body,CancellationToken ct)
 {
  if(body.Profile!=Profile||body.Discounting!=false||body.KnowledgeMode!="current"||body.PhysicalSides.IsDefaultOrEmpty||body.PhysicalSides.Length>4||body.CashSides.IsDefault||body.CashSides.Length>4)throw Invalid("Select the bounded fixed-quantity gas composition profile, explicit no discounting, one to four physical sides and up to four cash sides.");
  var physical=ImmutableArray.CreateBuilder<GasPhysicalComponent>();var assignments=new List<GasValuationAssignment>();var mappings=new List<JsonElement>();
  foreach(var p in body.PhysicalSides)
  {
   var m=await gas.Metadata(c,data,Selection(body,p),ct);assignments.Add(m.Assignment);mappings.Add(m.Mapping);
   var basis=p.QuantityBasis switch{"per_operating_day"=>GasQuantityBasis.PerOperatingDay,"period_total_equal_days"=>GasQuantityBasis.PeriodTotalEqualOperatingDays,_=>throw Invalid("Choose each physical quantity basis explicitly.")};
   var side=new PhysicalGasSide(Guid.NewGuid(),m.Assignment.Location,m.Assignment.Product,new(m.Clock,DateWorkspace.Date(p.DeliveryFrom),DateWorkspace.Date(p.DeliveryTo),basis,new(Number("1"),m.Assignment.Quote.Quantity),GasFuelConvention.None,default),[m.Assignment]);
   var link=p.Link is null?null:new GasCompositionLink(p.Link.Source,p.Link.Formula switch{"none"=>GasFuelConvention.None,"gross_less_fraction"=>GasFuelConvention.NetEqualsGrossTimesOneMinusFuel,"gross_divided_by_one_plus_fraction"=>GasFuelConvention.NetEqualsGrossDividedByOnePlusFuel,_=>throw Invalid("Select an installed fuel rule.")},Number(p.Link.Fraction));
   physical.Add(new(p.Code,side,Number(Sign(p.Direction,true).ToString(CultureInfo.InvariantCulture)),p.Quantity is null?null:Number(p.Quantity),link));
  }
  var cash=ImmutableArray.CreateBuilder<GasCashComponent>();
  foreach(var p in body.CashSides)
  {
   var indices=body.PhysicalSides.Select((v,n)=>(v,n)).Where(x=>x.v.Code==p.Source).ToArray();if(indices.Length!=1)throw Invalid("Cash quantity sources must name one physical side.");var n=indices[0].n;var source=physical[n].Side;
   string? price=p.Price;
   if(p.TariffCode is not null)
   {
    if(p.Role!="fee"||p.Stage!="delivery"||price is not null)throw Invalid("Configured usage tariffs require a delivery-based fee with no manual price.");
    price=await gas.UsageFee(c,data,Selection(body,body.PhysicalSides[n]) with{UsageTariffCode=p.TariffCode},mappings[n],ct);
   }
   if(price is null)throw Invalid("Specify the fixed cash price or an eligible usage tariff.");
   var payment=SchedulePreview.ParseAt(p.PaymentAt);
   if(payment.Ticks%10!=0||Number(price).CompareTo(Number("1000000000000"))>=0||Number(price).CompareTo(Number("-1000000000000"))<=0)throw Invalid("Cash prices must have absolute value below 10^12 and payments need microsecond precision.");
   if(payment<source.Quantities.Clock.Boundary(source.Quantities.To))throw Invalid("Payment must follow its source delivery term.");
   var total=source.GenerateSchedule().Quantities.TotalDelivery;
   var side=new FixedSide(Guid.NewGuid(),new FlatPrice(new UnitPrice(Number(price),assignments[n].Quote)),assignments[n].Quote,source.UnitQuantity,[new(source.Quantities.From,source.Quantities.To,payment,new(BigInteger.Parse(total.Numerator),BigInteger.Parse(total.Denominator)),null)]);
   cash.Add(new(p.Code,side,p.Role,p.Source,Stage(p.Stage),Number(Sign(p.Direction,false).ToString(CultureInfo.InvariantCulture))));
  }
  var instrument=new GasCompositionInstrument(Guid.NewGuid(),physical.ToImmutable(),cash.ToImmutable());var members=ImmutableArray.CreateBuilder<GasCompositionRow>();
  for(var n=0;n<physical.Count;n++){var p=body.PhysicalSides[n];members.Add(new(n,p.Code,"physical",Sign(p.Direction,true),instrument.Sides[n].QuantityRatio!,instrument.ReceiptRatios[n],p.Link?.Source,null,p.Link?.Formula,p.Link?.Fraction,p,null,assignments[n]));}
  for(var n=0;n<cash.Count;n++){var p=body.CashSides[n];var source=body.PhysicalSides.Select((v,i)=>(v,i)).Single(x=>x.v.Code==p.Source).i;members.Add(new(physical.Count+n,p.Code,p.Role,Sign(p.Direction,false),instrument.Sides[physical.Count+n].QuantityRatio!,instrument.Sides[physical.Count+n].QuantityRatio!,p.Source,p.Stage,null,null,null,p,assignments[source]));}
  return new(instrument,members.ToImmutable());
 }
 async Task<object> Value(NpgsqlConnection c,PreviewData data,InputFrame frame,ImmutableArray<GasCompositionRow> members,ExactDecimal quantity,GasCompositionInstrument? draft,Guid? setup,ImmutableArray<RetainedSideProgram>? retained,CancellationToken ct)
 {
  if(frame.ContextId is not {} contextId)throw Invalid("Use a certified calculation context.");
  var programs=retained??[..draft!.Sides.Select((m,n)=>{var d=ScheduleDefinitionCompiler.Generate(m.Side,ct);var p=BookedSideProgram.Compile(d);return new RetainedSideProgram(n,m.Side.Id,Guid.Empty,new(BookedSideProgram.Digest(p.Write()),d.Manifest,d.Family,d.EconomicIdentity,d.PricingBindingIdentity),p);})];
  var knowledge=(await new KnowledgeStore(database.Manifest.CatalogId).Read(c,contextId,ct)).Context;
  var snapshot=MarketSnapshotBinding.Bind(data.Snapshot(frame),contextId,SchedulePreview.ParseAt(knowledge.KnownAt));
  var assignments=members.Select(m=>m.Assignment).Distinct().ToImmutableArray();
  var dated=assignments.Select(a=>a.Lookup).Distinct().ToImmutableArray();var deps=dated.Select(x=>PeriodPriceSelector.ProjectionAt(x,snapshot)).OfType<CurveRequest>().Distinct().ToImmutableArray();
  var market=data.Model.Build(snapshot,deps,ct);var sources=new ScheduleMarketSources(market.Graph.Plan.Context,market,null,null,IntervalKnowledgePolicy.ResolvedCutoff,PendingActualsPolicy.RequireActuals,dated,[],assignments);
  var discount=new ExplicitDiscounting(Number("1"),DiscountFactorSource.Undiscounted);ExactMoney? total=null;var sides=new List<object>();var quantities=new Dictionary<string,ExactQuantity>(StringComparer.Ordinal);
  foreach(var member in members.OrderBy(x=>x.Slot))
  {
   var p=programs.Single(x=>x.Slot==member.Slot);var result=sources.Evaluate(p.Program,p.SideId,market,discount,ct);
   var amount=result.PresentValue.Scale(member.Ratio).Scale(quantity).Scale(Number(member.Direction.ToString(CultureInfo.InvariantCulture)));total=total is null?amount:total.Add(amount);
   var rows=new List<object>();ExactQuantity? qtotal=null;
   if(member.Role=="physical")
   {
    var meters=await gas.Meters(c,data,member.Assignment,setup,ct);
    foreach(var root in result.Roots.Where(r=>r.Measure=="schedule_quantity"))
    {
     var values=result.QuantityValues(root.Name,quantity);var grid=p.Program.Document.Grids[result.Values[root.Row].Scenario.Grid!.Value];
     for(var n=0;n<values.Length;n++){var q=values[n].Scale(member.Ratio);qtotal=qtotal is null?q:qtotal.Add(q);var interval=grid.Interval(n);rows.Add(new{day=interval.OperatingDate.ToString("yyyy-MM-dd",CultureInfo.InvariantCulture),interval.Start,interval.End,quantity=Ratio(q),quantitySource="contractual",meter=PhysicalGasStore.Meter(data,meters,interval.OperatingDate,setup)});}
    }
    quantities.Add(member.Code,qtotal!);
   }
   else
   {
    var literal=p.Program.Document.Instructions.Single(i=>i.Output.Operation=="quantity_literal");
    qtotal=result.QuantityColumn(literal.Output.Ordinal,quantity).Single().Scale(member.Ratio);
   }
   sides.Add(new{member.Code,member.Role,source=member.Source,stage=member.Stage,pipeline=member.Role=="physical"?member.Assignment.Location.Pipeline.Code:null,zone=member.Role=="physical"?member.Assignment.Location.Zone.Code:null,deliveryPoint=member.Role=="physical"?member.Assignment.Point.Code:null,direction=member.Direction==1?"receive":member.Role=="physical"?"deliver":"pay",presentValue=amount.ToExactString(),quantity=Ratio(qtotal!),deliveryRows=rows});
  }
  var links=members.Where(m=>m.Role=="physical"&&m.Source is not null).Select(m=>{var receipt=quantities[m.Source!];var delivery=quantities[m.Code];var fuel=receipt.Add(delivery.Scale(Number("-1")));return new{source=m.Source,target=m.Code,m.Formula,m.Fraction,receipt=Ratio(receipt),delivery=Ratio(delivery),fuel=Ratio(fuel)};}).ToArray();
  var specification=data.References["quantity_specifications"].Single(r=>r.Text("code")==members[0].Assignment.Quote.Quantity.Code);
  var unit=data.References["units"].Single(r=>r.Text("code")==specification.Text("unit_code")).Text("symbol");
  return new{presentValue=total!.ToExactString(),currency=members[0].Assignment.Quote.Currency.Code,quantityUnit=unit,sides,links};
 }
 public async Task<object> Preview(NpgsqlConnection c,PreviewData data,InputFrame frame,GasCompositionRequest body,CancellationToken ct)
 {var d=await Resolve(c,data,body,ct);return await Value(c,data,frame,d.Members,d.Instrument.BaseQuantity,d.Instrument,body.GasSetupId,null,ct);}
 internal Task<RetainedScheduleTerms> Prepare(NpgsqlConnection c,InputFrame frame,GasCompositionRequest body,CancellationToken ct)=>TradingBookingRead.Run(database,c,frame,async(data,pinned)=>{var d=await Resolve(c,data,body,ct);_=await Value(c,data,pinned,d.Members,d.Instrument.BaseQuantity,d.Instrument,body.GasSetupId,null,ct);return new RetainedScheduleTerms(data.DefinitionRevision,data.ReferenceRevision,d.Instrument);},ct);
 internal async Task<JsonElement> Save(NpgsqlConnection c,InputFrame frame,GasCompositionRequest body,CancellationToken ct)
 {await using var q=ScheduleStore.Command(c,"SELECT security.book_gas_composition($1,$2::jsonb,$3)::text",database.Manifest.CatalogId,JsonSerializer.Serialize(body,Json),frame.At);return JsonSerializer.Deserialize<JsonElement>((string)(await q.ExecuteScalarAsync(ct))!);}
 public async Task<object> Directory(NpgsqlConnection c,CancellationToken ct)=>new{trades=await PreviewDatabase.Rows(c,
  "SELECT row_to_json(x)::text FROM(SELECT r.trade_id,r.book_id,b.name book,(SELECT count(*) FROM trading.gas_component_terms m WHERE(m.catalog_id,m.instrument_id,m.role)=(r.catalog_id,r.instrument_id,'physical')) physical_count FROM trading.gas_trade_revisions r JOIN trading.gas_instruments i USING(catalog_id,instrument_id) JOIN trading.books b ON(b.catalog_id,b.id)=(r.catalog_id,r.book_id) WHERE r.catalog_id=$1 AND i.composition ORDER BY r.publication_order DESC LIMIT 256)x",[database.Manifest.CatalogId],ct)};
 public async Task<object> Editor(NpgsqlConnection c,PreviewData data,InputFrame frame,Guid trade,CancellationToken ct)
 {
  var header=(await PreviewDatabase.Rows(c,"SELECT row_to_json(x)::text FROM(SELECT r.*,i.definition_revision_id,i.gas_setup_id,t.request FROM trading.gas_trade_revisions r JOIN trading.gas_instruments i USING(catalog_id,instrument_id) JOIN trading.trades t ON(t.catalog_id,t.id)=(r.catalog_id,r.trade_id) WHERE(r.catalog_id,r.trade_id)=($1,$2) AND i.composition)x",[database.Manifest.CatalogId,trade],ct)).SingleOrDefault();
  if(header.ValueKind!=JsonValueKind.Object)throw Invalid("The composed gas trade is unavailable.");
  var rows=await PreviewDatabase.Rows(c,"SELECT row_to_json(x)::text FROM trading.gas_component_terms x WHERE(catalog_id,instrument_id)=($1,$2) ORDER BY slot",[database.Manifest.CatalogId,header.Id("instrument_id")],ct);
  // The original request is returned for inspection, not used to generate a Side
  // or executable program. Typed terms resolve exact market metadata only.
  var body=JsonSerializer.Deserialize<GasCompositionRequest>(header.GetProperty("request").GetRawText(),Json)!;
  var valuationBody=body with{DefinitionRevisionId=header.Id("definition_revision_id"),GasSetupId=header.OptionalText("gas_setup_id") is {} setup?Guid.Parse(setup):null,SnapshotId=frame.Id};
  var members=ImmutableArray.CreateBuilder<GasCompositionRow>();var phys=new Dictionary<int,(GasPhysicalRequest Physical,GasValuationAssignment Assignment)>();
  foreach(var r in rows.Where(r=>r.Text("role")=="physical"))
  {
   var p=new GasPhysicalRequest(r.Text("code"),r.Text("pipeline_code"),r.Text("zone_code"),r.Text("point_code"),r.Text("product_code"),r.Text("direction")=="1"?"receive":"deliver",r.Text("delivery_from"),r.Text("delivery_to"),r.Text("quantity_basis"),r.OptionalText("independent_quantity"),null);
   var m=await gas.Metadata(c,data,Selection(valuationBody,p),ct);phys.Add(int.Parse(r.Text("slot")),(p,m.Assignment));
  }
  foreach(var r in rows)
  {
   var slot=int.Parse(r.Text("slot"));var source=r.OptionalText("source_slot") is {} s?int.Parse(s):(int?)null;var a=phys[r.Text("role")=="physical"?slot:source!.Value].Assignment;
   members.Add(new(slot,r.Text("code"),r.Text("role"),int.Parse(r.Text("direction")),new(BigInteger.Parse(r.Text("ratio_n")),BigInteger.Parse(r.Text("ratio_d"))),new(BigInteger.Parse(r.Text("receipt_n")),BigInteger.Parse(r.Text("receipt_d"))),source is {} si?rows.Single(x=>x.Text("slot")==si.ToString(CultureInfo.InvariantCulture)).Text("code"):null,r.OptionalText("stage"),r.OptionalText("formula"),r.OptionalText("fraction"),null,null,a));
  }
  var programs=await new RetainedScheduleStore(database).LoadPrograms(c,header.Id("instrument_id"),frame.ContextId,frame.ContextId is null?null:frame.At,ct);
  var value=await Value(c,data,frame,members.ToImmutable(),Number(header.Text("quantity")),null,valuationBody.GasSetupId,programs,ct);
  return new{terms=body,valuation=value};
 }
}
