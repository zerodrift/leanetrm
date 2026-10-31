using System.Collections.Immutable;
using System.Numerics;
using OpenEtrm.MarketData.Trading;
using OpenEtrm.Shared.Numerics;

namespace OpenEtrm.MarketData.Gas;

public sealed record GasDailyContract(string Root,int Instruction,int Grid,DateOnly Day,DateTimeOffset Start,DateTimeOffset End,ExactQuantity Quantity);
public sealed record GasQuantityObservation(Guid Id,string Kind,string Status,ExactQuantity Quantity,string Evidence);
public sealed record GasSelectedQuantity(ExactQuantity Quantity,ExactQuantity NormalizedQuantity,string Kind,Guid? RevisionId,string? Evidence);

// A typed quantity-input consumer, not an alternative pricing or payment engine.
// Inputs are trade-owned absolute quantities; the retained unit schedule stays immutable.
public static class GasDailyQuantitySelection
{
 public const string ProjectionPolicy="operational_projection_v1",BillingPolicy="final_allocated_billing_v1";
 public static ImmutableArray<GasDailyContract> ReadContract(BookedSideProgram program,ExactRatio scale)
 {
  if(program.Document.Family!="gas"||scale is null||scale.Numerator<=0)throw Invalid("Select a retained gas program and its positive absolute membership scale.");
  var rows=ImmutableArray.CreateBuilder<GasDailyContract>();
  foreach(var root in program.Document.Roots.Where(r=>r.Measure=="schedule_quantity"))
  {
   var instruction=program.Document.Instructions[root.Row].Output;
   if(instruction.Operation!="quantity_literal"||instruction.Grid is not {} grid||instruction.Unit is not {} unit)throw Invalid("This profile requires retained literal daily gas quantities.");
   var g=program.Document.Grids[grid];if(g.CellCount!=1)throw Invalid("This profile requires one retained gas day per quantity root.");
   var interval=g.Interval(0);var q=new ExactQuantity(BigInteger.Parse(instruction.Parameters["numerator"]),BigInteger.Parse(instruction.Parameters["denominator"]),unit).Scale(scale);
   rows.Add(new(root.Name,root.Row,grid,interval.OperatingDate,interval.Start,interval.End,q));
  }
  if(rows.Count is <1 or >31||rows.Select(r=>r.Day).Distinct().Count()!=rows.Count)throw Invalid("Select a unique retained daily gas grid of one to 31 days.");
  return [..rows.OrderBy(r=>r.Start)];
 }
 public static GasSelectedQuantity Select(GasDailyContract contract,ImmutableArray<GasQuantityObservation> heads,string policy,DateTimeOffset at,ExactRatio scale)
 {
  if(contract is null||heads.IsDefault||scale is null||scale.Numerator<=0||policy is not(ProjectionPolicy or BillingPolicy))throw Invalid("Select an explicit supported quantity policy and absolute membership scale.");
  if(heads.Any(h=>h is null||h.Id==Guid.Empty||h.Kind is not("nominated" or "confirmed" or "scheduled" or "allocated")||h.Status is not("provisional" or "final" or "withdrawn")||h.Status=="final"&&(h.Kind!="allocated"||at<contract.End)||h.Quantity.Specification!=contract.Quantity.Specification||BigInteger.Parse(h.Quantity.Numerator)<0||string.IsNullOrWhiteSpace(h.Evidence))||heads.GroupBy(h=>h.Kind).Any(g=>g.Count()>1))throw Invalid("Supply one visible head per quantity kind, in the exact native unit, with source evidence.");
  var eligible=heads.Where(h=>h.Status!="withdrawn").ToArray();GasQuantityObservation? selected;
  if(policy==BillingPolicy)
  {
   selected=eligible.SingleOrDefault(h=>h.Kind=="allocated"&&h.Status=="final");
   if(at<contract.End||selected is null)throw Invalid("Billing requires a completed gas day and its final allocated quantity; no fallback is allowed.");
  }
  else selected=eligible.OrderByDescending(h=>h.Kind switch{"allocated"=>4,"scheduled"=>3,"confirmed"=>2,_=>1}).FirstOrDefault();
  var q=selected?.Quantity??contract.Quantity;
  return new(q,q.Scale(scale.Inverse()),selected?.Kind??"contractual",selected?.Id,selected?.Evidence);
 }
 static MarketDataException Invalid(string message)=>new("gas_quantity_selection",message);
}
