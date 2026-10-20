using OpenEtrm.MarketData;
using OpenEtrm.MarketData.Power;

internal static class PowerIntervalQuantityChecks
{
 public static int Run()
 {
  int passed=0;
  void Check(string label,Action action){action();passed++;Console.WriteLine("PASS power interval quantity: "+label);}
  void Equal(string actual,string expected){if(actual!=expected)throw new Exception($"Expected {expected}; got {actual}.");}
  void Reject(string code,Action action){try{action();}catch(MarketDataException e)when(e.Code==code){return;}throw new Exception("Expected "+code);}
  var revision=new ReferenceRevision(Guid.NewGuid(),Guid.NewGuid());ReferenceKey R(string code)=>new(revision,code);
  var energy=new QuantitySpecification(R("energy_quantity"),R("power_product"),R("delivered_energy"),R("megawatt_hour"),R("energy"));
  var power=new QuantitySpecification(R("power_quantity"),R("power_product"),R("delivery_rate"),R("megawatt"),R("power"));
  var index=new PowerIndex(R("index"),R("usd"),energy.Unit,energy.Key,"Synthetic total","Real-time");
  var utc=PowerClock.Capture(TimeZoneInfo.Utc);
  var periods=PowerHours.Compile(new(2026,10,20),new(2026,10,21),utc,TimeOfUse.All(15),[],15).Take(4).ToArray();
  PowerIntervalQuantity Row(PowerInterval p,string amount,QuantitySpecification specification,string? shape)=>
   new(p.Start,p.End,new(ExactDecimal.Parse(amount),specification.Key),shape is null?null:ExactDecimal.Parse(shape));
  PowerFixing[] Prices(PowerInterval[] selected,string[] prices)=>selected.Select((p,n)=>new PowerFixing(p.Start,p.End,index,ExactDecimal.Parse(prices[n]),null)).ToArray();
  PowerQuantitySettlement Settle(PowerInterval[] selected,PowerIntervalQuantity[] rows,QuantitySpecification specification,string[] prices)=>
   PowerSettlement.Calculate(PowerIntervalQuantities.Compile(selected,rows,specification,energy),energy,Prices(selected,prices),index,PowerSettlementPolicy.Preview);

  Check("quarter-hour MW applies each explicit shape and duration once",()=>{
   var rows=periods.Select((p,n)=>Row(p,"4",power,new[]{"1","2","0.5","1"}[n])).ToArray();
   var quantities=PowerIntervalQuantities.Compile(periods,rows.Reverse().ToArray(),power,energy);
   foreach(var (actual,expected) in quantities.Select((q,n)=>(q.Energy.ToQuantity().Amount.ToString(),new[]{"1","2","0.5","1"}[n])))Equal(actual,expected);
   var result=Settle(periods,rows,power,["10","-20","30","60"]);
   Equal(result.Energy.ToQuantity().Amount.ToString(),"4.5");Equal(result.Cash.Amount.ToString(),"45");
   Equal(result.Summary.AveragePrice,"20");Equal(result.Summary.Hours,"1");
  });
  Check("already-shaped MWh is not integrated or shaped again",()=>{
   var rows=periods.Select((p,n)=>Row(p,(n+1).ToString(),energy,null)).ToArray();
   var result=Settle(periods,rows,energy,["10","-20","30","60"]);
   Equal(result.Energy.ToQuantity().Amount.ToString(),"10");Equal(result.Cash.Amount.ToString(),"300");
   foreach(var shape in new[]{"0","1","2"})Reject("power_quantity",()=>PowerIntervalQuantities.Compile(periods,rows.Select(q=>q with {Shape=ExactDecimal.Parse(shape)}).ToArray(),energy,energy));
   Reject("power_quantity",()=>PowerIntervalQuantities.Compile(periods,periods.Select(p=>Row(p,"4",power,null)).ToArray(),power,energy));
  });
  Check("repeated local hour remains two distinct shaped MWh deliveries",()=>{
   var clock=PowerClock.Capture(TimeZoneInfo.FindSystemTimeZoneById("America/New_York"));
   var repeated=PowerHours.Compile(new(2026,11,1),new(2026,11,2),clock,TimeOfUse.All(60),[],60).Where(p=>p.HourBeginning==1).ToArray();
   if(repeated.Length!=2 || repeated[0].Occurrence!=1 || repeated[1].Occurrence!=2)throw new Exception("Expected both repeated local-hour instances.");
   var result=Settle(repeated,[Row(repeated[0],"3",energy,null),Row(repeated[1],"5",energy,null)],energy,["-20","30"]);
   Equal(result.Energy.ToQuantity().Amount.ToString(),"8");Equal(result.Cash.Amount.ToString(),"90");
  });
  Check("signed MW and prices follow 23/25 actual hours at hourly and quarter-hour resolution",()=>{
   var clock=PowerClock.Capture(TimeZoneInfo.FindSystemTimeZoneById("America/New_York"));
   foreach(var minutes in new[]{60,15})foreach(var fall in new[]{false,true})foreach(var sign in new[]{"2","-2"}){
    var date=fall?new DateOnly(2026,11,1):new DateOnly(2027,3,14);
    var selected=PowerHours.Compile(date,date.AddDays(1),clock,TimeOfUse.All(minutes),[],minutes);
    bool Special(PowerInterval p)=>fall?p.HourBeginning==1&&p.Occurrence==2:p.HourBeginning==3;
    var rows=selected.Select(p=>Row(p,sign,power,fall&&Special(p)?"2":"1")).ToArray();
    var result=Settle(selected,rows,power,selected.Select(p=>Special(p)?"-50":"10").ToArray());
    Equal(result.Energy.ToQuantity().Amount.ToString(),(sign=="-2"?"-":"")+(fall?"52":"46"));
    Equal(result.Cash.Amount.ToString(),(sign=="-2"?"-":"")+(fall?"280":"340"));
   }
  });
  Check("exact products defer fractional rounding until aggregate cash",()=>{
   var rows=periods.Select(p=>Row(p,"4",power,"1")).ToArray();
   var result=Settle(periods,rows,power,["0.005","0.005","0.005","0.005"]);
   Equal(result.Summary.ExactAmountNumerator,"1");Equal(result.Summary.ExactAmountDenominator,"50");Equal(result.Cash.Amount.ToString(),"0.02");
   var tiny=PowerIntervalQuantities.Compile(periods,[..periods.Select(p=>Row(p,"0.000000000000000001",power,"0.1"))],power,energy);
   Equal(tiny[0].Energy.Numerator,"1");Equal(tiny[0].Energy.Denominator,"40000000000000000000");
  });
  Check("zero energy still requires complete distinct exact UTC quantities and prices",()=>{
   var rows=periods.Select(p=>Row(p,"0",power,"0")).ToArray();
   Equal(Settle(periods,rows,power,["10","-20","30","60"]).Cash.Amount.ToString(),"0");
   foreach(var invalid in new[]{rows[..^1],rows.Append(rows[0]).ToArray(),rows.Select((q,n)=>n==1?rows[0]:q).ToArray(),
    rows.Select((q,n)=>n==0?q with {End=q.End.AddMinutes(1)}:q).ToArray(),
    rows.Select((q,n)=>n==0?q with {Start=q.Start.ToOffset(TimeSpan.FromHours(1)),End=q.End.ToOffset(TimeSpan.FromHours(1))}:q).ToArray()})
    Reject("settlement_coverage",()=>PowerIntervalQuantities.Compile(periods,invalid,power,energy));
   Reject("settlement_coverage",()=>PowerIntervalQuantities.Compile([],[],power,energy));
   Reject("settlement_coverage",()=>PowerIntervalQuantities.Compile(periods.Append(periods[0]).ToArray(),rows.Append(rows[0]).ToArray(),power,energy));
   var compiled=PowerIntervalQuantities.Compile(periods,rows,power,energy);
   Reject("settlement_coverage",()=>PowerSettlement.Calculate(compiled,energy,Prices(periods,["10","-20","30","60"])[..^1],index,PowerSettlementPolicy.Preview));
  });
  Check("exact units, basis, commodity and all embedded revision pins are required",()=>{
   var rows=periods.Select(p=>Row(p,"4",power,"1")).ToArray();
   var foreign=new ReferenceRevision(Guid.NewGuid(),Guid.NewGuid());
   foreach(var definition in new[]{power with {Dimension=R("energy")},power with {Unit=R("kilowatt")},power with {Commodity=R("other_product")},
    power with {MeasurementBasis=new(foreign,"delivery_rate")},power with {Key=new(foreign,"power_quantity")},power with {Dimension=new(revision,"")}})
    Reject("power_quantity",()=>PowerIntervalQuantities.Compile(periods,rows,definition,energy));
   Reject("power_quantity",()=>PowerIntervalQuantities.Compile(periods,rows.Select((q,n)=>n==0?q with {Quantity=new(q.Quantity.Amount,energy.Key)}:q).ToArray(),power,energy));
   var other=energy with {Key=R("another_energy_basis")};
   Reject("power_quantity",()=>PowerIntervalQuantities.Compile(periods,periods.Select(p=>Row(p,"1",other,null)).ToArray(),other,energy));
  });
  Check("array settlement rejects an incompatible energy basis or numerical policy",()=>{
   var compiled=PowerIntervalQuantities.Compile(periods,periods.Select(p=>Row(p,"1",energy,null)).ToArray(),energy,energy);
   var prices=Prices(periods,["10","-20","30","60"]);
   Reject("settlement_index",()=>PowerSettlement.Calculate(compiled,energy with {Key=R("other_basis")},prices,index,PowerSettlementPolicy.Preview));
   Reject("settlement_policy",()=>PowerSettlement.Calculate(compiled,energy,prices,index,PowerSettlementPolicy.Preview with {AmountScale=3}));
   Reject("power_quantity",()=>PowerSettlement.Calculate(compiled,energy,prices,index,null!));
  });
  return passed;
 }
}
