using System.Collections.Immutable;
using OpenEtrm.MarketData.Trading;
using OpenEtrm.Shared.Numerics;

namespace OpenEtrm.MarketData.Gas;

public enum GasQuantityStage { Receipt=1,Delivery,Fuel }
[EconomicDefinition("gas_composition_link_v1")]
public sealed record GasCompositionLink([property:IdentityField]string Source,[property:IdentityField]GasFuelConvention Formula,[property:IdentityField]ExactDecimal Fraction);
public sealed record GasPhysicalComponent(string Code,PhysicalGasSide Side,ExactDecimal Direction,ExactDecimal? Quantity,GasCompositionLink? Link);
public sealed record GasCashComponent(string Code,Side Side,string Role,string Source,GasQuantityStage Stage,ExactDecimal Direction);
[EconomicDefinition("gas_composition_role_v1")]
public sealed record GasCompositionRole([property:IdentityField]string Code,[property:IdentityField]string Role,[property:IdentityField]string? Source,[property:IdentityField]GasQuantityStage? Stage,[property:IdentityField]GasCompositionLink? Link);

// Fixed contractual profiles only. Exact native-unit membership ratios retain
// quantity normalization separately from direction; no operational selection or
// transport entitlement is inferred from a quantity link.
[EconomicDefinition("gas_composition_instrument_v1")]
public sealed record GasCompositionInstrument:SidedInstrument
{
 // The fixed profile's side bounds. Cash sides include fees.
 public const int MinPhysicalSides=1,MaxPhysicalSides=4,MaxCashSides=4;
 [IdentityField]public ImmutableArray<GasCompositionRole> Roles { get; }
 public ImmutableArray<GasPhysicalComponent> Physical { get; }
 public ImmutableArray<GasCashComponent> Cash { get; }
 public ImmutableArray<ExactRatio> ReceiptRatios { get; }
 public ExactDecimal BaseQuantity { get; }
 public override ReferenceKey Currency=>Physical[0].Side.Currency;
 public override Quantity UnitQuantity=>Physical[0].Side.UnitQuantity;
 public override DefinitionIdentity PricingBindingIdentity=>DefinitionIdentity.For(new GasInstrumentPricingBindings([..Sides.Select(m=>m.Side.PricingBindingIdentity?.Canonical)]));
 internal override bool SupportsSettlement=>false;
 public GasCompositionInstrument(Guid id,ImmutableArray<GasPhysicalComponent> physical,ImmutableArray<GasCashComponent> cash)
  :this(id,physical,cash,Resolve(physical,cash)) { }
 private GasCompositionInstrument(Guid id,ImmutableArray<GasPhysicalComponent> physical,ImmutableArray<GasCashComponent> cash,Composition c):base(id,c.Members)
 {
  Physical=physical;Cash=cash;BaseQuantity=c.Base;ReceiptRatios=c.Receipts;
  Roles=[..physical.Select(p=>new GasCompositionRole(p.Code,"physical",p.Link?.Source,null,p.Link)),..cash.Select(p=>new GasCompositionRole(p.Code,p.Role,p.Source,p.Stage,null))];
 }
 public ExactRatio StageRatio(int physical,GasQuantityStage stage)=>Stage(ReceiptRatios[physical],Sides[physical].QuantityRatio!,stage);
 private sealed record Composition(ImmutableArray<SideMembership> Members,ImmutableArray<ExactRatio> Receipts,ExactDecimal Base);
 private static Composition Resolve(ImmutableArray<GasPhysicalComponent> physical,ImmutableArray<GasCashComponent> cash)
 {
  if(physical.IsDefault||physical.Length is <MinPhysicalSides or >MaxPhysicalSides||cash.IsDefault||cash.Length>MaxCashSides||physical.Any(p=>p is null||p.Side is null)||cash.Any(p=>p is null||p.Side is null))
   throw Invalid($"Select {MinPhysicalSides} to {MaxPhysicalSides} physical sides and up to {MaxCashSides} cash sides.");
  var codes=physical.Select(p=>p.Code).Concat(cash.Select(p=>p.Code)).ToArray();
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
   if(p.Role is not("consideration" or "fee")||!Enum.IsDefined(p.Stage)||p.Direction!=one&&p.Direction!=minus||
    p.Side is not FixedSide||p.Side.GenerateSchedule() is not SidePaymentSchedule schedule||schedule.Currency!=native.Currency||schedule.UnitQuantity!=native.UnitQuantity||schedule.Rows.Count!=1)
    throw Invalid("Cash and fee sides need an independent direction, fixed price and one explicit physical quantity stage in the same native quote.");
   var row=schedule.Rows[0];
   if(row.DeliveryFrom!=a.Quantities.From||row.DeliveryTo!=a.Quantities.To||row.AllocatedQuantity!=a.GenerateSchedule().Quantities.TotalDelivery)throw Invalid("Cash coverage must match its selected physical quantity profile.");
   members.Add(new(p.Side,p.Direction,Stage(inputs[source],outputs[source]!,p.Stage)));
  }
  return new(members.ToImmutable(),[..inputs],amount);
 }
 private static ExactRatio Stage(ExactRatio input,ExactRatio output,GasQuantityStage stage)=>stage switch{
  GasQuantityStage.Receipt=>input,GasQuantityStage.Delivery=>output,
  GasQuantityStage.Fuel when input.Numerator*output.Denominator>output.Numerator*input.Denominator=>new(input.Numerator*output.Denominator-output.Numerator*input.Denominator,input.Denominator*output.Denominator),
  _=>throw Invalid("The selected fuel stage must have a positive quantity.")};
 private static MarketDataException Invalid(string message)=>new("gas_composition_input",message);
}
