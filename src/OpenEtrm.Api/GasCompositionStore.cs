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
public sealed record GasPhysicalRequest(string Code,string Pipeline,string Zone,string DeliveryPoint,string Product,string Direction,string DeliveryFrom,string DeliveryTo,string QuantityBasis,[property:JsonRequired]string? Quantity,[property:JsonRequired]GasQuantityLinkRequest? Link,[property:JsonIgnore(Condition=JsonIgnoreCondition.WhenWritingNull)]CustomCurveProjectionInput? ForecastCurve=null);
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record GasCashRequest(string Code,string Role,string Direction,string Source,string Stage,[property:JsonRequired]string? Price,[property:JsonRequired]string? TariffCode,string? PaymentAt,
 [property:JsonIgnore(Condition=JsonIgnoreCondition.WhenWritingNull)]GasIndexedCashRequest? Indexed=null);
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record GasCompositionRequest(Guid SnapshotId,string Convention,string KnowledgeMode,string Profile,Guid OperationId,Guid BookId,Guid DefinitionRevisionId,[property:JsonRequired]Guid? GasSetupId,bool? Discounting,Guid? LegalEntityId,int? LegalEntityRevision,Guid? CounterpartyId,int? CounterpartyRevision,ImmutableArray<GasPhysicalRequest> PhysicalSides,ImmutableArray<GasCashRequest> CashSides,
 [property:JsonIgnore(Condition=JsonIgnoreCondition.WhenWritingNull)]Guid? TradeId=null,[property:JsonIgnore(Condition=JsonIgnoreCondition.WhenWritingNull)]int? ExpectedRevision=null,
 [property:JsonIgnore(Condition=JsonIgnoreCondition.WhenWritingNull)]Guid? BindingId=null,[property:JsonIgnore(Condition=JsonIgnoreCondition.WhenWritingNull)]string? Reason=null,
 [property:JsonIgnore(Condition=JsonIgnoreCondition.WhenWritingNull)]string? AmendmentPolicy=null);
internal sealed record GasCompositionRow(int Slot,string Code,string Role,int Direction,ExactRatio Ratio,ExactRatio Receipt,string? Source,string? Stage,string? Formula,string? Fraction,GasPhysicalRequest? Physical,GasCashRequest? Cash,ImmutableArray<GasValuationAssignment> Assignments,GasDayClock Clock)
{
 // Quote and location are common to every period of the one exact mapping.
 internal GasValuationAssignment Assignment=>Assignments[0];
}
internal sealed record GasCompositionDraft(GasCompositionInstrument Instrument,ImmutableArray<GasCompositionRow> Members);
internal sealed record GasCompositionValue(string PresentValue,string Currency,string QuantityUnit,List<object> Sides,object[] Links,ScheduleView? Schedule,object[]? Summaries,GasScheduleRefusal? ScheduleRefusal,ImmutableDictionary<string,int> SideSlots,
 GasLifecycleMeasures? Measures=null)
{
 [JsonIgnore]internal PhysicalLifecycleValue? Lifecycle{get;init;}
}
// A started-delivery trade's P&L explain measures: unrealized (remaining delivery MTM, days not yet ended) and realized
// unsettled (ended, fixed and not yet paid). Expected cash is undiscounted, by due date, and is not part of the total.
// Discounting names the factor basis of both measures, from the trade's explicit selection.
internal sealed record GasLifecycleMeasures(string Unrealized,string RealizedUnsettled,string Total,string Label,string Discounting,ImmutableArray<GasExpectedCash> ExpectedCash)
{
 internal static GasLifecycleMeasures From(PhysicalLifecycleValue value,DiscountFactorSource discounting)=>new(value.Unrealized.ToExactString(),value.RealizedUnsettled.ToExactString(),value.Total.ToExactString(),
  GasPositions.LifecycleLabel,discounting switch{DiscountFactorSource.Undiscounted=>"undiscounted",_=>throw new MarketDataException("gas_composition_input","Gas compositions value only under their explicit no-discounting selection.")},
  [..value.ExpectedCash.Select(x=>new GasExpectedCash(x.DueAt,x.Amount.ToExactString()))]);
}
internal sealed record GasExpectedCash(DateTimeOffset DueAt,string Amount);
internal sealed record GasScheduleRefusal(string Code,string Message);
internal sealed record GasCompositionRead(GasCompositionRequest Terms,GasCompositionValue Valuation);
internal sealed record GasCompositionExecution
{
 internal GasCompositionRequest Terms{get;}
 internal Guid InstrumentId{get;}
 internal ImmutableArray<GasCompositionRow> Members{get;}
 internal ExactDecimal Quantity{get;}
 internal ImmutableArray<RetainedSideProgram> Programs{get;}
 internal Guid TradeId{get;}
 // Every retained program selects the started-delivery lifecycles; older or mixed trades keep their own refusals.
 internal bool Started=>GasCompositionStore.Started(Programs);
 internal GasCompositionExecution(Guid tradeId,GasCompositionRequest terms,Guid instrumentId,ImmutableArray<GasCompositionRow> members,
  ExactDecimal quantity,ImmutableArray<RetainedSideProgram> programs)
 {
  RequireSlots(members.IsDefault?[]:members.Select(m=>m.Slot),programs);
  if(members.Any(m=>programs.Single(p=>p.Slot==m.Slot).Program.Document.Family!=(m.Role=="physical"?"gas":m.Role=="indexed_consideration"?"floating":"fixed")))
   throw new MarketDataException("gas_program_membership","Every retained gas program requires its exact visible member slot and side family.");
  if(tradeId==Guid.Empty)throw new MarketDataException("gas_program_membership","Every retained gas program requires its exact held trade.");
  TradeId=tradeId;Terms=terms;InstrumentId=instrumentId;Members=members;Quantity=quantity;Programs=programs;
 }
 internal static void RequireSlots(IEnumerable<int> members,ImmutableArray<RetainedSideProgram> programs)
 {
  if(programs.IsDefaultOrEmpty||!members.Order().SequenceEqual(Enumerable.Range(0,programs.Length))||
   !programs.Select(p=>p.Slot).Order().SequenceEqual(Enumerable.Range(0,programs.Length)))
   throw new MarketDataException("gas_program_membership","Every retained gas program requires its exact visible member slot and side family.");
 }
 internal RetainedGasInstrument Instrument(ImmutableArray<IndexPriceLookup> lookups)=>new(InstrumentId,[..Members.OrderBy(m=>m.Slot).Select(m=>{
  var p=Programs.Single(p=>p.Slot==m.Slot);
  return new RetainedGasMember(p.SideId,ExactDecimal.Parse(m.Direction.ToString(CultureInfo.InvariantCulture)),m.Ratio,p.Program);
 })],[..Members.SelectMany(m=>m.Assignments).Distinct()],lookups);
}

public sealed class GasCompositionStore(PreviewDatabase database)
{
 internal const string Profile="fixed_gas_composition_v1";
 internal const string IndexedProfile="indexed_gas_composition_v1";
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
 internal static bool Started(ImmutableArray<RetainedSideProgram> programs)=>programs.All(p=>
 {
  var guards=p.Program.Document.Instructions.Where(i=>i.Output.Operation=="output_guard").ToArray();
  if(guards.Length==0||guards.Any(i=>!i.Output.Parameters.ContainsKey("lifecycle")))
   throw new MarketDataException("gas_program_membership","Every retained gas program requires output guards with an explicit lifecycle.");
  return guards.All(i=>i.Output.Parameters["lifecycle"] is ScheduleLifecycles.WholeGasDayDelivery or ScheduleLifecycles.AccruedPayment);
 });
 // The trade's quantity unit: its sides share one quantity specification, whose unit the reference data names.
 internal static string QuantityUnit(PreviewData data,IEnumerable<ReferenceKey> quantities)=>quantities.Distinct().ToArray() is [var one]?data.QuantityUnit(one.Code):
  throw Invalid("The sides' gas paths are quoted in different quantity specifications; one trade takes one quantity unit.");
 internal static PhysicalGasTerms Selection(GasCompositionRequest body,GasPhysicalRequest p)=>new(body.SnapshotId,body.Convention,body.KnowledgeMode,body.OperationId,body.BookId,body.DefinitionRevisionId,p.Pipeline,p.Zone,p.DeliveryPoint,p.Product,p.DeliveryFrom,p.DeliveryTo,p.Quantity,p.QuantityBasis,p.Direction,null,null,null,null,null,body.Discounting,body.LegalEntityId,body.LegalEntityRevision,body.CounterpartyId,body.CounterpartyRevision,body.GasSetupId);
 async Task<GasCompositionDraft> Resolve(NpgsqlConnection c,PreviewData data,GasCompositionRequest body,CancellationToken ct)
 {
  if(body.PhysicalSides.IsDefault||body.CashSides.IsDefault||body.PhysicalSides.Any(s=>s is null)||body.CashSides.Any(s=>s is null))throw Invalid("Supply each physical and cash Side explicitly.");
  if(body.Profile!=(body.CashSides.Any(s=>s.Indexed is not null)?IndexedProfile:Profile)||body.Discounting!=false||body.KnowledgeMode!="current"||body.PhysicalSides.IsDefault||body.PhysicalSides.Length is <GasCompositionInstrument.MinPhysicalSides or >GasCompositionInstrument.MaxPhysicalSides||body.CashSides.IsDefault||body.CashSides.Length>GasCompositionInstrument.MaxCashSides)
   throw Invalid($"Select the bounded fixed-quantity gas composition profile, explicit no discounting, {GasCompositionInstrument.MinPhysicalSides} to {GasCompositionInstrument.MaxPhysicalSides} physical sides and up to {GasCompositionInstrument.MaxCashSides} cash sides.");
  var physical=ImmutableArray.CreateBuilder<GasPhysicalComponent>();var assignments=new List<ImmutableArray<GasValuationAssignment>>();var clocks=new List<GasDayClock>();var mappings=new List<JsonElement>();
  foreach(var p in body.PhysicalSides)
  {
   var m=await gas.Metadata(c,data,Selection(body,p),ct);assignments.Add(m.Assignments);clocks.Add(m.Clock);mappings.Add(m.Mapping);
   var basis=p.QuantityBasis switch{"per_operating_day"=>GasQuantityBasis.PerOperatingDay,"period_total_equal_days"=>GasQuantityBasis.PeriodTotalEqualOperatingDays,_=>throw Invalid("Choose each physical quantity basis explicitly.")};
   var side=new PhysicalGasSide(Guid.NewGuid(),m.Assignments[0].Location,m.Assignments[0].Product,new(m.Clock,DateWorkspace.Date(p.DeliveryFrom),DateWorkspace.Date(p.DeliveryTo),basis,new(Number("1"),m.Assignments[0].Quote.Quantity),GasFuelConvention.None,default),m.Assignments,p.ForecastCurve?.ToBinding());
   var link=p.Link is null?null:new GasCompositionLink(p.Link.Source,p.Link.Formula switch{"none"=>GasFuelConvention.None,"gross_less_fraction"=>GasFuelConvention.NetEqualsGrossTimesOneMinusFuel,"gross_divided_by_one_plus_fraction"=>GasFuelConvention.NetEqualsGrossDividedByOnePlusFuel,_=>throw Invalid("Select an installed fuel rule.")},Number(p.Link.Fraction));
   physical.Add(new(p.Code,side,Number(Sign(p.Direction,true).ToString(CultureInfo.InvariantCulture)),p.Quantity is null?null:Number(p.Quantity),link));
  }
  var cash=ImmutableArray.CreateBuilder<GasCashComponent>();
  foreach(var p in body.CashSides)
  {
   var indices=body.PhysicalSides.Select((v,n)=>(v,n)).Where(x=>x.v.Code==p.Source).ToArray();if(indices.Length!=1)throw Invalid("Cash quantity sources must name one physical side.");var n=indices[0].n;var source=physical[n].Side;
   if(p.Indexed is not null){cash.Add(new(p.Code,GasIndexedCash.Resolve(data,source,p),p.Role,p.Source,Stage(p.Stage),Number(Sign(p.Direction,false).ToString(CultureInfo.InvariantCulture))));continue;}
   if(p.Role=="indexed_consideration")throw Invalid("An indexed cash Side requires its explicit index terms.");
   string? price=p.Price;
   if(p.TariffCode is not null)
   {
    if(p.Role!="fee"||p.Stage!="delivery"||price is not null)throw Invalid("Configured usage tariffs require a delivery-based fee with no manual price.");
    price=await gas.UsageFee(c,data,Selection(body,body.PhysicalSides[n]) with{UsageTariffCode=p.TariffCode},mappings[n],ct);
   }
   if(price is null)throw Invalid("Specify the fixed cash price or an eligible usage tariff.");
   var payment=SchedulePreview.ParseAt(p.PaymentAt??throw Invalid("Specify the fixed cash payment date."));
   if(payment.Ticks%10!=0||Number(price).CompareTo(Number("1000000000000"))>=0||Number(price).CompareTo(Number("-1000000000000"))<=0)throw Invalid("Cash prices must have absolute value below 10^12 and payments need microsecond precision.");
   if(payment<source.Quantities.Clock.Boundary(source.Quantities.To))throw Invalid("Payment must follow its source delivery term.");
   var total=source.GenerateSchedule().Quantities.TotalDelivery;
   var side=new FixedSide(Guid.NewGuid(),new FlatPrice(new UnitPrice(Number(price),assignments[n][0].Quote)),assignments[n][0].Quote,source.UnitQuantity,[new(source.Quantities.From,source.Quantities.To,payment,new(BigInteger.Parse(total.Numerator),BigInteger.Parse(total.Denominator)),null)]);
   cash.Add(new(p.Code,side,p.Role,p.Source,Stage(p.Stage),Number(Sign(p.Direction,false).ToString(CultureInfo.InvariantCulture))));
  }
  var instrument=new GasCompositionInstrument(Guid.NewGuid(),physical.ToImmutable(),cash.ToImmutable());var members=ImmutableArray.CreateBuilder<GasCompositionRow>();
  for(var n=0;n<physical.Count;n++){var p=body.PhysicalSides[n];members.Add(new(n,p.Code,"physical",Sign(p.Direction,true),instrument.Sides[n].QuantityRatio!,instrument.ReceiptRatios[n],p.Link?.Source,null,p.Link?.Formula,p.Link?.Fraction,p,null,assignments[n],clocks[n]));}
  for(var n=0;n<cash.Count;n++){var p=body.CashSides[n];var source=body.PhysicalSides.Select((v,i)=>(v,i)).Single(x=>x.v.Code==p.Source).i;members.Add(new(physical.Count+n,p.Code,p.Role,Sign(p.Direction,false),instrument.Sides[physical.Count+n].QuantityRatio!,instrument.Sides[physical.Count+n].QuantityRatio!,p.Source,p.Stage,null,null,null,p,assignments[source],clocks[source]));}
  return new(instrument,members.ToImmutable());
 }
 // recorded holds, per physical side code, the days whose operational quantity the view shows in place of the contract's (unsigned, as recorded).
 // A started composition is valued only with its lifecycle state: held, a booked trade's recorded cash; unrecorded, a draft loaded in its term.
 async Task<GasCompositionValue> Value(NpgsqlConnection c,PreviewData data,InputFrame frame,ImmutableArray<GasCompositionRow> members,ExactDecimal quantity,GasCompositionInstrument? draft,Guid? setup,ImmutableArray<RetainedSideProgram>? retained,bool view,CancellationToken ct,
  IReadOnlyDictionary<string,ImmutableDictionary<DateOnly,(ExactQuantity Quantity,string Kind)>>? recorded=null,Action<SideViewInput,GasDayClock>? inspect=null,GasCompositionExecution? held=null,PhysicalCashState? unrecorded=null)
 {
  if(frame.ContextId is not {} contextId)throw Invalid("Use a certified calculation context.");
  var unit=QuantityUnit(data,members.Select(m=>m.Assignments[0].Quote.Quantity));
  var programs=retained??[..draft!.Sides.Select((m,n)=>{var d=ScheduleDefinitionCompiler.Generate(m.Side,ct,ScheduleLifecycleSelection.StartedPhysicalDelivery);var p=BookedSideProgram.Compile(d);return new RetainedSideProgram(n,m.Side.Id,Guid.Empty,new(BookedSideProgram.Digest(p.Write()),d.Manifest,d.Family,d.EconomicIdentity,d.PricingBindingIdentity),p);})];
  var knowledge=(await new KnowledgeStore(database.Manifest.CatalogId).Read(c,contextId,ct)).Context;
  var snapshot=MarketSnapshotBinding.Bind(data.Snapshot(frame),contextId,SchedulePreview.ParseAt(knowledge.KnownAt));
  if(held is not {Started:true}&&unrecorded is null)PhysicalLifecycle.RequireUnstarted(programs.Select(p=>p.Program),snapshot.ValuationAt);
  var assignments=members.SelectMany(m=>m.Assignments).Distinct().ToImmutableArray();
  var dated=programs.SelectMany(p=>BookedScheduleLookups.Dated(data,p.Program)).Distinct().ToImmutableArray();
  foreach(var group in dated.Where(lookup=>lookup.CustomProjection is not null).GroupBy(lookup=>lookup.CustomProjection!))
   await CustomCurves.RequireProjection(c,database,data,frame,CustomCurveProjectionInput.FromBinding(group.Key),PriceUnit.FromQuote(group.First().Quote),[..group.Select(lookup=>lookup.ForecastDelivery)],ct);
  var deps=dated.Select(x=>PeriodPriceSelector.ProjectionAt(x,snapshot)).OfType<CurveRequest>().Concat(programs.SelectMany(p=>PhysicalLifecycle.DeliveryMarks(p.Program,dated,snapshot))).Distinct().ToImmutableArray();
  var prepared=await CustomCurves.PrepareMarket(c,database,data,frame,snapshot,knowledge,[],deps,CustomCurves.MarketPreparation.ValuationDependencies,ct,
   activePrices:[..dated.Select(lookup=>SchedulePriceTerm.FromLookup(lookup))]);
  var market=prepared.Valuation.ProgramMarket(prepared.Bindings,prepared.Authority,ct);var sources=new ScheduleMarketSources(market.Graph.Plan.Context,market,null,null,IntervalKnowledgePolicy.ResolvedCutoff,PendingActualsPolicy.RequireActuals,dated,[],assignments);
  // Both composition profiles admit only an explicit no-discounting selection (Resolve, and the held terms below).
  const DiscountFactorSource discounting=DiscountFactorSource.Undiscounted;
  var discount=new ExplicitDiscounting(Number("1"),discounting);ExactMoney? total=null;var views=view?ImmutableArray.CreateBuilder<SideViewInput>():null;var quantities=new Dictionary<string,ExactQuantity>(StringComparer.Ordinal);
  var valued=new List<(GasCompositionRow Member,ExactMoney Amount,ExactQuantity Quantity,List<object> Rows)>();var evaluations=ImmutableArray.CreateBuilder<LifecycleMember>();
  foreach(var member in members.OrderBy(x=>x.Slot))
  {
   var p=programs.Single(x=>x.Slot==member.Slot);var result=sources.Evaluate(p.Program,p.SideId,market,discount,ct);
   var direction=Number(member.Direction.ToString(CultureInfo.InvariantCulture));
   var amount=result.PresentValue.Scale(member.Ratio).Scale(quantity).Scale(direction);total=total is null?amount:total.Add(amount);
   evaluations.Add(new(p.Program,result,member.Role=="physical",v=>v.Scale(member.Ratio).Scale(quantity).Scale(direction)));
   // The view applies the same booked quantity and signed membership as the present value.
   // A recorded quantity is shown with the side's direction, like the side's other quantities.
   var days=recorded?.GetValueOrDefault(member.Code)?.ToImmutableDictionary(d=>d.Key,d=>new ScheduleViewQuantity(new(BigInteger.Parse(d.Value.Quantity.Numerator)*member.Direction,BigInteger.Parse(d.Value.Quantity.Denominator)),d.Value.Kind));
   if(views is not null||inspect is not null)
   {
    var sideView=new SideViewInput(member.Code,result,p.Program,quantity,new(member.Ratio.Numerator*member.Direction,member.Ratio.Denominator),TicketDescriptions.ViewColumns(GasTicket.SideKind(member.Role)),days);
    views?.Add(sideView);inspect?.Invoke(sideView,member.Clock);
   }
   var rows=new List<object>();ExactQuantity? qtotal=null;
   if(member.Role=="physical")
   {
    var meters=await gas.Meters(c,data,member.Assignment,setup,ct);
    // Contractual rows come from the retained quantities, so an ended gas day outside the measure keeps its row.
    foreach(var day in OpenEtrm.MarketData.Physical.PhysicalDeliverySchedule.ReadLiteralQuantities(p.Program,ExactRatio.One))
    {var q=day.Quantity.Scale(quantity).Scale(member.Ratio);qtotal=qtotal is null?q:qtotal.Add(q);rows.Add(new{day=day.OperatingDate.ToString("yyyy-MM-dd",CultureInfo.InvariantCulture),day.Start,day.End,quantity=Ratio(q),quantitySource="contractual",meter=PhysicalGasStore.Meter(data,meters,day.OperatingDate,setup)});}
    quantities.Add(member.Code,qtotal!);
   }
   else
   {
    qtotal=p.Program.Document.Instructions.Where(i=>i.Output.Operation=="quantity_literal").SelectMany(i=>result.QuantityColumn(i.Output.Ordinal,quantity)).Aggregate((a,b)=>a.Add(b)).Scale(member.Ratio);
   }
   valued.Add((member,amount,qtotal!,rows));
  }
  // Started delivery: the trade's value is unrealized plus realized unsettled, with its recorded cash.
  PhysicalLifecycleValue? lifecycle=null;var state=unrecorded;
  if(held is {Started:true})
  {
   if(held.Members.Length!=members.Length)throw Invalid("Started-delivery valuation requires every member of the held trade.");
   if(held.Terms.Discounting!=false)throw Invalid("A held gas composition requires its explicit no-discounting selection.");
   state=await GasCashPaymentStore.Read(c,database.Manifest.CatalogId,held.TradeId,held.InstrumentId,new(quantity,members[0].Assignment.Quote.Quantity),GasCashPaymentStore.LatestKnownRevision,ct);
   // The same admission of the recorded cash as the retained instrument's risk valuation.
   held.Instrument(dated).ValidateValuationState(state);
  }
  if(state is not null){lifecycle=PhysicalLifecycle.Evaluate(evaluations.ToImmutable(),state,snapshot,members[0].Assignment.Quote.Currency);total=lifecycle.Total;}
  var sides=valued.Select(v=>(object)new{v.Member.Code,v.Member.Role,source=v.Member.Source,stage=v.Member.Stage,pipeline=v.Member.Role=="physical"?v.Member.Assignment.Location.Pipeline.Code:null,zone=v.Member.Role=="physical"?v.Member.Assignment.Location.Zone.Code:null,deliveryPoint=v.Member.Role=="physical"?v.Member.Assignment.Point.Code:null,
   direction=v.Member.Direction==1?"receive":v.Member.Role=="physical"?"deliver":"pay",
   presentValue=(lifecycle is null?v.Amount:lifecycle.Rows.Where(r=>r.Slot==v.Member.Slot).Aggregate(ExactMoney.Parse("0",v.Amount.Currency),(sum,r)=>sum.Add(r.Unrealized).Add(r.RealizedUnsettled))).ToExactString(),
   quantity=Ratio(v.Quantity),deliveryRows=v.Rows}).ToList();
  var links=!view?Array.Empty<object>():members.Where(m=>m.Role=="physical"&&m.Source is not null).Select(m=>{var receipt=quantities[m.Source!];var delivery=quantities[m.Code];var fuel=receipt.Add(delivery.Scale(Number("-1")));return(object)new{source=m.Source,target=m.Code,m.Formula,m.Fraction,receipt=receipt.ToExactString(),delivery=delivery.ToExactString(),fuel=fuel.ToExactString()};}).ToArray();
  // The schedule is a display projection: a trade it cannot show keeps its valuation and reports why.
  ScheduleView? schedule=null;GasScheduleRefusal? refusal=null;
  if(views is not null)
   try
   {
    var clock=members.Where(m=>m.Role=="physical").Select(m=>m.Clock).Distinct().ToArray();
    if(clock.Length!=1)throw new MarketDataException("schedule_view","A gas schedule view requires every physical side to share one gas-day clock.");
    schedule=ScheduleViews.Build(views.ToImmutable(),GasTicket.Description.Levels,clock[0].Boundary);
   }
   catch(MarketDataException error)when(error.Code is "schedule_view" or "gas_day_boundary"){refusal=new(error.Code,error.Message);}
  // No contract-against-market measure is defined for these sides yet, so none is reported.
  var summaries=schedule is null?null:views!.Select(v=>(object)new{side=v.Code,billedQuantity=schedule.Totals[v.Code+".quantity"].Value,amount=schedule.Totals[v.Code+".amount"].Value,againstMarket=(string?)null}).ToArray();
  return new(total!.ToExactString(),members[0].Assignment.Quote.Currency.Code,unit,sides,links,schedule,summaries,refusal,members.ToImmutableDictionary(m=>m.Code,m=>m.Slot),
   lifecycle is null?null:GasLifecycleMeasures.From(lifecycle,discounting)){Lifecycle=lifecycle};
 }
 // A preview and a booking are held to the gas setup their context resolves (GasTicket.RequireSetup).
 async Task RequireSetup(NpgsqlConnection c,PreviewData data,GasCompositionRequest body,CancellationToken ct)=>GasTicket.RequireSetup(body.GasSetupId,(await gas.CurrentSetup(c,data,ct)).Id);
 public async Task<object> Preview(NpgsqlConnection c,PreviewData data,InputFrame frame,GasCompositionRequest body,CancellationToken ct)
 {if(body.TradeId is not null)await GasAmendments.Require(c,database.Manifest.CatalogId,body,frame.At,ct);await RequireSetup(c,data,body,ct);var d=await Resolve(c,data,body,ct);return await Value(c,data,frame,d.Members,d.Instrument.BaseQuantity,d.Instrument,body.GasSetupId,null,true,ct);}
 // The single market price a physical side's schedule shows over its delivery term, evaluated from a price probe (GasTicket.PriceProbe).
 internal async Task<string> MarketPrice(NpgsqlConnection c,PreviewData data,InputFrame frame,GasCompositionRequest probe,CancellationToken ct)
 {
  GasCompositionValue value;
  try{var d=await Resolve(c,data,probe,ct);value=await Value(c,data,frame,d.Members,d.Instrument.BaseQuantity,d.Instrument,probe.GasSetupId,null,true,ct);}
  catch(MarketDataException error)when(error.Code is "gas_input" or "gas_composition_input"){throw new MarketDataException("ticket_lookup",error.Message);}
  var code=probe.PhysicalSides.Single().Code;
  if(value.Schedule is null)throw new MarketDataException("ticket_lookup",$"The side's schedule cannot be shown, so its market price is not read: {value.ScheduleRefusal!.Message}");
  return value.Schedule.Totals[$"{code}.market_price"].Value??throw new MarketDataException("ticket_lookup","The side's delivery term has no single market price.");
 }
 // An in-term load (inTerm) values its draft with no recorded cash: there is no trade yet, so the operation that books it keys that state.
 internal Task<RetainedScheduleTerms> Prepare(NpgsqlConnection c,InputFrame frame,GasCompositionRequest body,bool inTerm,CancellationToken ct)=>TradingBookingRead.Run(database,c,frame,async(data,pinned)=>{if(body.TradeId is not null)await GasAmendments.Require(c,database.Manifest.CatalogId,body,pinned.At,ct);await RequireSetup(c,data,body,ct);var d=await Resolve(c,data,body,ct);
  var unrecorded=inTerm?new PhysicalCashState(body.OperationId,d.Instrument.Id,new(d.Instrument.BaseQuantity,d.Members[0].Assignment.Quote.Quantity),[],PhysicalCashState.Policy):null;
  _=await Value(c,data,pinned,d.Members,d.Instrument.BaseQuantity,d.Instrument,body.GasSetupId,null,false,ct,unrecorded:unrecorded);return new RetainedScheduleTerms(data.DefinitionRevision,data.ReferenceRevision,d.Instrument);},ct,physicalMarket:PhysicalMarketSelection.Gas,physicalTrade:body.TradeId);
 // A new trade names no amended trade; the ticket and the administrator in-term load both book new trades only.
 internal static void RequireNewTrade(GasCompositionRequest body)
 {if(body.TradeId is not null||body.ExpectedRevision is not null||body.BindingId is not null||body.Reason is not null||body.AmendmentPolicy is not null)throw Invalid("Use the explicit gas amendment operation to amend a trade.");}
 // Books, amends or (inTerm) loads one composition as the profile's identity: staging, locking and publication are
 // idempotent by operation id (D238), so a retried operation receives its first outcome.
 internal async Task<JsonElement> Book(string profile,InputFrame frame,GasCompositionRequest body,bool inTerm,CancellationToken ct)
 {
  var retained=new RetainedScheduleStore(database);var permission=body.TradeId is null?"BookTrade":"AmendTrade";
  var terms=await database.WithIdentity(profile,permission,async(connection,ct)=>{
   var status=await gas.Status(connection,body.OperationId,ct);
   return status.TryGetProperty("tradeId",out _)?null:await Prepare(connection,frame,body,inTerm,ct);
  },ct);
  if(terms is not null)await retained.Stage(profile,body.OperationId,body,permission,body.TradeId,body.ExpectedRevision,terms.Generate(ct),ct);
  return await database.WithIdentity(profile,permission,async(connection,ct)=>{
   if(terms is not null)await retained.Lock(connection,body.OperationId,ct);
   var result=await Save(connection,frame,body,inTerm,ct);
   if(terms is not null&&!(result.TryGetProperty("unchanged",out var unchanged)&&unchanged.GetBoolean()))await retained.Publish(connection,body.OperationId,ct);
   return result;
  },ct);
 }
 async Task<JsonElement> Save(NpgsqlConnection c,InputFrame frame,GasCompositionRequest body,bool inTerm,CancellationToken ct)
 {var operation=inTerm?"load_gas_composition_in_term":body.TradeId is null?"book_gas_composition":"amend_gas_composition";await using var q=ScheduleStore.Command(c,$"SELECT security.{operation}($1,$2::jsonb,$3)::text",database.Manifest.CatalogId,JsonSerializer.Serialize(body,Json),frame.At);return JsonSerializer.Deserialize<JsonElement>((string)(await q.ExecuteScalarAsync(ct))!);}
 public async Task<object> Editor(NpgsqlConnection c,PreviewData data,InputFrame frame,Guid trade,CancellationToken ct)=>await Read(c,data,frame,trade,ct);
 internal async Task<SideScheduleSnapshot> InspectSchedule(NpgsqlConnection c,PreviewData data,InputFrame frame,GasCompositionExecution execution,int slot,CancellationToken ct)
 {
  var member=execution.Members.SingleOrDefault(m=>m.Slot==slot)??throw new MarketDataException("side_schedule_inspection","Select one visible Side slot.");
  var program=execution.Programs.Single(p=>p.Slot==member.Slot);SideScheduleSnapshot? result=null;
  if(execution.Started)
  {
   // The Side's rows come from the whole instrument's lifecycle: its cash accrues over its physical gas days.
   SideViewInput? side=null;
   var value=await Value(c,data,frame,execution.Members,execution.Quantity,null,execution.Terms.GasSetupId,execution.Programs,false,ct,
    inspect:(input,_)=>{if(input.Code==member.Code)side=input;},held:execution);
   var rows=value.Lifecycle!.Rows.Where(r=>r.Slot==slot).ToImmutableArray();
   var final=await GasCashPaymentStore.FinalDeterminations(c,database.Manifest.CatalogId,execution.TradeId,execution.InstrumentId,ct);
   // A cash row shows a final determination when every gas day it covers has one; it never replaces the accrual.
   var determinations=rows.Where(r=>r.Settlement is not null).Select(r=>(r.Occurrence,Days:Enumerable.Range(0,r.DeliveryTo.DayNumber-r.DeliveryFrom.DayNumber).Select(r.DeliveryFrom.AddDays).ToArray()))
    .Where(r=>r.Days.All(d=>final.ContainsKey((slot,d))))
    .ToImmutableDictionary(r=>r.Occurrence,r=>r.Days.Select(d=>final[(slot,d)]).Aggregate((a,b)=>a.Add(b)));
   return SideScheduleSnapshot.FromLifecycle(program.SideId,frame.Id,side??throw new MarketDataException("side_schedule_inspection","The Side produced no evaluated schedule."),rows,determinations,member.Clock.Boundary);
  }
  try
  {
   await Value(c,data,frame,[member],execution.Quantity,null,execution.Terms.GasSetupId,[program],false,ct,
    inspect:(side,clock)=>result=SideScheduleSnapshot.FromProgram(program.SideId,frame.Id,side,clock.Boundary));
  }
  catch(MarketDataException error)when(ValuationStatus.PositionSpecific(error)||error.Code is "gas_delivery_lifecycle" or "side_payment_due")
  {
   var knowledge=(await new KnowledgeStore(database.Manifest.CatalogId).Read(c,frame.ContextId!.Value,ct)).Context;
   result=SideScheduleSnapshot.Plan(program.SideId,frame.Id,frame.ContextId!.Value,member.Code,program.Program,member.Clock.Boundary,frame.At,SchedulePreview.ParseAt(knowledge.KnownAt),error.Code);
  }
  return result??throw new MarketDataException("side_schedule_inspection","The Side produced no evaluated schedule.");
 }
 // A single-point trade read as the composition it is (GasTicket.SinglePoint): every side a whole membership of the booked quantity on the
 // one physical side's path, valued from the trade's own retained programs, whose slots are the composition's: gas, payment, then fee.
 internal async Task<GasCompositionRead> ReadSinglePoint(NpgsqlConnection c,PreviewData data,InputFrame frame,Guid trade,CancellationToken ct)
 {
  var execution=await LoadSinglePoint(c,data,frame,trade,ct);
  return new(execution.Terms,await Value(c,data,frame,execution,ct));
 }
 internal async Task<GasCompositionExecution> LoadSinglePoint(NpgsqlConnection c,PreviewData data,InputFrame frame,Guid trade,CancellationToken ct)
 {
  var(terms,instrument)=await gas.Retained(c,frame,trade,ct);var body=GasTicket.SinglePoint(terms);
  var m=await gas.Metadata(c,data,terms,ct);var whole=new ExactRatio(1,1);var physical=body.PhysicalSides.Single();
  ImmutableArray<GasCompositionRow> members=[new(0,physical.Code,"physical",Sign(physical.Direction,true),whole,whole,null,null,null,null,null,null,m.Assignments,m.Clock),
   ..body.CashSides.Select((s,n)=>new GasCompositionRow(n+1,s.Code,s.Role,Sign(s.Direction,false),whole,whole,s.Source,s.Stage,null,null,null,null,m.Assignments,m.Clock))];
  var programs=await new RetainedScheduleStore(database).LoadPrograms(c,instrument,frame.ContextId,frame.ContextId is null?null:frame.At,ct);
  return new(trade,body,instrument,members,Number(physical.Quantity!),programs);
 }
 // The valuation of a loaded trade, as its reads value it.
 internal Task<GasCompositionValue> Value(NpgsqlConnection c,PreviewData data,InputFrame frame,GasCompositionExecution execution,CancellationToken ct,
  IReadOnlyDictionary<string,ImmutableDictionary<DateOnly,(ExactQuantity Quantity,string Kind)>>? recorded=null)=>
  Value(c,data,frame,execution.Members,execution.Quantity,null,execution.Terms.GasSetupId,execution.Programs,true,ct,recorded,held:execution);
 // programs, when the caller already loaded the trade's retained programs under this frame, are used in place of loading them again.
 internal async Task<GasCompositionRead> Read(NpgsqlConnection c,PreviewData data,InputFrame frame,Guid trade,CancellationToken ct,
  IReadOnlyDictionary<string,ImmutableDictionary<DateOnly,(ExactQuantity Quantity,string Kind)>>? recorded=null,ImmutableArray<RetainedSideProgram>? programs=null)
 {
  var execution=await Load(c,data,frame,trade,ct,programs);
  return new(execution.Terms,await Value(c,data,frame,execution,ct,recorded));
 }
 internal async Task<GasCompositionExecution> Load(NpgsqlConnection c,PreviewData data,InputFrame frame,Guid trade,CancellationToken ct,
  ImmutableArray<RetainedSideProgram>? programs=null)
 {
  var selected=(await GasPositions.Read(c,database.Manifest,[],[],ct,trade:trade)).SingleOrDefault()??throw Invalid("The selected gas position is unavailable.");
  var header=(await PreviewDatabase.Rows(c,"SELECT row_to_json(x)::text FROM(SELECT r.*,i.definition_revision_id,i.gas_setup_id,CASE WHEN r.revision=1 AND t.operation_id=r.operation_id THEN t.request ELSE a.request END request FROM trading.gas_trade_revisions r JOIN trading.gas_instruments i USING(catalog_id,instrument_id) JOIN trading.trades t ON(t.catalog_id,t.id)=(r.catalog_id,r.trade_id) LEFT JOIN trading.gas_amendment_commands a ON(a.catalog_id,a.operation_id,a.trade_id,a.result_revision,a.changed)=(r.catalog_id,r.operation_id,r.trade_id,r.revision,true) WHERE(r.catalog_id,r.trade_id,r.instrument_id,r.revision)=($1,$2,$3,$4) AND i.composition)x",[database.Manifest.CatalogId,trade,selected.Binding,int.Parse(selected.Head.Text("revision"),CultureInfo.InvariantCulture)],ct)).SingleOrDefault();
  if(header.ValueKind!=JsonValueKind.Object)throw Invalid("The composed gas trade is unavailable.");
  var rows=await PreviewDatabase.Rows(c,"SELECT row_to_json(x)::text FROM trading.gas_component_terms x WHERE(catalog_id,instrument_id)=($1,$2) ORDER BY slot",[database.Manifest.CatalogId,header.Id("instrument_id")],ct);
  programs??=await new RetainedScheduleStore(database).LoadPrograms(c,header.Id("instrument_id"),frame.ContextId,frame.ContextId is null?null:frame.At,ct);
  GasCompositionExecution.RequireSlots(rows.Select(r=>int.Parse(r.Text("slot"),CultureInfo.InvariantCulture)),programs.Value);
  // The original request is returned for inspection, not used to generate a Side
  // or executable program. Typed terms resolve exact market metadata only.
  if(header.GetProperty("request").ValueKind!=JsonValueKind.Object)throw Invalid("The selected gas revision requires its exact retained command.");
  var body=JsonSerializer.Deserialize<GasCompositionRequest>(header.GetProperty("request").GetRawText(),Json)!;
  var valuationBody=body with{DefinitionRevisionId=header.Id("definition_revision_id"),GasSetupId=header.OptionalText("gas_setup_id") is {} setup?Guid.Parse(setup):null,SnapshotId=frame.Id};
  var members=ImmutableArray.CreateBuilder<GasCompositionRow>();var phys=new Dictionary<int,(GasPhysicalRequest Physical,ImmutableArray<GasValuationAssignment> Assignments,GasDayClock Clock)>();
  foreach(var r in rows.Where(r=>r.Text("role")=="physical"))
  {
   var p=new GasPhysicalRequest(r.Text("code"),r.Text("pipeline_code"),r.Text("zone_code"),r.Text("point_code"),r.Text("product_code"),r.Text("direction")=="1"?"receive":"deliver",r.Text("delivery_from"),r.Text("delivery_to"),r.Text("quantity_basis"),r.OptionalText("independent_quantity"),null);
   var m=await gas.Metadata(c,data,Selection(valuationBody,p),ct);phys.Add(int.Parse(r.Text("slot")),(p,m.Assignments,m.Clock));
  }
  foreach(var r in rows)
  {
   var slot=int.Parse(r.Text("slot"));var source=r.OptionalText("source_slot") is {} s?int.Parse(s):(int?)null;var a=phys[r.Text("role")=="physical"?slot:source!.Value];
   members.Add(new(slot,r.Text("code"),r.Text("role"),int.Parse(r.Text("direction")),new(BigInteger.Parse(r.Text("ratio_n")),BigInteger.Parse(r.Text("ratio_d"))),new(BigInteger.Parse(r.Text("receipt_n")),BigInteger.Parse(r.Text("receipt_d"))),source is {} si?rows.Single(x=>x.Text("slot")==si.ToString(CultureInfo.InvariantCulture)).Text("code"):null,r.OptionalText("stage"),r.OptionalText("formula"),r.OptionalText("fraction"),null,null,a.Assignments,a.Clock));
  }
  return new(trade,body,header.Id("instrument_id"),members.ToImmutable(),Number(header.Text("quantity")),programs.Value);
 }
}
