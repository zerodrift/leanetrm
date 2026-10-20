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
