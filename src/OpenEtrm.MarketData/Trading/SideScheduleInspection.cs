using System.Collections.Immutable;
using OpenEtrm.Shared.Numerics;

namespace OpenEtrm.MarketData.Trading;

public sealed record ScheduleInspectionLifecycle(string Delivery,string? Due,string? Cash,string? EconomicClass,string? Coverage,string? UnavailableReason)
{public string? Fulfillment{get;init;}}
public sealed record ScheduleInspectionRow(string Key,string Operation,string Kind,DateOnly? DeliveryFrom,DateOnly? DeliveryTo,
 DateTimeOffset? Start,DateTimeOffset? End,DateOnly? ObservationFrom,DateOnly? ObservationTo,DateTimeOffset? ValueAt,
 string? Quantity,string? Price,string? Amount,string? DiscountFactor,string? PresentValue,
 string? QuantityState,ImmutableArray<string> PriceStates,ScheduleInspectionLifecycle Lifecycle)
{
 public DateTimeOffset? PaymentAt=>Kind==nameof(ComponentKind.Payment)?ValueAt:null;
 public ReferenceKey? QuantitySpecification{get;init;}
 public ReferenceKey? Currency{get;init;}
 public string? QuoteMultiplier{get;init;}
 public DateOnly? OperatingDate{get;init;}
 public string? Paid{get;init;}
 public string? Outstanding{get;init;}
}
public sealed record ScheduleInspectionPage(string Side,string Manifest,DateTimeOffset ValuationAt,DateTimeOffset KnownAt,
 int Offset,int Total,ImmutableArray<ScheduleInspectionRow> Rows);

// Read evaluated schedules without regenerating dates or performing pricing.
public static partial class SideScheduleInspection
{
 public const int MaximumPageSize=128;
 public static ScheduleInspectionPage Read(SideViewInput side,Func<DateOnly,DateTimeOffset> boundary,int offset,int limit,CancellationToken ct=default)
 {
  RequirePage(offset,limit);ct.ThrowIfCancellationRequested();RequireEvaluation(side);
  if(boundary is null)throw Error("Select an explicit delivery boundary.");
  var evaluation=side.Evaluation;var program=side.Program.Document;
  var total=evaluation.Contributions.Sum(c=>c.Amount.Values.Length);
  var fraction=side.Scale.Power(1);var scale=new SignedRatio(fraction.Numerator,fraction.Denominator)*side.Coefficient;
  var rows=ImmutableArray.CreateBuilder<ScheduleInspectionRow>();var position=0;
  foreach(var contribution in evaluation.Contributions)
  {
   ct.ThrowIfCancellationRequested();var guard=program.Instructions[contribution.Instruction];
   if(guard.Output.Operation!="output_guard"||contribution.States.Length!=contribution.Amount.Values.Length)throw Error("Select complete contribution columns.");
   var first=position;position=checked(position+contribution.Amount.Values.Length);
   if(position<=offset||rows.Count==limit)continue;
   for(var cell=Math.Max(0,offset-first);cell<contribution.Amount.Values.Length&&rows.Count<limit;cell++)
   {
    ct.ThrowIfCancellationRequested();RequireContribution(contribution,cell);
    var state=contribution.States[cell];
    var amountRow=Operand(program,guard,"amount");
    var quantity=Column(evaluation.Values[Operand(program,amountRow,"quantity").Output.Ordinal],cell);
    var price=Column(evaluation.Values[Operand(program,amountRow,"price").Output.Ordinal],cell);
    if(quantity.Column.Presence[quantity.Cell]!=ScheduleCellPresence.Resolved||quantity.Column.States[quantity.Cell].Quantity is null||
     price.Column.Presence[price.Cell]!=ScheduleCellPresence.Resolved||price.Column.States[price.Cell].Prices==SchedulePriceFacts.None)
     throw Error("The selected contribution requires its evaluated quantity and price states.");
    var pvRow=Operand(program,guard,"discounted");
    var discount=Column(evaluation.Values[Operand(program,pvRow,"discount").Output.Ordinal],cell);
    var geometry=Geometry(program,guard.Output,cell,boundary);
    string? Scaled(ScheduleValueColumn column,int index,bool apply)=>column.Presence[index]==ScheduleCellPresence.Resolved?
     ExactNumberText.Format(new SignedRatio(column.Values[index].Numerator,column.Values[index].Denominator)*(apply?scale:new(1,1))):null;
    var prices=Enum.GetValues<SchedulePriceFacts>().Where(f=>f!=SchedulePriceFacts.None&&(price.Column.States[price.Cell].Prices&f)!=0).Select(f=>f.ToString()).ToImmutableArray();
    var due=state.Kind==ComponentKind.Payment?guard.Output.ValueAt<=evaluation.Context.ValuationAt?"Due":"NotDue":null;
    rows.Add(new($"{guard.Output.Ordinal}:{cell}",guard.Output.Operation,state.Kind.ToString(),geometry.From,geometry.To,geometry.Start,geometry.End,
     guard.Output.ObservationFrom,guard.Output.ObservationTo,guard.Output.ValueAt,Scaled(quantity.Column,quantity.Cell,true),Scaled(price.Column,price.Cell,false),
     Scaled(contribution.Amount,cell,true),Scaled(discount.Column,discount.Cell,false),Scaled(contribution.PresentValue,cell,true),quantity.Column.States[quantity.Cell].Quantity?.ToString(),prices,
     new ScheduleInspectionLifecycle(Delivery(geometry.Start,geometry.End,evaluation.Context.ValuationAt),due,state.Kind==ComponentKind.Payment?"NotTracked":null,state.Class?.ToString(),state.Coverage.ToString(),state.UnavailableReason)
      {Fulfillment=state.Kind is ComponentKind.Delivery or ComponentKind.Holding?"NotTracked":null})
      {QuantitySpecification=quantity.Column.Unit,Currency=contribution.Amount.Currency,QuoteMultiplier=price.Column.QuoteMultiplier,OperatingDate=geometry.OperatingDate});
   }
  }
  return new(side.Code,program.ScheduleManifest,evaluation.Context.ValuationAt,evaluation.Context.KnownAt,offset,total,rows.ToImmutable());
 }
 internal static void RequireEvaluation(SideViewInput side)
 {
  if(side?.Evaluation is null||side.Program is null||string.IsNullOrWhiteSpace(side.Code)||
   side.Evaluation.ScheduleManifest!=side.Program.Document.ScheduleManifest||side.Evaluation.BaseScenarioId!=side.Evaluation.ScenarioId||
   side.Evaluation.Values.Length!=side.Program.Document.Instructions.Length||side.Scale.CompareTo(default)<=0||
   side.Coefficient.Denominator.Sign<=0||side.Coefficient.Numerator.IsZero||
   side.Evaluation.SourceReceipt is {} receipt&&(receipt.Program!=side.Program.Identity||receipt.Context!=side.Evaluation.Context))
   throw Error("Select one exact evaluated Side, source context and signed trade scale.");
 }
 internal static void RequireContribution(ScheduleContribution contribution,int cell)
 {
  var count=contribution.Amount.Values.Length;
  if(contribution.Amount.Presence.Length!=count||contribution.Amount.States.Length!=count||contribution.States.Length!=count||
   contribution.PresentValue.Values.Length!=count||contribution.PresentValue.Presence.Length!=count||
   (uint)cell>=(uint)count||contribution.Amount.Presence[cell]!=ScheduleCellPresence.Resolved||!Enum.IsDefined(contribution.States[cell].Kind))
   throw Error("Select a fully evaluated contribution cell; use the retained plan for unavailable values.");
 }
 public static void RequirePage(int offset,int limit)
 {if(offset<0||limit is <1 or >MaximumPageSize)throw Error($"Select a nonnegative offset and 1–{MaximumPageSize} rows.");}
 internal static string Delivery(DateTimeOffset? start,DateTimeOffset? end,DateTimeOffset valuation)=>
  start is null||end is null?"NotApplicable":end<=valuation?"Elapsed":start>valuation?"Scheduled":"InProgress";
 internal static BookedSideInstruction Operand(BookedSideProgramDocument program,BookedSideInstruction row,string port)=>
  row.Inputs.TryGetValue(port,out var refs)&&refs is [var ordinal]&&(uint)ordinal<(uint)program.Instructions.Length?program.Instructions[ordinal]:throw Error($"The retained row requires one exact '{port}' input.");
 internal static (ScheduleValueColumn Column,int Cell) Column(ScheduleLaneColumns lanes,int cell)
 {
  foreach(var column in new[]{lanes.Scenario,lanes.Base})
  {
   var at=column.Values.Length==1?0:cell;
   if((uint)at>=(uint)column.Values.Length||column.Values.Length!=column.Presence.Length||column.Values.Length!=column.States.Length)throw Error("The retained value column has incompatible cells.");
   if(column.Presence[at]==ScheduleCellPresence.Resolved)return(column,at);
  }
  return(lanes.Scenario,lanes.Scenario.Values.Length==1?0:cell);
 }
 internal static (DateOnly? From,DateOnly? To,DateTimeOffset? Start,DateTimeOffset? End,DateOnly? OperatingDate) Geometry(BookedSideProgramDocument program,ScheduleDefinitionRow row,int cell,Func<DateOnly,DateTimeOffset> boundary)
 {
  if(row.Grid is {} grid){var interval=program.Grids[grid].Interval(cell);return(null,null,interval.Start,interval.End,interval.OperatingDate);}
  if(row.Interval is {} native)return(null,null,native.Start,native.End,native.OperatingDate);
  return(row.DeliveryFrom,row.DeliveryTo,row.DeliveryFrom is {} from?boundary(from):null,row.DeliveryTo is {} to?boundary(to):null,null);
 }
 internal static MarketDataException Error(string message)=>new("side_schedule_inspection",message);
}
