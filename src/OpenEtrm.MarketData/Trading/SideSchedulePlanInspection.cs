using System.Collections.Immutable;

namespace OpenEtrm.MarketData.Trading;

public static partial class SideScheduleInspection
{
 // Retained dates remain inspectable when the selected profile cannot value
 // them. Unavailable values stay null; no earlier market or zero is substituted.
 public static ScheduleInspectionPage Plan(string side,BookedSideProgram program,Func<DateOnly,DateTimeOffset> boundary,
  DateTimeOffset valuationAt,DateTimeOffset knownAt,int offset,int limit,string unavailableReason,CancellationToken ct=default)
 {
  RequirePage(offset,limit);ct.ThrowIfCancellationRequested();
  if(program is null||boundary is null||string.IsNullOrWhiteSpace(side)||string.IsNullOrWhiteSpace(unavailableReason)||
   !TradingInstant.IsSupported(valuationAt)||!TradingInstant.IsSupported(knownAt))throw Error("Select retained terms, explicit temporal coordinates and the valuation refusal.");
  var guards=program.Document.Instructions.Where(i=>i.Output.Operation=="output_guard").ToArray();
  int Count(ScheduleDefinitionRow row)=>row.Grid is {} grid?program.Document.Grids[grid].CellCount:1;
  var rows=ImmutableArray.CreateBuilder<ScheduleInspectionRow>();var position=0;var total=guards.Sum(i=>Count(i.Output));
  foreach(var instruction in guards)
  {
   ct.ThrowIfCancellationRequested();var row=instruction.Output;var count=Count(row);var first=position;position=checked(position+count);
   if(position<=offset||rows.Count==limit)continue;
   for(var cell=Math.Max(0,offset-first);cell<count&&rows.Count<limit;cell++)
   {
    ct.ThrowIfCancellationRequested();
    var window=Geometry(program.Document,row,cell,boundary);
    var payment=row.Grid is null||row.Parameters["lifecycle"]=="interval_payment_v1";
    var kind=payment?ComponentKind.Payment:row.Parameters["lifecycle"]=="certificate_holding_v1"&&window.End<=valuationAt?ComponentKind.Holding:ComponentKind.Delivery;
    rows.Add(new($"{row.Ordinal}:{cell}",row.Operation,kind.ToString(),window.From,window.To,window.Start,window.End,row.ObservationFrom,row.ObservationTo,row.ValueAt,
     null,null,null,null,null,null,[],new ScheduleInspectionLifecycle(Delivery(window.Start,window.End,valuationAt),payment?row.ValueAt<=valuationAt?"Due":"NotDue":null,
      payment?"NotTracked":null,null,null,unavailableReason){Fulfillment=payment?null:"NotTracked"})
      {OperatingDate=window.OperatingDate,Currency=row.Currency,QuantitySpecification=Operand(program.Document,Operand(program.Document,instruction,"amount"),"quantity").Output.Unit});
   }
  }
  return new(side,program.Document.ScheduleManifest,valuationAt,knownAt,offset,total,rows.ToImmutable());
 }
}
