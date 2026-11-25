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
public sealed record GasPhysicalRequest(string Code,string Pipeline,string Zone,string DeliveryPoint,string Product,string Direction,string DeliveryFrom,string DeliveryTo,string QuantityBasis,[property:JsonRequired]string? Quantity,[property:JsonRequired]GasQuantityLinkRequest? Link,[property:JsonIgnore(Condition=JsonIgnoreCondition.WhenWritingNull)]CustomCurveProjectionInput? ForecastCurve=null)
{
 [System.Text.Json.Serialization.JsonIgnore(Condition=System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
 public SideCalendarPolicyInput CalendarPolicy { get; init; }=null!;
 [JsonIgnore(Condition=JsonIgnoreCondition.WhenWritingNull)]
 public string? QuantityPolicy { get; init; }
}
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record GasQuantityPartRequest(string Kind,string Lower,string? Upper);
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record GasCashRequest(string Code,string Role,string Direction,string Source,string Stage,[property:JsonRequired]string? Price,[property:JsonRequired]string? TariffCode,string? PaymentAt,
 [property:JsonIgnore(Condition=JsonIgnoreCondition.WhenWritingNull)]GasIndexedCashRequest? Indexed=null)
{
 // Absent only in retained predecessor commands; fresh admission requires an explicit share.
 [JsonIgnore(Condition=JsonIgnoreCondition.WhenWritingNull)] public string Share { get; init; }=null!;
 [JsonIgnore(Condition=JsonIgnoreCondition.WhenWritingNull)] public GasQuantityPartRequest? Part { get; init; }
 [JsonIgnore(Condition=JsonIgnoreCondition.WhenWritingNull)] public string? DeliveryFrom { get; init; }
 [JsonIgnore(Condition=JsonIgnoreCondition.WhenWritingNull)] public string? DeliveryTo { get; init; }
 [System.Text.Json.Serialization.JsonIgnore(Condition=System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
 public SideCalendarPolicyInput CalendarPolicy { get; init; }=null!;
}
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record GasChargeRequest(string Code,string Basis,string Direction,string Price,string? DailyQuantity,string? From,string? To,string? PaymentAt,int? PaymentDay,string ClockSide)
{
 public SideCalendarPolicyInput CalendarPolicy {get;init;}=null!;
 internal ChargeBasis RequireBasis()=>Basis switch{
  "lump_sum" when PaymentAt is not null&&DailyQuantity is null&&From is null&&To is null&&PaymentDay is null=>ChargeBasis.LumpSum,
  "capacity" when PaymentAt is null&&DailyQuantity is not null&&From is not null&&To is not null&&PaymentDay is >=1 and <=28=>ChargeBasis.Capacity,
  _=>throw new MarketDataException("gas_composition_input","Supply a lump sum with one payment instant, or capacity with daily quantity, dates and a following-month payment day from 1 to 28.")};
}
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record GasExerciseRightRequest(ImmutableArray<string> Entries,string Period,string Holder,string Settlement,int? LeadHours);
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record GasImbalanceCashoutRequest(string Code,string Profile,string Side,string Index,string SpreadOwedToUs,string SpreadOwedByUs,
 int PaymentDay,string StatementPolicy,string StatementBasis,string AccountNetting)
{
 public SideCalendarPolicyInput CalendarPolicy {get;init;}=null!;
}
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record GasCompositionRequest(Guid SnapshotId,string Convention,string KnowledgeMode,string Profile,Guid OperationId,Guid BookId,Guid DefinitionRevisionId,[property:JsonRequired]Guid? GasSetupId,bool? Discounting,Guid? LegalEntityId,int? LegalEntityRevision,Guid? CounterpartyId,int? CounterpartyRevision,ImmutableArray<GasPhysicalRequest> PhysicalSides,ImmutableArray<GasCashRequest> CashSides,
 [property:JsonIgnore(Condition=JsonIgnoreCondition.WhenWritingNull)]Guid? TradeId=null,[property:JsonIgnore(Condition=JsonIgnoreCondition.WhenWritingNull)]int? ExpectedRevision=null,
 [property:JsonIgnore(Condition=JsonIgnoreCondition.WhenWritingNull)]Guid? BindingId=null,[property:JsonIgnore(Condition=JsonIgnoreCondition.WhenWritingNull)]string? Reason=null,
 [property:JsonIgnore(Condition=JsonIgnoreCondition.WhenWritingNull)]string? AmendmentPolicy=null,
 [property:JsonIgnore(Condition=JsonIgnoreCondition.WhenWritingNull)]Guid? PreviewTradeId=null,
 [property:JsonIgnore(Condition=JsonIgnoreCondition.WhenWritingNull)]Guid? PreviewContextId=null)
{
 [JsonIgnore(Condition=JsonIgnoreCondition.WhenWritingNull)]public ImmutableArray<GasChargeRequest>? ChargeSides {get;init;}
 [JsonIgnore(Condition=JsonIgnoreCondition.WhenWritingNull)]public ImmutableArray<GasExerciseRightRequest>? ExerciseRights {get;init;}
 [JsonIgnore(Condition=JsonIgnoreCondition.WhenWritingNull)]public ImmutableArray<GasImbalanceCashoutRequest>? ImbalanceCashouts {get;init;}
 [JsonConverter(typeof(GasSwingRightsJson)),JsonIgnore(Condition=JsonIgnoreCondition.WhenWritingNull)]public ImmutableArray<GasSwingRightRequest>? SwingRights {get;init;}
}
internal sealed record GasCompositionRow(int Slot,string Code,string Role,int Direction,ExactRatio Ratio,ExactRatio Receipt,string? Source,string? Stage,string? Formula,string? Fraction,GasPhysicalRequest? Physical,GasCashRequest? Cash,ImmutableArray<GasValuationAssignment> Assignments,GasDayClock Clock,string? QuantityPolicy=null,ExactDecimal? Share=null,QuantityPart? Part=null,OwnedQuantity? Owned=null,ReportedBalanceFollow? Balance=null)
{
 // Quote and location are common to every period of the one exact mapping.
 internal GasValuationAssignment Assignment=>Assignments[0];
}
internal sealed record GasCompositionDraft(GasCompositionInstrument Instrument,ImmutableArray<GasCompositionRow> Members);
internal sealed record GasCompositionValue(string PresentValue,string Currency,string QuantityUnit,List<object> Sides,object[] Links,ScheduleView? Schedule,object[]? Summaries,GasScheduleRefusal? ScheduleRefusal,ImmutableDictionary<string,int> SideSlots,
 GasLifecycleMeasures? Measures=null)
{
 public ImmutableArray<ExerciseGroupReport> Groups{get;init;}=[];
 public int UnassessedDays=>Lifecycle?.UnassessedDays??0;
 public bool Incomplete=>UnassessedDays!=0;
 public ImmutableArray<GasImbalanceReportRow> ImbalanceRows{get;init;}=[];
 public ImmutableArray<TicketScheduleEntry> ScheduleEntries{get;init;}=[];
 public CatalogueSource? Source{get;init;}
 [JsonIgnore]internal PhysicalLifecycleValue? Lifecycle{get;init;}
 [JsonIgnore]internal ImmutableArray<InstrumentEntryValue> EntryValues{get;init;}=[];
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
 internal ImmutableArray<ExerciseRight> Rights{get;}
 internal ImmutableArray<SwingRight> SwingRights{get;}
 internal ImmutableArray<SwingPricingBinding> SwingPricingBindings{get;}
 internal Guid TradeId{get;}
 // Every retained program selects the started-delivery lifecycles; older or mixed trades keep their own refusals.
 internal bool Started=>GasCompositionStore.Started(Programs);
 internal GasCompositionExecution(Guid tradeId,GasCompositionRequest terms,Guid instrumentId,ImmutableArray<GasCompositionRow> members,
  ExactDecimal quantity,ImmutableArray<RetainedSideProgram> programs,ImmutableArray<ExerciseRight> rights=default,
  ImmutableArray<SwingRight> swingRights=default,ImmutableArray<SwingPricingBinding> swingPricingBindings=default)
 {
  RequireSlots(members.IsDefault?[]:members.Select(m=>m.Slot),programs);
  if(members.Any(m=>programs.Single(p=>p.Slot==m.Slot).Program.Document.Family!=(m.Role=="physical"?"gas":m.Role is "indexed_consideration" or "imbalance_cashout"?"floating":"fixed")))
   throw new MarketDataException("gas_program_membership","Every retained gas program requires its exact visible member slot and side family.");
  if(tradeId==Guid.Empty)throw new MarketDataException("gas_program_membership","Every retained gas program requires its exact held trade.");
  TradeId=tradeId;Terms=terms;InstrumentId=instrumentId;Members=members;Quantity=quantity;Programs=programs;Rights=rights.IsDefault?[]:rights;
  SwingRights=swingRights.IsDefault?[]:swingRights;SwingPricingBindings=swingPricingBindings.IsDefault?[]:swingPricingBindings;
 }
 internal static void RequireSlots(IEnumerable<int> members,ImmutableArray<RetainedSideProgram> programs)
 {
  if(programs.IsDefaultOrEmpty||!members.Order().SequenceEqual(Enumerable.Range(0,programs.Length))||
   !programs.Select(p=>p.Slot).Order().SequenceEqual(Enumerable.Range(0,programs.Length)))
   throw new MarketDataException("gas_program_membership","Every retained gas program requires its exact visible member slot and side family.");
 }
 internal RetainedGasInstrument Instrument(ImmutableArray<IndexPriceLookup> lookups)=>BuildInstrument(InstrumentId,Members,Programs,lookups,Rights,SwingRights,SwingPricingBindings);
 internal static RetainedGasInstrument BuildInstrument(Guid id,ImmutableArray<GasCompositionRow> members,
  ImmutableArray<RetainedSideProgram> programs,ImmutableArray<IndexPriceLookup> lookups,ImmutableArray<ExerciseRight> rights=default,
  ImmutableArray<SwingRight> swingRights=default,ImmutableArray<SwingPricingBinding> swingPricingBindings=default)
 {
  RequireSlots(members.Select(m=>m.Slot),programs);
  var quantityTerms=!rights.IsDefaultOrEmpty||!swingRights.IsDefaultOrEmpty||members.Any(m=>m.QuantityPolicy is not null||m.Part is not null||m.Balance is not null);
  var byCode=members.ToDictionary(m=>m.Code,StringComparer.Ordinal);
  var ordered=members.OrderBy(m=>m.Slot).ToImmutableArray();
  var sources=ordered.Select(m=>m.Owned is not null?(int?)null:m.Role=="physical"?m.Slot:byCode[m.Source!].Slot).ToImmutableArray();
  return new(id,[..ordered.Select(m=>{
   var p=programs.Single(p=>p.Slot==m.Slot);
   return new RetainedGasMember(p.SideId,ExactDecimal.Parse(m.Direction.ToString(CultureInfo.InvariantCulture)),m.Ratio,p.Program,
    quantityTerms?sources[m.Slot]:null,m.QuantityPolicy,m.Part,m.Owned,m.Balance);
  })],[..members.SelectMany(m=>m.Assignments).Distinct()],lookups,rights:rights,physicalSources:sources,swingRights:swingRights,swingPricingBindings:swingPricingBindings);
 }

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
 static ExercisePeriod Period(string value)=>value switch{"delivery_day"=>ExercisePeriod.DeliveryDay,"delivery_month"=>ExercisePeriod.DeliveryMonth,_=>throw Invalid("Select a supported exercise period.")};
 static ExerciseHolder Holder(string value)=>value switch{"us"=>ExerciseHolder.Us,"counterparty"=>ExerciseHolder.Counterparty,_=>throw Invalid("Select an explicit exercise holder.")};
 static ExerciseSettlement Settlement(string value)=>value switch{"physical"=>ExerciseSettlement.Physical,"cash_automatic"=>ExerciseSettlement.CashAutomatic,_=>throw Invalid("Select a supported exercise settlement.")};
 async Task<GasCompositionDraft> Resolve(NpgsqlConnection c,PreviewData data,GasCompositionRequest body,DateTimeOffset at,CancellationToken ct)
 {
  var charges=body.ChargeSides??[];
  if(charges.IsDefault||charges.Any(p=>p is null)||body.PhysicalSides.IsDefault||body.CashSides.IsDefault||body.PhysicalSides.Any(s=>s is null)||body.CashSides.Any(s=>s is null))throw Invalid("Supply each physical and payment Side explicitly.");
  if(body.ExerciseRights is {IsDefaultOrEmpty:false}&&body.PhysicalSides.Any(p=>p.Link is not null))throw Invalid("Exercise rights require independent physical sources without quantity links.");
  if(body.PhysicalSides.Any(p=>p.QuantityPolicy is not(null or GasDeliveryQuantityStore.Policy))||
   body.PhysicalSides.Any(p=>p.QuantityPolicy is not null)&&body.PhysicalSides.Any(p=>p.Link is not null))
   throw new MarketDataException("gas_quantity_policy","Delivery schedule quantities require independent physical sides and the saved delivery schedule policy.");
  var entries=GasCashoutTerms.Entries(body);
  var cashRequests=entries.Select(entry=>entry.Cash).ToImmutableArray();
  data=await SideCalendarPolicies.Prepare(c,data,body.PhysicalSides.Select(s=>s.CalendarPolicy).Concat(cashRequests.Select(s=>s.CalendarPolicy)),ct);
  if(body.Profile!=(cashRequests.Any(s=>s.Indexed is not null)?IndexedProfile:Profile)||body.Discounting!=false||body.KnowledgeMode!="current"||body.PhysicalSides.IsDefault||body.PhysicalSides.Length is <GasCompositionInstrument.MinPhysicalSides or >GasCompositionInstrument.MaxPhysicalSides||body.CashSides.IsDefault||cashRequests.Length+charges.Length>GasCompositionInstrument.MaxCashSides||body.PhysicalSides.Length+cashRequests.Length+charges.Length>GasCompositionInstrument.MaxSides)
   throw Invalid($"Select the bounded fixed-quantity gas composition profile, explicit no discounting, {GasCompositionInstrument.MinPhysicalSides} to {GasCompositionInstrument.MaxPhysicalSides} physical sides and up to {GasCompositionInstrument.MaxCashSides} cash sides.");
  foreach(var charge in charges)
  {
   try{data=await SideCalendarPolicies.Prepare(c,data,[charge.CalendarPolicy],ct);}
   catch(MarketDataException error)when(error.Code=="calendar_reference")
   {throw ChargeField(charge,"calendar_policy",error);}
  }
  var physical=ImmutableArray.CreateBuilder<GasPhysicalComponent>();var assignments=new List<ImmutableArray<GasValuationAssignment>>();var clocks=new List<GasDayClock>();var mappings=new List<JsonElement>();
  foreach(var p in body.PhysicalSides)
  {
   var m=await gas.Metadata(c,data,Selection(body,p),ct);assignments.Add(m.Assignments);clocks.Add(m.Clock);mappings.Add(m.Mapping);
   var basis=p.QuantityBasis switch{"per_operating_day"=>GasQuantityBasis.PerOperatingDay,"period_total_equal_days"=>GasQuantityBasis.PeriodTotalEqualOperatingDays,_=>throw Invalid("Choose each physical quantity basis explicitly.")};
   var side=new PhysicalGasSide(Guid.NewGuid(),m.Assignments[0].Location,m.Assignments[0].Product,new(m.Clock,DateWorkspace.Date(p.DeliveryFrom),DateWorkspace.Date(p.DeliveryTo),basis,new(Number("1"),m.Assignments[0].Quote.Quantity),GasFuelConvention.None,default),m.Assignments,SideCalendarPolicies.Resolve(data,p.CalendarPolicy),GasPointPricing.Resolve(database.Manifest.CatalogId,m.Mapping,p.ForecastCurve)?.ToBinding());
   var link=p.Link is null?null:new GasCompositionLink(p.Link.Source,p.Link.Formula switch{"none"=>GasFuelConvention.None,"gross_less_fraction"=>GasFuelConvention.NetEqualsGrossTimesOneMinusFuel,"gross_divided_by_one_plus_fraction"=>GasFuelConvention.NetEqualsGrossDividedByOnePlusFuel,_=>throw Invalid("Select an installed fuel rule.")},Number(p.Link.Fraction));
   physical.Add(new(p.Code,side,Number(Sign(p.Direction,true).ToString(CultureInfo.InvariantCulture)),p.Quantity is null?null:Number(p.Quantity),link,p.QuantityPolicy));
  }
  var baseQuantity=physical.FirstOrDefault(p=>p.Link is null)?.Quantity??throw Invalid("An independent physical quantity is required.");
  if(baseQuantity.CompareTo(default)<=0)throw Invalid("An independent positive physical quantity is required.");
  var cash=ImmutableArray.CreateBuilder<GasCashComponent>();
  foreach(var entry in entries)
  {
   var p=entry.Cash;
   var indices=body.PhysicalSides.Select((v,n)=>(v,n)).Where(x=>x.v.Code==p.Source).ToArray();if(indices.Length!=1)throw Invalid("Cash quantity sources must name one physical side.");var n=indices[0].n;var source=physical[n].Side;
   var share=PaymentShare(p.Share);var part=PaymentPart(p.Part,baseQuantity);
   var from=p.DeliveryFrom is null?source.Quantities.From:DateWorkspace.Date(p.DeliveryFrom);
   var to=p.DeliveryTo is null?source.Quantities.To:DateWorkspace.Date(p.DeliveryTo);
   if(from<source.Quantities.From||to>source.Quantities.To||from>=to)throw Invalid("Payment dates must lie within the source's gas days.");
   if(p.Indexed is not null){cash.Add(new(p.Code,GasIndexedCash.Resolve(data,source,p,mappings[n],from,to),entry.Balance is null?p.Role:"imbalance_cashout",p.Source,Stage(p.Stage),Number(Sign(p.Direction,false).ToString(CultureInfo.InvariantCulture)),share,part,entry.Balance));continue;}
   if(p.Role=="indexed_consideration")throw Invalid("An indexed cash Side requires its explicit index terms.");
   string? price=p.Price;
   if(p.TariffCode is not null)
   {
    if(p.Role!="fee"||p.Stage!="delivery"||price is not null)throw Invalid("Configured usage tariffs require a delivery-based fee with no manual price.");
    price=await gas.UsageFee(c,data,Selection(body,body.PhysicalSides[n]) with{UsageTariffCode=p.TariffCode,DeliveryFrom=from.ToString("yyyy-MM-dd",CultureInfo.InvariantCulture),DeliveryTo=to.ToString("yyyy-MM-dd",CultureInfo.InvariantCulture)},mappings[n],ct);
   }
   if(price is null)throw Invalid("Specify the fixed cash price or an eligible usage tariff.");
   var payment=SchedulePreview.ParseAt(p.PaymentAt??throw Invalid("Specify the fixed cash payment date."));
   if(payment.Ticks%10!=0||Number(price).CompareTo(Number("1000000000000"))>=0||Number(price).CompareTo(Number("-1000000000000"))<=0)throw Invalid("Cash prices must have absolute value below 10^12 and payments need microsecond precision.");
   PaymentDatePolicy.RequireCompletion(payment,source.Quantities.Clock.Boundary(to));
   var total=source.GenerateSchedule().Quantities.Rows.Where(d=>d.OperatingDay>=from&&d.OperatingDay<to).Select(d=>d.Delivery).Aggregate((x,y)=>x.Add(y));
   var side=new FixedSide(Guid.NewGuid(),new FlatPrice(new UnitPrice(Number(price),assignments[n][0].Quote)),assignments[n][0].Quote,source.UnitQuantity,[new(from,to,payment,new(BigInteger.Parse(total.Numerator),BigInteger.Parse(total.Denominator)),null){DeliveryCompletionAt=source.Quantities.Clock.Boundary(to)}],SideCalendarPolicies.Resolve(data,p.CalendarPolicy),gasDeliveryClock:source.Quantities.Clock);
   cash.Add(new(p.Code,side,p.Role,p.Source,Stage(p.Stage),Number(Sign(p.Direction,false).ToString(CultureInfo.InvariantCulture)),share,part));
  }
  var owning=ImmutableArray.CreateBuilder<GasChargeComponent>();
  var clockSlots=new List<int>();
  foreach(var p in charges)
  {
   var matches=body.PhysicalSides.Select((v,i)=>(v,i)).Where(x=>x.v.Code==p.ClockSide).ToArray();
   if(matches.Length!=1)throw ChargeField(p,"charge_clock_side",Invalid("Select one physical Side of this trade for the charge clock."));
   var slot=matches[0].i;clockSlots.Add(slot);
   var charge=ResolveCharge(p,assignments[slot][0].Quote,physical[slot].Side.UnitQuantity,clocks[slot],
    ()=>SideCalendarPolicies.Resolve(data,p.CalendarPolicy));
   if(body.TradeId is not null&&clocks[slot].Boundary(charge.Side.DeliveryFrom)<=at)
    throw new MarketDataException("gas_amendment_started",GasAmendments.Message("gas_amendment_started")!);
   owning.Add(charge);
  }
  var rights=(body.ExerciseRights??[]).Select(r=>r is null?throw Invalid("Supply each exercise right explicitly."):
   new GasExerciseRight(r.Entries,Period(r.Period),Holder(r.Holder),Settlement(r.Settlement),r.LeadHours)).ToImmutableArray();
  var (components,bindings)=GasSwingBooking.Resolve(body.SwingRights);
  var instrument=new GasCompositionInstrument(Guid.NewGuid(),physical.ToImmutable(),cash.ToImmutable(),rights,owning.ToImmutable(),components,bindings);var members=ImmutableArray.CreateBuilder<GasCompositionRow>();
  for(var n=0;n<physical.Count;n++){var p=body.PhysicalSides[n];members.Add(new(n,p.Code,"physical",Sign(p.Direction,true),instrument.Sides[n].QuantityRatio!,instrument.ReceiptRatios[n],p.Link?.Source,null,p.Link?.Formula,p.Link?.Fraction,p,null,assignments[n],clocks[n],p.QuantityPolicy));}
  for(var n=0;n<cash.Count;n++){var p=cashRequests[n];var source=body.PhysicalSides.Select((v,i)=>(v,i)).Single(x=>x.v.Code==p.Source).i;members.Add(new(physical.Count+n,p.Code,cash[n].Role,Sign(p.Direction,false),instrument.Sides[physical.Count+n].QuantityRatio!,instrument.Sides[physical.Count+n].QuantityRatio!,p.Source,p.Stage,null,null,null,p,assignments[source],clocks[source],Share:cash[n].Share,Part:cash[n].Part,Balance:cash[n].Balance));}
  for(var n=0;n<owning.Count;n++)
  {
   var p=charges[n];var slot=physical.Count+cash.Count+n;var ratio=instrument.Sides[slot].QuantityRatio!;
   var clock=clockSlots[n];
   members.Add(new(slot,p.Code,"charge",Sign(p.Direction,false),ratio,ratio,null,null,null,null,null,null,assignments[clock],clocks[clock],Owned:instrument.Roles[slot].Owned));
  }
  return new(instrument,members.ToImmutable());
 }
 static TicketFieldException ChargeField(GasChargeRequest charge,string field,MarketDataException error)=>
  new(error.Code,error.Message,charge.Code,field);

 internal static (ChargeBasis Basis,ExactDecimal Nominal,ExactDecimal DailyQuantity,ExactDecimal Direction,DateOnly? From,DateOnly? To,DateTimeOffset? PaymentAt)
  ReadChargeTerms(GasChargeRequest charge)
 {
  ChargeBasis basis;
  try{basis=charge.RequireBasis();}
  catch(MarketDataException error)when(error.Code=="gas_composition_input"){throw ChargeField(charge,"charge_basis",error);}
  var capacity=basis==ChargeBasis.Capacity;
  ExactDecimal nominal;
  try
  {
   nominal=ChargeNominal(charge.Price);
   if(capacity&&nominal.CompareTo(default)<=0)throw Invalid("Enter a positive capacity rate.");
  }
  catch(MarketDataException error)when(error.Code=="gas_composition_input"){throw ChargeField(charge,"charge_price",error);}
  ExactDecimal direction;
  try{direction=Sign(charge.Direction,false)==1?Number("1"):Number("-1");}
  catch(MarketDataException error)when(error.Code=="gas_composition_input"){throw ChargeField(charge,"charge_direction",error);}
  if(!capacity)
  {
   try{return (basis,nominal,Number("1"),direction,null,null,TradingInstant.Parse(charge.PaymentAt,"gas_composition_input"));}
   catch(MarketDataException error)when(error.Code=="gas_composition_input"){throw ChargeField(charge,"charge_payment_at",error);}
  }
  ExactDecimal quantity;
  try
  {
   quantity=EntryNumber(charge.DailyQuantity!);
   if(quantity.CompareTo(default)<=0)throw Invalid("Enter a positive daily quantity.");
  }
  catch(MarketDataException error)when(error.Code=="gas_composition_input"){throw ChargeField(charge,"charge_daily_quantity",error);}
  if(!DateOnly.TryParseExact(charge.From,"yyyy-MM-dd",CultureInfo.InvariantCulture,DateTimeStyles.None,out var from))
   throw ChargeField(charge,"charge_start",Invalid("Enter the first gas day as YYYY-MM-DD."));
  if(!DateOnly.TryParseExact(charge.To,"yyyy-MM-dd",CultureInfo.InvariantCulture,DateTimeStyles.None,out var to)||to<=from)
   throw ChargeField(charge,"charge_end",Invalid("Enter an exclusive gas day as YYYY-MM-DD after the first day."));
  return (basis,nominal,quantity,direction,from,to,null);
 }

 internal static GasChargeComponent ResolveCharge(GasChargeRequest charge,QuoteSpecification quote,Quantity unit,GasDayClock clock,Func<SideCalendarPolicy> resolveCalendars)
 {
  var terms=ReadChargeTerms(charge);
  UnitPrice price;
  try{price=ChargePrice(terms.Nominal,quote);}
  catch(MarketDataException error)when(error.Code=="gas_composition_input"){throw ChargeField(charge,"charge_price",error);}
  SideCalendarPolicy calendars;
  try{calendars=resolveCalendars();}
  catch(MarketDataException error)when(error.Code is "side_calendar_policy" or "side_calendar_conflict" or "calendar_reference")
  {throw ChargeField(charge,"calendar_policy",error);}
  var lump=terms.Basis==ChargeBasis.LumpSum;
  FixedSide side;
  try
  {
   side=lump?GasChargeSides.LumpSum(Guid.NewGuid(),price,unit,clock,terms.PaymentAt!.Value,calendars,quote):
    GasChargeSides.Capacity(Guid.NewGuid(),price,unit,clock,terms.From!.Value,terms.To!.Value,charge.PaymentDay!.Value,calendars,quote);
   // Fixed payment rows validated their completion boundaries. Owned-day valuation
   // also needs each day's start, including the first and the days inside a row.
   for(var day=side.DeliveryFrom;day<side.DeliveryTo;day=day.AddDays(1))clock.Boundary(day);
  }
  catch(MarketDataException error)when(!lump&&error.Code=="payment_before_completion")
  {throw ChargeField(charge,"charge_payment_day",Invalid(error.Message));}
  catch(MarketDataException error)when(error.Code is "calendar_coverage" or "side_calendar_applicability")
  {throw ChargeField(charge,"calendar_policy",error);}
  catch(MarketDataException error)when(error.Code=="gas_day_boundary")
  {throw ChargeField(charge,"charge_clock_side",error);}
  // The entered terms already passed syntax, ordering, rate and payment-day checks.
  // These remaining domain refusals bound the date or dated period count.
  catch(MarketDataException error)when(error.Code is "gas_composition_input" or "swap_side_periods")
  {throw ChargeField(charge,lump?"charge_payment_at":"charge_end",error);}
  return new(charge.Code,side,terms.Basis,charge.ClockSide,terms.DailyQuantity,terms.Direction);
 }

 static ExactDecimal ChargeNominal(string? text)
 {
  if(text is null)throw Invalid("Supply a stated charge amount or rate.");
  var nominal=EntryNumber(text);var limit=Number("1000000000000");
  if(nominal.CompareTo(limit)>=0||nominal.CompareTo(-limit)<=0)throw Invalid("The absolute stated charge must be below 10^12.");
  return nominal;
 }
 // Contractual cash is stated in the trade currency per unit. Preserve that amount
 // when the captured quote unit carries a multiplier.
 internal static UnitPrice ChargePrice(string? text,QuoteSpecification quote)=>ChargePrice(ChargeNominal(text),quote);
 static UnitPrice ChargePrice(ExactDecimal nominal,QuoteSpecification quote)
 {
  try{return new(nominal.DivideExact(quote.Multiplier),PriceUnit.FromQuote(quote));}
  catch(MarketDataException error)when(error.Code is "precision_loss" or "numeric_range")
  {throw Invalid("The stated charge must be exactly representable in the selected quote unit.");}
 }
 static ExactDecimal StoredShare(string numerator,string denominator)
 {
  var share=new SignedRatio(BigInteger.Parse(numerator,CultureInfo.InvariantCulture),BigInteger.Parse(denominator,CultureInfo.InvariantCulture));
  if(!share.TryScaleToInteger(BigInteger.Pow(10,18),out var scaled))throw Invalid("The retained share must be an exact supported decimal.");
  var digits=scaled.ToString(CultureInfo.InvariantCulture).PadLeft(19,'0');
  return Number(digits[..^18]+"."+digits[^18..]);
 }
 internal static ExactDecimal PaymentShare(string? text)
 {
  if(text is null)throw Invalid("Supply an explicit positive payment share.");
  var share=EntryNumber(text);
  return share.CompareTo(default)>0?share:throw Invalid("Supply an explicit positive payment share.");
 }
 static ExactDecimal EntryNumber(string text)
 {
  try{return Number(text);}
  catch(MarketDataException e)when(e.Code is "numeric_format" or "numeric_range"){throw Invalid(e.Message);}
 }
 internal static QuantityPart? PaymentPart(GasQuantityPartRequest? part,ExactDecimal baseQuantity)
 {
  if(part is null)return null;
  SignedRatio Level(string text)
  {
   if(text is null)throw Invalid("Supply every required daily level.");
   var value=EntryNumber(text);
   if(value.CompareTo(default)<0)throw Invalid("Daily part levels must be nonnegative.");
   var level=ScheduleNumber.Parse(value.ToString());var basis=ScheduleNumber.Parse(baseQuantity.ToString());
   return new SignedRatio(level.Numerator*basis.Denominator,level.Denominator*basis.Numerator);
  }
  try{return part.Kind switch{
   "band"=>QuantityPart.Band(Level(part.Lower),part.Upper is null?null:Level(part.Upper)),
   "shortfall" when part.Upper is null=>QuantityPart.Shortfall(Level(part.Lower)),
   _=>throw Invalid("Select a band or a shortfall with its exact daily levels.")};}
  catch(MarketDataException e)when(e.Code=="quantity_part"){throw Invalid(e.Message);}
 }
 // recorded holds, per physical side code, the days whose operational quantity the view shows in place of the contract's (unsigned, as recorded).
 // A started composition is valued only with its lifecycle state: held, a booked trade's recorded cash; unrecorded, a draft loaded in its term.
 async Task<(string Unit,ImmutableArray<RetainedSideProgram> Programs,ScheduleContext Knowledge,MarketSnapshot Snapshot,ImmutableArray<IndexPriceLookup> Dated)> PrepareSchedules(
  NpgsqlConnection c,PreviewData data,InputFrame frame,ImmutableArray<GasCompositionRow> members,GasCompositionInstrument? draft,
  ImmutableArray<RetainedSideProgram>? retained,bool allowStarted,CustomCurves.SavedVersions? savedVersions,CancellationToken ct)
 {
  if(frame.ContextId is not {} contextId)throw Invalid("Use a certified calculation context.");
  var unit=QuantityUnit(data,members.Select(m=>m.Assignments[0].Quote.Quantity));
  var programs=retained??[..draft!.Sides.Select((m,n)=>{var d=ScheduleDefinitionCompiler.Generate(m.Side,ct,ScheduleLifecycleSelection.StartedPhysicalDelivery);var p=BookedSideProgram.Compile(d);return new RetainedSideProgram(n,m.Side.Id,Guid.Empty,new(BookedSideProgram.Digest(p.Write()),d.Manifest,d.Family,d.EconomicIdentity,d.PricingBindingIdentity),p);})];
  var knowledge=(await new KnowledgeResolver(database.Manifest.CatalogId).Read(c,contextId,ct)).Context;
  var snapshot=MarketSnapshotBinding.Bind(data.Snapshot(frame),contextId,SchedulePreview.ParseAt(knowledge.KnownAt));
  // Fresh booking still requires future physical delivery. An independent charge
  // has its own held lifecycle from booking, including an already owed lump sum.
  if(!allowStarted)PhysicalLifecycle.RequireUnstarted(programs.Where(p=>members[p.Slot].Owned is null).Select(p=>p.Program),snapshot.ValuationAt);
  var dated=programs.SelectMany(p=>BookedScheduleLookups.Dated(data,p.Program)).Distinct().ToImmutableArray();
  foreach(var group in dated.Where(lookup=>lookup.CustomProjection is not null).GroupBy(lookup=>lookup.CustomProjection!))
   await CustomCurves.RequireProjection(c,database,data,frame,CustomCurveProjectionInput.FromBinding(group.Key),PriceUnit.FromQuote(group.First().Quote),[..group.Select(lookup=>lookup.ForecastDelivery)],ct,savedVersions);
  return(unit,programs,knowledge,snapshot,dated);
 }
 async Task<GasCompositionValue> Value(NpgsqlConnection c,PreviewData data,InputFrame frame,ImmutableArray<GasCompositionRow> members,ExactDecimal quantity,GasCompositionInstrument? draft,Guid? setup,ImmutableArray<RetainedSideProgram>? retained,bool view,CancellationToken ct,
  IReadOnlyDictionary<string,ImmutableDictionary<DateOnly,(ExactQuantity Quantity,string Kind)>>? recorded=null,Action<SideViewInput,GasDayClock>? inspect=null,GasCompositionExecution? held=null,PhysicalCashState? unrecorded=null,
  CustomCurves.SavedVersions? savedVersions=null)
 {
  var (unit,programs,knowledge,snapshot,dated)=await PrepareSchedules(c,data,frame,members,draft,retained,held is {Started:true}||unrecorded is not null,savedVersions,ct);
  var rights=held?.Rights??draft!.ResolveRights();
  var swings=held?.SwingRights??draft!.ResolveSwingRights();
  var pricing=held?.SwingPricingBindings??draft!.SwingPricingBindings;
  var deps=PhysicalLifecycle.PriceDependencies(programs.Select(p=>p.Program),dated,snapshot);
  var prepared=await CustomCurves.PrepareMarket(c,database,data,frame,snapshot,knowledge,[],deps,CustomCurves.MarketPreparation.ValuationDependencies,ct,
   activePrices:PhysicalLifecycle.ActivePrices(programs.Select(p=>p.Program),dated),savedVersions:savedVersions);
  var market=prepared.Valuation.ProgramMarket(prepared.Bindings,prepared.Authority,ct);
  // Both composition profiles admit only an explicit no-discounting selection (Resolve, and the held terms below).
  const DiscountFactorSource discounting=DiscountFactorSource.Undiscounted;
  var discount=new ExplicitDiscounting(Number("1"),discounting);var views=view?ImmutableArray.CreateBuilder<SideViewInput>():null;var quantities=new Dictionary<string,ExactQuantity>(StringComparer.Ordinal);
  // Started delivery: the trade's value is unrealized plus realized unsettled, with its recorded cash.
  PhysicalLifecycleValue? lifecycle=null;var state=unrecorded;
  if(held is {Started:true})
  {
   if(held.Members.Length!=members.Length)throw Invalid("Started-delivery valuation requires every member of the held trade.");
   if(held.Terms.Discounting!=false)throw Invalid("A held gas composition requires its explicit no-discounting selection.");
   state=await GasCashPaymentStore.Read(c,database.Manifest.CatalogId,held,held.Terms.BookId,new(quantity,members[0].Assignment.Quote.Quantity),GasCashPaymentStore.LatestKnownRevision,snapshot.ValuationAt,ct);
  }
  else if(!rights.IsEmpty||!swings.IsEmpty||members.Any(m=>m.QuantityPolicy is not null||m.Part is not null||m.Balance is not null||m.Owned is not null))
  {
   var selected=GasDeliveryQuantityStore.Build(members,programs,quantity,snapshot.ValuationAt,[],rights,swings);
   state=new(unrecorded?.TradeId??Guid.NewGuid(),unrecorded?.BindingId??draft!.Id,new(quantity,members[0].Assignment.Quote.Quantity),null,[],PhysicalCashState.Policy,
    selected?.Days??default);
  }
  var instrument=GasCompositionExecution.BuildInstrument(held?.InstrumentId??draft!.Id,members,programs,dated,rights,swings,pricing);
  if(!rights.IsEmpty)instrument=await new CurveMarkStore(database.Manifest.CatalogId).Bind(c,instrument,snapshot,state,ct);
  var valuation=instrument.PV(instrument.PrepareValuation(market,market,discount,state,ct));
  var groups=valuation.Groups.Select(ExerciseGroupReport.FromValue).ToImmutableArray();
  var total=valuation.PresentValue.Scale(quantity);
  if(state is not null)lifecycle=PhysicalLifecycle.FromValuation(valuation);
  var valued=new List<(GasCompositionRow Member,ExactMoney Amount,ExactQuantity? Quantity,List<object> Rows)>();
  var imbalanceRows=ImmutableArray.CreateBuilder<GasImbalanceReportRow>();
  foreach(var member in members.OrderBy(x=>x.Slot))
  {
   var p=programs.Single(x=>x.Slot==member.Slot);
   var contribution=valuation.Sides[member.Slot];var sideValue=(RetainedProgramValuation)contribution.Value;
   var amount=contribution.Contribution.Scale(quantity);
   var sideView=sideValue.ViewInput(member.Code,quantity,new(member.Ratio.Numerator*member.Direction,member.Ratio.Denominator),TicketDescriptions.ViewColumns(GasTicket.SideKind(member.Role=="imbalance_cashout"?"indexed_consideration":member.Role)));
   if(member.Owned?.Basis==ChargeBasis.Capacity)sideView=sideView with{MonthlyScalarRows=true};
   // Contract-policy operational overlays remain display-only. Selected-policy
   // rows already carry their exact entry quantities from the Instrument.
   if(sideView.FollowedDays is null&&recorded?.GetValueOrDefault(member.Code) is {} recordedDays)
    sideView=sideView with{Recorded=recordedDays.ToImmutableDictionary(d=>d.Key,d=>new ScheduleViewQuantity(new(BigInteger.Parse(d.Value.Quantity.Numerator)*member.Direction,BigInteger.Parse(d.Value.Quantity.Denominator)),d.Value.Kind))};
   if(rights.FirstOrDefault(r=>r.Slots.Contains(member.Slot)) is {} right)
    sideView=sideView with{ExerciseGroups=[..groups.Where(g=>g.Right==right.Ordinal)]};
   views?.Add(sideView);inspect?.Invoke(sideView,member.Clock);
   if(view&&member.Balance is not null)
    imbalanceRows.AddRange(GasImbalanceReport.Rows(member,sideValue,quantity,state!,programs.Single(p=>p.Slot==member.Slot).Program,ct));
   var rows=new List<object>();ExactQuantity? qtotal=null;
   if(member.Role=="physical")
   {
    var meters=await gas.Meters(c,data,member.Assignment,setup,ct);
    // Contractual rows come from the retained quantities, so an ended gas day outside the measure keeps its row.
    foreach(var day in OpenEtrm.MarketData.Physical.PhysicalDeliverySchedule.ReadLiteralQuantities(p.Program,ExactRatio.One))
    {var selected=sideView.FollowedDays?.GetValueOrDefault(day.OperatingDate);
     var q=selected is null?day.Quantity.Scale(quantity).Scale(member.Ratio):ExactQuantity.FromRatio(selected.Taken,day.Quantity.Specification);qtotal=qtotal is null?q:qtotal.Add(q);
     rows.Add(new{day=day.OperatingDate.ToString("yyyy-MM-dd",CultureInfo.InvariantCulture),day.Start,day.End,quantity=Ratio(q),quantitySource=selected?.Source??"contractual",meter=PhysicalGasStore.Meter(data,meters,day.OperatingDate,setup)});}
    quantities.Add(member.Code,qtotal!);
   }
   else
   {
    var q=ScheduleViews.TotalQuantity(sideView with{Recorded=null},member.Clock.Boundary);
    qtotal=sideView.ReportedRows is {} reported&&!reported.Values.Any(row=>row.ReportedQuantity?.Quantity is not null)?null:
     ExactQuantity.FromRatio(new(BigInteger.Abs(q.Numerator),q.Denominator),member.Assignment.Quote.Quantity);
   }
   valued.Add((member,amount,qtotal!,rows));
  }
  int Unassessed(int slot)=>lifecycle?.Rows.Count(row=>row.Slot==slot&&row.ReportedQuantity?.State==ReportedDayQuantity.Unassessed)??0;
  var sides=valued.Select(v=>(object)new{v.Member.Code,v.Member.Role,source=v.Member.Source,stage=v.Member.Stage,pipeline=v.Member.Role=="physical"?v.Member.Assignment.Location.Pipeline.Code:null,zone=v.Member.Role=="physical"?v.Member.Assignment.Location.Zone.Code:null,deliveryPoint=v.Member.Role=="physical"?v.Member.Assignment.Point.Code:null,
   direction=v.Member.Direction==1?"receive":v.Member.Role=="physical"?"deliver":"pay",
   presentValue=v.Amount.ToExactString(),
   quantity=v.Quantity is null?null:Ratio(v.Quantity),unassessedDays=Unassessed(v.Member.Slot),incomplete=Unassessed(v.Member.Slot)!=0,deliveryRows=v.Rows}).ToList();
  var links=!view?Array.Empty<object>():members.Where(m=>m.Role=="physical"&&m.Source is not null).Select(m=>{var receipt=quantities[m.Source!];var delivery=quantities[m.Code];var fuel=receipt.Add(delivery.Scale(Number("-1")));return(object)new{source=m.Source,target=m.Code,m.Formula,m.Fraction,receipt=receipt.ToExactString(),delivery=delivery.ToExactString(),fuel=fuel.ToExactString()};}).ToArray();
  // The schedule is a display projection: a trade it cannot show keeps its valuation and reports why.
  ScheduleView? schedule=null;GasScheduleRefusal? refusal=null;
  if(views is not null)
   try
   {
    var clock=members.Where(m=>m.Role=="physical").Select(m=>m.Clock).Distinct().ToArray();
    if(clock.Length!=1)throw new MarketDataException("schedule_view","A gas schedule view requires every physical side to share one gas-day clock.");
    schedule=ScheduleViews.Build(views.ToImmutable(),GasTicket.Description.Levels,clock[0].Boundary,
     independentIntervals:members.Any(m=>m.Owned is not null));
   }
   catch(MarketDataException error)when(error.Code is "schedule_view" or "gas_day_boundary"){refusal=new(error.Code,error.Message);}
  // No contract-against-market measure is defined for these sides yet, so none is reported.
  var summaries=schedule is null?null:views!.Select(v=>(object)new{side=v.Code,billedQuantity=schedule.Totals[v.Code+".quantity"].Value,amount=schedule.Totals[v.Code+".amount"].Value,againstMarket=(string?)null}).ToArray();
  return new(total!.ToExactString(),members[0].Assignment.Quote.Currency.Code,unit,sides,links,schedule,summaries,refusal,members.ToImmutableDictionary(m=>m.Code,m=>m.Slot),
   lifecycle is null?null:GasLifecycleMeasures.From(lifecycle,discounting)){Groups=groups,ScheduleEntries=GasTicket.ScheduleEntries(members),ImbalanceRows=imbalanceRows.ToImmutable(),Lifecycle=lifecycle,EntryValues=[..valued.Select(v=>new InstrumentEntryValue(v.Member.Slot,v.Amount){UnassessedDays=Unassessed(v.Member.Slot)})],Source=new(data.DefinitionRevision.RevisionId,data.ReferenceRevision.CatalogId,data.ReferenceRevision.RevisionId)};
 }
 // A preview and a booking are held to the gas setup their context resolves (GasTicket.RequireSetup).
 async Task RequireSetup(NpgsqlConnection c,PreviewData data,GasCompositionRequest body,CancellationToken ct)=>GasTicket.RequireSetup(body.GasSetupId,(await gas.CurrentSetup(c,data,ct)).Id);
 public Task<object> Preview(NpgsqlConnection c,PreviewData data,InputFrame frame,GasCompositionRequest body,CancellationToken ct)=>
  Preview(c,data,frame,body,ct,ticket:false);

 internal Task<object> TicketPreview(NpgsqlConnection c,PreviewData data,InputFrame frame,GasCompositionRequest body,CancellationToken ct)=>
  Preview(c,data,frame,body,ct,ticket:true);

 async Task<object> Preview(NpgsqlConnection c,PreviewData data,InputFrame frame,GasCompositionRequest body,CancellationToken ct,bool ticket)
 {
  if(body.PreviewTradeId is {} trade)
  {
   if(body.TradeId is not null || body.ExpectedRevision is not null || body.BindingId is not null || body.Reason is not null || body.AmendmentPolicy is not null)
    throw Invalid("A held preview selects its saved composition without amendment terms.");
   var execution=await Load(c,data,frame,trade,ct);
   static GasCompositionRequest Terms(GasCompositionRequest request)=>request with{SnapshotId=Guid.Empty,OperationId=Guid.Empty,Convention="",KnowledgeMode="",
    TradeId=null,ExpectedRevision=null,BindingId=null,Reason=null,AmendmentPolicy=null,PreviewTradeId=null,PreviewContextId=null};
   if(JsonSerializer.Serialize(Terms(body),Json)!=JsonSerializer.Serialize(Terms(execution.Terms),Json))
    throw Invalid("A held preview requires the exact saved terms; operational records cannot price a changed draft.");
   return await Value(c,data,frame,execution,ct);
  }
  if(body.PreviewContextId is not null)throw Invalid("A preview cutoff requires an explicit saved trade.");
  if(body.TradeId is not null)await GasAmendments.Require(c,database.Manifest.CatalogId,body,frame.At,ct);
  await RequireSetup(c,data,body,ct);var d=await Resolve(c,data,body,frame.At,ct);
  try{return await Value(c,data,frame,d.Members,d.Instrument.BaseQuantity,d.Instrument,body.GasSetupId,null,true,ct);}
  catch(MarketDataException error)when(ticket&&(!d.Instrument.Rights.IsEmpty||!d.Instrument.SwingRights.IsEmpty)&&error.Code=="exercise_marks")
  {
   // The valuation reaches marks after it builds the retained Instrument. The
   // ticket can review those structural terms with the valuation explicitly unavailable.
   return new{source=new CatalogueSource(data.DefinitionRevision.RevisionId,data.ReferenceRevision.CatalogId,data.ReferenceRevision.RevisionId),
    quantityUnit=QuantityUnit(data,d.Members.Select(m=>m.Assignment.Quote.Quantity)),currency=d.Members[0].Assignment.Quote.Currency.Code,
    presentValue=(string?)null,schedule=(ScheduleView?)null,summaries=(object[]?)null,groups=(ExerciseGroupReport[]?)null,links=Array.Empty<object>(),
    scheduleRefusal=new GasScheduleRefusal(error.Code,error.Message)};
  }
 }
 // The single market price a physical side's schedule shows over its delivery term, evaluated from a price probe (GasTicket.PriceProbe).
 internal async Task<string> MarketPrice(NpgsqlConnection c,PreviewData data,InputFrame frame,GasCompositionRequest probe,CancellationToken ct)
 {
  GasCompositionValue value;
  try{var d=await Resolve(c,data,probe,frame.At,ct);value=await Value(c,data,frame,d.Members,d.Instrument.BaseQuantity,d.Instrument,probe.GasSetupId,null,true,ct);}
  catch(MarketDataException error)when(error.Code is "gas_input" or "gas_composition_input"){throw new MarketDataException("ticket_lookup",error.Message);}
  var code=probe.PhysicalSides.Single().Code;
  if(value.Schedule is null)throw new MarketDataException("ticket_lookup",$"The side's schedule cannot be shown, so its market price is not read: {value.ScheduleRefusal!.Message}");
  return value.Schedule.Totals[$"{code}.market_price"].Value??throw new MarketDataException("ticket_lookup","The side's delivery term has no single market price.");
 }
 // An in-term load (inTerm) values its draft with no recorded cash: there is no trade yet, so the operation that books it keys that state.
 internal Task<RetainedScheduleTerms> Prepare(NpgsqlConnection c,InputFrame frame,GasCompositionRequest body,bool inTerm,CancellationToken ct)=>TradingBookingRead.Run(database,c,frame,async(data,pinned)=>{if(body.TradeId is not null)await GasAmendments.Require(c,database.Manifest.CatalogId,body,pinned.At,ct);await RequireSetup(c,data,body,ct);var d=await Resolve(c,data,body,pinned.At,ct);
  if(d.Instrument.Rights.IsEmpty&&d.Instrument.SwingRights.IsEmpty)
  {
   var unrecorded=inTerm?new PhysicalCashState(body.OperationId,d.Instrument.Id,new(d.Instrument.BaseQuantity,d.Members[0].Assignment.Quote.Quantity),null,[],PhysicalCashState.Policy):null;
   _=await Value(c,data,pinned,d.Members,d.Instrument.BaseQuantity,d.Instrument,body.GasSetupId,null,false,ct,unrecorded:unrecorded);
  }
  else
  {
   var schedules=await PrepareSchedules(c,data,pinned,d.Members,d.Instrument,null,inTerm,null,ct);
   _=GasCompositionExecution.BuildInstrument(d.Instrument.Id,d.Members,schedules.Programs,schedules.Dated,d.Instrument.ResolveRights(),d.Instrument.ResolveSwingRights(),d.Instrument.SwingPricingBindings);
  }
  return new RetainedScheduleTerms(data.DefinitionRevision,data.ReferenceRevision,d.Instrument);},ct,physicalMarket:PhysicalMarketSelection.Gas,physicalTrade:body.TradeId);
 // A new trade names no amended trade; the ticket and the administrator in-term load both book new trades only.
 internal static void RequireNewTrade(GasCompositionRequest body)
 {if(body.TradeId is not null||body.ExpectedRevision is not null||body.BindingId is not null||body.Reason is not null||body.AmendmentPolicy is not null||body.PreviewTradeId is not null||body.PreviewContextId is not null)throw Invalid("Use the explicit gas amendment operation to amend a trade.");}
 // Books, amends or (inTerm) loads one composition as the profile's identity: staging, locking and publication are
 // idempotent by operation id (D238), so a retried operation receives its first outcome.
 internal async Task<JsonElement> Book(string profile,InputFrame frame,GasCompositionRequest body,bool inTerm,CancellationToken ct)
 {
  if(body.PreviewTradeId is not null||body.PreviewContextId is not null)throw Invalid("A held preview is read-only and cannot book or amend a trade.");
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
 internal async Task<JsonElement> Save(NpgsqlConnection c,InputFrame frame,GasCompositionRequest body,bool inTerm,CancellationToken ct)
 {var operation=inTerm?"load_gas_composition_in_term":body.TradeId is null?"book_gas_composition":"amend_gas_composition";await using var q=ScheduleStore.Command(c,$"SELECT security.{operation}($1,$2::jsonb,$3)::text",database.Manifest.CatalogId,JsonSerializer.Serialize(body,Json),frame.At);return JsonSerializer.Deserialize<JsonElement>((string)(await q.ExecuteScalarAsync(ct))!);}
 public async Task<object> Editor(NpgsqlConnection c,PreviewData data,InputFrame frame,Guid trade,CancellationToken ct)
 {
  var execution=await Load(c,data,frame,trade,ct);
  return new GasCompositionRead(execution.Terms,await Value(c,data,frame,execution,ct));
 }
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
   await Value(c,data,frame,execution.Members,execution.Quantity,null,execution.Terms.GasSetupId,execution.Programs,false,ct,
    inspect:(side,clock)=>{if(side.Code==member.Code)result=SideScheduleSnapshot.FromProgram(program.SideId,frame.Id,side,clock.Boundary);},held:execution);
  }
  catch(MarketDataException error)when(ValuationStatus.PositionSpecific(error)||error.Code is "gas_delivery_lifecycle" or "side_payment_due")
  {
   var knowledge=(await new KnowledgeResolver(database.Manifest.CatalogId).Read(c,frame.ContextId!.Value,ct)).Context;
   result=SideScheduleSnapshot.Plan(program.SideId,frame.Id,frame.ContextId!.Value,member.Code,program.Program,member.Clock.Boundary,frame.At,SchedulePreview.ParseAt(knowledge.KnownAt),error.Code);
  }
  return result??throw new MarketDataException("side_schedule_inspection","The Side produced no evaluated schedule.");
 }
 // A single-point trade read as the composition it is (GasTicket.SinglePoint): every side a whole membership of the booked quantity on the
 // one physical side's path, valued from the trade's own retained programs, whose slots are the composition's: gas, payment, then fee.
 internal async Task<GasCompositionExecution> LoadSinglePoint(NpgsqlConnection c,PreviewData data,InputFrame frame,Guid trade,CancellationToken ct)
 {
  var(terms,instrument)=await gas.Retained(c,frame,trade,ct);var body=GasTicket.SinglePoint(terms);
  var m=await gas.Metadata(c,data,terms,ct);var whole=new ExactRatio(1,1);var physical=body.PhysicalSides.Single();
  ImmutableArray<GasCompositionRow> members=[new(0,physical.Code,"physical",Sign(physical.Direction,true),whole,whole,null,null,null,null,null,null,m.Assignments,m.Clock),
   ..body.CashSides.Select((s,n)=>new GasCompositionRow(n+1,s.Code,s.Role,Sign(s.Direction,false),whole,whole,s.Source,s.Stage,null,null,null,null,m.Assignments,m.Clock))];
  var programs=await new RetainedScheduleStore(database).LoadPrograms(c,instrument,frame.ContextId,frame.ContextId is null?null:frame.At,ct);
  return new(trade,body,instrument,members,Number(physical.Quantity!),programs);
 }
 // The valuation of a loaded trade, as its reads value it. A read valuing several trades shares its saved custom curve versions.
 internal Task<GasCompositionValue> Value(NpgsqlConnection c,PreviewData data,InputFrame frame,GasCompositionExecution execution,CancellationToken ct,
  IReadOnlyDictionary<string,ImmutableDictionary<DateOnly,(ExactQuantity Quantity,string Kind)>>? recorded=null,CustomCurves.SavedVersions? savedVersions=null)=>
  Value(c,data,frame,execution.Members,execution.Quantity,null,execution.Terms.GasSetupId,execution.Programs,true,ct,recorded,held:execution,savedVersions:savedVersions);
 // programs, when the caller already loaded the trade's retained programs under this frame, are used in place of loading them again.
 internal async Task<GasCompositionExecution> Load(NpgsqlConnection c,PreviewData data,InputFrame frame,Guid trade,CancellationToken ct,
  ImmutableArray<RetainedSideProgram>? programs=null)
 {
  var selected=(await GasPositions.Read(c,database.Manifest,[],[],ct,trade:trade)).SingleOrDefault()??throw Invalid("The selected gas position is unavailable.");
  var header=(await PreviewDatabase.Rows(c,"SELECT row_to_json(x)::text FROM(SELECT r.*,i.definition_revision_id,i.gas_setup_id,trading.gas_exercise_terms(r.catalog_id,r.instrument_id) exercise_rights,trading.gas_swing_terms(r.catalog_id,r.instrument_id) swing_terms,CASE WHEN r.revision=1 AND t.operation_id=r.operation_id THEN t.request ELSE a.request END request FROM trading.gas_trade_revisions r JOIN trading.gas_instruments i USING(catalog_id,instrument_id) JOIN trading.trades t ON(t.catalog_id,t.id)=(r.catalog_id,r.trade_id) LEFT JOIN trading.gas_amendment_commands a ON(a.catalog_id,a.operation_id,a.trade_id,a.result_revision,a.changed)=(r.catalog_id,r.operation_id,r.trade_id,r.revision,true) WHERE(r.catalog_id,r.trade_id,r.instrument_id,r.revision)=($1,$2,$3,$4) AND i.composition)x",[database.Manifest.CatalogId,trade,selected.Binding,int.Parse(selected.Head.Text("revision"),CultureInfo.InvariantCulture)],ct)).SingleOrDefault();
  if(header.ValueKind!=JsonValueKind.Object)throw Invalid("The composed gas trade is unavailable.");
  var rows=await PreviewDatabase.Rows(c,"SELECT row_to_json(x)::text FROM trading.gas_component_terms x WHERE(catalog_id,instrument_id)=($1,$2) ORDER BY slot",[database.Manifest.CatalogId,header.Id("instrument_id")],ct);
  programs??=await new RetainedScheduleStore(database).LoadPrograms(c,header.Id("instrument_id"),frame.ContextId,frame.ContextId is null?null:frame.At,ct);
  GasCompositionExecution.RequireSlots(rows.Select(r=>int.Parse(r.Text("slot"),CultureInfo.InvariantCulture)),programs.Value);
  // The original request is returned for inspection, not used to generate a Side
  // or executable program. Typed terms resolve exact market metadata only.
  if(header.GetProperty("request").ValueKind!=JsonValueKind.Object)throw Invalid("The selected gas revision requires its exact retained command.");
  var body=JsonSerializer.Deserialize<GasCompositionRequest>(header.GetProperty("request").GetRawText(),Json)!;
  var (swings,pricing)=GasSwingBooking.Read(header.GetProperty("swing_terms"),body.SwingRights?.Length??0);
  var valuationBody=body with{DefinitionRevisionId=header.Id("definition_revision_id"),GasSetupId=header.OptionalText("gas_setup_id") is {} setup?Guid.Parse(setup):null,SnapshotId=frame.Id};
  var members=ImmutableArray.CreateBuilder<GasCompositionRow>();var phys=new Dictionary<int,(GasPhysicalRequest Physical,ImmutableArray<GasValuationAssignment> Assignments,GasDayClock Clock)>();
  foreach(var r in rows.Where(r=>r.Text("role")=="physical"))
  {
   var p=new GasPhysicalRequest(r.Text("code"),r.Text("pipeline_code"),r.Text("zone_code"),r.Text("point_code"),r.Text("product_code"),r.Text("direction")=="1"?"receive":"deliver",r.Text("delivery_from"),r.Text("delivery_to"),r.Text("quantity_basis"),r.OptionalText("independent_quantity"),null);
   var m=await gas.Metadata(c,data,Selection(valuationBody,p),ct);phys.Add(int.Parse(r.Text("slot")),(p,m.Assignments,m.Clock));
  }
  foreach(var r in rows)
  {
   var slot=int.Parse(r.Text("slot"));var source=r.OptionalText("source_slot") is {} s?int.Parse(s):(int?)null;var role=r.Text("role");
   var a=phys[role=="physical"?slot:role=="charge"?int.Parse(r.Text("clock_slot")):source!.Value];
   OwnedQuantity? owned=role=="charge"?new(r.Text("charge_basis") switch{"lump_sum"=>ChargeBasis.LumpSum,"capacity"=>ChargeBasis.Capacity,_=>throw Invalid("Unknown retained charge basis.")},
    a.Clock,new(BigInteger.Parse(r.Text("charge_quantity_n")),BigInteger.Parse(r.Text("charge_quantity_d")))):null;
   members.Add(new(slot,r.Text("code"),r.Text("role"),int.Parse(r.Text("direction")),new(BigInteger.Parse(r.Text("ratio_n")),BigInteger.Parse(r.Text("ratio_d"))),new(BigInteger.Parse(r.Text("receipt_n")),BigInteger.Parse(r.Text("receipt_d"))),source is {} si?rows.Single(x=>x.Text("slot")==si.ToString(CultureInfo.InvariantCulture)).Text("code"):null,r.OptionalText("stage"),r.OptionalText("formula"),r.OptionalText("fraction"),null,null,a.Assignments,a.Clock,r.OptionalText("quantity_policy"),r.OptionalText("share_n") is {} sn?StoredShare(sn,r.Text("share_d")):null,
    r.OptionalText("part_kind") is {} kind?new QuantityPart(kind=="band"?QuantityPartKind.Band:QuantityPartKind.Shortfall,new(BigInteger.Parse(r.Text("part_lower_n")),BigInteger.Parse(r.Text("part_lower_d"))),r.OptionalText("part_upper_n") is {} un?new SignedRatio(BigInteger.Parse(un),BigInteger.Parse(r.Text("part_upper_d"))):null):null,owned,role=="imbalance_cashout"?GasCashoutTerms.Terms(r.Text("cashout_profile"),r.Text("balance_side")=="owed_to_us",r.Text("statement_basis"),r.Text("account_netting"),r.Text("statement_policy")):null));
  }
  var rights=header.GetProperty("exercise_rights").EnumerateArray().Select(r=>new ExerciseRight(r.GetProperty("ordinal").GetInt32(),
   [..r.GetProperty("slots").EnumerateArray().Select(v=>v.GetInt32())],Period(r.Text("period")),Holder(r.Text("holder")),Settlement(r.Text("settlement")),
   r.Text("model"),r.Text("day_count"),[..r.GetProperty("groups").EnumerateArray().Select(g=>new ExerciseGroup(
    DateWorkspace.Date(g.Text("period_from")),DateWorkspace.Date(g.Text("period_to")),SchedulePreview.ParseAt(g.Text("exercise_at"))))])).ToImmutableArray();
  return new(trade,body,header.Id("instrument_id"),members.ToImmutable(),Number(header.Text("quantity")),programs.Value,rights,swings,pricing);
 }
}
