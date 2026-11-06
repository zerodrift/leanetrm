using System.Collections.Immutable;
using System.Globalization;

namespace OpenEtrm.MarketData.Trading;

// The source contract keeps forecast-query geometry separate from the index
// observation period. Both remain exact immutable inputs to normal Side PV.
public static class CurvePriceScheduleSource
{
    public static IndexPriceLookup Bind(IndexPriceLookup observation,CustomCurveProjectionBinding source,ProjectionDelivery query)
    {
        source.Validate();
        if(source.Selection!=CustomCurveProjectionBinding.ContainingDeliverySelection)
            throw new MarketDataException("custom_projection","This source requires an explicit whole-interval selection policy.");
        var bound=observation with{CustomProjection=source,ProjectionInterval=query==new ProjectionDelivery(observation.Contract.DeliveryFrom,observation.Contract.DeliveryTo)?null:query};
        _=bound.ProjectionRequest;
        return bound;
    }
    public static bool SameObservation(IndexPriceLookup bound,IndexPriceLookup original)=>
        bound with{CustomProjection=original.CustomProjection,ProjectionInterval=original.ProjectionInterval}==original;
    public static DefinitionIdentity PricingIdentity(DefinitionIdentity original,CustomCurveProjectionBinding source)=>
        DefinitionIdentity.For(new CurveSourcePricingIdentity(original.Canonical,source.CatalogId,source.CurveId,source.Version,
            source.OutputNode,source.Selection,source.Implementation));
    public const string Operation="curve_price_lookup";
    public static bool IsLookup(string operation)=>operation is "price_lookup" or Operation;
    internal static ImmutableSortedDictionary<string,string> Parameters(IndexPriceLookup lookup)
    {
        if(lookup.CustomProjection?.Selection!=CustomCurveProjectionBinding.ContainingDeliverySelection)
            throw ScheduleDefinition.Error("Select an explicit whole-interval curve binding.");
        var interval=lookup.ForecastDelivery;
        return new Dictionary<string,string>{["selection"]=DefinitionIdentity.For(lookup.Settlement).Canonical,
            ["policy"]=CustomCurveProjectionBinding.ContainingDeliverySelection,["from"]=interval.From.ToString("yyyy-MM-dd",CultureInfo.InvariantCulture),
            ["to"]=interval.To.ToString("yyyy-MM-dd",CultureInfo.InvariantCulture)}.ToImmutableSortedDictionary(StringComparer.Ordinal);
    }
    public static ProjectionDelivery Interval(ScheduleDefinitionRow row)
    {
        if(row.Operation!=Operation||row.Grid is not null||row.Parameters.GetValueOrDefault("policy")!=CustomCurveProjectionBinding.ContainingDeliverySelection||
            !DateOnly.TryParseExact(row.Parameters.GetValueOrDefault("from"),"yyyy-MM-dd",CultureInfo.InvariantCulture,DateTimeStyles.None,out var from)||
            !DateOnly.TryParseExact(row.Parameters.GetValueOrDefault("to"),"yyyy-MM-dd",CultureInfo.InvariantCulture,DateTimeStyles.None,out var to)||from>=to||
            row.ObservationFrom is null||row.ObservationTo is null||from<row.ObservationFrom||to>row.ObservationTo)
            throw ScheduleDefinition.Error("A curve price source requires an explicit supported policy and positive delivery interval.");
        return new(from,to);
    }
    public static bool Matches(ScheduleDefinitionRow row,IndexPriceLookup lookup)=>row.Operation==Operation?
        lookup.CustomProjection?.Selection==CustomCurveProjectionBinding.ContainingDeliverySelection&&lookup.ForecastDelivery==Interval(row):
        row.Operation=="price_lookup"&&lookup.CustomProjection?.Selection!=CustomCurveProjectionBinding.ContainingDeliverySelection&&lookup.ProjectionInterval is null;
}

[EconomicDefinition("delivery_curve_pricing_binding_v1")]
internal sealed record CurveSourcePricingIdentity([property:IdentityField]string Original,[property:IdentityField]Guid Catalog,
    [property:IdentityField]Guid Curve,[property:IdentityField]int Version,[property:IdentityField]string Output,
    [property:IdentityField]string Selection,[property:IdentityField]string Implementation);
