using System.Collections.Immutable;
using OpenEtrm.Shared.Numerics;

namespace OpenEtrm.MarketData.Trading;

// One explicitly mapped publication period prices one delivery day. The original
// monthly fixing remains monthly; it is never copied into daily observation facts.
public sealed partial record FloatingSide
{
 public const string DeliveryIndexImplementation="delivery_index_fixings_v1";
 public FloatingSide(Guid id,QuoteSpecification quote,Quantity unitQuantity,ImmutableArray<FloatingSidePeriod> periods,string implementation):base(id)
 {
  if(implementation!=DeliveryIndexImplementation||quote is null||unitQuantity is null||quote.Kind!=QuoteKind.Outright||
   unitQuantity.Amount!=ExactDecimal.Parse("1")||unitQuantity.Specification!=quote.Quantity||periods.IsDefaultOrEmpty||periods.Any(p=>p is null))throw DeliveryIndexInvalid();
  SwapSidePeriods.Validate([..periods.Select(p=>(p.DeliveryFrom,p.DeliveryTo,p.PaymentAt,p.Allocation,p.PaymentSchedule))],quote.Currency);
  foreach(var p in periods)
  {
   if(p.ObservationImplementation!=implementation||p.DeliveryTo!=p.DeliveryFrom.AddDays(1)||p.Allocation.Numerator<=0||
    p.Averaging!=FloatingAveraging.CalendarDayArithmetic||p.Calendar is not null||p.PaymentSchedule is not null||!p.ContractObservations.IsDefaultOrEmpty||
    p.Projections.IsDefaultOrEmpty||p.Fixings.IsDefaultOrEmpty||p.Projections.Length!=1||p.Fixings.Length!=1||p.Projections[0] is not {} projection||projection.Contract is not {} contract||p.Fixings[0] is not {} fixing||
    contract.Kind!=ContractKind.Forward||contract.FuturesSeries is not null||projection.Market.Contract!=contract.Key||projection.Market.Curve.Revision!=contract.Key.Revision||
    projection.AveragingQuantity is not null||contract.DeliveryFrom>p.DeliveryFrom||contract.DeliveryTo<p.DeliveryTo||
    !((contract.DeliveryFrom==p.DeliveryFrom&&contract.DeliveryTo==p.DeliveryTo)||(contract.DeliveryFrom.Day==1&&contract.DeliveryTo==contract.DeliveryFrom.AddMonths(1)))||
    fixing.From!=contract.DeliveryFrom||fixing.To!=contract.DeliveryTo||fixing.Index.Revision!=quote.Key.Revision||fixing.Quote!=quote.Key||fixing.Source.Revision!=quote.Key.Revision||
    fixing.Series.Revision!=contract.Key.Revision||fixing.Series.Id==Guid.Empty||string.IsNullOrWhiteSpace(fixing.Source.Code)||
    fixing.Implementation!=IndexFixing.SupportedImplementation||!TradingInstant.IsSupported(fixing.FixingAt)||p.FirstFixingAt!=fixing.FixingAt||fixing.FixingAt>=p.PaymentAt||
    p.Spread is not {} spread||spread.Unit!=PriceUnit.FromQuote(quote)||
    p.CustomProjection is {} custom&&custom.Selection!=CustomCurveProjectionBinding.ContainingDeliverySelection)throw DeliveryIndexInvalid();
  }
  DateQuote=quote;UnitQuantity=unitQuantity;Periods=periods;
 }
 private static MarketDataException DeliveryIndexInvalid()=>new("delivery_index_terms","Supply one exact daily or containing monthly publication fixing per delivery day, with positive allocation, explicit spread and payment terms.");
 internal IndexPriceLookup DeliveryLookup(FloatingSidePeriod period)
 {
  var projection=period.Projections[0];var lookup=new IndexPriceLookup(PriceSettlementSelection.CurveTarget(period.Fixings[0].Index),Quote,projection.Market,projection.Contract,null,period.Fixings[0]){PublishedWhenAvailable=true};
  return period.CustomProjection is {} custom?CurvePriceScheduleSource.Bind(lookup,custom,new(period.DeliveryFrom,period.DeliveryTo)):lookup;
 }
 private SidePaymentSchedule GenerateDeliveryIndexSchedule()
 {
  var one=ExactDecimal.Parse("1");
  var rows=Periods.Select((p,n)=>
  {
   var primitive=new ScheduleRow(Id,p.DeliveryFrom,p.DeliveryTo,p.PaymentAt,Currency,UnitQuantity,null,p.Averaging,null,
    [new SchedulePriceTerm(one,null,DeliveryLookup(p),p.Spread)],DeliveryIndexImplementation,
    [new FloatingObservationApplication(p.DeliveryFrom,p.DeliveryTo,0,one,DeliveryIndexImplementation)],DeliveryIndexImplementation,null,null);
   return new ScheduleRow(primitive,Id,n,new(p.Allocation.Numerator,p.Allocation.Denominator,UnitQuantity.Specification));
  }).ToImmutableArray();
  return new(Id,Currency,UnitQuantity,DeliveryIndexImplementation,rows);
 }
}
