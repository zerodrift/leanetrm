using System.Collections.Immutable;
using OpenEtrm.MarketData.Calculation;

namespace OpenEtrm.MarketData.Trading;

public sealed record ProjectionDelivery(DateOnly From,DateOnly To);
public sealed record ProjectionOutput(int Position,CurvePointKey Point,ProjectionDelivery Delivery);

// An index of graph references, never a materialized price panel. Daily reset
// mapping uses delivery identity independently of display/output ordinals.
public sealed class CustomProjectionMapping
{
    readonly Dictionary<int,ImmutableArray<CurvePointKey>> positions;
    readonly Dictionary<ProjectionDelivery,ImmutableArray<CurvePointKey>> deliveries;
    public ImmutableArray<ProjectionDelivery> DailyCoverage{get;}
    public CustomProjectionMapping(IEnumerable<ProjectionOutput> outputs)
    {
        var points=outputs.ToImmutableArray();
        positions=points.GroupBy(p=>p.Position).ToDictionary(g=>g.Key,g=>g.Select(p=>p.Point).ToImmutableArray());
        deliveries=points.GroupBy(p=>p.Delivery).ToDictionary(g=>g.Key,g=>g.Select(p=>p.Point).ToImmutableArray());
        DailyCoverage=!points.IsEmpty&&deliveries.Count==points.Length&&deliveries.Keys.All(d=>d.To.DayNumber-d.From.DayNumber==1)
            ?deliveries.Keys.OrderBy(d=>d.From).ToImmutableArray():[];
    }
    public CurvePointKey Resolve(CustomCurveProjectionBinding binding,MarketContract reset)
    {
        binding.Validate();
        var outputs=binding.Selection==CustomCurveProjectionBinding.SupportedSelection?
            positions.GetValueOrDefault(binding.Position!.Value):deliveries.GetValueOrDefault(new(reset.DeliveryFrom,reset.DeliveryTo));
        return !outputs.IsDefault&&outputs.Length==1?outputs[0]:
            throw new MarketDataException("custom_projection","Each reset must select exactly one computed output under its position or exact delivery-interval policy.");
    }
}
