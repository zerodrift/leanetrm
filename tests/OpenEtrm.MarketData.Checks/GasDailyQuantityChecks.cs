using System.Collections.Immutable;
using OpenEtrm.MarketData;
using OpenEtrm.MarketData.Gas;
using OpenEtrm.MarketData.Trading;
using OpenEtrm.Shared.Numerics;

internal static class GasDailyQuantityChecks
{
 internal static void Run()
 {
  var f=new CommodityLookupFixture(1);var unit=f.Quote.Quantity;
  var start=new DateTimeOffset(2021,1,1,6,0,0,TimeSpan.Zero);var end=start.AddDays(1);
  var contract=new GasDailyContract("gas/0/quantity",0,0,new DateOnly(2021,1,1),start,end,ExactQuantity.FromQuantity(new(ExactDecimal.Parse("1000"),unit)));
  GasQuantityObservation Observation(string kind,string status,string amount)=>new(Guid.NewGuid(),kind,status,ExactQuantity.FromQuantity(new(ExactDecimal.Parse(amount),unit)),"Synthetic allocation evidence","manual_daily_v1");
  var nominated=Observation("nominated","provisional","990");var scheduled=Observation("scheduled","provisional","985");var allocated=Observation("allocated","final","980");
  var result=GasDailyQuantitySelection.Select(contract,[nominated,scheduled,allocated],"operational_projection_v1",end,new ExactRatio(1000,1));
  Equal(result.Quantity,"980");if(result.RevisionId!=allocated.Id)throw new Exception("Selected quantity lost its source revision.");
  Equal(result.NormalizedQuantity,"0.98");Equal(result.NormalizedQuantity.Scale(new ExactRatio(1000,1)),"980");
  Equal(GasDailyQuantitySelection.Select(contract,[nominated,scheduled],"operational_projection_v1",start,new ExactRatio(1000,1)).Quantity,"985");
  Equal(GasDailyQuantitySelection.Select(contract,[],"operational_projection_v1",start,new ExactRatio(1000,1)).Quantity,"1000");
  Reject(()=>GasDailyQuantitySelection.Select(contract,[scheduled],"final_allocated_billing_v1",end,new ExactRatio(1000,1)));
  Reject(()=>GasDailyQuantitySelection.Select(contract,[allocated],"final_allocated_billing_v1",start,new ExactRatio(1000,1)));
  Equal(GasDailyQuantitySelection.Select(contract,[allocated],"final_allocated_billing_v1",end,new ExactRatio(1000,1)).Quantity,"980");
  Equal(GasDailyQuantitySelection.Select(contract,[allocated with{Quantity=ExactQuantity.FromQuantity(new(default,unit))}],"final_allocated_billing_v1",end,new ExactRatio(1000,1)).Quantity,"0");
  Reject(()=>GasDailyQuantitySelection.Select(contract,[allocated,allocated with{Id=Guid.NewGuid()}],"operational_projection_v1",end,new ExactRatio(1000,1)));
  Reject(()=>GasDailyQuantitySelection.Select(contract,[allocated],"unknown",end,new ExactRatio(1000,1)));
  Equal(GasDailyQuantitySelection.Select(contract,[allocated with{Status="withdrawn"}],"operational_projection_v1",end,new ExactRatio(1000,1)).Quantity,"1000");
  Reject(()=>GasDailyQuantitySelection.Select(contract,[allocated],"final_facility_allocated_billing_v1",end,new ExactRatio(1000,1)));
  var facility=allocated with{Source="facility_allocation_adoption_v1"};
  Equal(GasDailyQuantitySelection.Select(contract,[facility],"final_facility_allocated_billing_v1",end,new ExactRatio(1000,1)).Quantity,"980");
  Reject(()=>GasDailyQuantitySelection.Select(contract,[facility with{Status="withdrawn"}],"final_facility_allocated_billing_v1",end,new ExactRatio(1000,1)));
  ValuationSelection(contract,start,end,Observation);
  Console.WriteLine("PASS daily gas quantities: explicit selection, missing billing refusal, zero, finality, causal evidence and exactly-once normalization");
 }
 static void ValuationSelection(GasDailyContract contract,DateTimeOffset start,DateTimeOffset end,Func<string,string,string,GasQuantityObservation> observation)
 {
  const string policy="delivery_schedule_valuation_v1";var scale=new ExactRatio(1000,1);
  GasQuantityObservation Manual(string kind,string status,string amount)=>observation(kind,status,amount);
  GasQuantityObservation Facility(string status,string amount)=>Manual("allocated",status,amount) with{Source="facility_allocation_adoption_v1"};
  GasSelectedQuantity Pick(params GasQuantityObservation[] heads)=>GasDailyQuantitySelection.Select(contract,[..heads],policy,end,scale);
  var scheduled=Manual("scheduled","provisional","1050");var final=Manual("allocated","final","980");var provisional=Manual("allocated","provisional","970");
  Equal(Pick(final,scheduled).Quantity,"980");
  Equal(Pick(provisional,scheduled).Quantity,"970");
  Equal(Pick(Manual("nominated","provisional","990"),Manual("confirmed","provisional","988"),scheduled).Quantity,"1050");
  var fallback=Pick(Manual("nominated","provisional","990"),Manual("confirmed","provisional","988"));
  Equal(fallback.Quantity,"1000");if(fallback.RevisionId is not null)throw new Exception("Contract fallback must not name a record.");
  Equal(Pick(scheduled,final with{Status="withdrawn"}).Quantity,"1050");
  Equal(Pick(Manual("allocated","final","0")).Quantity,"0");
  Equal(Pick(final,Facility("withdrawn","0")).Quantity,"980");
  Equal(Pick(final with{Status="withdrawn"},Facility("final","975")).Quantity,"975");
  Equal(Pick(final with{Status="withdrawn"},Facility("withdrawn","0"),scheduled).Quantity,"1050");
  Equal(Pick(provisional,Facility("final","960")).Quantity,"960");
  Equal(GasDailyQuantitySelection.Select(contract,[provisional,scheduled],policy,start,scale).Quantity,"970");
  Reject(()=>GasDailyQuantitySelection.Select(contract,[final],policy,start,scale));
  Reject(()=>Pick(final,Facility("final","975")));
  Reject(()=>Pick(provisional,Facility("provisional","975")));
  Reject(()=>Pick(final,final with{Id=Guid.NewGuid()}));
  Reject(()=>Pick(Facility("provisional","970") with{Kind="scheduled"}));
  var agreed=Facility("final","980");var selected=Pick(final,agreed);
  Equal(selected.NormalizedQuantity,"0.98");
  if(!selected.Provenance.Select(p=>p.Id).ToHashSet().SetEquals([final.Id,agreed.Id]))throw new Exception("Equal winners must retain both evidence references.");
  if(!selected.Provenance.SequenceEqual(Pick(agreed,final).Provenance))throw new Exception("Input order must not choose provenance.");
  if(selected.Status!="final"||Pick(provisional).Status!="provisional")throw new Exception("The winning allocation rank must be retained.");
  Console.WriteLine("PASS valuation gas quantities: ranked sources, independent withdrawals, zero and conflicting evidence");
 }
 static void Equal(ExactQuantity q,string text){if(q.ToQuantity().Amount.ToString()!=text)throw new Exception($"Expected {text}, got {q.Numerator}/{q.Denominator}");}
 static void Reject(Action action){try{action();}catch(MarketDataException e)when(e.Code=="gas_quantity_selection"){return;}throw new Exception("Expected quantity-selection refusal");}
}
