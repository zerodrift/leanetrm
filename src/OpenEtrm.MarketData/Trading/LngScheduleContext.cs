using System.Collections.Immutable;
using OpenEtrm.MarketData.Lng;
using OpenEtrm.MarketData.Power;

namespace OpenEtrm.MarketData.Trading;

internal sealed record LngExecutionContext(ScheduleMarketSources Sources, CurveBuild Scenario);
public sealed partial class SideValuationContext
{
    internal LngExecutionContext? LngExecution { get; init; }
    public static SideValuationContext ForPhysicalLng(CurveBuild basis, CurveBuild scenario, ISideDiscounting discounting,
        ImmutableArray<LngValuationAssignment> assignments) => new(scenario, discounting)
        { LngExecution = new(ScheduleMarketSources.ForPhysicalLng(basis, assignments), scenario) };
}
public sealed partial class ScheduleMarketSources
{
    private ImmutableArray<LngValuationAssignment> lngAssignments = [];
    public static ScheduleMarketSources ForPhysicalLng(CurveBuild basis, ImmutableArray<LngValuationAssignment> assignments)
    {
        if (basis is null || assignments.IsDefaultOrEmpty || assignments.Any(a => a is null) ||
            assignments.Select(a => a.Key).Distinct().Count() != assignments.Length)
            throw Invalid("LNG sources require distinct exact product/discharge custody mappings.");
        return new(basis.Graph.Plan.Context, basis, null, null, IntervalKnowledgePolicy.ResolvedCutoff, PendingActualsPolicy.RequireActuals,
            [..assignments.Select(a => a.Lookup).Distinct()], []) { lngAssignments = assignments };
    }
}
