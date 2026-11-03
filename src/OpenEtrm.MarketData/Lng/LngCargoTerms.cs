using OpenEtrm.MarketData.Trading;

namespace OpenEtrm.MarketData.Lng;

internal static class LngQuantityRules
{
    internal static bool Matches(QuantitySpecification? quantity, string basis, string dimension) => quantity is not null &&
        quantity.MeasurementBasis.Code == basis && quantity.Dimension.Code == dimension &&
        new[] { quantity.Key, quantity.Commodity, quantity.MeasurementBasis, quantity.Unit, quantity.Dimension }.All(r =>
            r.Revision == quantity.Key.Revision && r.Revision.CatalogId != Guid.Empty && r.Revision.RevisionId != Guid.Empty &&
            !string.IsNullOrWhiteSpace(r.Code));
}

[EconomicDefinition("lng_mass_energy_conversion_v1")]
public sealed record LngMassEnergyConversion
{
    public const string Policy = "gross_discharge_energy_per_mass_v1";
    [IdentityField] public ReferenceKey Key { get; }
    [IdentityField] public QuantitySpecification Mass { get; }
    [IdentityField] public QuantitySpecification Energy { get; }
    [IdentityField] public ExactDecimal EnergyPerMass { get; }
    [IdentityField] public string Implementation { get; }

    public LngMassEnergyConversion(ReferenceKey key, QuantitySpecification mass, QuantitySpecification energy,
        ExactDecimal energyPerMass, string implementation)
    {
        Money.RequireKey(key);
        if (mass is null || energy is null || mass.Key.Revision != key.Revision || energy.Key.Revision != key.Revision ||
            mass.Commodity != energy.Commodity || !LngQuantityRules.Matches(mass, "discharge_mass", "mass") ||
            !LngQuantityRules.Matches(energy, "gross_energy", "energy") ||
            energyPerMass.CompareTo(default) <= 0 || implementation != Policy)
            throw new MarketDataException("lng_quantity_conversion", "Select one exact positive gross discharge energy-per-mass conversion for the same LNG commodity.");
        Key = key; Mass = mass; Energy = energy; EnergyPerMass = energyPerMass; Implementation = implementation;
    }
}

[EconomicDefinition("lng_cargo_quantity_v1")]
public sealed record LngCargoQuantity
{
    [IdentityField] public Quantity Original { get; }
    [IdentityField] public LngMassEnergyConversion? Conversion { get; }
    public Quantity Energy { get; }

    public LngCargoQuantity(Quantity original, QuantitySpecification energy, LngMassEnergyConversion? conversion)
    {
        if (original is null || original.Amount.CompareTo(default) <= 0 || !LngQuantityRules.Matches(energy, "gross_energy", "energy"))
            throw new MarketDataException("lng_quantity", "Supply a positive fixed discharge quantity and exact gross-energy specification.");
        if (original.Specification == energy.Key)
        {
            if (conversion is not null) throw Invalid();
            Energy = original;
        }
        else
        {
            if (conversion is null || conversion.Mass.Key != original.Specification || conversion.Energy != energy) throw Invalid();
            Energy = new(original.Amount * conversion.EnergyPerMass, energy.Key);
        }
        Original = original; Conversion = conversion;
    }
    private static MarketDataException Invalid() => new("lng_quantity_conversion",
        "An energy quantity must not apply a mass conversion; a mass quantity requires its exact explicit discharge-energy conversion.");
}

[EconomicDefinition("lng_incoterms_v1")]
public sealed record LngIncoterms
{
    public ReferenceKey Edition => Rule.Edition;
    [IdentityField] public LngIncotermsRule Rule { get; }
    [IdentityField] public ReferenceKey NamedPlace { get; }

    public LngIncoterms(ReferenceKey edition, LngIncotermsRule rule, ReferenceKey namedPlace)
    {
        Money.RequireKey(edition); Money.RequireKey(namedPlace);
        if (rule is null || edition != rule.Edition || edition.Revision != namedPlace.Revision)
            throw new MarketDataException("lng_incoterms", "Select an exact Incoterms edition, rule and named place from the same reference revision.");
        Rule = rule; NamedPlace = namedPlace;
    }
}

[EconomicDefinition("lng_incoterms_rule_v1")]
public sealed record LngIncotermsRule
{
    [IdentityField] public ReferenceKey Key { get; }
    [IdentityField] public ReferenceKey Edition { get; }
    public LngIncotermsRule(ReferenceKey key, ReferenceKey edition)
    {
        Money.RequireKey(key); Money.RequireKey(edition);
        if (key.Revision != edition.Revision)
            throw new MarketDataException("lng_incoterms", "An Incoterms rule requires its exact reference edition.");
        Key = key; Edition = edition;
    }
}
