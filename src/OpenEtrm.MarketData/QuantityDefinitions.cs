namespace OpenEtrm.MarketData;

internal static class QuantityDefinitions
{
    internal static bool Coherent(QuantitySpecification? definition)
    {
        if (definition is null) return false;
        var keys = new[] { definition.Key, definition.Commodity, definition.MeasurementBasis, definition.Unit, definition.Dimension };
        try { foreach (var key in keys) Money.RequireKey(key); }
        catch (MarketDataException) { return false; }
        return keys.All(key => key.Revision == definition.Key.Revision);
    }
}
