using System.Collections.Immutable;
using OpenEtrm.Shared.Numerics;
using OpenEtrm.MarketData.Physical;

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
   var geometry=Geometry(program,guard.Output,0,boundary);var one=new SignedRatio(1,1);var cash=state.Kind==ComponentKind.Payment;
   var selected=ScheduleSelectedQuantity.Read(side,guard.Output,0);
   SignedRatio? contract = quantity.Column.Presence[quantity.Cell] == ScheduleCellPresence.Resolved
    ? new SignedRatio(quantity.Column.Values[quantity.Cell].Numerator, quantity.Column.Values[quantity.Cell].Denominator) * scale : null;
   if(selected is not null && contract is null)
   {
    var literal = PhysicalDeliverySchedule.ReadLiteralQuantities(side.Program, ExactRatio.One).Single(d => d.OperatingDate == row.DeliveryFrom);
    contract = new SignedRatio(literal.Quantity.NumeratorValue, literal.Quantity.DenominatorValue) * scale;
   }
   var prices=price.Column.Presence[price.Cell]!=ScheduleCellPresence.Resolved?[]:
    Enum.GetValues<SchedulePriceFacts>().Where(f=>f!=SchedulePriceFacts.None&&(price.Column.States[price.Cell].Prices&f)!=0).Select(f=>f.ToString()).ToImmutableArray();
   var due=state.Kind==ComponentKind.Payment?PaymentDatePolicy.IsDue(guard.Output.ValueAt!.Value,evaluation.Context.ValuationAt)?"Due":"NotDue":null;
   var measures=row.Measures.IsEmpty?null:string.Join(",",row.Measures);
   var shownQuantity=row.ReportedQuantity is {} reported
    ?reported.Quantity?.ScaleFraction(scale.Numerator.Sign,1).ToExactString()
    :selected is null?Value(quantity.Column,quantity.Cell,scale):ExactNumberText.Format(contract!.Value*selected.Fraction);
   if (selected?.NoSelectedQuantity == true) shownQuantity = null;
   var shownAmount=row.ReportedQuantity is not null?row.Expected?.Add(row.Paid).ToExactString():Value(contribution.Amount,0,scale*(selected?.Fraction??one));
   if (selected?.NoSelectedQuantity == true) shownAmount = null;
   var shownValue=row.Unrealized is {} unrealized&&row.RealizedUnsettled is {} unsettled?unrealized.Add(unsettled).ToExactString():null;
   rows.Add(new($"{guard.Output.Ordinal}:0",guard.Output.Operation,state.Kind.ToString(),geometry.From,geometry.To,geometry.Start,geometry.End,
    guard.Output.ObservationFrom,guard.Output.ObservationTo,guard.Output.ValueAt,shownQuantity,Value(price.Column,price.Cell,one),
    shownAmount,Value(discount.Column,discount.Cell,one),shownValue,side.Part?.QuantityBasis??row.QuantityBasis,prices,
    new ScheduleInspectionLifecycle(row.Delivery.ToString(),due,row.Settlement?.ToString()??"NotApplicable",state.Class?.ToString(),measures,row.MissingSwingDays.IsEmpty ? null : string.Join(",",row.MissingSwingDays.Select(d=>d.Reason).Distinct())))
    {ExercisePeriods=side.ExerciseGroups.IsEmpty?default:side.Periods(row.DeliveryFrom,row.DeliveryTo),
     SourceQuantity=selected is null || selected.NoSelectedQuantity?null:ExactNumberText.Format(selected.Selected),SourceQuantityStates=selected?.Tags??default,PaymentTiming=RetainedTiming(guard.Output,state.Kind),QuantitySpecification=quantity.Column.Unit,Currency=contribution.Amount.Currency,QuoteMultiplier=price.Column.QuoteMultiplier,OperatingDate=geometry.OperatingDate,
     Accrued=cash?row.Accrued?.ToExactString():null,Paid=cash?row.Paid.ToExactString():null,Outstanding=cash?row.Outstanding?.ToExactString():null,PaidAt=row.PaidAt,
     Remaining=cash?row.Unrealized?.ToExactString():null,
     FinalDetermination=determinations.TryGetValue(row.Occurrence,out var final)?final.ToExactString():null,QuantityEvidence=row.ReportedQuantity?.Evidence??selected?.Evidence??default,QuantityAdjustment=selected is null?null:new(ExactNumberText.Format(contract!.Value),ExactNumberText.Format(selected.Fraction))});
  }
  return new(side.Code,program.ScheduleManifest,evaluation.Context.ValuationAt,evaluation.Context.KnownAt,offset,lifecycle.Length,rows.ToImmutable()){SwingRights=side.SwingRights.IsEmpty?default:side.SwingRights,Groups=side.ExerciseGroups.IsEmpty?default:side.ExerciseGroups};
 }
}
