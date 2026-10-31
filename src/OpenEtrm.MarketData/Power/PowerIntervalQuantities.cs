namespace OpenEtrm.MarketData.Power;

public sealed record PowerIntervalQuantity(DateTimeOffset Start,DateTimeOffset End,Quantity Quantity,ExactDecimal? Shape);

public static class PowerIntervalQuantities
{
 public const string Implementation="explicit_interval_power_quantity_v1";

 internal static bool IsEnergy(QuantitySpecification? definition)=>QuantityDefinitions.Coherent(definition) &&
  definition!.Unit.Code=="megawatt_hour" && definition.Dimension.Code=="energy";



 public static PowerEnergyInterval[] Compile(PowerInterval[] periods,PowerIntervalQuantity[] rows,
  QuantitySpecification inputDefinition,QuantitySpecification energyDefinition)
 {
  if(!QuantityDefinitions.Coherent(inputDefinition) || !IsEnergy(energyDefinition) ||
    inputDefinition.Key.Revision!=energyDefinition.Key.Revision || inputDefinition.Commodity!=energyDefinition.Commodity ||
    inputDefinition!=energyDefinition && (inputDefinition.Unit.Code!="megawatt" || inputDefinition.Dimension.Code!="power"))
   throw new MarketDataException("power_quantity","Select exact compatible MW or MWh interval quantity specifications.");
  if(periods is null || rows is null || periods.Length==0 || periods.Length>110000 || rows.Length!=periods.Length ||
    periods.Any(p=>p is null) || rows.Any(r=>r is null))
   throw new MarketDataException("settlement_coverage","Supply exactly one quantity per selected interval.");
  var isEnergy=inputDefinition==energyDefinition;
  foreach(var row in rows)
   if(row.Quantity is null || row.Quantity.Specification!=inputDefinition.Key || isEnergy && row.Shape is not null || !isEnergy && row.Shape is null)
    throw new MarketDataException("power_quantity","MW requires an explicit shape per interval; already-shaped MWh must not have another shape.");
  var byStart=IntervalAlignment.Align(periods,rows,r=>r.Start,r=>r.End,"settlement_coverage",
   "Quantity intervals must have distinct exact UTC boundaries.","Quantities must cover the selected UTC intervals without overlap.");
  var scale=ExactDecimal.WorkingScale;
  return periods.Select(p=>
  {
   var row=byStart[p.Start];
   var energy=isEnergy?ExactQuantity.FromQuantity(row.Quantity):new ExactQuantity(
    row.Quantity.Amount.ScaledValue*row.Shape!.Value.ScaledValue*(p.End-p.Start).Ticks,
    scale*scale*TimeSpan.TicksPerHour,energyDefinition.Key);
   return new PowerEnergyInterval(p,energy);
  }).ToArray();
 }
}
