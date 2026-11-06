using System.Collections.Immutable;
using OpenEtrm.MarketData.Calculation;
using OpenEtrm.MarketData.Curves;

namespace OpenEtrm.MarketData.Trading;

public sealed record ProjectionDelivery(DateOnly From,DateOnly To);
public sealed record ProjectionOutput(int Position,CurvePointKey Point,ProjectionDelivery Delivery);

// An index of graph references, never a materialized price panel. Daily reset
// mapping uses delivery identity independently of display/output ordinals.
public sealed class CurvePriceMapping
{
    readonly Dictionary<int,ImmutableArray<CurvePointKey>> positions;
    readonly Dictionary<ProjectionDelivery,ImmutableArray<CurvePointKey>> deliveries;
    readonly ImmutableArray<ProjectionOutput> points;
    private readonly Lazy<CurveLookupGrid<CurvePointKey>> containing;
    public ImmutableArray<ProjectionDelivery> DailyCoverage{get;}
    public CurvePriceMapping(IEnumerable<ProjectionOutput> outputs)
    {
        points=outputs.ToImmutableArray();
        containing=new(()=>new([..points.Select(p=>new CurveLookupMember<CurvePointKey>(p.Point,p.Delivery.From,p.Delivery.To))],ContainingSettings));
        positions=points.GroupBy(p=>p.Position).ToDictionary(g=>g.Key,g=>g.Select(p=>p.Point).ToImmutableArray());
        deliveries=points.GroupBy(p=>p.Delivery).ToDictionary(g=>g.Key,g=>g.Select(p=>p.Point).ToImmutableArray());
        DailyCoverage=!points.IsEmpty&&deliveries.Count==points.Length&&deliveries.Keys.All(d=>d.To.DayNumber-d.From.DayNumber==1)
            ?deliveries.Keys.OrderBy(d=>d.From).ToImmutableArray():[];
    }
    public CurvePointKey Resolve(CustomCurveProjectionBinding binding,MarketContract reset)
        =>Resolve(binding,new ProjectionDelivery(reset.DeliveryFrom,reset.DeliveryTo));
    public CurvePointKey Resolve(CustomCurveProjectionBinding binding,ProjectionDelivery interval)
    {
        binding.Validate();
        if(binding.Selection==CustomCurveProjectionBinding.ContainingDeliverySelection)
        {
            return containing.Value.SelectInterval(interval.From,interval.To).Single().Id;
        }
        var outputs=binding.Selection==CustomCurveProjectionBinding.SupportedSelection?
            positions.GetValueOrDefault(binding.Position!.Value):deliveries.GetValueOrDefault(interval);
        return !outputs.IsDefault&&outputs.Length==1?outputs[0]:
            throw new MarketDataException("custom_projection","Each reset must select exactly one computed output under its position or exact delivery-interval policy.");
    }
    // The selected policy is whole-interval containment, with no interpolation
    // or extrapolation. The span/axis settings do not enter containment arithmetic.
    public static CurveLookupSettings ContainingSettings{get;}=new(CurveLookupMethod.ContainingDelivery,CurveLookupAxis.DeliveryStart,3660,
        "reject","reject",CurveLookupSettings.Version);
    public static ImmutableHashSet<ProjectionDelivery> RequiredIntervals(string selection,IEnumerable<ProjectionDelivery> coverage,
        IEnumerable<ProjectionDelivery> queries)
    {
        var intervals=coverage.ToImmutableArray();var requested=queries.ToImmutableHashSet();
        if(selection==CustomCurveProjectionBinding.DeliverySelection)
        {
            if(requested.Any(query=>intervals.Count(interval=>interval==query)!=1))throw new MarketDataException("curve_coverage","Every requested exact interval requires one source output.");
            return requested;
        }
        if(selection!=CustomCurveProjectionBinding.ContainingDeliverySelection)
            throw new MarketDataException("custom_projection","Select an explicit supported delivery-interval binding.");
        var grid=new CurveLookupGrid<ProjectionDelivery>([..intervals.Select(interval=>new CurveLookupMember<ProjectionDelivery>(interval,interval.From,interval.To))],ContainingSettings);
        return requested.Select(query=>grid.SelectInterval(query.From,query.To).Single().Id).ToImmutableHashSet();
    }
}
