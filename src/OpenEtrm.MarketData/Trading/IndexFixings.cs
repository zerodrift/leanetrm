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
public static class IndexFixingPricing
{
    public static (UnitPrice Price,ImmutableHashSet<ObservationKey> Lineage)? Resolve(CurveBuild market,IndexPriceLookup lookup)
    {
        var fixing=lookup.Fixing;
        if(fixing is null)return null;
        if(fixing.Implementation!=IndexFixing.SupportedImplementation || fixing.From!=lookup.Contract.DeliveryFrom ||
            fixing.To!=lookup.Contract.DeliveryTo || !TradingInstant.IsSupported(fixing.FixingAt))
            throw new MarketDataException("index_fixing_definition","Use the exact supported publication schedule and period.");
        market.Model.RequireIndexFixing(lookup,fixing);
        if(lookup.PublishedWhenAvailable)return ResolveSelected(market.Snapshot,lookup);
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
    // This policy belongs only to delivery_index_fixings_v1. The selected context
    // has already resolved receipt/correction knowledge; an expected date never
    // invents an actual or blocks an already eligible final publication.
    internal static (UnitPrice Price,ImmutableHashSet<ObservationKey> Lineage)? ResolveSelected(MarketSnapshot snapshot,IndexPriceLookup lookup)
    {
        var fixing=lookup.Fixing??throw new MarketDataException("index_fixing_definition","Publication selection requires an exact fixing definition.");
        if(!lookup.PublishedWhenAvailable||snapshot.Selections.IsDefault)throw new MarketDataException("index_fixing_selection","Select the explicit publication availability contract and coherent snapshot.");
        var matches=snapshot.Selections.Where(s=>s.Input==new InputKey(fixing.Series,InputUse.FrozenFact)).Take(2).ToArray();
        if(matches.Length==0)return null;
        if(matches.Length!=1)throw new MarketDataException("index_fixing_selection","The selected context contains ambiguous fixing inputs.");
        var observation=matches[0].Observation;
        if(observation.Series!=fixing.Series||observation.Source!=fixing.Source||observation.Role!=ObservationRole.IndexFixing||observation.State!=PublicationState.Final||
            !TradingInstant.IsSupported(observation.ObservedAt)||observation.ObservedAt<fixing.FixingAt||observation.ObservedAt>snapshot.MarketCutoff||observation.ObservedAt>snapshot.ValuationAt)
            throw new MarketDataException("index_fixing_selection","The selected final publication must match its exact source and be available at the valuation and market cutoff.");
        return(new(observation.Value,lookup.Quote),ImmutableHashSet.Create(observation.Key));
    }

}
