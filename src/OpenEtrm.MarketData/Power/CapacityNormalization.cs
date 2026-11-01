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
    internal static Prepared Prepare(ImmutableArray<GenerationAsset> assets,VolumeContext context)=>new(assets,context);
    public static NormalizedQuantity PerUnit(ImmutableArray<GenerationAsset> assets,ImmutableArray<AssetVolume> contributions,
        DeliveryInterval interval,QuantityStage stage,VolumeContext context)=>Prepare(assets,context).PerUnit(contributions,interval,stage);

    // One authorized quantity preparation owns immutable assets and context.
    // Only their admission and the basis within a shared version window are reused;
    // every contribution still proves its interval, source state and exact version.
    internal sealed class Prepared
    {
        private readonly ImmutableArray<GenerationAsset> assets;
        private readonly VolumeContext context;
        private readonly Dictionary<AssetKey,int> assetOrdinals;
        private sealed record BasisWindow(DateTimeOffset From,DateTimeOffset To,ImmutableArray<AssetVersion> Versions,ExactQuantity Quantity);
        private volatile BasisWindow? latest;
        internal Prepared(ImmutableArray<GenerationAsset> assets,VolumeContext context)
        {
            Validate(assets,context);this.assets=assets;this.context=context;
            assetOrdinals=assets.Select((asset,n)=>(asset.Key,n)).ToDictionary(pair=>pair.Key,pair=>pair.n);
        }
        internal NormalizedQuantity PerUnit(ImmutableArray<AssetVolume> contributions,DeliveryInterval interval,QuantityStage stage)
        {
            if(!Enum.IsDefined(stage)||contributions.IsDefault||contributions.Length!=assets.Length)
                throw Invalid("Every declared asset needs exactly one compatible whole-asset contribution, including explicit zero quantities.");
            Span<bool> seen=assets.Length<=64?stackalloc bool[assets.Length]:new bool[assets.Length];seen.Clear();
            foreach(var contribution in contributions)
            {
                if(!assetOrdinals.TryGetValue(contribution.Asset,out var ordinal)||seen[ordinal]||
                    contribution.Interval!=interval||contribution.Stage!=stage||contribution.Energy.NumeratorValue.Sign<0)
                    throw Invalid("Every declared asset needs exactly one compatible whole-asset contribution, including explicit zero quantities.");
                seen[ordinal]=true;
                if(contribution.Context!=context)throw Invalid("All contributions must retain the same exact calculation context.");
                if(contribution.Source.Revision!=context.Definitions)throw new MarketDataException("ppa_definition_revision","Quantity sources must retain the selected definition revision.");
                if(contribution.Energy.Specification.Revision!=context.Reference)throw new MarketDataException("ppa_reference_revision","Quantity contributions must retain the selected reference revision.");
            }
            var basis=BasisFor(interval);
            var components=ImmutableArray.CreateBuilder<NormalizedContribution>(contributions.Length);
            var total=contributions[0].Energy;var estimated=false;var metered=true;var forecast=true;
            for(var n=0;n<contributions.Length;n++)
            {
                var contribution=contributions[n];var ordinal=assetOrdinals[contribution.Asset];
                if(contribution.AssetVersion is not {} pin||pin.Asset!=assets[ordinal].Key||pin.From!=basis.Versions[ordinal].From||
                    contribution.KnownAt>context.KnownAt||contribution.State is not(VolumeState.Forecast or VolumeState.Metered or VolumeState.Estimated)||
                    contribution.State==VolumeState.Metered&&interval.End>context.ValuationAt||
                    contribution.State==VolumeState.Estimated&&(context.Policy!=PendingActualsPolicy.EstimateUntilActual||interval.End>context.ValuationAt))
                    throw Invalid("Contributions must preserve their selected asset versions and eligible source states.");
                if(contribution.Energy.Specification!=total.Specification)throw Invalid("Aggregate contributions require the same exact energy specification.");
                if(n>0)total=total.Add(contribution.Energy);
                components.Add(new(contribution,Divide(contribution.Energy,basis.Quantity)));
                estimated|=contribution.State==VolumeState.Estimated;metered&=contribution.State==VolumeState.Metered;forecast&=contribution.State==VolumeState.Forecast;
            }
            var state=estimated?VolumeState.Estimated:metered?VolumeState.Metered:forecast?VolumeState.Forecast:
                throw Invalid("Mixed source states must explicitly identify estimated contributions.");
            return new(interval,contributions.Length==1?components[0].PerUnit:Divide(total,basis.Quantity),total,basis.Quantity,state,components.MoveToImmutable());
        }
        private BasisWindow BasisFor(DeliveryInterval interval)
        {
            var cached=latest;
            if(cached is not null&&interval.Start<interval.End&&cached.From<=interval.Start&&cached.To>=interval.End)return cached;
            var versions=assets.Select(asset=>AssetDefinitions.At(asset,interval.Start,interval.End)).ToImmutableArray();
            var quantity=versions.Select((version,n)=>ExactQuantity.FromQuantity(new(version.CapacityBasis,assets[n].CapacityQuantity.Key))).Aggregate((a,b)=>a.Add(b));
            var selected=new BasisWindow(versions.Max(version=>version.From),versions.Min(version=>version.To),versions,quantity);
            latest=selected;return selected;
        }
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
