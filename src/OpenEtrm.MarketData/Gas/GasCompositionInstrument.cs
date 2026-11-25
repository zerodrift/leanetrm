using System.Collections.Immutable;
using OpenEtrm.MarketData.Options;
using OpenEtrm.MarketData.Risk;
using OpenEtrm.MarketData.Trading;
using OpenEtrm.Shared.Numerics;

namespace OpenEtrm.MarketData.Gas;

public enum GasQuantityStage { Receipt=1,Delivery,Fuel }
[EconomicDefinition("gas_composition_link_v1")]
public sealed record GasCompositionLink([property:IdentityField]string Source,[property:IdentityField]GasFuelConvention Formula,[property:IdentityField]ExactDecimal Fraction);
public sealed record GasPhysicalComponent(string Code,PhysicalGasSide Side,ExactDecimal Direction,ExactDecimal? Quantity,GasCompositionLink? Link,string? QuantityPolicy=null);
public sealed record GasCashComponent(string Code,Side Side,string Role,string Source,GasQuantityStage Stage,ExactDecimal Direction,ExactDecimal? Share=null,QuantityPart? Part=null,ReportedBalanceFollow? Balance=null);
public sealed record GasChargeComponent(string Code,FixedSide Side,ChargeBasis Basis,string ClockSide,ExactDecimal DailyQuantity,ExactDecimal Direction);
[EconomicDefinition("gas_composition_role_v1")]
public sealed record GasCompositionRole([property:IdentityField]string Code,[property:IdentityField]string Role,[property:IdentityField]string? Source,[property:IdentityField]GasQuantityStage? Stage,[property:IdentityField]GasCompositionLink? Link,
 [property:IdentityField]ExactDecimal? Share=null,[property:IdentityField]QuantityPart? Part=null,[property:IdentityField]string? QuantityPolicy=null,
 [property:IdentityField]OwnedQuantity? Owned=null,[property:IdentityField]string? ClockSide=null,[property:IdentityField]ReportedBalanceFollow? Balance=null);

public sealed class GasExerciseTermException(int ordinal,string message,bool leadHours=false):MarketDataException("gas_composition_input",message)
{
 public int Ordinal{get;}=ordinal;
 public bool LeadHours{get;}=leadHours;
}

[EconomicDefinition("gas_exercise_right_v1")]
public sealed record GasExerciseRight(
 [property:IdentityField]ImmutableArray<string> Entries,
 [property:IdentityField]ExercisePeriod Period,
 [property:IdentityField]ExerciseHolder Holder,
 [property:IdentityField]ExerciseSettlement Settlement,
 [property:IdentityField]int? LeadHours)
{
 public static ImmutableArray<int> ClaimEntries(int ordinal,ImmutableArray<string> entries,bool physical,
  IReadOnlyDictionary<string,int> slotsByCode,int physicalCount,ISet<int> claimed)
 {
  if(entries.IsDefaultOrEmpty)throw new GasExerciseTermException(ordinal,"Select the sides covered by this exercise right.");
  var slots=ImmutableArray.CreateBuilder<int>(entries.Length);
  foreach(var code in entries)
  {
   if(code is null||!slotsByCode.TryGetValue(code,out var slot)||!claimed.Add(slot))
    throw new GasExerciseTermException(ordinal,"Every exercise right must name existing entries claimed exactly once.");
   slots.Add(slot);
  }
  if(physical!=slots.Any(slot=>slot<physicalCount))
   throw new GasExerciseTermException(ordinal,"Physical exercise requires a physical entry and cash automatic exercise requires payment entries.");
  return slots.MoveToImmutable();
 }
}

// Contractual physical and payment sides. Exact native-unit membership ratios retain
// quantity normalization separately from direction; no operational selection or
// transport entitlement is inferred from a quantity link.
[EconomicDefinition("gas_composition_instrument_v1")]
public sealed record GasCompositionInstrument:SidedInstrument
{
 // The fixed profile's side bounds. Cash sides include fees.
 public const int MinPhysicalSides=1,MaxPhysicalSides=4,MaxCashSides=7,MaxSides=8;
 [IdentityField]public ImmutableArray<GasCompositionRole> Roles { get; }
 [IdentityField]public ImmutableArray<GasExerciseRight> Rights { get; }
 [IdentityField]public ImmutableArray<SwingRight> SwingRights { get; }
 public ImmutableArray<SwingPricingBinding> SwingPricingBindings { get; }
 private readonly ImmutableArray<ExerciseRight> resolvedRights;
 public ImmutableArray<GasPhysicalComponent> Physical { get; }
 public ImmutableArray<GasCashComponent> Cash { get; }
 public ImmutableArray<GasChargeComponent> Charges { get; }
 public ImmutableArray<ExactRatio> ReceiptRatios { get; }
 public ExactDecimal BaseQuantity { get; }
 public override ReferenceKey Currency=>Physical[0].Side.Currency;
 public override Quantity UnitQuantity=>Physical[0].Side.UnitQuantity;
 public override DefinitionIdentity PricingBindingIdentity=>DefinitionIdentity.For(new GasInstrumentPricingBindings([..Sides.Select(m=>m.Side.PricingBindingIdentity?.Canonical)],SwingPricingBindings.IsEmpty?null:SwingPricingBindings[0]));
 internal override bool SupportsSettlement=>false;
 public GasCompositionInstrument(Guid id,ImmutableArray<GasPhysicalComponent> physical,ImmutableArray<GasCashComponent> cash,
  ImmutableArray<GasExerciseRight> rights=default,ImmutableArray<GasChargeComponent> charges=default,
  ImmutableArray<GasSwingComponent> swings=default,ImmutableArray<SwingPricingBinding> swingPricingBindings=default)
  :this(id,physical,cash,rights,charges,swings,swingPricingBindings,Resolve(physical,cash,charges.IsDefault?[]:charges,!rights.IsDefaultOrEmpty||!swings.IsDefaultOrEmpty)) { }
 private GasCompositionInstrument(Guid id,ImmutableArray<GasPhysicalComponent> physical,ImmutableArray<GasCashComponent> cash,
  ImmutableArray<GasExerciseRight> rights,ImmutableArray<GasChargeComponent> charges,ImmutableArray<GasSwingComponent> swings,ImmutableArray<SwingPricingBinding> swingPricingBindings,Composition c):base(id,c.Members)
 {
  Physical=physical;Cash=cash;Charges=charges.IsDefault?[]:charges;BaseQuantity=c.Base;ReceiptRatios=c.Receipts;
  var entryTerms=physical.Any(p=>p.QuantityPolicy is not null)||cash.Any(p=>p.Part is not null);
  Roles=[..physical.Select(p=>new GasCompositionRole(p.Code,"physical",p.Link?.Source,null,p.Link,QuantityPolicy:p.QuantityPolicy)),
   ..cash.Select(p=>new GasCompositionRole(p.Code,p.Role,p.Source,p.Stage,null,entryTerms||p.Share!=ExactDecimal.Parse("1")?p.Share:null,p.Part,Balance:p.Balance)),
   ..c.ChargeRoles];
  Rights=rights.IsDefault?[]:rights;
  resolvedRights=ResolveBookingRights();
  SwingPricingBindings=swingPricingBindings.IsDefault?[]:swingPricingBindings;
  if(!swings.IsDefaultOrEmpty && (swings.Length!=1||swings[0] is null))
   throw SwingFeasibility.InvalidRight("Select at most one physical swing right.");
  if(SwingPricingBindings.Length!=(swings.IsDefaultOrEmpty?0:1))
   throw new MarketDataException("swing_model_scope","Select one explicit pricing binding per swing right.");
  if(!SwingPricingBindings.IsEmpty)GasSwingAdmission.RequirePricing(SwingPricingBindings[0]);
  SwingRights=swings.IsDefaultOrEmpty?[]:[swings[0].Resolve(this)];
 }
 public ImmutableArray<SwingRight> ResolveSwingRights()=>SwingRights;
 public ImmutableArray<ExerciseRight> ResolveRights()=>resolvedRights;
 public override UnitValuation PV(UnitValuationContext context)
 {
  RequireNoExerciseRights();
  return base.PV(context);
 }
 protected internal override void RequireBeforeValuation(SideValuationContext context)
 {
  RequireNoExerciseRights();
  if(Physical.Any(p=>p.QuantityPolicy is not null)||Cash.Any(p=>p.Part is not null||p.Balance is not null))
   throw Invalid("Value quantity policies, parts and reported balances through the saved Instrument with its held facts.");
 }
 private void RequireNoExerciseRights()
 {
  if(!Rights.IsEmpty||!SwingRights.IsEmpty)throw Invalid("Value exercise rights through the saved Instrument with its held exercise terms.");
 }

 private ImmutableArray<ExerciseRight> ResolveBookingRights()
 {
  if(Rights.IsEmpty)return [];
  if(Physical.Any(p=>p.Link is not null))throw Invalid("Exercise rights require independent physical sources without quantity links.");
  var slotsByCode=Roles.Select((role,slot)=>(role.Code,slot)).ToDictionary(e=>e.Code,e=>e.slot,StringComparer.Ordinal);
  var claimed=new HashSet<int>();
  var saved=ImmutableArray.CreateBuilder<ExerciseRight>(Rights.Length);
  foreach(var right in Rights)
  {
   if(right is null||right.Entries.IsDefaultOrEmpty||!Enum.IsDefined(right.Period)||!Enum.IsDefined(right.Holder)||!Enum.IsDefined(right.Settlement))
    throw Invalid("Each exercise right needs explicit entries, period, holder and settlement.");
   var physical=right.Settlement==ExerciseSettlement.Physical;
   if(physical?right.LeadHours is null or <0:right.LeadHours is not null)
    throw Invalid("Physical exercise needs a nonnegative lead in hours and cash automatic exercise needs fixing times.");

   var ordinal=saved.Count;
   var slots=GasExerciseRight.ClaimEntries(ordinal,right.Entries,physical,slotsByCode,Physical.Length,claimed);
   if(slots.Any(slot=>Roles[slot].Owned is not null||Roles[slot].Balance is not null))
    throw new GasExerciseTermException(ordinal,"An exercise right cannot cover an independently owned charge or a reported-balance cash-out.");
   try{saved.Add(ResolveGroups(ordinal,right,slots));}
   catch(MarketDataException error)when(error.Code=="gas_composition_input"&&error is not GasExerciseTermException)
   {throw new GasExerciseTermException(ordinal,error.Message);}
  }
  return saved.MoveToImmutable();
 }

 private ExerciseRight ResolveGroups(int ordinal,GasExerciseRight right,ImmutableArray<int> slots)
 {
   var entries=slots.Select(slot=>ExerciseEntry.Read(Sides[slot].Side)).ToArray();
   var (from,to)=entries[0].DeliveryRange;
   if(entries.Any(entry=>entry.DeliveryRange!=(from,to)))
    throw Invalid("Every entry selected by one exercise right must cover the same delivery days.");
   var groups=ImmutableArray.CreateBuilder<ExerciseGroup>();
   for(var day=from;day<to;)
   {
    var end=right.Period==ExercisePeriod.DeliveryDay?day.AddDays(1):new DateOnly(day.Year,day.Month,1).AddMonths(1);
    if(end>to)end=to;
    var at=right.LeadHours is {} lead?PhysicalExerciseAt(entries,day,lead,ordinal):CashExerciseAt(entries,from,day,end);
    groups.Add(new(day,end,at));
    day=end;
   }
   return new(ordinal,slots,right.Period,right.Holder,right.Settlement,
    KirkSpreadCall.Implementation,OptionNumericalPolicies.ActualOver365FixedInstant,groups.ToImmutable());
 }

 private sealed record ExerciseEntry(Side Side,SideSchedule Schedule)
 {
  internal static ExerciseEntry Read(Side side)
  {
   try { return new(side,side.GenerateSchedule()); }
   catch(MarketDataException error) when(error.Code=="gas_day_boundary") { throw MissingExerciseDayStart(); }
  }

  // Composition admission already proves each member covers a contiguous range of gas days.
  internal (DateOnly From,DateOnly To) DeliveryRange=>Schedule is PhysicalGasSchedule gas
   ?(gas.Quantities.Terms.From,gas.Quantities.Terms.To)
   :(Schedule.Rows[0].DeliveryFrom,Schedule.Rows[^1].DeliveryTo);
  internal DateTimeOffset Start(DateOnly day)=>Schedule is PhysicalGasSchedule gas
   ?gas.Quantities.Rows[day.DayNumber-gas.Quantities.Terms.From.DayNumber].Start
   :(Side switch{FixedSide fixedSide=>fixedSide.GasDeliveryClock,FloatingSide floating=>floating.GasDeliveryClock,_=>null})?.Boundary(day)
    ??throw MissingExerciseDayStart();
 }

 private static DateTimeOffset PhysicalExerciseAt(ExerciseEntry[] entries,DateOnly firstDay,int lead,int ordinal)
 {
  // A lead applies before every selected member's first day in the group.
  try { return entries.Min(entry=>entry.Start(firstDay)).AddHours(-lead); }
  catch(MarketDataException error) when(error.Code=="gas_day_boundary")
  { throw MissingExerciseDayStart(); }
  catch(ArgumentOutOfRangeException)
  { throw new GasExerciseTermException(ordinal,"The lead must produce a supported physical exercise instant.",leadHours:true); }
 }

 private static MarketDataException MissingExerciseDayStart()=>Invalid("Physical exercise requires each selected entry's exact retained gas-day start.");

 private static DateTimeOffset CashExerciseAt(ExerciseEntry[] entries,DateOnly termFrom,DateOnly from,DateOnly to)
 {
  var first=from.DayNumber-termFrom.DayNumber;
  var end=to.DayNumber-termFrom.DayNumber;
  DateTimeOffset? latest=null;
  foreach(var entry in entries)
  {
   if(entry.Side is not FloatingSide)continue;
   // The composition requires one indexed payment row for every covered gas day.
   for(var row=first;row<end;row++)
    foreach(var price in entry.Schedule.Rows[row].Prices)
    {
     if(price.FixedPrice is not null)continue;
     var fixing=price.Lookup?.Fixing?.FixingAt??price.ContractObservation?.FixingAt;
     if(fixing is not {} at||!TradingInstant.IsSupported(at))
      throw Invalid("Each indexed payment price needs an exact fixing instant for cash automatic exercise.");
     if(latest is null||at>latest)latest=at;
    }
  }
  return latest??throw Invalid("Every cash automatic exercise group requires an actual payment fixing time.");
 }

 public ExactRatio StageRatio(int physical,GasQuantityStage stage)=>Stage(ReceiptRatios[physical],Sides[physical].QuantityRatio!,stage);
 private sealed record Composition(ImmutableArray<SideMembership> Members,ImmutableArray<ExactRatio> Receipts,ExactDecimal Base,
  ImmutableArray<GasCompositionRole> ChargeRoles);
 private static Composition Resolve(ImmutableArray<GasPhysicalComponent> physical,ImmutableArray<GasCashComponent> cash,
  ImmutableArray<GasChargeComponent> charges,bool hasRights)
 {
  if(physical.IsDefault||physical.Length is <MinPhysicalSides or >MaxPhysicalSides||cash.IsDefault||
   (long)cash.Length+charges.Length>MaxCashSides||physical.Length+cash.Length+charges.Length>MaxSides||
   physical.Any(p=>p is null||p.Side is null)||cash.Any(p=>p is null||p.Side is null)||charges.Any(p=>p is null||p.Side is null))
   throw Invalid($"Select {MinPhysicalSides} to {MaxPhysicalSides} physical sides, up to {MaxCashSides} payment and charge sides and at most {MaxSides} in all.");
  if(physical.Any(p=>p.QuantityPolicy is not(null or GasHeldQuantities.DeliverySchedulePolicy))||
   physical.Any(p=>p.Link is not null)&&(physical.Any(p=>p.QuantityPolicy is not null)||cash.Any(p=>p.Part is not null||p.Balance is not null)))
   throw Invalid("Quantity policies, parts and reported balances require independent physical sources and no quantity links.");
  if(cash.Any(p=>p.Share is not {} share||share.CompareTo(default)<=0))
   throw Invalid("Every payment requires an explicit positive share of its source quantity.");
  var codes=physical.Select(p=>p.Code).Concat(cash.Select(p=>p.Code)).Concat(charges.Select(p=>p.Code)).ToArray();
  if(codes.Any(code=>string.IsNullOrWhiteSpace(code)||!System.Text.RegularExpressions.Regex.IsMatch(code,"^[a-z][a-z0-9_.-]{0,63}$"))||codes.Distinct(StringComparer.Ordinal).Count()!=codes.Length)throw Invalid("Every side requires its own unique code.");
  var root=physical.FirstOrDefault(p=>p.Link is null);
  if(root?.Quantity is not {} amount||amount.CompareTo(default)<=0)throw Invalid("An independent positive physical quantity is required.");
  var native=physical[0].Side;var one=ExactDecimal.Parse("1");var minus=ExactDecimal.Parse("-1");
  foreach(var p in physical)
   if(p.Side.UnitQuantity!=native.UnitQuantity||p.Side.Currency!=native.Currency||p.Side.Quantities.FuelConvention!=GasFuelConvention.None||p.Direction!=one&&p.Direction!=minus)
    throw Invalid("Physical sides need one native quantity specification and currency, explicit directions and fuel only on the declared links.");
  if(physical.Where(p=>p.Link is not null).GroupBy(p=>p.Link!.Source).Any(g=>g.Count()>1))throw Invalid("Splitting one physical source needs an allocation profile and is not supported here.");
  var outputs=new ExactRatio?[physical.Length];var inputs=new ExactRatio[physical.Length];var visiting=new HashSet<int>();
  int Index(string code){var found=physical.Select((p,n)=>(p,n)).Where(x=>x.p.Code==code).ToArray();return found.Length==1?found[0].n:throw Invalid("Quantity links must name an existing physical side.");}
  ExactRatio Ratio(ExactDecimal q)=>new(q.ScaledValue,ExactDecimal.WorkingScale);
  var basis=Ratio(amount);
  ExactRatio Visit(int n)
  {
   if(outputs[n] is {} cached)return cached;
   if(!visiting.Add(n))throw Invalid("Physical quantity links cannot form a cycle.");
   var p=physical[n];ExactRatio result;
   if(p.Link is null)
   {
    if(p.Quantity is not {} quantity||quantity.CompareTo(default)<=0)throw Invalid("Independent sides require an explicit positive quantity.");
    result=inputs[n]=Ratio(quantity).Times(basis.Inverse());
   }
   else
   {
    if(p.Quantity is not null||!Enum.IsDefined(p.Link.Formula)||p.Link.Fraction.CompareTo(default)<0||p.Link.Fraction.CompareTo(one)>=0||p.Link.Formula==GasFuelConvention.None&&p.Link.Fraction!=default)throw Invalid("Linked sides need one explicit fuel formula and fraction in [0,1), with no independent quantity.");
    var source=Index(p.Link.Source);var a=physical[source].Side;var b=p.Side;
    if(a.Product!=b.Product||a.Quantities.From!=b.Quantities.From||a.Quantities.To!=b.Quantities.To||a.Quantities.Basis!=b.Quantities.Basis||a.Quantities.Clock!=b.Quantities.Clock)
     throw Invalid("Linked sides require matching product, gas-day intervals and quantity basis; independent sides may have separate delivery terms.");
    inputs[n]=Visit(source);
    var factor=GasFuelRule.DeliveryFactor(p.Link.Formula,p.Link.Fraction);
    result=inputs[n].Times(factor);
   }
   visiting.Remove(n);outputs[n]=result;return result;
  }
  var members=ImmutableArray.CreateBuilder<SideMembership>();
  for(var n=0;n<physical.Length;n++)members.Add(new(physical[n].Side,physical[n].Direction,Visit(n)));
  foreach(var p in cash)
  {
   var source=Index(p.Source);var a=physical[source].Side;
   if(p.Balance is not null||p.Role=="imbalance_cashout")GasImbalanceCashoutEntries.Require(p,a);
   GasCashDelivery.Require(p.Side,a,"gas_composition_input");
   var indexed=p.Role is("indexed_consideration" or "imbalance_cashout")&&p.Side is FloatingSide;
   if(!(indexed||p.Role is("consideration" or "fee")&&p.Side is FixedSide)||!Enum.IsDefined(p.Stage)||p.Direction!=one&&p.Direction!=minus||
    p.Side.GenerateSchedule() is not SidePaymentSchedule schedule||schedule.Currency!=native.Currency||schedule.UnitQuantity!=native.UnitQuantity||schedule.Rows.Count<1||!indexed&&schedule.Rows.Count!=1)
    throw Invalid("Cash sides require their declared fixed or indexed price, independent direction and explicit physical quantity stage in the same native quote.");
   var rows=schedule.Rows;
   var delivery=SourceDays(a,p,hasRights);
   if(rows[0].DeliveryFrom<a.Quantities.From||rows[^1].DeliveryTo>a.Quantities.To||rows.Where((row,n)=>n>0&&rows[n-1].DeliveryTo!=row.DeliveryFrom).Any())
    throw Invalid("Payment dates must cover a contiguous range within the source's gas days.");
   foreach(var row in rows)
   {
    var days=delivery.Where(d=>d.OperatingDay>=row.DeliveryFrom&&d.OperatingDay<row.DeliveryTo).ToArray();
    if(days.Length!=row.DeliveryTo.DayNumber-row.DeliveryFrom.DayNumber||days.Length==0||indexed&&days.Length!=1||
     row.AllocatedQuantity!=days.Select(d=>d.Delivery).Aggregate((x,y)=>x.Add(y)))
     throw Invalid("Every payment row requires the source's exact contract quantity over its own gas days.");
   }
   members.Add(new(p.Side,p.Direction,Ratio(p.Share!.Value).Times(Stage(inputs[source],outputs[source]!,p.Stage))));
  }
  GasImbalanceCashoutEntries.RequirePairs(cash);
  var chargeRoles=ImmutableArray.CreateBuilder<GasCompositionRole>(charges.Length);
  foreach(var charge in charges)
  {
   var clock=physical.FirstOrDefault(p=>p.Code==charge.ClockSide)?.Side.Quantities.Clock
    ??throw Invalid("Each charge must select the clock of a physical side of this trade.");
   if(charge.Side.UnitQuantity!=native.UnitQuantity||charge.Side.Currency!=native.Currency||
    charge.Direction!=one&&charge.Direction!=minus)
    throw Invalid("Charges require the trade's native quantity unit and currency and an explicit direction.");
   if(charge.DailyQuantity.CompareTo(default)<=0||charge.Basis==ChargeBasis.LumpSum&&charge.DailyQuantity!=one)
    throw Invalid("A charge requires a positive daily quantity, exactly one for a lump sum.");
   GasChargeSides.RequireShape(charge.Side,charge.Basis,clock);
   var owned=new OwnedQuantity(charge.Basis,clock,Ratio(charge.DailyQuantity));
   members.Add(new(charge.Side,charge.Direction,owned.DailyQuantity.Times(basis.Inverse())));
   chargeRoles.Add(new(charge.Code,"charge",null,null,null,Owned:owned,ClockSide:charge.ClockSide));
  }
  return new(members.ToImmutable(),[..inputs],amount,chargeRoles.MoveToImmutable());
 }
 private static ImmutableArray<GasQuantityRow> SourceDays(PhysicalGasSide source,GasCashComponent payment,bool hasRights)
 {
  try{return source.GenerateSchedule().Quantities.Rows;}
  catch(MarketDataException error)when(hasRights&&error.Code=="gas_day_boundary")
  {throw Invalid($"Physical source '{payment.Source}' needs its exact retained gas-day boundaries for payment '{payment.Code}'.");}
 }
 private static ExactRatio Stage(ExactRatio input,ExactRatio output,GasQuantityStage stage)=>stage switch{
  GasQuantityStage.Receipt=>input,GasQuantityStage.Delivery=>output,
  GasQuantityStage.Fuel when input.Numerator*output.Denominator>output.Numerator*input.Denominator=>new(input.Numerator*output.Denominator-output.Numerator*input.Denominator,input.Denominator*output.Denominator),
  _=>throw Invalid("The selected fuel stage must have a positive quantity.")};
 private static MarketDataException Invalid(string message)=>new("gas_composition_input",message);
}
