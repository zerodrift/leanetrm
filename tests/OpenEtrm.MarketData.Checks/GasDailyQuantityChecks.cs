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
  GasQuantityObservation Observation(string kind,string status,string amount)=>new(Guid.NewGuid(),kind,status,ExactQuantity.FromQuantity(new(ExactDecimal.Parse(amount),unit)),"Synthetic allocation evidence");
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
  Console.WriteLine("PASS daily gas quantities: explicit selection, missing billing refusal, zero, finality, causal evidence and exactly-once normalization");
 }
 static void Equal(ExactQuantity q,string text){if(q.ToQuantity().Amount.ToString()!=text)throw new Exception($"Expected {text}, got {q.Numerator}/{q.Denominator}");}
 static void Reject(Action action){try{action();}catch(MarketDataException e)when(e.Code=="gas_quantity_selection"){return;}throw new Exception("Expected quantity-selection refusal");}
}
