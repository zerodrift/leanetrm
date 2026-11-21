using System.Collections.Immutable;

namespace OpenEtrm.MarketData.Trading;

public static partial class SideScheduleInspection
{
 // Interval and dated Side valuations expose the same inspection row. Cash
 // evidence belongs to the evaluated binding; dates never manufacture it.
 public static ScheduleInspectionRow FromValue(ScheduleRowValue value,DateTimeOffset valuationAt,Func<DateOnly,DateTimeOffset> boundary,string quantityReferenceKind=ScheduleReferenceKinds.Quantity)
 {
  if(value?.Row.IsValid!=true||boundary is null||!TradingInstant.IsSupported(valuationAt))throw Error("Select an evaluated schedule row and explicit valuation instant.");
  var window=value.Row.DeliveryWindow;
  DateOnly? from=null,to=null;DateTimeOffset? start=null,end=null;
  if(window is DatedScheduleWindow dated){from=dated.From;to=dated.To;start=boundary(dated.From);end=boundary(dated.To);}
  else if(window is IntervalScheduleWindow interval){from=interval.Interval.OperatingDate;to=from.Value.AddDays(1);start=interval.Interval.Start;end=interval.Interval.End;}
  else if(window is ValueScheduleWindow nativeValue){start=nativeValue.From;end=nativeValue.To;}
  else throw Error("Unsupported delivery window.");
  var due=value.Row.Value?.Term.Payment is {Timing:ValueTimingKind.Instant} payment?
   valuationAt>=payment.PaymentAt:PaymentDatePolicy.IsDue(value.ValueAt,valuationAt);
  var prices=value.PriceComponents.Select(p=>p.Price).Distinct().ToArray();
  return new($"{value.Row.Ordinal}","contribution",value.Kind.ToString(),window is DatedScheduleWindow?from:null,window is DatedScheduleWindow?to:null,start,end,null,null,value.ValueAt,
   value.Quantity.Scale(value.Coefficient).ToExactString(),prices is [var price]?price.ToExactString():null,value.Amount.ToExactString(),value.Discount?.DiscountFactor.ToString(),value.PresentValue?.ToExactString(),
   value.QuantityState.ToString(),[..value.PriceComponents.Select(p=>p.State.ToString()).Distinct().Order(StringComparer.Ordinal)],
   new ScheduleInspectionLifecycle(Delivery(start,end,valuationAt),value.Kind==ComponentKind.Payment?due?"Due":"NotDue":null,
    value.Kind==ComponentKind.Payment?value.CashBalance?.State.ToString()??"NotTracked":null,value.RowClass?.ToString(),value.Coverage.ToString(),value.UnavailableReason)
    {Fulfillment=value.Kind is ComponentKind.Delivery or ComponentKind.Holding?"NotTracked":null})
   {QuantityReferenceKind=quantityReferenceKind,PaymentTiming=value.Kind==ComponentKind.Payment?Timing(value.Row.Key.Timing):null,QuantitySpecification=value.Quantity.Specification,Currency=value.Amount.Currency,PriceUnit=prices is [var single]?single.Unit:null,QuoteMultiplier=prices is [var quoted]?quoted.Unit.Multiplier.ToString():null,OperatingDate=window is IntervalScheduleWindow native?native.Interval.OperatingDate:null,
    Paid=value.CashBalance?.Paid.ToExactString(),Outstanding=value.CashBalance?.Outstanding.ToExactString()};
 }
}
