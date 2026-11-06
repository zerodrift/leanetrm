using System.Collections.Immutable;
using OpenEtrm.Shared.Numerics;

namespace OpenEtrm.MarketData.Trading;

public sealed record ScheduleCalculationInput(string Port,string Key);
public sealed record ScheduleCalculationRow(string Key,string Role,string Operation,string Kind,string? Value,string Presence,
 DateOnly? DeliveryFrom,DateOnly? DeliveryTo,DateTimeOffset? Start,DateTimeOffset? End,DateOnly? ObservationFrom,DateOnly? ObservationTo,
 DateTimeOffset? ValueAt,ReferenceKey? Unit,ReferenceKey? Currency,string? QuantityState,ImmutableArray<string> PriceStates,
 ImmutableArray<ScheduleCalculationInput> Inputs,ImmutableArray<ScheduleDefinitionBinding> Bindings,
 DateTimeOffset? FixingAt,DateOnly? ContractFrom,DateOnly? ContractTo,ImmutableArray<ObservationKey> Lineage)
{
 public ImmutableSortedDictionary<string,string> Parameters{get;init;}=ImmutableSortedDictionary<string,string>.Empty;
 public Guid? ActualBatch{get;init;}
 public int? ActualRevision{get;init;}
 public DateTimeOffset? ActualKnownAt{get;init;}
}
public sealed record ScheduleCalculationPage(string Side,string Manifest,string Contribution,string ValueBasis,string TradeQuantityScale,string MembershipNumerator,string MembershipDenominator,
 int Offset,int Total,ImmutableArray<ScheduleCalculationRow> Rows);

public static partial class SideScheduleInspection
{
 // A drill-down follows the stored typed operand addresses. Reductions expose
 // all of their native input cells; scalar inputs broadcast without duplication.
 // This reads the existing evaluation and never evaluates another price or sum.
 public static ScheduleCalculationPage Calculation(SideViewInput side,Func<DateOnly,DateTimeOffset> boundary,string contribution,int offset,int limit,CancellationToken ct=default)
 {
  RequirePage(offset,limit);ct.ThrowIfCancellationRequested();RequireEvaluation(side);
  var parts=contribution?.Split(':');
  if(parts is not [var ordinalText,var cellText]||!int.TryParse(ordinalText,out var ordinal)||!int.TryParse(cellText,out var selected)||ordinal<0||selected<0||
   side?.Program is null||side.Evaluation is null||side.Evaluation.ScheduleManifest!=side.Program.Document.ScheduleManifest||
   side.Evaluation.BaseScenarioId!=side.Evaluation.ScenarioId||boundary is null)throw Error("Select an exact contribution in this evaluated Side.");
  var program=side.Program.Document;var evaluation=side.Evaluation;
  if((uint)ordinal>=(uint)program.Instructions.Length||program.Instructions[ordinal].Output.Operation!="output_guard"||selected>=Count(ordinal))throw Error("Select a retained contribution cell.");
  var selectedContribution=evaluation.Contributions.SingleOrDefault(c=>c.Instruction==ordinal)??throw Error("Select an evaluated contribution.");
  RequireContribution(selectedContribution,selected);
  var seen=new HashSet<(int Instruction,int Cell)>();var pending=new Stack<(int Instruction,int Cell)>();pending.Push((ordinal,selected));
  while(pending.TryPop(out var address))
  {
   ct.ThrowIfCancellationRequested();if(!seen.Add(address))continue;
   if(seen.Count>65536)throw Error("This calculation exceeds the bounded inspection cell count; select a smaller contribution.");
   foreach(var input in Inputs(address.Instruction,address.Cell))pending.Push(input.Address);
  }
  var ordered=seen.OrderBy(a=>a.Instruction).ThenBy(a=>a.Cell).ToArray();
  var rows=ImmutableArray.CreateBuilder<ScheduleCalculationRow>();
  foreach(var address in ordered.Skip(offset).Take(limit))
  {
   ct.ThrowIfCancellationRequested();var instruction=program.Instructions[address.Instruction];var row=instruction.Output;
   var (column,cell)=Column(evaluation.Values[address.Instruction],address.Cell);var resolved=column.Presence[cell]==ScheduleCellPresence.Resolved;
   var geometry=Geometry(program,row,row.Operation=="discount"?0:address.Cell,boundary);
   var source=evaluation.SourceReceipt?.Cells.GetValueOrDefault(new(address.Instruction,address.Cell,evaluation.ScenarioId))??
    evaluation.SourceReceipt?.Cells.GetValueOrDefault(new(address.Instruction,address.Cell,evaluation.BaseScenarioId));
   var native=source as ScheduleNativePriceEvidence;
   var lookup=(source as ScheduleDatedPriceEvidence)?.Lookup??native?.Resolution.PeriodSelection?.Lookup;
   var observation=(source as ScheduleContractObservationEvidence)?.Lookup;
   var lineage=source switch{ScheduleDatedPriceEvidence p=>p.Resolution.Lineage,ScheduleContractObservationEvidence p=>p.Resolution.Lineage,
    ScheduleNativePriceEvidence p=>p.Resolution.ForwardLineage.Union(p.Resolution.PeriodSelection?.FixingLineage??ImmutableHashSet<ObservationKey>.Empty)
     .Union(p.Resolution.Anchor?.Lineage??ImmutableHashSet<ObservationKey>.Empty),_=>ImmutableHashSet<ObservationKey>.Empty};
   rows.Add(new(Key(address),row.Role,row.Operation,column.Kind,resolved?ExactNumberText.Format(new SignedRatio(column.Values[cell].Numerator,column.Values[cell].Denominator)):null,
    column.Presence[cell].ToString(),geometry.From,geometry.To,geometry.Start,geometry.End,row.ObservationFrom,row.ObservationTo,row.ValueAt,row.Unit,row.Currency,
    resolved?column.States[cell].Quantity?.ToString():null,resolved?Facts(column.States[cell].Prices):[],
    [..Inputs(address.Instruction,address.Cell).Select(i=>new ScheduleCalculationInput(i.Port,Key(i.Address)))],instruction.Bindings,
    lookup?.Fixing?.FixingAt??observation?.FixingAt,lookup?.Contract.DeliveryFrom??observation?.Contract.DeliveryFrom,
    lookup?.Contract.DeliveryTo??observation?.Contract.DeliveryTo,[..lineage.OrderBy(k=>k.Id)])
    {Parameters=row.Parameters,ActualBatch=native?.Resolution.Actual?.BatchId,ActualRevision=native?.Resolution.Actual?.Revision,ActualKnownAt=native?.Resolution.Actual?.KnownAt});
  }
  return new(side.Code,program.ScheduleManifest,$"{ordinal}:{selected}","unit_side_v1",side.Scale.ToString(),side.Coefficient.Numerator.ToString(System.Globalization.CultureInfo.InvariantCulture),
   side.Coefficient.Denominator.ToString(System.Globalization.CultureInfo.InvariantCulture),offset,ordered.Length,rows.ToImmutable());
  int Count(int at){var r=program.Instructions[at].Output;return r.Operation=="discount"?1:r.Grid is {} g?program.Grids[g].CellCount:1;}
  IEnumerable<(string Port,(int Instruction,int Cell) Address)> Inputs(int at,int cell)
  {
   var instruction=program.Instructions[at];
   foreach(var port in instruction.Inputs)foreach(var input in port.Value)
   {
    if(instruction.Output.Operation is "sum_cells" or "quantity_sum_cells")
    {for(var n=0;n<Count(input);n++)yield return(port.Key,(input,n));}
    else yield return(port.Key,(input,Count(input)==1?0:cell));
   }
  }
  static string Key((int Instruction,int Cell) address)=>$"{address.Instruction}:{address.Cell}";
 }
 internal static ImmutableArray<string> Facts(SchedulePriceFacts facts)=>[..Enum.GetValues<SchedulePriceFacts>().Where(f=>f!=SchedulePriceFacts.None&&(facts&f)!=0).Select(f=>f.ToString())];
}
