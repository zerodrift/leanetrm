using System.Collections.Immutable;

namespace OpenEtrm.MarketData.Trading;

public static partial class SideScheduleInspection
{
 // Interval and dated Side valuations expose the same inspection row. Cash
 // evidence belongs to the evaluated binding; dates never manufacture it.
 public static ScheduleInspectionRow FromValue(ScheduleRowValue value,DateTimeOffset valuationAt,Func<DateOnly,DateTimeOffset> boundary)
 {
  if(value?.Row.IsValid!=true||boundary is null||!TradingInstant.IsSupported(valuationAt))throw Error("Select an evaluated schedule row and explicit valuation instant.");
  var window=value.Row.DeliveryWindow;
  var from=window switch{DatedScheduleWindow w=>w.From,IntervalScheduleWindow w=>w.Interval.OperatingDate,_=>throw Error("Unsupported delivery window.")};
  var to=window is DatedScheduleWindow dated?dated.To:from.AddDays(1);
  var start=window is IntervalScheduleWindow interval?interval.Interval.Start:boundary(from);
  var end=window is IntervalScheduleWindow last?last.Interval.End:boundary(to);
  var prices=value.PriceComponents.Select(p=>p.Price).Distinct().ToArray();
  return new($"{value.Row.Ordinal}","contribution",value.Kind.ToString(),window is DatedScheduleWindow?from:null,window is DatedScheduleWindow?to:null,start,end,null,null,value.ValueAt,
   value.Quantity.Scale(value.Coefficient).ToExactString(),prices is [var price]?price.ToExactString():null,value.Amount.ToExactString(),value.Discount?.DiscountFactor.ToString(),value.PresentValue?.ToExactString(),
   value.QuantityState.ToString(),[..value.PriceComponents.Select(p=>p.State.ToString()).Distinct().Order(StringComparer.Ordinal)],
   new ScheduleInspectionLifecycle(Delivery(start,end,valuationAt),value.Kind==ComponentKind.Payment?value.ValueAt<=valuationAt?"Due":"NotDue":null,
    value.Kind==ComponentKind.Payment?value.CashBalance?.State.ToString()??"NotTracked":null,value.RowClass?.ToString(),value.Coverage.ToString(),value.UnavailableReason)
    {Fulfillment=value.Kind is ComponentKind.Delivery or ComponentKind.Holding?"NotTracked":null})
   {QuantitySpecification=value.Quantity.Specification,Currency=value.Amount.Currency,QuoteMultiplier=prices is [var quoted]?quoted.Unit.Multiplier.ToString():null,OperatingDate=window is IntervalScheduleWindow native?native.Interval.OperatingDate:null,
    Paid=value.CashBalance?.Paid.ToExactString(),Outstanding=value.CashBalance?.Outstanding.ToExactString()};
 }
}
