using System.Collections.Immutable;
using OpenEtrm.Shared.Temporal;

namespace OpenEtrm.MarketData.Power;

public enum QuantityStage { Generated=1, Delivered }
public enum VolumeState { Forecast=1, Metered, Contractual, Estimated }
public enum PendingActualsPolicy { RequireActuals=1, EstimateUntilActual }
public enum VolumeRange { GenerationNonNegative=1 }
public sealed record AssetKey(DefinitionRevision Revision,string Code);
public sealed record VolumeSourceKey(DefinitionRevision Revision,string Code);
public sealed record AssetVersionKey(AssetKey Asset,DateTimeOffset From);
public sealed record AssetVersion(DateTimeOffset From,DateTimeOffset To,ExactDecimal CapacityBasis,ExactDecimal? AccreditedCapacity);
public sealed record GenerationAsset(AssetKey Key,ReferenceKey Location,QuantitySpecification CapacityQuantity,
    ReferenceKey Clock,PowerClock OperatingClock,ImmutableArray<AssetVersion> Versions);
public sealed record ForecastAdjustment(ExactDecimal? Factor,bool ReflectedInForecast);
public sealed record Degradation(ExactDecimal Rate,ContractYearBasis Basis,DateOnly BaseDate);
public sealed record MeterDeadline(string Version,int OffsetMinutes);
public sealed record VolumeSourceDefinition(VolumeSourceKey Key,AssetKey Asset,Guid ForecastSeries,QuantityStage ForecastStage,
    Guid MeteredSeries,QuantityStage MeteredStage,ExactDecimal Loss,ForecastAdjustment Availability,ForecastAdjustment Curtailment,
    Degradation? Degradation,bool DegradationReflected,VolumeRange Range,ReferenceKey Clock,int IntervalMinutes,MeterDeadline MeterDue);
public readonly record struct QuantityCell(DateTimeOffset Start,DateTimeOffset End,ExactDecimal Value,int Revision,DateTimeOffset KnownAt,Guid ContextId);
public sealed record QuantitySeries(Guid Id,string Kind,ReferenceKey Clock,int IntervalMinutes,ReferenceKey? Quantity,ImmutableArray<QuantityCell> Cells)
{
    public IntervalQuantityData? Data { get; init; }
    public IReadOnlyList<QuantityCell> Values=>Data is {} data
        ?Cells.IsDefaultOrEmpty && data.Values.IntervalMinutes==IntervalMinutes?data
            :throw new MarketDataException("volume_source","Supply one interval representation with the declared resolution.")
        :Cells;
}
public sealed record VolumeContext(DefinitionRevision Definitions,ReferenceRevision Reference,DateTimeOffset ValuationAt,
    DateTimeOffset KnownAt,PendingActualsPolicy Policy);
public sealed record AssetVolume(DeliveryInterval Interval,ExactQuantity Energy,VolumeState State,AssetKey Asset,VolumeSourceKey Source,
    AssetVersionKey AssetVersion,QuantityStage Stage,QuantityStage SourceStage,Guid Series,int Revision,DateTimeOffset KnownAt,
    Guid ContextId,DateTimeOffset? MeterDueAt,VolumeContext Context);

// Definitions and selected cells stay immutable. This consumer performs no data lookup or implicit source fallback.
public static class AssetVolumes
{
    public const string Implementation="asset_volume_v1";
    public const string DeadlineVersion="meter_due_v1";
    private static readonly ExactDecimal One=ExactDecimal.Parse("1");
    public static ImmutableArray<AssetVolume> Compute(VolumeSourceDefinition source,GenerationAsset asset,QuantityStage stage,
        IReadOnlyList<PowerInterval> intervals,QuantitySeries forecast,QuantitySeries metered,VolumeContext context,QuantitySpecification energy)=>
        Compute(source,asset,stage,intervals,forecast,metered,context,energy,new(context));

    internal static ImmutableArray<AssetVolume> Compute(VolumeSourceDefinition source,GenerationAsset asset,QuantityStage stage,
        IReadOnlyList<PowerInterval> intervals,QuantitySeries forecast,QuantitySeries metered,VolumeContext context,QuantitySpecification energy,
        QuantitySeriesIndexes indexes)
    {
        AssetDefinitions.Validate(asset,context);
        AssetDefinitions.Energy(asset.CapacityQuantity,energy,context.Reference);
        if(source.Key.Revision!=context.Definitions)throw new MarketDataException("ppa_definition_revision","The source must use the selected definition revision.");
        if(source.Clock.Revision!=context.Reference)throw new MarketDataException("ppa_reference_revision","The source clock must use the selected reference revision.");
        if(source.Asset!=asset.Key || source.Clock!=asset.Clock || string.IsNullOrWhiteSpace(source.Key.Code) ||
            !Enum.IsDefined(stage) || !Enum.IsDefined(source.ForecastStage) || !Enum.IsDefined(source.MeteredStage) ||
            source.Range!=VolumeRange.GenerationNonNegative || source.IntervalMinutes is not(15 or 60) ||
            source.ForecastSeries==Guid.Empty || source.MeteredSeries==Guid.Empty || source.ForecastSeries==source.MeteredSeries ||
            source.MeterDue is null || source.MeterDue.Version!=DeadlineVersion || source.MeterDue.OffsetMinutes is <0 or >129600 ||
            source.DegradationReflected!=(source.Degradation is null))
            throw Invalid("Supply the exact asset, clock, series, stages, range, adjustments and meter deadline.");
        if(source.Loss.CompareTo(default)<0 || source.Loss.CompareTo(One)>=0)throw new MarketDataException("volume_range","Stage loss must be in [0,1).");
        ValidateAdjustment(source.Availability);ValidateAdjustment(source.Curtailment);
        if(source.Degradation is {} degradation)
        {
            if(degradation.Rate.CompareTo(default)<0 || degradation.Rate.CompareTo(One)>=0)throw new MarketDataException("volume_range","Degradation must be in [0,1).");
            ContractYears.Exponent(degradation.BaseDate,degradation.BaseDate,degradation.Basis);
        }
        var forecasts=indexes.Resolve(forecast,source.ForecastSeries,"capacity_factor_forecast",null,source,asset,context);
        var meters=indexes.Resolve(metered,source.MeteredSeries,"metered_energy",energy.Key,source,asset,context);
        if(intervals.Count==0 || intervals.Count>35136 || intervals.Select(p=>p.Start).Distinct().Count()!=intervals.Count)
            throw new MarketDataException("volume_grid","Supply distinct native intervals within the supported monthly read bound.");
        var expected=intervals.GroupBy(p=>new DateOnly(p.OperatingDate.Year,p.OperatingDate.Month,1))
            .SelectMany(g=>PowerHours.Compile(g.Key,g.Key.AddMonths(1),asset.OperatingClock,TimeOfUse.All(source.IntervalMinutes),[],source.IntervalMinutes))
            .ToDictionary(p=>p.Start);
        if(intervals.Any(p=>!expected.TryGetValue(p.Start,out var retained) || retained!=p))
            throw new MarketDataException("volume_grid","Intervals must retain the asset clock's exact native boundaries and labels.");
        return [..intervals.Select(interval=>
        {
            var version=AssetDefinitions.At(asset,interval.Start,interval.End);
            var delivered=interval.End<=context.ValuationAt;
            DateTimeOffset due;
            try{due=interval.End.AddMinutes(source.MeterDue.OffsetMinutes);}
            catch(ArgumentOutOfRangeException){throw Invalid("The meter deadline exceeds supported time.");}
            QuantityCell cell;VolumeState state;QuantityStage inputStage;Guid series;
            ExactQuantity quantity;
            if(delivered && meters.TryGetValue(interval.Start,out var actual))
            {
                cell=actual;state=VolumeState.Metered;inputStage=source.MeteredStage;series=source.MeteredSeries;
                if(cell.Value.CompareTo(default)<0)throw new MarketDataException("volume_range","Generation meters must be nonnegative MWh.");
                quantity=ExactQuantity.FromQuantity(new(cell.Value,energy.Key));
            }
            else
            {
                if(delivered && context.ValuationAt>=due)throw new MarketDataException("meter_actual_missing",$"The meter for {interval.Start:O} is due at {due:O} and missing.");
                if(delivered && context.Policy==PendingActualsPolicy.RequireActuals)throw new MarketDataException("actuals_required","A delivered interval requires an eligible meter reading under this policy.");
                if(!forecasts.TryGetValue(interval.Start,out cell!))throw new MarketDataException("volume_missing","The selected forecast does not cover every required interval.");
                if(cell.Value.CompareTo(default)<0 || cell.Value.CompareTo(One)>0)throw new MarketDataException("volume_range","Forecast capacity factors must be in [0,1].");
                state=delivered?VolumeState.Estimated:VolumeState.Forecast;inputStage=source.ForecastStage;series=source.ForecastSeries;
                quantity=ExactQuantity.FromQuantity(new(cell.Value,energy.Key)).Scale(version.CapacityBasis)
                    .ScaleFraction((interval.End-interval.Start).Ticks,TimeSpan.TicksPerHour);
                if(!source.Availability.ReflectedInForecast)quantity=quantity.Scale(source.Availability.Factor!.Value);
                if(!source.Curtailment.ReflectedInForecast)quantity=quantity.Scale(One-source.Curtailment.Factor!.Value);
                if(source.Degradation is {} adjustment)
                {
                    var (n,d)=(One-adjustment.Rate).Power(ContractYears.Exponent(adjustment.BaseDate,interval.OperatingDate,adjustment.Basis));
                    quantity=quantity.ScaleFraction(n,d);
                }
            }
            if(cell.End!=interval.End)throw new MarketDataException("volume_grid","A quantity observation must match its entire native interval.");
            if(inputStage!=stage)
            {
                var remaining=One-source.Loss;
                quantity=inputStage==QuantityStage.Generated?quantity.Scale(remaining):quantity.ScaleFraction(ExactDecimal.WorkingScale,remaining.ScaledValue);
            }
            return new AssetVolume(interval.Delivery,quantity,state,asset.Key,source.Key,new(asset.Key,version.From),stage,inputStage,
                series,cell.Revision,cell.KnownAt,cell.ContextId,delivered?due:null,context);
        })];
    }
    private static void ValidateAdjustment(ForecastAdjustment value)
    {
        if(value is null || value.ReflectedInForecast!=(value.Factor is null))throw Invalid("An adjustment is explicitly applied or already reflected, never both.");
        if(value.Factor is {} factor && (factor.CompareTo(default)<0 || factor.CompareTo(One)>0))
            throw new MarketDataException("volume_range","Availability and curtailment must be in [0,1].");
    }
    private static MarketDataException Invalid(string text)=>new("volume_source",text);
}

internal static class AssetDefinitions
{
    internal static void Context(VolumeContext context)
    {
        if(context is null || context.Definitions.CatalogId==Guid.Empty || context.Definitions.RevisionId==Guid.Empty ||
            context.Reference.CatalogId==Guid.Empty || context.Reference.RevisionId==Guid.Empty ||
            context.ValuationAt.Offset!=TimeSpan.Zero || context.KnownAt.Offset!=TimeSpan.Zero || !Enum.IsDefined(context.Policy))
            throw new MarketDataException("volume_source","Supply exact definition/reference pins and explicit UTC valuation, knowledge and pending-actuals policy.");
    }
    internal static void Validate(GenerationAsset asset,VolumeContext context)
    {
        Context(context);
        if(asset.Key.Revision!=context.Definitions)throw new MarketDataException("ppa_definition_revision","Assets must use the selected definition revision.");
        if(asset.Location.Revision!=context.Reference || asset.Clock.Revision!=context.Reference || asset.CapacityQuantity.Key.Revision!=context.Reference)
            throw new MarketDataException("ppa_reference_revision","Assets must retain the selected exact reference version.");
        Money.RequireKey(asset.Location);Money.RequireKey(asset.Clock);
        Specification(asset.CapacityQuantity,context.Reference);
        if(asset.OperatingClock is null || string.IsNullOrWhiteSpace(asset.Key.Code) || asset.CapacityQuantity.Unit.Code!="megawatt" || asset.CapacityQuantity.Dimension.Code!="power" || asset.Versions.IsDefaultOrEmpty)
            throw new MarketDataException("asset_version","An asset needs its declared clock, MW capacity specification and effective versions.");
        var ordered=asset.Versions.OrderBy(v=>v.From).ToArray();
        foreach(var version in ordered)
        {
            bool DayStart(DateTimeOffset instant)=>instant.Offset==TimeSpan.Zero && asset.OperatingClock.StartOfDay(DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(instant,asset.OperatingClock.Zone).DateTime)).ToUniversalTime()==instant;
            if(version.From>=version.To || !DayStart(version.From) || !DayStart(version.To) || version.CapacityBasis.CompareTo(default)<=0 || version.AccreditedCapacity is {} accredited && accredited.CompareTo(default)<0)
                throw new MarketDataException("asset_version","Capacity versions need native operating-day boundaries, positive bases and nonnegative explicit accreditation.");
        }
        if(ordered.Zip(ordered.Skip(1)).Any(p=>p.First.To>p.Second.From))throw new MarketDataException("asset_version","Asset capacity versions cannot overlap.");
    }
    internal static AssetVersion At(GenerationAsset asset,DateTimeOffset from,DateTimeOffset to)
    {
        var found=asset.Versions.Where(v=>v.From<=from && v.To>=to).ToArray();
        return found.Length==1?found[0]:throw new MarketDataException("asset_version","Exactly one asset version must cover the whole quantity interval.");
    }
    internal static void Specification(QuantitySpecification spec,ReferenceRevision reference)
    {
        var keys=new[]{spec.Key,spec.Commodity,spec.MeasurementBasis,spec.Unit,spec.Dimension};
        if(keys.Any(k=>k.Revision!=reference))throw new MarketDataException("ppa_reference_revision","Quantity relationships must retain the selected reference revision.");
        foreach(var key in keys)Money.RequireKey(key);
    }
    internal static void Energy(QuantitySpecification capacity,QuantitySpecification energy,ReferenceRevision reference)
    {
        Specification(energy,reference);
        if(energy.Unit.Code!="megawatt_hour" || energy.Dimension.Code!="energy" || energy.Commodity!=capacity.Commodity)
            throw new MarketDataException("volume_source","The source requires compatible exact MW and MWh commodity specifications.");
    }
}
