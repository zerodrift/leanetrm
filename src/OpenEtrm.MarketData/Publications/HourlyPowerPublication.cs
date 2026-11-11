using System.Collections.Immutable;

namespace OpenEtrm.MarketData.Publications;

// Captured native intervals have no final-release assertion. The interval
// consumer validates their exact grid, unit, source and certified knowledge.
// Date-only release/fixing operations cannot flatten an hourly price package.
public sealed class HourlyPowerPublication : PricePublicationBehavior
{
    public const string Implementation = "power_captured_hourly_v1";
    public static string Statistic(string market) => market switch
    {
        "DA" => "day_ahead_hourly_total_lmp",
        "RT" => "real_time_hourly_total_lmp",
        _ => throw Error("publication_power_market", "Select an explicit DA or RT market.")
    };
    internal HourlyPowerPublication(PricePublicationDefinition definition) : base(definition) { }
    static MarketDataException IntervalRequired() => Error("publication_interval_required", "Use the native hourly captured-price consumer; date-only releases cannot represent this publication.");
    public override ImmutableArray<PublicationPeriod> CoveredPeriods(PricePublicationOccurrence occurrence) => throw IntervalRequired();
    public override PublicationAssessmentWindow AssessmentWindow(PricePublicationOccurrence occurrence) => throw IntervalRequired();
    public override DateTimeOffset ExpectedRelease(PricePublicationOccurrence occurrence) => throw IntervalRequired();
}
