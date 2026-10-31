using System.Collections.Immutable;
using OpenEtrm.MarketData.Gas;
using OpenEtrm.MarketData.Power;

namespace OpenEtrm.MarketData.Trading;

internal sealed record GasExecutionContext(ScheduleMarketSources Sources, CurveBuild Scenario);

public sealed partial class SideValuationContext
{
    internal GasExecutionContext? GasExecution { get; init; }

    public static SideValuationContext ForPhysicalGas(CurveBuild basis, CurveBuild scenario, ISideDiscounting discounting,
        ImmutableArray<GasValuationAssignment> assignments)
    {
        if (basis is null || scenario is null || assignments.IsDefaultOrEmpty || assignments.Any(a => a is null))
            throw new MarketDataException("side_market_sources", "Supply explicit base/scenario markets and qualified gas valuation assignments.");
        var sources = new ScheduleMarketSources(basis.Graph.Plan.Context, basis, null, null,
            IntervalKnowledgePolicy.ResolvedCutoff, PendingActualsPolicy.RequireActuals,
            [..assignments.Select(a => a.Lookup).Distinct()], [], assignments);
        return new(scenario, discounting) { GasExecution = new(sources, scenario) };
    }
}
