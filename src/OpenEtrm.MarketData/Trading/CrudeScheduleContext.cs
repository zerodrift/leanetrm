using System.Collections.Immutable;
using OpenEtrm.MarketData.Crude;
using OpenEtrm.MarketData.Power;

namespace OpenEtrm.MarketData.Trading;

internal sealed record CrudeExecutionContext(ScheduleMarketSources Sources, CurveBuild Scenario);
public sealed partial class SideValuationContext
{
    internal CrudeExecutionContext? CrudeExecution { get; init; }
    public static SideValuationContext ForPhysicalCrude(CurveBuild basis, CurveBuild scenario, ISideDiscounting discounting,
        ImmutableArray<CrudeValuationAssignment> assignments)
    {
        var sources = ScheduleMarketSources.ForPhysicalCrude(basis, assignments);
        return new(scenario, discounting) { CrudeExecution = new(sources, scenario) };
    }
}
public sealed partial class ScheduleMarketSources
{
    private ImmutableArray<CrudeValuationAssignment> crudeAssignments = [];
    public static ScheduleMarketSources ForPhysicalCrude(CurveBuild basis, ImmutableArray<CrudeValuationAssignment> assignments)
    {
        if (basis is null || assignments.IsDefaultOrEmpty || assignments.Any(a => a is null) ||
            assignments.Select(a => a.Key).Distinct().Count() != assignments.Length)
            throw Invalid("Crude sources require distinct exact grade/custody assignment definitions.");
        return new(basis.Graph.Plan.Context,basis,null,null,IntervalKnowledgePolicy.ResolvedCutoff,PendingActualsPolicy.RequireActuals,
            [..assignments.Select(a => a.Lookup).Distinct()],[]) { crudeAssignments = assignments };
    }
}
