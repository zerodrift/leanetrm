using System.Collections.Immutable;

namespace OpenEtrm.MarketData.Trading;

// The authorized definition reader supplies this exact dated publication rule.
// The domain never derives it from delivery dates or a display time zone.
[EconomicDefinition("IndexFixing_v1")]
public sealed record IndexFixing([property: IdentityField] ReferenceKey Index,
    [property: IdentityField] ReferenceKey Quote, [property: IdentityField] DateOnly From,
    [property: IdentityField] DateOnly To, [property: IdentityField] DateTimeOffset FixingAt,
    [property: IdentityField] SeriesKey Series, [property: IdentityField] ReferenceKey Source,
    [property: IdentityField] string Implementation)
{
    public const string SupportedImplementation = "published_index_observations_v1";
}

// Shared eligibility for swaps and period-price consumers; neither may substitute a settlement anchor.
internal static class IndexFixingPricing
{
    internal static (UnitPrice Price,ImmutableHashSet<ObservationKey> Lineage)? Resolve(CurveBuild market,IndexPriceLookup lookup)
    {
        var fixing=lookup.Fixing;
        if(fixing is null)return null;
        if(fixing.Implementation!=IndexFixing.SupportedImplementation || fixing.From!=lookup.Contract.DeliveryFrom ||
            fixing.To!=lookup.Contract.DeliveryTo || !TradingInstant.IsSupported(fixing.FixingAt))
            throw new MarketDataException("index_fixing_definition","Use the exact supported publication schedule and period.");
        market.Model.RequireIndexFixing(lookup,fixing);
        if(market.Snapshot.ValuationAt<fixing.FixingAt)return null;
        if(!market.SelectedInputs.TryGetValue(new(fixing.Series,InputUse.FrozenFact),out var selected))
            throw new MarketDataException("index_fixing_missing","The exact published index fixing is required; a forecast cannot replace it.");
        var observation=selected.Observation;
        if(observation.Series!=fixing.Series || observation.Source!=fixing.Source ||
            observation.Role!=ObservationRole.IndexFixing || observation.State!=PublicationState.Final ||
            !TradingInstant.IsSupported(observation.ObservedAt) || observation.ObservedAt<fixing.FixingAt || observation.ObservedAt>market.Snapshot.MarketCutoff)
            throw new MarketDataException("index_fixing_selection","The pinned actual must match the qualified series, source, final role and publication interval.");
        return (new(observation.Value,lookup.Quote),ImmutableHashSet.Create(observation.Key));
    }
}
