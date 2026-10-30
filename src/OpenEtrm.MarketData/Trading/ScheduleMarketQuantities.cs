using System.Collections.Immutable;
using OpenEtrm.MarketData.Power;

namespace OpenEtrm.MarketData.Trading;

public sealed partial class ScheduleMarketSources
{
    private ImmutableArray<AssetVolume> Volumes(BookedSideInstruction instruction,ScheduleDefinitionGrid? grid,int[] cells)
    {
        var row=instruction.Output;var binding=instruction.Bindings.Single();
        if(binding.Kind!="quantity_source")throw new MarketDataException("side_input_port","Sibling quantities require the exact instrument composition adapter.");
        RequireNative(grid);
        var sourceKey=new VolumeSourceKey(binding.Definitions!.Value,binding.Code!);
        if(!reference!.Sources.TryGetValue(sourceKey,out var definition)||
            !reference.Assets.TryGetValue(definition.Asset,out var asset)||definition.Clock!=grid!.Clock||
            !reference.Quantities.TryGetValue(row.Unit!.Value,out var energy)||
            !read!.Series.TryGetValue(definition.ForecastSeries,out var forecast)||!read.Series.TryGetValue(definition.MeteredSeries,out var meter)||
            !Enum.TryParse<QuantityStage>(row.Parameters["stage"],out var stage)||!Enum.IsDefined(stage))
            throw Invalid("The stored quantity binding requires its exact source, asset, stage, units and series.");
        var periods=cells.Select(cell=>
        {
            var interval=grid.Interval(cell);var local=TimeZoneInfo.ConvertTime(interval.Start,asset.OperatingClock.Zone);
            return new PowerInterval(interval.Start,interval.End,interval.OperatingDate,interval.HourBeginning,interval.HourBeginning+1,
                interval.Minute,(int)local.Offset.TotalMinutes,interval.Occurrence,false);
        }).ToArray();
        return AssetVolumes.Compute(definition,asset,stage,periods,forecast,meter,volumeContext!,energy);
    }
    private (ScheduleNumber Value,ScheduleCapacityEvidence Evidence) Capacity(BookedSideInstruction instruction,ScheduleDefinitionGrid? grid,int cell)
    {
        RequireNative(grid);var row=instruction.Output;var binding=instruction.Bindings.Single();
        if(!reference!.Assets.TryGetValue(new(binding.Definitions!.Value,binding.Code!),out var asset)||asset.Clock!=grid!.Clock||asset.CapacityQuantity.Key!=row.Unit)
            throw Invalid("Capacity must retain its exact asset, operating clock and quantity specification.");
        AssetDefinitions.Validate(asset,volumeContext!);
        var interval=grid.Interval(cell);var version=AssetDefinitions.At(asset,interval.Start,interval.End);
        var capacity=row.Parameters["measure"] switch
        {
            "capacity_basis_v1"=>version.CapacityBasis,
            "accredited_capacity_v1"=>version.AccreditedCapacity??throw new MarketDataException("asset_version","Accredited capacity must be explicit."),
            _=>throw Invalid("The stored capacity measure is unsupported.")
        };
        if(row.Parameters["measure"]=="accredited_capacity_v1"&&
            (interval.Start!=asset.OperatingClock.StartOfDay(interval.OperatingDate).ToUniversalTime()||
             interval.End!=asset.OperatingClock.StartOfDay(interval.OperatingDate.AddDays(1)).ToUniversalTime()))
            throw Invalid("Accredited capacity requires a whole operating day.");
        return (ScheduleNumber.Parse(capacity.ToString()),new(asset.Key,version,interval,row.Parameters["measure"]));
    }
}
