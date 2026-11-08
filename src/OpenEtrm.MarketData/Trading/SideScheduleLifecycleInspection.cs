using System.Collections.Immutable;
using OpenEtrm.Shared.Numerics;

namespace OpenEtrm.MarketData.Trading;

public static partial class SideScheduleInspection
{
 // One row per contribution with its lifecycle row: delivery and settlement states, the quantity basis, the measures
 // it contributes to and its signed held contribution to them. Evaluated cells outside the measure stay null.
 public static ScheduleInspectionPage Lifecycle(SideViewInput side,ImmutableArray<PhysicalLifecycleRow> lifecycle,ImmutableDictionary<int,ExactMoney> determinations,
  Func<DateOnly,DateTimeOffset> boundary,int offset,int limit,CancellationToken ct=default)
 {
  RequirePage(offset,limit);ct.ThrowIfCancellationRequested();RequireEvaluation(side);
  var evaluation=side.Evaluation;var program=side.Program.Document;
  if(boundary is null||lifecycle.IsDefault||determinations is null||lifecycle.Length!=evaluation.Contributions.Length||
   evaluation.Contributions.Any(c=>c.Amount.Values.Length!=1||c.States.Length!=1)||
   lifecycle.Select((row,n)=>(row,n)).Any(x=>program.Instructions[evaluation.Contributions[x.n].Instruction].Output.PaymentOrdinal!=x.row.Occurrence)||
   determinations.Keys.Any(k=>!lifecycle.Any(r=>r.Occurrence==k&&r.Settlement is not null)))
   throw Error("Select the Side's lifecycle rows, one per retained occurrence in contribution order.");
  var fraction=side.Scale.Power(1);var scale=new SignedRatio(fraction.Numerator,fraction.Denominator)*side.Coefficient;
  static string? Value(ScheduleValueColumn column,int index,SignedRatio scale)=>column.Presence[index]==ScheduleCellPresence.Resolved?
   ExactNumberText.Format(new SignedRatio(column.Values[index].Numerator,column.Values[index].Denominator)*scale):null;
  var rows=ImmutableArray.CreateBuilder<ScheduleInspectionRow>();
  for(var n=offset;n<lifecycle.Length&&rows.Count<limit;n++)
  {
   ct.ThrowIfCancellationRequested();
   var contribution=evaluation.Contributions[n];var row=lifecycle[n];var guard=program.Instructions[contribution.Instruction];var state=contribution.States[0];
   var amountRow=Operand(program,guard,"amount");
   var quantity=Column(evaluation.Values[Operand(program,amountRow,"quantity").Output.Ordinal],0);
   var price=Column(evaluation.Values[Operand(program,amountRow,"price").Output.Ordinal],0);
   var discount=Column(evaluation.Values[Operand(program,Operand(program,guard,"discounted"),"discount").Output.Ordinal],0);
   var geometry=Geometry(program,guard.Output,0,boundary);var one=new SignedRatio(1,1);var cash=row.Settlement is not null;
   var prices=price.Column.Presence[price.Cell]!=ScheduleCellPresence.Resolved?[]:
    Enum.GetValues<SchedulePriceFacts>().Where(f=>f!=SchedulePriceFacts.None&&(price.Column.States[price.Cell].Prices&f)!=0).Select(f=>f.ToString()).ToImmutableArray();
   var due=state.Kind==ComponentKind.Payment?guard.Output.ValueAt<=evaluation.Context.ValuationAt?"Due":"NotDue":null;
   var measures=row.Measures.IsEmpty?null:string.Join(",",row.Measures);
   rows.Add(new($"{guard.Output.Ordinal}:0",guard.Output.Operation,state.Kind.ToString(),geometry.From,geometry.To,geometry.Start,geometry.End,
    guard.Output.ObservationFrom,guard.Output.ObservationTo,guard.Output.ValueAt,Value(quantity.Column,quantity.Cell,scale),Value(price.Column,price.Cell,one),
    Value(contribution.Amount,0,scale),Value(discount.Column,discount.Cell,one),row.RemainingPv.Add(row.OutstandingPv).ToExactString(),row.QuantityBasis,prices,
    new ScheduleInspectionLifecycle(row.Delivery.ToString(),due,row.Settlement?.ToString()??"NotApplicable",state.Class?.ToString(),measures,null))
    {QuantitySpecification=quantity.Column.Unit,Currency=contribution.Amount.Currency,QuoteMultiplier=price.Column.QuoteMultiplier,OperatingDate=geometry.OperatingDate,
     Accrued=cash?row.Accrued.ToExactString():null,Paid=cash?row.Paid.ToExactString():null,Outstanding=cash?row.Outstanding.ToExactString():null,PaidAt=row.PaidAt,
     FinalDetermination=determinations.TryGetValue(row.Occurrence,out var final)?final.ToExactString():null});
  }
  return new(side.Code,program.ScheduleManifest,evaluation.Context.ValuationAt,evaluation.Context.KnownAt,offset,lifecycle.Length,rows.ToImmutable());
 }
}
