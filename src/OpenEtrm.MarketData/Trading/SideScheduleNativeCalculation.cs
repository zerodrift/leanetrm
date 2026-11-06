using System.Collections.Immutable;

namespace OpenEtrm.MarketData.Trading;

public abstract partial class SideScheduleSnapshot
{
 // These are named native valuation records, not another pricing algorithm or
 // fabricated stored-program instructions. Original weights and applications
 // accompany their already evaluated contributions.
 static ImmutableArray<ScheduleCalculationRow> NativeCalculation(ScheduleRowValue row,DateTimeOffset at,Func<DateOnly,DateTimeOffset> boundary,CancellationToken ct)
 {
  var inspected=SideScheduleInspection.FromValue(row,at,boundary);var result=ImmutableArray.CreateBuilder<ScheduleCalculationRow>();
  ImmutableSortedDictionary<string,string> Terms(params (string Key,string? Value)[] values)=>values.Where(v=>v.Value is not null).ToImmutableSortedDictionary(v=>v.Key,v=>v.Value!,StringComparer.Ordinal);
  ScheduleCalculationRow Cell(string key,string operation,string kind,string? value,ImmutableArray<ScheduleCalculationInput> inputs=default)=>
   new(key,"native_evaluation",operation,kind,value,value is null?"OutsideMeasure":"Resolved",inspected.DeliveryFrom,inspected.DeliveryTo,inspected.Start,inspected.End,
    null,null,row.ValueAt,kind=="quantity"?row.Quantity.Specification:null,kind is "money" or "price"?row.Amount.Currency:null,
    kind=="quantity"?row.QuantityState.ToString():null,[],inputs.IsDefault?[]:inputs,[],null,null,null,[]);
  result.Add(Cell("quantity","quantity","quantity",row.Quantity.ToExactString()));
  var prices=ImmutableArray.CreateBuilder<ScheduleCalculationInput>();
  if(row.Row.DeliveryWindow is DatedScheduleWindow)
  {
   for(var n=0;n<row.Resets.Length;n++)
   {
    ct.ThrowIfCancellationRequested();var reset=row.Resets[n];var price=reset.Price;var lookup=price.Term.Lookup;var observation=price.Term.ContractObservation;var key=$"reset:{n}";
    var market=lookup?.ProjectionRequest??observation?.Market;
    result.Add(Cell(key,price.Term.FixedPrice is null?"price_lookup":"constant_price","price",price.ExactPrice.ToExactString()) with{
     ObservationFrom=lookup?.Contract.DeliveryFrom??observation?.From,ObservationTo=lookup?.Contract.DeliveryTo??observation?.To,
     FixingAt=lookup?.Fixing?.FixingAt??observation?.FixingAt,ContractFrom=lookup?.Contract.DeliveryFrom??observation?.Contract.DeliveryFrom,ContractTo=lookup?.Contract.DeliveryTo??observation?.Contract.DeliveryTo,
     PriceStates=[price.State.ToString()],Lineage=[..price.Lineage.OrderBy(p=>p.Id)],
     Bindings=[..market is {} m?new[]{new ScheduleDefinitionBinding(0,"market","curve",null,m,null,null,null,null,null)}:[],
      ..lookup?.CustomProjection is {} projection?new[]{new ScheduleDefinitionBinding(0,"projection","projection",null,null,null,projection.CurveId,projection.Version,projection.OutputNode,projection.Position,null,null,projection.CatalogId)}:[]],
     Parameters=Terms(("weight",price.Term.Weight.ToString()),("weightTotal",row.ObservationWeightTotal),("spread",price.Term.Spread?.Value.ToString()),("projectionSelection",lookup?.CustomProjection?.Selection),
      ("projectionFrom",lookup?.ProjectionInterval?.From.ToString("yyyy-MM-dd")),("projectionTo",lookup?.ProjectionInterval?.To.ToString("yyyy-MM-dd")))});
   }
   for(var n=0;n<row.Applications.Length;n++)
   {
    ct.ThrowIfCancellationRequested();var application=row.Applications[n];var key=$"application:{n}";
    result.Add(Cell(key,"price_application","money",application.CashContribution.ToExactString(),[new("price",$"reset:{application.Application.PriceOrdinal}")]) with{
     DeliveryFrom=application.Application.TargetFrom,DeliveryTo=application.Application.TargetTo,Start=null,End=null,
     Parameters=Terms(("weight",application.Application.Weight.ToString()),("mapping",application.Application.MappingRule),("weightTotal",row.ObservationWeightTotal))});
    prices.Add(new("application",key));
   }
  }
  else for(var n=0;n<row.PriceComponents.Length;n++)
  {
   ct.ThrowIfCancellationRequested();var price=row.PriceComponents[n];var resolution=price.Resolution;var lookup=resolution?.PeriodSelection?.Lookup;var key=$"price:{n}";
   var lineage=resolution?.ForwardLineage??ImmutableHashSet<ObservationKey>.Empty;
   lineage=lineage.Union(resolution?.PeriodSelection?.FixingLineage??ImmutableHashSet<ObservationKey>.Empty).Union(resolution?.Anchor?.Lineage??ImmutableHashSet<ObservationKey>.Empty);
   result.Add(Cell(key,"price_component","price",price.Price.ToExactString()) with{
    ObservationFrom=lookup?.Contract.DeliveryFrom,ObservationTo=lookup?.Contract.DeliveryTo,FixingAt=lookup?.Fixing?.FixingAt,
    ContractFrom=lookup?.Contract.DeliveryFrom,ContractTo=lookup?.Contract.DeliveryTo,PriceStates=[price.State.ToString()],Lineage=[..lineage.OrderBy(p=>p.Id)],
    Bindings=price.Curve is {} c?[new(0,"market","curve",null,null,null,null,null,null,null,c.Revision,c.Code)]:[],
    ActualBatch=resolution?.Actual?.BatchId,ActualRevision=resolution?.Actual?.Revision,ActualKnownAt=resolution?.Actual?.KnownAt,
    Parameters=Terms(("quantity",price.Quantity.ToExactString()),("multiplier",price.Multiplier.ToString()),("adder",price.Adder?.Value.ToString()))});
   prices.Add(new("price",key));
  }
  result.Add(Cell("amount","amount","money",row.Amount.ToExactString(),[new("quantity","quantity"),..prices]));
  if(row.Discount is {} discount)
   result.Add(Cell("discount","discount","factor",discount.DiscountFactor.ToString()) with{Parameters=Terms(("source",discount.Source.ToString()),("model",discount.RateDefinition?.ToString()))});
    if(row.CashBalance is {} cash)
  {
   result.Add(Cell("paid","paid","money",cash.Paid.ToExactString()));
   result.Add(Cell("outstanding","outstanding","money",cash.Outstanding.ToExactString(),[new("amount","amount"),new("paid","paid")]) with{
    Parameters=Terms(("determination",cash.DeterminationId?.ToString()),("cashState",cash.State.ToString()))});
  }
  result.Add(Cell("pv","pv","money",row.PresentValue?.ToExactString(),[new(row.CashBalance is null?"amount":"outstanding",row.CashBalance is null?"amount":"outstanding"),..row.Discount is null?Array.Empty<ScheduleCalculationInput>():[new("discount","discount")]]));
  return result.ToImmutable();
 }
}
