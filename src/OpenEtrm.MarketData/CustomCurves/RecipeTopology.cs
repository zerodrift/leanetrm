using System.Collections.Immutable;
using OpenEtrm.MarketData.Calculation;

namespace OpenEtrm.MarketData.CustomCurves;

public sealed record RecipeOutputShape(int Position,DateOnly DeliveryFrom,DateOnly DeliveryTo,PriceUnit Unit);
public sealed record RecipeNativeDemand(CurveKey Source,DateOnly Date,ContractKey Contract);
// Price-free preparation retains the full declared panel shape. Consumer demand
// walks formula edges before observations are loaded or live eligibility checked.
public sealed class RecipeTopology
{
    internal RecipeEngine.Panel Panel{get;}
    public ImmutableArray<RecipeOutputShape> Outputs=>[..Panel.Cells.Select(cell=>new RecipeOutputShape(cell.Position,cell.DeliveryFrom,cell.DeliveryTo,cell.Unit))];
    private readonly ImmutableDictionary<CurvePointKey,(PublishedRecipeReference Reference,RecipeDelivery Delivery)> published;
    internal RecipeTopology(RecipeEngine.Panel panel,ImmutableDictionary<CurvePointKey,(PublishedRecipeReference,RecipeDelivery)> published)
    {Panel=panel;this.published=published;}
    private IEnumerable<RecipeEngine.CellFormula> Formulas(ImmutableHashSet<RecipeDelivery>? deliveries)
    {
        var pending=new Stack<RecipeEngine.CellFormula>(Panel.Cells.Where(c=>deliveries is null||deliveries.Contains(new(c.DeliveryFrom,c.DeliveryTo))).Select(c=>c.Formula));
        var visited=new HashSet<RecipeEngine.CellFormula>(ReferenceEqualityComparer.Instance);
        while(pending.TryPop(out var formula))
        {
            if(!visited.Add(formula))continue;yield return formula;
            if(formula.PublishedPoint is null)foreach(var child in formula.Inputs)pending.Push(child);
        }
    }
    public ImmutableHashSet<RecipeNativeDemand> NativeInputs(ImmutableHashSet<RecipeDelivery>? deliveries)=>
        Formulas(deliveries).Where(f=>f.Sample is not null).Select(f=>new RecipeNativeDemand(f.Sample!.Source,f.Sample.Date,f.Sample.Contract)).ToImmutableHashSet();
    public ImmutableDictionary<PublishedRecipeReference,ImmutableHashSet<RecipeDelivery>> PublishedInputs(ImmutableHashSet<RecipeDelivery>? deliveries)=>
        Formulas(deliveries).Where(f=>f.PublishedPoint is not null).Select(f=>published[f.PublishedPoint!.Value])
            .GroupBy(p=>p.Reference).ToImmutableDictionary(g=>g.Key,g=>g.Select(p=>p.Delivery).ToImmutableHashSet());
}

public static partial class RecipeEngine
{
    public static RecipeTopology Topology(RecipeDefinition definition,RecipeInputs inputs,
        IReadOnlyDictionary<string,RecipeTopology> publishedSources,ImmutableDictionary<DateOnly,DateTimeOffset> samplingInstants,
        CancellationToken cancellation=default)
    {
        var references=ImmutableDictionary.CreateBuilder<CurvePointKey,(PublishedRecipeReference,RecipeDelivery)>();
        var sources=new Dictionary<string,PanelSource>();
        foreach(var (node,topology) in publishedSources)
        {
            var reference=definition.Nodes.Single(n=>n.Id==node).PublishedSource??throw Invalid("An exact published source is required.");
            // These identities are local planning edges, never calculation points.
            var curve=new CurveKey(new(reference.CatalogId,ConnectedIdentity.Id(new{Version="recipe_topology_v1",reference})),"topology");
            var keys=topology.Panel.Cells.Select((cell,i)=>new CurvePointKey(curve,ConnectedIdentity.Id(new{Index=i}))).ToArray();
            for(var i=0;i<keys.Length;i++)
            {
                var cell=topology.Panel.Cells[i];var value=(reference,new RecipeDelivery(cell.DeliveryFrom,cell.DeliveryTo));
                if(references.TryGetValue(keys[i],out var prior)&&prior!=value)throw Invalid("Conflicting published topology.");
                references[keys[i]]=value;
            }
            sources.Add(node,new(topology.Panel,i=>keys[i]));
        }
        var panel=Prepare(definition,inputs,[],cancellation,sources,new(ImmutableHashSet<RecipeDelivery>.Empty,samplingInstants));
        return new(panel,references.ToImmutable());
    }
}
