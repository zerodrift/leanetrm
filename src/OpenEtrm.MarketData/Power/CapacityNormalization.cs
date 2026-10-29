using System.Collections.Immutable;

namespace OpenEtrm.MarketData.Power;

public sealed record NormalizedContribution(AssetVolume Volume,ExactQuantity PerUnit);
public sealed record NormalizedQuantity(DeliveryInterval Interval,ExactQuantity PerUnit,ExactQuantity Aggregate,ExactQuantity CapacityBasis,
    VolumeState State,ImmutableArray<NormalizedContribution> Contributions);
public sealed record NormalizedCapacity(DateOnly OperatingDay,ExactQuantity PerUnit,ExactQuantity Aggregate,ExactQuantity CapacityBasis,
    ImmutableArray<AssetVersionKey> Versions);

public static class CapacityNormalization
{
    public const string Implementation="contract_mw_of_capacity_in_force_v1";
    public static NormalizedQuantity PerUnit(ImmutableArray<GenerationAsset> assets,ImmutableArray<AssetVolume> contributions,
        DeliveryInterval interval,QuantityStage stage,VolumeContext context)
    {
        Validate(assets,context);
        if(!Enum.IsDefined(stage) || contributions.IsDefault || contributions.Length!=assets.Length ||
            contributions.Select(c=>c.Asset).Distinct().Count()!=assets.Length ||
            !contributions.Select(c=>c.Asset).ToHashSet().SetEquals(assets.Select(a=>a.Key)) ||
            contributions.Any(c=>c.Interval!=interval || c.Stage!=stage || c.Energy.NumeratorValue.Sign<0))
            throw Invalid("Every declared asset needs exactly one compatible whole-asset contribution, including explicit zero quantities.");
        foreach(var contribution in contributions)
        {
            if(contribution.Context!=context)throw Invalid("All contributions must retain the same exact calculation context.");
            if(contribution.Source.Revision!=context.Definitions)throw new MarketDataException("ppa_definition_revision","Quantity sources must retain the selected definition revision.");
            if(contribution.Energy.Specification.Revision!=context.Reference)throw new MarketDataException("ppa_reference_revision","Quantity contributions must retain the selected reference revision.");
            var asset=assets.Single(a=>a.Key==contribution.Asset);var version=AssetDefinitions.At(asset,interval.Start,interval.End);
            if(contribution.AssetVersion!=new AssetVersionKey(asset.Key,version.From) || contribution.KnownAt>context.KnownAt ||
                contribution.State is not(VolumeState.Forecast or VolumeState.Metered or VolumeState.Estimated) ||
                contribution.State==VolumeState.Metered && interval.End>context.ValuationAt ||
                contribution.State==VolumeState.Estimated && (context.Policy!=PendingActualsPolicy.EstimateUntilActual || interval.End>context.ValuationAt))
                throw Invalid("Contributions must preserve their selected asset versions and eligible source states.");
        }
        if(contributions.Select(c=>c.Energy.Specification).Distinct().Count()!=1)throw Invalid("Aggregate contributions require the same exact energy specification.");
        var basis=Basis(assets,interval.Start,interval.End);
        var total=contributions.Select(c=>c.Energy).Aggregate((a,b)=>a.Add(b));
        var components=contributions.Select(c=>new NormalizedContribution(c,Divide(c.Energy,basis))).ToImmutableArray();
        var state=contributions.Any(c=>c.State==VolumeState.Estimated)?VolumeState.Estimated:
            contributions.All(c=>c.State==VolumeState.Metered)?VolumeState.Metered:
            contributions.All(c=>c.State==VolumeState.Forecast)?VolumeState.Forecast:
            throw Invalid("Mixed source states must explicitly identify estimated contributions.");
        return new(interval,Divide(total,basis),total,basis,state,components);
    }
    public static NormalizedCapacity AccreditedPerUnit(ImmutableArray<GenerationAsset> assets,DateOnly operatingDay,VolumeContext context)
    {
        Validate(assets,context);
        var from=assets[0].OperatingClock.StartOfDay(operatingDay).ToUniversalTime();
        var to=assets[0].OperatingClock.StartOfDay(operatingDay.AddDays(1)).ToUniversalTime();
        return AccreditedPerInterval(assets,new(from,to,operatingDay,0,0,1),context);
    }
    internal static NormalizedCapacity AccreditedPerInterval(ImmutableArray<GenerationAsset> assets,DeliveryInterval interval,VolumeContext context)
    {
        Validate(assets,context);
        var from=interval.Start;var to=interval.End;var operatingDay=interval.OperatingDate;
        if(from!=assets[0].OperatingClock.StartOfDay(operatingDay).ToUniversalTime()||to!=assets[0].OperatingClock.StartOfDay(operatingDay.AddDays(1)).ToUniversalTime())
            throw Invalid("An accredited entitlement must retain one complete operating day under its exact asset clock.");
        var versions=assets.Select(a=>AssetDefinitions.At(a,from,to)).ToArray();
        if(versions.Any(v=>v.AccreditedCapacity is null))throw new MarketDataException("asset_version","Every declared asset needs explicit accreditation; missing is not zero.");
        var basis=Basis(assets,from,to);
        var total=versions.Select(v=>ExactQuantity.FromQuantity(new(v.AccreditedCapacity!.Value,basis.Specification))).Aggregate((a,b)=>a.Add(b));
        return new(operatingDay,Divide(total,basis),total,basis,[..assets.Select((a,i)=>new AssetVersionKey(a.Key,versions[i].From))]);
    }
    private static void Validate(ImmutableArray<GenerationAsset> assets,VolumeContext context)
    {
        AssetDefinitions.Context(context);
        if(assets.IsDefaultOrEmpty || assets.Select(a=>a.Key).Distinct().Count()!=assets.Length)throw Invalid("Declare a nonempty distinct asset set.");
        foreach(var asset in assets)AssetDefinitions.Validate(asset,context);
        if(assets.Any(a=>a.CapacityQuantity!=assets[0].CapacityQuantity || a.Clock!=assets[0].Clock || a.OperatingClock.Fingerprint!=assets[0].OperatingClock.Fingerprint))
            throw Invalid("Asset aggregation requires exact compatible capacity units and one declared operating clock.");
    }
    private static ExactQuantity Basis(ImmutableArray<GenerationAsset> assets,DateTimeOffset from,DateTimeOffset to)=>
        assets.Select(a=>ExactQuantity.FromQuantity(new(AssetDefinitions.At(a,from,to).CapacityBasis,a.CapacityQuantity.Key))).Aggregate((a,b)=>a.Add(b));
    private static ExactQuantity Divide(ExactQuantity quantity,ExactQuantity basis)=>quantity.ScaleFraction(basis.DenominatorValue,basis.NumeratorValue);
    private static MarketDataException Invalid(string message)=>new("ppa_normalization",message);
}
