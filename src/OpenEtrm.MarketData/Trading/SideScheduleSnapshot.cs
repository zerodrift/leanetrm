using System.Collections.Immutable;
using OpenEtrm.Shared.Numerics;

namespace OpenEtrm.MarketData.Trading;

// An evaluated Side owns its values. Inspection only projects those values;
// storage admission, valuation policies and current authority belong to the caller.
public abstract partial class SideScheduleSnapshot
{
 public Guid SideId {get;}
 public Guid SnapshotId {get;}
 public Guid ContextId {get;}
 public string Manifest {get;}
 public DateTimeOffset ValuationAt {get;}
 public DateTimeOffset KnownAt {get;}
 protected SideScheduleSnapshot(Guid sideId,Guid snapshotId,Guid contextId,string manifest,DateTimeOffset valuationAt,DateTimeOffset knownAt)
 {
  if(sideId==Guid.Empty||snapshotId==Guid.Empty||contextId==Guid.Empty||string.IsNullOrWhiteSpace(manifest)||!TradingInstant.IsSupported(valuationAt)||!TradingInstant.IsSupported(knownAt))
   throw new MarketDataException("side_schedule_inspection","Select an exact Side, retained schedule and resolved temporal coordinates.");
  SideId=sideId;SnapshotId=snapshotId;ContextId=contextId;Manifest=manifest;ValuationAt=valuationAt;KnownAt=knownAt;
 }
 public abstract ScheduleInspectionPage Read(int offset,int limit,CancellationToken ct=default);
 public abstract ScheduleCalculationPage Calculation(string contribution,int offset,int limit,CancellationToken ct=default);

 public static SideScheduleSnapshot FromRows(SideValuation value,ImmutableArray<ScheduleRowValue> rows,string manifest,ExactDecimal scale,SignedRatio coefficient,Func<DateOnly,DateTimeOffset> boundary)=>
  new Evaluated(value,rows,manifest,scale,coefficient,boundary);
 public static SideScheduleSnapshot FromProgramValue(SideValuation value,BookedSideEvaluation execution,BookedSideProgram program,string manifest,ExactDecimal scale,SignedRatio coefficient,Func<DateOnly,DateTimeOffset> boundary)
 {
  if(!value.Context.IsBaseScenario||manifest!=program.Document.ScheduleManifest||execution.Context.ContextId!=value.Snapshot.Knowledge?.ContextId||execution.Context.ValuationAt!=value.Snapshot.ValuationAt||execution.Context.MarketAsOf!=value.Snapshot.MarketCutoff||execution.Context.KnownAt!=value.Snapshot.Knowledge?.KnownAt)
   throw new MarketDataException("side_schedule_inspection","The program values require the Side valuation's exact schedule and context.");
  return FromProgram(value.SideId,value.Snapshot.Id,new(value.SideId.ToString(),execution,program,scale,coefficient,[]),boundary);
 }
 public static SideScheduleSnapshot FromProgram(Guid side,Guid snapshot,SideViewInput input,Func<DateOnly,DateTimeOffset> boundary)
 {
  if(input.Evaluation.SourceReceipt is {} receipt&&receipt.SideId!=side)throw new MarketDataException("side_schedule_inspection","Source evidence belongs to another Side.");
  return new ProgramValues(side,snapshot,input,boundary);
 }

 public static SideScheduleSnapshot Plan(Guid side,Guid snapshot,Guid context,string label,BookedSideProgram program,Func<DateOnly,DateTimeOffset> boundary,DateTimeOffset at,DateTimeOffset known,string refusal)=>new Unavailable(side,snapshot,context,label,program,boundary,at,known,refusal);
 sealed class Unavailable(Guid side,Guid snapshot,Guid context,string label,BookedSideProgram program,Func<DateOnly,DateTimeOffset> boundary,DateTimeOffset at,DateTimeOffset known,string refusal):SideScheduleSnapshot(side,snapshot,context,program.Document.ScheduleManifest,at,known)
 {
  public override ScheduleInspectionPage Read(int offset,int limit,CancellationToken ct=default)=>SideScheduleInspection.Plan(label,program,boundary,ValuationAt,KnownAt,offset,limit,refusal,ct);
  public override ScheduleCalculationPage Calculation(string key,int offset,int limit,CancellationToken ct=default)=>throw new MarketDataException("side_schedule_inspection",$"The selected profile cannot value this contribution: {refusal}.");
 }
 sealed class ProgramValues(Guid side,Guid snapshot,SideViewInput input,Func<DateOnly,DateTimeOffset> boundary):SideScheduleSnapshot(side,snapshot,input.Evaluation.Context.ContextId,input.Program.Document.ScheduleManifest,input.Evaluation.Context.ValuationAt,input.Evaluation.Context.KnownAt)
 {
  public override ScheduleInspectionPage Read(int offset,int limit,CancellationToken ct=default)=>SideScheduleInspection.Read(input,boundary,offset,limit,ct);
  public override ScheduleCalculationPage Calculation(string key,int offset,int limit,CancellationToken ct=default)=>SideScheduleInspection.Calculation(input,boundary,key,offset,limit,ct);
 }
 sealed class Evaluated:SideScheduleSnapshot
 {
  readonly ImmutableArray<ScheduleRowValue> rows;readonly ExactDecimal scale;readonly SignedRatio coefficient;readonly Func<DateOnly,DateTimeOffset> boundary;
  internal Evaluated(SideValuation value,ImmutableArray<ScheduleRowValue> rows,string manifest,ExactDecimal scale,SignedRatio coefficient,Func<DateOnly,DateTimeOffset> boundary)
   :base(value.SideId,value.Snapshot.Id,value.Snapshot.Knowledge?.ContextId??throw new MarketDataException("side_schedule_inspection","The valuation requires a certified knowledge context."),manifest,value.Snapshot.ValuationAt,value.Snapshot.Knowledge?.KnownAt??throw new MarketDataException("side_schedule_inspection","Inspection requires the valuation's certified knowledge context."))
  {
   if(!value.Context.IsBaseScenario||rows.IsDefault||rows.Any(r=>r.SideId!=value.SideId)||boundary is null||scale.CompareTo(default)<=0||coefficient.Denominator.Sign<=0||coefficient.Numerator.IsZero)
    throw new MarketDataException("side_schedule_inspection","Select base-scenario native Side rows and the exact signed holding scale.");
   this.rows=rows;this.scale=scale;this.coefficient=coefficient;this.boundary=boundary;
  }
  public override ScheduleInspectionPage Read(int offset,int limit,CancellationToken ct=default)
  {
   SideScheduleInspection.RequirePage(offset,limit);ct.ThrowIfCancellationRequested();
   var factor=scale.Power(1);var signed=new SignedRatio(factor.Numerator,factor.Denominator)*coefficient;
   return new(SideId.ToString(),Manifest,ValuationAt,KnownAt,offset,rows.Length,[..rows.Skip(offset).Take(limit).Select(row=>{
    ct.ThrowIfCancellationRequested();return Scale(SideScheduleInspection.FromValue(row,ValuationAt,boundary),signed);
   })]);
  }
  public override ScheduleCalculationPage Calculation(string key,int offset,int limit,CancellationToken ct=default)
  {
   SideScheduleInspection.RequirePage(offset,limit);ct.ThrowIfCancellationRequested();
   var matches=rows.Where(r=>r.Row.Ordinal.ToString(System.Globalization.CultureInfo.InvariantCulture)==key).ToArray();
   if(matches is not [var row])throw new MarketDataException("side_schedule_inspection","Select one exact evaluated contribution.");
   var cells=NativeCalculation(row,ValuationAt,boundary,ct);
   return new(SideId.ToString(),Manifest,key,"unit_side_v1",scale.ToString(),coefficient.Numerator.ToString(System.Globalization.CultureInfo.InvariantCulture),coefficient.Denominator.ToString(System.Globalization.CultureInfo.InvariantCulture),offset,cells.Length,[..cells.Skip(offset).Take(limit)]);
  }
 }
 internal static ScheduleInspectionRow Scale(ScheduleInspectionRow row,SignedRatio scale)
 {
  string? Apply(string? value){if(value is null)return null;var(n,d)=ExactNumberText.Parse(value);return ExactNumberText.Format(new SignedRatio(n,d)*scale);}
  return row with{Quantity=Apply(row.Quantity),Amount=Apply(row.Amount),PresentValue=Apply(row.PresentValue),Paid=Apply(row.Paid),Outstanding=Apply(row.Outstanding)};
 }
}

public sealed record SideScheduleIdentity(Guid SideId,string Manifest);
public static class InstrumentScheduleInspection
{
 public static ImmutableArray<SideScheduleSnapshot> InspectSchedules(this InstrumentValuation value,ImmutableArray<SideScheduleIdentity> schedules,ExactDecimal scale,Func<DateOnly,DateTimeOffset> boundary)
 {
  if(value is null||schedules.IsDefault||value.Sides.Length!=schedules.Length||!value.Sides.Select(s=>s.SideId).SequenceEqual(schedules.Select(s=>s.SideId)))
   throw new MarketDataException("side_schedule_inspection","Inspect the exact valued instrument's complete Side memberships and retained schedules.");
  return [..value.Sides.Select((side,slot)=>side.Value.InspectSchedule(schedules[slot].Manifest,scale,
   new SignedRatio(side.QuantityRatio.Numerator,side.QuantityRatio.Denominator)*new SignedRatio(side.Coefficient.ScaledValue,ExactDecimal.WorkingScale),boundary))];
 }
}
