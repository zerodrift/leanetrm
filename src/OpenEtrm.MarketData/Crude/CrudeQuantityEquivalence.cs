using System.Collections.Immutable;
using OpenEtrm.MarketData.Trading;

namespace OpenEtrm.MarketData.Crude;

// A declared one-to-one bridge between two immutable standard-volume masters.
// It never converts units, density or temperature, or creates a market factor.
[EconomicDefinition("crude_quantity_equivalence_v1")]
public sealed record CrudeQuantityEquivalence
{
    public const string Policy = "same_standard_volume_at_60f_v1";
    [IdentityField] public QuantitySpecification Delivery { get; }
    [IdentityField] public QuantitySpecification Pricing { get; }
    [IdentityField] public ImmutableArray<int> DeliveryDimensions { get; }
    [IdentityField] public ImmutableArray<int> PricingDimensions { get; }
    [IdentityField] public string Implementation { get; }

    public CrudeQuantityEquivalence(QuantitySpecification delivery, QuantitySpecification pricing,
        ImmutableArray<int> deliveryDimensions, ImmutableArray<int> pricingDimensions, string implementation,
        UnitConverter deliveryUnits, UnitConverter pricingUnits)
    {
        static bool Volume(ImmutableArray<int> powers) => !powers.IsDefault && powers.SequenceEqual(new[] {0,3,0,0,0,0,0});
        static bool Coherent(QuantitySpecification quantity) => quantity is not null &&
            new[] { quantity.Key, quantity.Commodity, quantity.MeasurementBasis, quantity.Unit, quantity.Dimension }
                .All(k => k.Revision == quantity.Key.Revision && k.Revision.CatalogId != Guid.Empty &&
                    k.Revision.RevisionId != Guid.Empty && !string.IsNullOrWhiteSpace(k.Code));
        if (!Coherent(delivery) || !Coherent(pricing) || implementation != Policy ||
            !Volume(deliveryDimensions) || !Volume(pricingDimensions) ||
            delivery.Commodity.Code != pricing.Commodity.Code ||
            delivery.MeasurementBasis.Code != pricing.MeasurementBasis.Code ||
            delivery.Unit.Code != pricing.Unit.Code || delivery.Dimension.Code != "volume" || pricing.Dimension.Code != "volume")
            throw new MarketDataException("crude_quantity_equivalence",
                "Declare matching exact standard-volume commodity, measurement basis, unit and volume dimensions at 60 Fahrenheit. Conversion is not admitted.");
        if (deliveryUnits is null || pricingUnits is null || deliveryUnits.Specification(delivery.Key) != delivery ||
            pricingUnits.Specification(pricing.Key) != pricing) throw Invalid();
        var deliveryScales = deliveryUnits.RelativeUnitScales(delivery.Unit);
        var pricingScales = pricingUnits.RelativeUnitScales(pricing.Unit);
        if (pricingScales.Any(p =>
            !deliveryScales.TryGetValue(p.Key, out var scale) || scale != p.Value)) throw Invalid();
        Delivery = delivery; Pricing = pricing; DeliveryDimensions = deliveryDimensions;
        PricingDimensions = pricingDimensions; Implementation = implementation;
    }
    private static MarketDataException Invalid() => new("crude_quantity_equivalence",
        "The exact connected standard-volume unit definitions must agree without conversion.");
}
