using System.Collections.Immutable;
using OpenEtrm.MarketData.CustomCurves;

namespace OpenEtrm.MarketData.Trading;

public enum PriceSettlementMode { CurveTarget = 1, Override }
public sealed record SettlementProjection(Guid SideId,CurveKey Curve,PriceSettlementSelection Selection);

[EconomicDefinition("price_settlement_selection_v1")]
public sealed record PriceSettlementSelection
{
    public const string ForwardCurveNoBasis = "forward_curve_no_basis_v1";
    [IdentityField] public PriceSettlementMode Mode { get; }
    [IdentityField] public ReferenceKey CurveTargetIndex { get; }
    [IdentityField] public ReferenceKey SelectedIndex { get; }
    [IdentityField] public string? ProjectionPolicy { get; }
    public PriceSettlementSelection(PriceSettlementMode mode, ReferenceKey curveTargetIndex, ReferenceKey selectedIndex, string? projectionPolicy)
    {
        Money.RequireKey(curveTargetIndex); Money.RequireKey(selectedIndex);
        if (curveTargetIndex.Revision != selectedIndex.Revision || !Enum.IsDefined(mode) ||
            (mode == PriceSettlementMode.CurveTarget ? selectedIndex != curveTargetIndex || projectionPolicy is not null :
                selectedIndex == curveTargetIndex || projectionPolicy != ForwardCurveNoBasis))
            throw new MarketDataException("price_settlement_selection", "Select the exact curve target, or a different pinned index with forward_curve_no_basis_v1.");
        Mode = mode; CurveTargetIndex = curveTargetIndex; SelectedIndex = selectedIndex; ProjectionPolicy = projectionPolicy;
    }
    public static PriceSettlementSelection CurveTarget(ReferenceKey index) => new(PriceSettlementMode.CurveTarget, index, index, null);
}

// This receipt declares the selector's dependencies. A due observation has only
// its selected fixing; the curve's expired mark is never requested. A live
// observation retains its exact projection, including a custom output binding.
public sealed record PeriodPriceAdmission(IndexPriceLookup Lookup, CurveRequest? Projection,
    ExactUnitPrice? FixingPrice, ImmutableHashSet<ObservationKey> FixingLineage)
{
    public PriceSettlementSelection Settlement => Lookup.Settlement;
    public SchedulePriceState State => Projection is null ? SchedulePriceState.Actual : SchedulePriceState.Projected;
}
public sealed record ResolvedPeriodPrice(ExactUnitPrice Price, SchedulePriceState State,
    ImmutableHashSet<ObservationKey> Lineage, PriceSettlementSelection Settlement);

public static class PeriodPriceSelector
{
    // The term can declare dependencies before market evaluation, while complete
    // model and observation eligibility is enforced by Admit below.
    public static CurveRequest? ProjectionAt(IndexPriceLookup lookup, MarketSnapshot snapshot)
    {
        if (lookup is null || lookup.Settlement is null || snapshot is null || !TradingInstant.IsSupported(snapshot.ValuationAt))
            throw new MarketDataException("price_settlement_selection", "An exact period lookup, settlement selection and valuation instant are required.");
        return RequiresProjection(lookup.Settlement, lookup.Contract, lookup.Fixing, snapshot.ValuationAt, lookup.CustomProjection is not null) ? lookup.ProjectionRequest : null;
    }
    internal static bool RequiresProjection(PriceSettlementSelection settlement, MarketContract contract, IndexFixing? fixing,
        DateTimeOffset valuationAt, bool customProjection = false)
    {
        Qualify(settlement, contract, fixing, customProjection);
        if (fixing is not null && valuationAt >= fixing.FixingAt) return false;
        if (valuationAt >= contract.Expiry)
            throw new MarketDataException("source_expired", "An unfixed period contract requires its live projection; its last mark cannot replace the selected fixing.");
        return true;
    }
    private static void Qualify(PriceSettlementSelection settlement, MarketContract contract, IndexFixing? fixing, bool customProjection)
    {
        if (settlement.Mode == PriceSettlementMode.Override && (fixing is null || customProjection))
            throw new MarketDataException("price_settlement_selection", "An override requires qualified fixing metadata for every observation and projects only from its curve's forward marks.");
        if (fixing is not null && (fixing.Implementation != IndexFixing.SupportedImplementation || fixing.Index != settlement.SelectedIndex ||
            fixing.From != contract.DeliveryFrom || fixing.To != contract.DeliveryTo || !TradingInstant.IsSupported(fixing.FixingAt)))
            throw new MarketDataException("index_fixing_definition", "The selector requires the selected index's exact qualified publication period and implementation.");
    }

    // D360: physical gas still to be delivered is worth the location curve's own mark. A published fixing locks only the
    // payment price, so a delivery mark never reads it and never falls back to it. Under delivery_mark_final_settlement_v1
    // the mark is the contract's live curve mark until it expires; afterwards, until the gas day ends, it is that contract's
    // own recorded final settlement, pinned by the context. A missing mark or settlement refuses.
    public const string DeliveryMarkPolicy = "delivery_mark_final_settlement_v1";
    // The live curve dependency of a delivery mark, or null once the contract has expired and its final settlement applies.
    public static CurveRequest? DeliveryMarkAt(IndexPriceLookup lookup, MarketSnapshot snapshot)
    {
        if (lookup is null || lookup.Settlement is null || snapshot is null || !TradingInstant.IsSupported(snapshot.ValuationAt))
            throw new MarketDataException("price_settlement_selection", "An exact period lookup, settlement selection and valuation instant are required.");
        Qualify(lookup.Settlement, lookup.Contract, lookup.Fixing, lookup.CustomProjection is not null);
        if (snapshot.ValuationAt < lookup.Contract.Expiry) return lookup.ProjectionRequest;
        if (lookup.CustomProjection is not null)
            throw new MarketDataException("delivery_mark_final_settlement", "An expired contract's delivery mark is its own final settlement, which a custom projection does not record.");
        return null;
    }
    internal static ResolvedPeriodPrice ResolveDeliveryMark(CurveBuild market, IndexPriceLookup lookup,
        Func<CurveRequest, ExactUnitPrice> price, Func<CurveRequest, ImmutableHashSet<ObservationKey>> lineage)
    {
        if (market is null || lookup is null || lookup.Settlement is null)
            throw new MarketDataException("price_settlement_selection", "Select an exact market context and explicit period settlement contract.");
        market.Model.RequireIndexProjection(lookup.Market, lookup.Contract, lookup.Settlement.CurveTargetIndex, lookup.Quote);
        if (lookup.Fixing is {} fixing) market.Model.RequireIndexFixing(lookup, fixing);
        if (DeliveryMarkAt(lookup, market.Snapshot) is not {} request)
        {
            var series = market.Model.PriceSeries(lookup.Contract.Key, lookup.Quote.Key);
            if (!market.SelectedInputs.TryGetValue(new(series, InputUse.FrozenFact), out var selected) ||
                selected.Observation is not { Role: ObservationRole.FinalContractSettlement, State: PublicationState.Final } settled ||
                settled.Series != series || settled.ObservedAt < lookup.Contract.Expiry)
                throw new MarketDataException("delivery_mark_final_settlement", "Undelivered physical gas after its contract expired requires that contract's recorded final settlement; no other mark or fixing replaces it.");
            return new(ExactUnitPrice.FromPrice(new(settled.Value, lookup.Quote)), SchedulePriceState.Actual, ImmutableHashSet.Create(settled.Key), lookup.Settlement);
        }
        var value = price(request);
        if (value.Unit != PriceUnit.FromQuote(lookup.Quote))
            throw new MarketDataException("custom_projection", "The selected projection must retain the contractual quote unit.");
        return new(value, SchedulePriceState.Projected, lineage(request), lookup.Settlement);
    }

    public static PeriodPriceAdmission Admit(CurveBuild market, IndexPriceLookup lookup)
    {
        if (market is null || lookup is null || lookup.Settlement is null)
            throw new MarketDataException("price_settlement_selection", "Select an exact market context and explicit period settlement contract.");
        market.Model.RequireIndexProjection(lookup.Market, lookup.Contract, lookup.Settlement.CurveTargetIndex, lookup.Quote);
        // This is the common complete fixing qualification for dated and period
        // consumers, including metadata checks before a future publication.
        var fixedPrice = IndexFixingPricing.Resolve(market, lookup);
        var projection = ProjectionAt(lookup, market.Snapshot);
        if (fixedPrice is {} observed)
            return new(lookup, null, ExactUnitPrice.FromPrice(observed.Price), observed.Lineage);
        if (projection is null)
            throw new MarketDataException("index_fixing_missing", "The selected due fixing is unavailable.");
        return new(lookup, projection, null, ImmutableHashSet<ObservationKey>.Empty);
    }

    internal static ResolvedPeriodPrice Resolve(CurveBuild market, IndexPriceLookup lookup,
        Func<CurveRequest, ExactUnitPrice> price, Func<CurveRequest, ImmutableHashSet<ObservationKey>> lineage)
    {
        var source = Admit(market, lookup);
        if (source.Projection is not {} request)
            return new(source.FixingPrice!, SchedulePriceState.Actual, source.FixingLineage, source.Settlement);
        var value = price(request);
        if (value.Unit != PriceUnit.FromQuote(lookup.Quote))
            throw new MarketDataException("custom_projection", "The selected projection must retain the contractual quote unit.");
        return new(value, SchedulePriceState.Projected, lineage(request), source.Settlement);
    }
}
