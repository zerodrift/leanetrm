using System.Collections.Immutable;
using OpenEtrm.MarketData.Power;
using OpenEtrm.MarketData.Risk;

namespace OpenEtrm.MarketData.Trading;

[EconomicDefinition("rec_forward_instrument_v1")]
public sealed record RecForwardInstrument:SidedInstrument
{
 [IdentityField]public ReferenceKey Product {get;}
 [IdentityField]public DateOnly VintageFrom {get;}
 [IdentityField]public DateOnly VintageTo {get;}
 [IdentityField]public override Quantity UnitQuantity {get;}
 [IdentityField]public override ReferenceKey Currency {get;}
 internal override bool SupportsSettlement=>false;
 public RecForwardInstrument(Guid id,ReferenceKey product,DateOnly from,DateOnly to,ImmutableArray<SideMembership> sides):base(id,sides)
 {
  RecForwardRules.Product(product,from,to);
  if(sides.Length!=2||sides[0].Side is not FloatingSide {OperatingDefinition:{Kind:ComponentKind.Delivery} delivery}||
   sides[1].Side is not (FixedSide {OperatingDefinition:{Kind:ComponentKind.Payment}} or FloatingSide {OperatingDefinition:{Kind:ComponentKind.Payment}})||
   sides.Any(s=>s.QuantityRatio is not null||s.Coefficient!=ExactDecimal.Parse("1")&&s.Coefficient!=ExactDecimal.Parse("-1"))||sides[0].Coefficient==sides[1].Coefficient||sides[0].Side.Id==sides[1].Side.Id)
   throw RecForwardRules.Invalid();
  var payment=OperatingSchedules.Terms(sides[1].Side);var terms=new[]{delivery,payment};
  if(terms.Any(t=>t.UnitQuantity!=delivery.UnitQuantity||t.UnitQuantity.Specification!=t.Quote.Quantity||t.Quote.Currency!=delivery.Quote.Currency||
    t.Quote.Key.Revision!=product.Revision||t.Periods.Length!=1||t.Periods[0].Quantity.Port is not null||t.Periods[0].Quantity.Literal!=delivery.UnitQuantity)||
   payment.Periods[0].Interval!=delivery.Periods[0].Interval)
   throw RecForwardRules.Invalid();
  Product=product;VintageFrom=from;VintageTo=to;UnitQuantity=delivery.UnitQuantity;Currency=delivery.Quote.Currency;
 }
 public override void ValidateValuationState(IBindingValuationState? state)
 {if(state is not null)throw new MarketDataException("rec_lifecycle","Certificate forwards currently require an unstarted contractual valuation without cash or inventory fulfillment state.");}
 public override void ValidateBinding(Quantity quantity,DeliveryCoverage? coverage)
 {RecForwardRules.Quantity(quantity,UnitQuantity);base.ValidateBinding(quantity,coverage);}
 protected internal override void RequireBeforeValuation(SideValuationContext context)
 {
  if(OperatingSchedules.Terms(Sides[0].Side).Periods[0].Interval.Start<=context.Snapshot.ValuationAt)
   throw new MarketDataException("rec_delivery_lifecycle","A started certificate delivery requires a separately selected fulfillment lifecycle.");
 }
}

[EconomicDefinition("retained_rec_forward_v1")]
public sealed record RetainedRecForwardInstrument:RetainedProgramInstrument<RetainedOperatingMember>
{
 [IdentityField]public ReferenceKey Product {get;}
 [IdentityField]public DateOnly VintageFrom {get;}
 [IdentityField]public DateOnly VintageTo {get;}
 [IdentityField]public override Quantity UnitQuantity {get;}
 [IdentityField]public override ReferenceKey Currency {get;}
 [IdentityField]public override ImmutableArray<RetainedOperatingMember> Members {get;}
 public RetainedRecForwardInstrument(Guid id,ReferenceKey product,DateOnly from,DateOnly to,Quantity unit,ReferenceKey currency,ImmutableArray<RetainedOperatingMember> members):base(id,members,unit,currency)
 {
  RecForwardRules.Product(product,from,to);Money.RequireKey(currency);
  if(unit is null||unit.Amount!=ExactDecimal.Parse("1")||unit.Specification.Revision!=product.Revision||currency.Revision!=product.Revision||members.IsDefault||members.Length!=2||
   members.Any(m=>m is null)||members[0].SideId==members[1].SideId||members[0].Coefficient==members[1].Coefficient||
   members.Any(m=>m.Program.Document.Instructions.Any(i=>i.Output.Currency is {} c&&c!=currency))||
   !members[0].Program.Document.Roots.Any(r=>r.Measure=="physical_value")||!members[1].Program.Document.Roots.Any(r=>r.Measure=="contractual_amount"))
   throw RecForwardRules.Invalid();
  var physical=RecForwardRules.Program(members[0].Program,0,unit);
  var cash=RecForwardRules.Program(members[1].Program,1,unit);
  if(physical!=cash)throw RecForwardRules.Invalid();
  Product=product;VintageFrom=from;VintageTo=to;UnitQuantity=unit;Currency=currency;Members=members;
 }
 public override void ValidateBinding(Quantity quantity,DeliveryCoverage? coverage)
 {RecForwardRules.Quantity(quantity,UnitQuantity);base.ValidateBinding(quantity,coverage);}
 public override ExactMoney Value(UnitValuationContext context)
 {
        RequireUnstarted(context.Snapshot);
  return base.Value(context);
 }
    protected internal override void RequireBeforeValuation(SideValuationContext context) => RequireUnstarted(context.Snapshot);
    private void RequireUnstarted(MarketSnapshot snapshot)
    {
        if(Members[0].Program.Document.Instructions.Any(i=>i.Output.Operation=="output_guard"&&i.Output.Interval!.Start<=snapshot.ValuationAt))
            throw new MarketDataException("rec_delivery_lifecycle","A started certificate delivery requires a separately selected fulfillment lifecycle.");
    }
 public override UnitPricingContract Declare(UnitValuationContext context)=>RetainedDeclaration(new(Id,"retained_rec_forward_side_v2",
  [..Members.SelectMany(m=>m.Program.Document.Instructions).SelectMany(i=>i.Bindings).Where(b=>b.Kind=="contract_price").Select(b=>b.Market!.Value).Distinct()],
  UnitQuantity.Specification,Currency){Time=ValuationTimeDependency.Pinned,ScheduleIdentity=RetainedScheduleIdentity});
 protected override ScheduleMarketSources Sources(CurveBuild basis)=>ScheduleMarketSources.ForOperating(basis);
}
internal static class RecForwardRules
{
 internal static MarketDataException Invalid()=>new("rec_forward","Supply one exact product/vintage, independent physical delivery and consideration sides, count unit and opposite membership signs.");
 internal static void Product(ReferenceKey product,DateOnly from,DateOnly to)
 {Money.RequireKey(product);if(from.Year<1901||to.Year>2199||from>=to)throw Invalid();}
 internal static DeliveryInterval Program(BookedSideProgram program,int slot,Quantity unit)
 {
  var d=program.Document;var rows=d.Instructions;
  if(rows.Length!=7||!d.Grids.IsEmpty||!d.Ports.IsEmpty||d.Roots.Length!=4||d.Roots.Select(r=>(r.Row,r.Measure)).Distinct().Count()!=4||!d.Roots.Any(r=>r.Name=="pv"&&r.Row==6&&r.Measure=="pv"))throw Invalid();
  var operations=new[]{"quantity_literal",rows[1].Output.Operation,"amount","discount","pv","output_guard","sum"};
  if(operations[1] is not("constant_price" or "operating_contract_price")||slot==0&&operations[1]!="operating_contract_price"||
   rows.Where((row,index)=>row.Output.Operation!=operations[index]).Any())throw Invalid();
  var quantity=rows[0].Output;
  if(quantity.Unit!=unit.Specification||!quantity.Parameters.TryGetValue("numerator",out var numerator)||!quantity.Parameters.TryGetValue("denominator",out var denominator)||
   !System.Numerics.BigInteger.TryParse(numerator,out var n)||!System.Numerics.BigInteger.TryParse(denominator,out var z)||n!=z||z<=0)throw Invalid();
  var guard=rows[5].Output;
  if(guard.Interval is not {} interval||rows.Take(6).Any(row=>row.Output.Interval!=interval||row.Output.ValueAt!=guard.ValueAt)||
   guard.Parameters.GetValueOrDefault("lifecycle")!=(slot==0?"future_operating_delivery_v1":"undue_payment_v1")||
   guard.Parameters.GetValueOrDefault("measure")!="present_value_v1"||guard.Parameters.GetValueOrDefault("pricePolicy")!="selected_context_v1"||
   rows[3].Output.Parameters.GetValueOrDefault("policy")!="context_required_v1"||slot==0&&guard.ValueAt!=interval.End||slot==1&&guard.ValueAt<interval.End||
   slot==0&&guard.Parameters.GetValueOrDefault("paymentRule")!="not_applicable_delivery_v1"||
   rows[1].Output.Unit!=unit.Specification)throw Invalid();
  bool Inputs(int row,params (string Port,int Source)[] inputs)=>rows[row].Inputs.Count==inputs.Length&&
   inputs.All(input=>rows[row].Inputs.TryGetValue(input.Port,out var values)&&values.Length==1&&values[0]==input.Source);
  if(!Inputs(2,("quantity",0),("price",1))||!Inputs(4,("amount",2),("discount",3))||!Inputs(5,("amount",2),("discounted",4))||!Inputs(6,("input",5))||
   d.Roots.Any(r=>(r.Row,r.Measure) is not(0,"schedule_quantity") and not(5,"contribution_pv") and not(6,"pv")&&
    (r.Row!=2||r.Measure!=(slot==0?"physical_value":"contractual_amount"))))throw Invalid();
  return interval;
 }
 internal static void Quantity(Quantity quantity,Quantity unit)
 {if(quantity is null||quantity.Specification!=unit.Specification||quantity.Amount.Power(1).Denominator!=1||quantity.Amount.CompareTo(default)<=0)throw new MarketDataException("rec_quantity","Book a positive integral certificate count in its exact declared unit.");}
}
