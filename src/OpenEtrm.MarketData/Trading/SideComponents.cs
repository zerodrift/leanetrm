using System.Collections.Immutable;

namespace OpenEtrm.MarketData.Trading;

public static class SideComponents
{
    public static ImmutableArray<Guid> ActualFixingPins(MarketSnapshot snapshot,IEnumerable<Side> sides)
    {
        var series=sides.OfType<FloatingSide>().SelectMany(s=>s.Fixings).Select(f=>f.Series).ToHashSet();
        return snapshot.Selections.Where(p=>p.Input.Use==InputUse.FrozenFact&&p.Observation.Role==ObservationRole.IndexFixing&&series.Contains(p.Input.Series))
            .Select(p=>p.Observation.Key.Id).Distinct().Order().ToImmutableArray();
    }
}
