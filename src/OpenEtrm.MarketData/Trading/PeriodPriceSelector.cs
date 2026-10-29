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
        if (settlement.Mode == PriceSettlementMode.Override && (fixing is null || customProjection))
            throw new MarketDataException("price_settlement_selection", "An override requires qualified fixing metadata for every observation and projects only from its curve's forward marks.");
        if (fixing is not null && (fixing.Implementation != IndexFixing.SupportedImplementation || fixing.Index != settlement.SelectedIndex ||
            fixing.From != contract.DeliveryFrom || fixing.To != contract.DeliveryTo || !TradingInstant.IsSupported(fixing.FixingAt)))
            throw new MarketDataException("index_fixing_definition", "The selector requires the selected index's exact qualified publication period and implementation.");
        if (fixing is not null && valuationAt >= fixing.FixingAt) return false;
        if (valuationAt >= contract.Expiry)
            throw new MarketDataException("source_expired", "An unfixed period contract requires its live projection; its last mark cannot replace the selected fixing.");
        return true;
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
