using OpenEtrm.MarketData.Trading;

namespace OpenEtrm.MarketData.Gas;

[EconomicDefinition("gas_delivery_location_v1")]
public sealed record GasDeliveryLocation
{
    [IdentityField] public ReferenceKey Pipeline { get; }
    [IdentityField] public ReferenceKey Zone { get; }
    [IdentityField] public ReferenceKey Point { get; }

    public GasDeliveryLocation(ReferenceKey pipeline, ReferenceKey zone, ReferenceKey point)
    {
        foreach (var key in new[] { pipeline, zone, point }) Money.RequireKey(key);
        if (pipeline.Revision != zone.Revision || zone.Revision != point.Revision)
            throw new MarketDataException("gas_delivery_location", "Select one exact reference revision for the pipeline, zone and delivery location.");
        Pipeline = pipeline; Zone = zone; Point = point;
    }
}
