using System.Collections.Frozen;

namespace OpenEtrm.Api;

// Native catalog metadata shared by delivery editors and their future authorized
// assistant reader. Descriptions explain supported terms; they select no terms.
internal static class DeliverySetupDescriptions
{
    public static IReadOnlyDictionary<string,string> Gas {get;}=new Dictionary<string,string>
    {
        ["tab"]="Choose the relationships to inspect: delivery points, shared pricing groups, dated meter assignments or usage tariffs.",
        ["row"]="Choose a record within this setup. This selects a delivery relationship, not a different version or market context.",
        ["paths.pipeline"]="The pipeline location that owns this delivery path. Choose an existing location reference.",
        ["paths.zone"]="The zone within the selected pipeline. Each zone belongs to one pipeline in this setup.",
        ["paths.point"]="The delivery location within this zone. Each point belongs to one pipeline and zone in this setup.",
        ["paths.product"]="The commodity delivered along this path. Its pricing group and meter quantity must refer to the same product.",
        ["paths.clock"]="The fixed-offset clock used to turn operating days into delivery instants. This setup does not apply daylight-saving rules.",
        ["paths.dayStartMinute"]="The gas-day start in minutes after local midnight: for example, 540 means 09:00 in the selected clock.",
        ["paths.groupCode"]="The group supplying this point's curve and shared price adjustment. No group leaves the point explicitly unpriced; its point adjustment must then be zero.",
        ["paths.adjustment"]="An additive point price adjustment in the selected curve's quote units. The point mark combines the curve, group adjustment and this adjustment.",
        ["groups.code"]="A unique code for this pricing group within the setup. Delivery points use this code to select the group.",
        ["groups.pipeline"]="The pipeline shared by this group and every delivery point assigned to it.",
        ["groups.product"]="The commodity shared by this group, its curve and every assigned delivery point.",
        ["groups.curve"]="An existing outright index curve supplying the group's market price. Any upstream curve dependencies must already be explicitly defined in that curve.",
        ["groups.adjustment"]="An additive adjustment shared by every point in this group, expressed in the curve's quote units. It is separate from each point's adjustment.",
        ["meters.code"]="A unique code for this dated meter assignment within the setup.",
        ["meters.pipeline"]="The pipeline for the meter assignment. The meter's path must match an existing delivery point in this setup.",
        ["meters.zone"]="The pipeline zone for the meter's delivery path.",
        ["meters.point"]="The delivery point served by this meter during its validity period.",
        ["meters.product"]="The commodity scheduled through this meter. The native quantity specification must refer to the same product.",
        ["meters.identifier"]="The pipeline's meter identifier for contractual schedules. This assignment creates no measured quantities or facility allocations.",
        ["meters.quantity"]="The product, basis and unit used for this meter's scheduled quantity. Selecting it does not perform an implicit conversion.",
        ["meters.validFrom"]="The first operating day covered by this assignment, inclusive. This is delivery coverage, not the header's As of date.",
        ["meters.validTo"]="The first operating day no longer covered by this assignment. Adjacent assignments can meet at this exclusive boundary.",
        ["tariffs.code"]="A unique code for this dated usage tariff within the setup. A trade must explicitly select a tariff to create its charge.",
        ["tariffs.pipeline"]="The pipeline on which this usage tariff can be selected.",
        ["tariffs.product"]="The commodity to which this tariff applies. The rate quote must refer to the same product.",
        ["tariffs.quote"]="The reference defining the rate's currency, quantity unit and multiplier. This setup supports an outright per-unit rate, not a percentage fee.",
        ["tariffs.rate"]="The exact per-unit rate in the selected quote's units. It creates a separate contractual charge when selected on a trade; it does not alter the delivery mark.",
        ["tariffs.validFrom"]="The first operating day covered by this tariff, inclusive.",
        ["tariffs.validTo"]="The first operating day no longer covered by this tariff, exclusive.",
        ["from"]="The first delivery operating day to check, inclusive. The check uses the saved draft; it does not move the header context.",
        ["to"]="The first delivery operating day outside the check, exclusive. Choose a span of at most 31 operating days."
    }.ToFrozenDictionary();

    public static IReadOnlyDictionary<string,string> Crude {get;}=new Dictionary<string,string>
    {
        ["binding"]="A named delivery-to-price relationship. Its published version is resolved from the header's As of date and knowledge cutoff.",
        ["code"]="A stable code for this delivery-to-price relationship. Revisions retain the code; a new binding needs its own code.",
        ["commodity"]="The delivered commodity from the maintained reference catalog. Choose it explicitly before saving the relationship.",
        ["grade"]="The delivered grade, including its standard-volume quantity and temperature basis. The grade must belong to the selected commodity.",
        ["facility"]="The referenced physical site for delivery. The facility, grade and custody point must form a maintained delivery relationship.",
        ["custodyPoint"]="The referenced location where this delivery relationship identifies custody transfer. This choice does not define title-transfer events or commercial delivery terms.",
        ["source"]="An existing monthly outright assessment curve in an explicitly admitted pricing revision. Each trade or value-preview interval must fit one source contract; the binding's coverage can span several months. No alternative curve is inferred.",
        ["clock"]="The admitted clock for interpreting this delivery relationship. Preview instants below are entered explicitly in UTC.",
        ["policy"]="The explicit relationship between delivery and pricing quantities. The supported policy requires the same standard volume at 60 °F with no conversion.",
        ["from"]="The first delivery date covered by this binding, inclusive. This is contractual coverage, not the header's As of date.",
        ["to"]="The first delivery date no longer covered by this binding, exclusive.",
        ["preview-from"]="The delivery start instant in UTC, inclusive. It must follow the header's valuation As of and fall within the binding's coverage.",
        ["preview-to"]="The delivery end instant in UTC, exclusive. The whole interval must be covered by this binding and one monthly source contract.",
        ["quantity"]="The positive total standard volume in the grade's quantity unit. The preview values this amount; it does not assume a daily rate."
    }.ToFrozenDictionary();
}
