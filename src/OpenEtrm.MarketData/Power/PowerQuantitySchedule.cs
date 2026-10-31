using System.Collections.Immutable;
using System.Numerics;

namespace OpenEtrm.MarketData.Power;

public enum PowerQuantityBasis { FlatPower=1,TotalEnergy }
public enum PowerAllocation { FlatRate=1 }
public sealed record PowerEnergyInterval(PowerInterval Interval,ExactQuantity Energy);

// A delivery-quantity rule for the unsaved power consumer. Booked side persistence
// must retain these same clock, calendar, mask and quantity inputs before reuse.
public sealed class PowerQuantitySchedule
{
 public const string Implementation="selected_interval_power_quantity_v1";
 public Quantity Input { get; }
 public QuantitySpecification InputDefinition { get; }
 public QuantitySpecification EnergyDefinition { get; }
 public PowerQuantityBasis Basis { get; }
 public PowerAllocation? Allocation { get; }
 public DateOnly From { get; }
 public DateOnly To { get; }
 public PowerClock Clock { get; }
 public TimeOfUse Mask { get; }
 public ImmutableArray<DateOnly> Holidays { get; }
 public int IntervalMinutes { get; }
 public ImmutableArray<PowerEnergyInterval> Intervals { get; }
 public ExactQuantity Total { get; }
 public TimeSpan Duration { get; }
 public string Hours=>new AmountFormat(18,CashTieRule.ToEven,false,true).FormatFraction(Duration.Ticks,TimeSpan.TicksPerHour);

 private PowerQuantitySchedule(Quantity input,PowerQuantityBasis basis,QuantitySpecification inputDefinition,QuantitySpecification energyDefinition,
  DateOnly from,DateOnly to,PowerClock clock,TimeOfUse mask,ImmutableArray<DateOnly> holidays,int minutes,PowerAllocation? allocation)
 {
  if(input is null || !QuantityDefinitions.Coherent(inputDefinition) || !QuantityDefinitions.Coherent(energyDefinition) || clock is null || mask is null ||
    input.Specification!=inputDefinition.Key || !Enum.IsDefined(basis) ||
    energyDefinition.Unit.Code!="megawatt_hour" || energyDefinition.Dimension.Code!="energy" ||
    inputDefinition.Key.Revision!=energyDefinition.Key.Revision || inputDefinition.Commodity!=energyDefinition.Commodity ||
    basis==PowerQuantityBasis.FlatPower && (inputDefinition.Unit.Code!="megawatt" || inputDefinition.Dimension.Code!="power" || allocation is not null) ||
    basis==PowerQuantityBasis.TotalEnergy && (inputDefinition!=energyDefinition || allocation!=PowerAllocation.FlatRate))
   throw new MarketDataException("power_quantity","Select exact compatible MW/MWh specifications and an explicit total-energy allocation; a flat-MW rate needs no allocation choice.");
  var periods=PowerHours.Compile(from,to,clock,mask,holidays.ToArray(),minutes);
  if(periods.Length==0)throw new MarketDataException("settlement_coverage","The selected delivery rule has no intervals.");
  var ticks=periods.Sum(p=>(p.End-p.Start).Ticks);
  Input=input;InputDefinition=inputDefinition;EnergyDefinition=energyDefinition;Basis=basis;Allocation=allocation;
  From=from;To=to;Clock=clock;Mask=mask;Holidays=holidays;IntervalMinutes=minutes;Duration=TimeSpan.FromTicks(ticks);
  var scale=ExactDecimal.WorkingScale;
  BigInteger Divisor()=>scale*(basis==PowerQuantityBasis.FlatPower?TimeSpan.TicksPerHour:ticks);
  Intervals=periods.Select(p=>new PowerEnergyInterval(p,new(input.Amount.ScaledValue*(p.End-p.Start).Ticks,Divisor(),energyDefinition.Key))).ToImmutableArray();
  Total=new(input.Amount.ScaledValue*(basis==PowerQuantityBasis.FlatPower?ticks:TimeSpan.TicksPerHour),scale*TimeSpan.TicksPerHour,energyDefinition.Key);
 }

 public static PowerQuantitySchedule Compile(Quantity input,PowerQuantityBasis basis,QuantitySpecification inputDefinition,QuantitySpecification energyDefinition,
  DateOnly from,DateOnly to,PowerClock clock,TimeOfUse mask,DateOnly[] holidays,int intervalMinutes,PowerAllocation? allocation)=>
  new(input,basis,inputDefinition,energyDefinition,from,to,clock,mask,
   holidays is null?throw new MarketDataException("power_holidays","Supply an explicit holiday list."):holidays.ToImmutableArray(),intervalMinutes,allocation);
}
