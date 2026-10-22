using System.Collections.Immutable;

namespace OpenEtrm.MarketData.Calculation;

public sealed record ConnectedInputClosure(ImmutableArray<ConnectedPrimitive> Primitives,ImmutableHashSet<ObservationKey> Lineage);

// Index once per prepared plan; resolve only each requested declared closure.
// Numeric inputs contribute evidence without becoming commodity quote shocks.
public sealed class ConnectedInputEvidence
{
    private readonly ConnectedCurvePlan plan;
    private readonly Dictionary<int,ConnectedPrimitive> primitives;
    private readonly Dictionary<int,ObservationKey> numeric;
    public ConnectedInputEvidence(ConnectedCurvePlan plan)
    {
        this.plan=plan??throw new ArgumentNullException(nameof(plan));
        primitives=plan.Primitives.ToDictionary(p=>p.Node);
        numeric=plan.Points.Values.Where(p=>p.Expression is ConnectedBuiltCurve {MultiplierInput:not null})
            .GroupBy(p=>plan.IndexOf(p.Key)).ToDictionary(g=>g.Key,g=>((ConnectedBuiltCurve)g.First().Expression).MultiplierInput!.Observation.Key);
    }
    public ConnectedInputClosure Resolve(ImmutableArray<CurvePointKey> points,CancellationToken cancellation=default)
    {
        cancellation.ThrowIfCancellationRequested();
        if(points.IsDefault)throw new MarketDataException("risk_dependency","Supply explicit connected dependency coordinates.");
        var seen=new HashSet<int>();var pending=new Stack<int>();foreach(var point in points)pending.Push(plan.IndexOf(point));
        var found=ImmutableArray.CreateBuilder<ConnectedPrimitive>();var lineage=ImmutableHashSet.CreateBuilder<ObservationKey>();
        while(pending.TryPop(out var node))
        {
            cancellation.ThrowIfCancellationRequested();if(!seen.Add(node))continue;
            if(primitives.TryGetValue(node,out var primitive)){found.Add(primitive);lineage.Add(primitive.Source.Observation.Key);}
            if(numeric.TryGetValue(node,out var observation))lineage.Add(observation);
            foreach(var dependency in plan.DependencyPlan.Dependencies(node))pending.Push(dependency);
        }
        return new(found.OrderBy(p=>p.Node).ToImmutableArray(),lineage.ToImmutable());
    }
}
