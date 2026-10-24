using System.Collections.Immutable;

namespace OpenEtrm.MarketData.Trading;

public static class SideComponents
{
    public static IEnumerable<FloatingSide> Floating(Side side)=>side switch {
        FloatingSide floating=>[floating],
        MonthlySwapSide monthly=>monthly.Periods.SelectMany(Floating),
        ScheduledSwapSide scheduled=>scheduled.Periods.SelectMany(Floating),
        _=>[]
    };
    public static ImmutableArray<Guid> ActualFixingPins(MarketSnapshot snapshot,IEnumerable<Side> sides)
    {
        var series=sides.SelectMany(Floating).SelectMany(s=>s.Fixings).Select(f=>f.Series).ToHashSet();
        return snapshot.Selections.Where(p=>p.Input.Use==InputUse.FrozenFact&&p.Observation.Role==ObservationRole.IndexFixing&&series.Contains(p.Input.Series))
            .Select(p=>p.Observation.Key.Id).Distinct().Order().ToImmutableArray();
    }
}
